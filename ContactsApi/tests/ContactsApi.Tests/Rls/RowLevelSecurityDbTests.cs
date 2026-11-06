using System.Collections.Concurrent;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using ContactsApi.Infrastructure.Tenancy;
using ContactsApi.WebApi.Services;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ContactsApi.Tests.Rls;

/// <summary>
/// Database isolation (AD-49 rule 9, story 21.15) against PostgreSQL 18 in a container:
/// the policies, the roles, the interceptor and the converted cross-Owner paths, as the
/// runtime roles. Skipped, with the reason, where Docker is not available.
/// </summary>
[Collection(RlsCollection.Name)]
public class RowLevelSecurityDbTests
{
    private const string A = RlsDatabase.A, B = RlsDatabase.B, C = RlsDatabase.C;
    private readonly RlsDatabase _db;

    public RowLevelSecurityDbTests(RlsDatabase db) => _db = db;

    private static string? SqlState(Exception ex) =>
        ex is PostgresException pg ? pg.SqlState : ex.InnerException == null ? null : SqlState(ex.InnerException);

    // ---- 9.1 Catalog

    [DockerFact]
    public async Task Every_owner_table_forces_its_policy_and_has_an_owner_column()
    {
        await using var c = new NpgsqlConnection(_db.Superuser);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT n.nspname || '.' || t.relname AS name, t.relrowsecurity, t.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policy p WHERE p.polrelid = t.oid AND p.polname = 'owner_isolation'
                      AND p.polpermissive AND p.polcmd = '*'
                      AND pg_get_expr(p.polqual, p.polrelid) = pg_get_expr(p.polwithcheck, p.polrelid)
                      AND (pg_get_expr(p.polqual, p.polrelid) LIKE '%current_setting(''app.owner_id''::text, true)%'
                           OR (t.relname = 'track_points' AND pg_get_expr(p.polqual, p.polrelid) LIKE '%atlas.activities%'))) AS policies,
                   (SELECT count(*) FROM pg_policy p WHERE p.polrelid = t.oid) AS all_policies,
                   EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = t.oid AND a.attname = 'OwnerId' AND NOT a.attisdropped) AS has_owner
              FROM pg_class t JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname IN ('atlas', 'routes') AND t.relkind IN ('r', 'p')
             ORDER BY 1
            """, c);
        var rows = new List<(string Name, bool Rls, bool Force, long Policies, long All, bool Owner)>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                rows.Add((r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetInt64(3), r.GetInt64(4), r.GetBoolean(5)));
            }
        }

        // The system allowlist (AD-49 rule 1): the history tables only
        var system = new[] { "atlas.__ef_migrations", "routes.__ef_migrations" };
        Assert.Equal(system, rows.Where(r => !r.Rls).Select(r => r.Name).ToArray());
        var owner = rows.Where(r => !system.Contains(r.Name)).ToList();
        Assert.Equal(RlsDatabase.AtlasOwnerTables.Count() + 1 + RlsDatabase.RoutesOwnerTables.Count(), owner.Count);
        Assert.All(owner, r =>
        {
            Assert.True(r.Rls && r.Force, $"{r.Name}: row-level security must be enabled and forced");
            Assert.True(r.Policies == 1 && r.All == 1, $"{r.Name}: exactly the owner_isolation policy");
            Assert.True(r.Owner || r.Name == "atlas.track_points", $"{r.Name}: no OwnerId column");
        });
    }

    [DockerFact]
    public async Task Every_table_of_both_models_is_covered_by_row_level_security()
    {
        await using var atlas = _db.MigratorAtlas();
        await using var routes = _db.MigratorRoutes();
        var tables = atlas.Model.GetEntityTypes().Select(e => $"{e.GetSchema()}.{e.GetTableName()}")
            .Concat(routes.Model.GetEntityTypes().Select(e => $"{e.GetSchema()}.{e.GetTableName()}")).Distinct().ToList();
        foreach (var table in tables)
        {
            var on = await RlsDatabase.ScalarAsync<bool>(_db.Superuser,
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{table}'::regclass");
            Assert.True(on, $"{table} has no forced row-level security");
        }
    }

    [DockerFact]
    public async Task The_runtime_roles_own_nothing_bypass_nothing_and_hold_no_ddl_privilege()
    {
        foreach (var role in new[] { "contactsapi", "echo_routes" })
        {
            Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser,
                $"SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = '{role}'"));
            Assert.Equal(0L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"""
                SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                         WHERE c.relowner = '{role}'::regrole AND n.nspname IN ('atlas', 'routes', 'public'))
                     + (SELECT count(*) FROM pg_namespace WHERE nspowner = '{role}'::regrole)
                     + (SELECT count(*) FROM pg_proc WHERE proowner = '{role}'::regrole)
                     + (SELECT count(*) FROM pg_database WHERE datdba = '{role}'::regrole)
                """));
            Assert.Equal(0L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"""
                SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname IN ('atlas', 'routes') AND c.relkind IN ('r', 'p')
                   AND (has_table_privilege('{role}', c.oid, 'TRUNCATE') OR has_table_privilege('{role}', c.oid, 'TRIGGER')
                        OR has_table_privilege('{role}', c.oid, 'REFERENCES'))
                """));
            foreach (var schema in new[] { "atlas", "routes", "public" })
            {
                Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, $"SELECT has_schema_privilege('{role}', '{schema}', 'CREATE')"));
            }
        }

        // History tables: SELECT only for the role that checks readiness
        Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, "SELECT has_table_privilege('contactsapi', 'atlas.__ef_migrations', 'INSERT')"));
        Assert.True(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, "SELECT has_table_privilege('contactsapi', 'atlas.__ef_migrations', 'SELECT')"));
        Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, "SELECT has_table_privilege('echo_routes', 'routes.__ef_migrations', 'DELETE')"));
    }

    // ---- 9.2 Unset means nothing

    [DockerFact]
    public async Task A_raw_connection_with_no_owner_sees_no_row_and_cannot_write()
    {
        await using (var c = new NpgsqlConnection(_db.As("contactsapi", pooling: false)))
        {
            await c.OpenAsync();
            foreach (var table in RlsDatabase.AtlasOwnerTables.Append("track_points"))
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM atlas.{table}", c);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            }

            await using var insert = new NpgsqlCommand(
                $"INSERT INTO atlas.imports (\"Id\", \"OwnerId\", \"Source\", \"CreatedAt\") VALUES (gen_random_uuid(), '{A}', 'x', now())", c);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);
        }

        await using (var c = new NpgsqlConnection(_db.As("echo_routes", pooling: false)))
        {
            await c.OpenAsync();
            foreach (var table in RlsDatabase.RoutesOwnerTables)
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM routes.{table}", c);
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            }

            await using var delete = new NpgsqlCommand("DELETE FROM routes.matched_routes", c);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
        }

        Assert.True(await RlsDatabase.ScalarAsync<long>(_db.Superuser, "SELECT count(*) FROM routes.matched_routes") >= 3);
    }

    [DockerFact]
    public async Task Through_ef_with_no_owner_every_query_throws_before_it_runs()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        var atlas = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        await Assert.ThrowsAsync<OwnerScopeMissingException>(() => atlas.Activities.CountAsync());
        await Assert.ThrowsAsync<OwnerScopeMissingException>(() => atlas.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").ToListAsync());
        await Assert.ThrowsAsync<OwnerScopeMissingException>(() => atlas.Imports.Where(i => i.OwnerId == A).ExecuteDeleteAsync());
        var routes = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        await Assert.ThrowsAsync<OwnerScopeMissingException>(() => routes.Jobs.ToListAsync());

        // The declared unscoped work runs, and sees nothing (the readiness check, the sys_* calls)
        using (scope.ServiceProvider.GetRequiredService<OwnerScope>().AllowUnscoped())
        {
            Assert.Equal(0, await atlas.Activities.CountAsync());
            Assert.Empty(await EchoMigrator.BlockingPendingAsync(atlas, Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.Id, true, default));
        }
    }

    [DockerFact]
    public async Task Shadow_mode_logs_the_missing_owner_and_runs_with_none()
    {
        var logs = new CapturingLogs();
        await using var sp = _db.Backend(OwnerRlsMode.Shadow, logs: logs);
        using var scope = sp.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Activities.CountAsync());
        Assert.Contains(logs.Messages, m => m.Contains(OwnerRlsInterceptor.MissingMessage, StringComparison.Ordinal));
    }

    // ---- 9.3 Cross-tenant read and write

    [DockerFact]
    public async Task An_owner_sees_only_their_rows_even_without_a_where_or_with_another_owners_ids()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(A);
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        Assert.Equal(new[] { A }, await db.Activities.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Places.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Visits.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.HealthSamples.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Imports.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.AccessTokens.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Fusions.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.FusionDecisions.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Connections.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.FusionQueue.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.FusionSettings.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Efforts.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Photos.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.OwnerSettings.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Flights.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.Reviews.Select(x => x.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await db.ReviewReminders.Select(x => x.OwnerId).Distinct().ToListAsync());
        // track_points through its activity
        Assert.Equal(new[] { _db.ActivityOf[A] }, await db.TrackPoints.Select(p => p.ActivityId).Distinct().ToListAsync());

        // Raw SQL with no owner predicate at all
        Assert.Equal(new[] { A }, await db.Database.SqlQuery<string>($"SELECT DISTINCT \"OwnerId\" AS \"Value\" FROM atlas.activities").ToListAsync());
        Assert.Equal(0, (await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM atlas.track_points WHERE \"ActivityId\" = {_db.ActivityOf[B]}").ToListAsync()).Single());

        // Handed B's ids: by id, and through the raw SQL of the routes feed
        var bActivity = _db.ActivityOf[B];
        Assert.Null(await db.Activities.FirstOrDefaultAsync(a => a.Id == bActivity));
        var feed = scope.ServiceProvider.GetRequiredService<AtlasRoutesFeed>();
        Assert.Empty(await feed.TracksAsync(B, new[] { bActivity }));
        Assert.Empty(await feed.ExistsAsync(B, new List<Guid> { bActivity }));

        // routes, as the backend reads it
        var routes = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        Assert.Equal(new[] { A }, await routes.MatchedRoutes.Select(m => m.OwnerId).Distinct().ToListAsync());
        Assert.Equal(new[] { A }, await routes.Decisions.Select(m => m.OwnerId).Distinct().ToListAsync());
    }

    [DockerFact]
    public async Task Another_owners_rows_cannot_be_changed_deleted_or_written()
    {
        await using var sp = _db.Backend();
        var bActivity = _db.ActivityOf[B];
        var bPlace = _db.PlaceOf[B];

        async Task<AtlasDbContext> AsA(IServiceScope s)
        {
            s.ServiceProvider.GetRequiredService<OwnerScope>().Set(A);
            return await Task.FromResult(s.ServiceProvider.GetRequiredService<AtlasDbContext>());
        }

        using (var scope = sp.CreateScope())
        {
            var db = await AsA(scope);
            Assert.Equal(0, await db.Activities.Where(a => a.Id == bActivity).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "pwned")));
            Assert.Equal(0, await db.Places.Where(p => p.Id == bPlace).ExecuteDeleteAsync());
            Assert.Equal(0, await db.Database.ExecuteSqlAsync($"DELETE FROM atlas.track_points WHERE \"ActivityId\" = {bActivity}"));
            Assert.Equal(0, await db.Database.ExecuteSqlAsync($"UPDATE atlas.activities SET \"ClusterId\" = \"Id\" WHERE \"Id\" = {bActivity}"));
        }

        using (var scope = sp.CreateScope())
        {
            var db = await AsA(scope);
            db.Imports.Add(new ImportRun { OwnerId = B, Source = "x" });
            Assert.Equal("42501", SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())));
        }

        using (var scope = sp.CreateScope())
        {
            // Moving an own row to B fails WITH CHECK
            var db = await AsA(scope);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                db.Imports.Where(i => i.OwnerId == A).ExecuteUpdateAsync(s => s.SetProperty(i => i.OwnerId, B)));
            Assert.Equal("42501", SqlState(ex));
        }

        using (var scope = sp.CreateScope())
        {
            // A point on B's activity: the parent is not visible, so WITH CHECK fails
            var db = await AsA(scope);
            db.TrackPoints.Add(new TrackPoint { ActivityId = bActivity, Time = DateTime.UtcNow, Lat = 1, Lon = 1 });
            Assert.Equal("42501", SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())));
        }

        using (var scope = sp.CreateScope())
        {
            // The backend's routes writes (decisions) are scoped the same way
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(A);
            var routes = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
            routes.Decisions.Add(new RouteDecision { OwnerId = B, SourceKey = "evil" });
            Assert.Equal("42501", SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => routes.SaveChangesAsync())));
        }

        Assert.Equal(1L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM atlas.places WHERE \"Id\" = '{bPlace}'"));
        Assert.Equal(2L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM atlas.track_points WHERE \"ActivityId\" = '{bActivity}'"));
        Assert.Equal(0L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, "SELECT count(*) FROM atlas.activities WHERE \"Name\" = 'pwned'"));
    }

    // ---- 9.4 Pooling

    [DockerFact]
    public async Task A_pooled_connection_never_carries_the_previous_owner()
    {
        await using var sp = _db.Backend(maxPool: 1);
        string pooled;
        using (var scope = sp.CreateScope())
        {
            pooled = scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.GetConnectionString()!;
        }

        foreach (var owner in new[] { A, B, A })
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(owner);
            Assert.Equal(new[] { owner }, await scope.ServiceProvider.GetRequiredService<AtlasDbContext>()
                .Activities.Select(a => a.OwnerId).Distinct().ToListAsync());
        }

        // A raw connection from the same pool (size 1: the same physical connection)
        await using var raw = new NpgsqlConnection(pooled);
        await raw.OpenAsync();
        await using var setting = new NpgsqlCommand("SELECT coalesce(current_setting('app.owner_id', true), '')", raw);
        Assert.Equal(string.Empty, (string)(await setting.ExecuteScalarAsync())!);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM atlas.activities", raw);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [DockerFact]
    public async Task The_owner_cannot_change_inside_a_transaction()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<OwnerScope>();
        owners.Set(A);
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        Assert.Equal(1, await db.Activities.CountAsync());
        Assert.Throws<OwnerScopeChangeException>(() => owners.Set(B));
        Assert.Throws<OwnerScopeChangeException>(() => owners.Use(B));
        await tx.RollbackAsync();
        owners.Set(B);
        Assert.Equal(new[] { B }, await db.Activities.Select(a => a.OwnerId).ToListAsync());
    }

    [DockerFact]
    public async Task After_a_rollback_or_a_rollback_to_a_savepoint_the_owner_is_applied_again()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(A);
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();

        async Task ClearBehindTheInterceptorsBackAsync(System.Data.Common.DbTransaction? tx = null)
        {
            // What a reverted set_config amounts to: the session no longer carries the Owner,
            // while the interceptor last applied it
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "RESET app.owner_id";
            await cmd.ExecuteNonQueryAsync();
        }

        // A rollback
        Assert.Equal(1, await db.Activities.CountAsync());
        await ClearBehindTheInterceptorsBackAsync();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await tx.RollbackAsync();
        }

        Assert.Equal(1, await db.Activities.CountAsync());

        // A rollback to a savepoint
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await ClearBehindTheInterceptorsBackAsync(tx.GetDbTransaction());
            await tx.CreateSavepointAsync("s1");
            await tx.RollbackToSavepointAsync("s1");
            Assert.Equal(1, await db.Activities.CountAsync());
            await tx.CommitAsync();
        }

        // A transaction disposed without a commit (no interceptor event) counts as rolled back:
        // the session holds '' before it; inside it the Owner is applied again (after the
        // savepoint rollback), and the dispose reverts that to ''
        Assert.Equal(1, await db.Activities.CountAsync());
        await ClearBehindTheInterceptorsBackAsync();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await tx.CreateSavepointAsync("s2");
            await tx.RollbackToSavepointAsync("s2");
            Assert.Equal(1, await db.Activities.CountAsync());
        }

        Assert.Equal(1, await db.Activities.CountAsync());
        await db.Database.CloseConnectionAsync();
    }

    [DockerFact]
    public async Task A_worker_loop_over_several_owners_in_one_scope_sees_the_right_rows_each_time()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<OwnerScope>();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        // One open connection for the whole loop: the per-command check must re-apply
        await db.Database.OpenConnectionAsync();
        foreach (var owner in new[] { A, B, A, C, B })
        {
            using (owners.Use(owner))
            {
                Assert.Equal(new[] { owner }, await db.Activities.AsNoTracking().Select(a => a.OwnerId).Distinct().ToListAsync());
                Assert.Equal(owner, (await db.Database.SqlQuery<string>($"SELECT current_setting('app.owner_id') AS \"Value\"").ToListAsync()).Single());
            }
        }

        await db.Database.CloseConnectionAsync();
    }

    [DockerFact]
    public async Task Two_owners_hammering_a_pool_of_two_never_read_each_other()
    {
        await using var sp = _db.Backend(maxPool: 2);
        var wrong = new ConcurrentBag<string>();

        async Task Loop(string owner)
        {
            for (var i = 0; i < 1000; i++)
            {
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(owner);
                var seen = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Imports.AsNoTracking()
                    .Select(x => x.OwnerId).ToListAsync();
                if (seen.Count == 0 || seen.Any(o => o != owner))
                {
                    wrong.Add($"{owner} saw [{string.Join(",", seen)}] at {i}");
                }
            }
        }

        await Task.WhenAll(Task.Run(() => Loop(A)), Task.Run(() => Loop(B)));
        Assert.Empty(wrong);
    }

    // ---- 9.5 Definer functions

    [DockerFact]
    public async Task The_definer_functions_return_only_ids_or_counts_and_are_locked_down()
    {
        await using var c = new NpgsqlConnection(_db.Superuser);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT n.nspname, p.proname, p.prosecdef, format_type(p.prorettype, NULL), p.proretset,
                   pg_get_userbyid(p.proowner), coalesce(array_to_string(p.proconfig, ';'), ''),
                   coalesce(array_to_string(p.proacl, ';'), '')
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname IN ('atlas', 'routes') ORDER BY 1, 2
            """, c);
        var functions = new List<(string Schema, string Name, bool Definer, string Returns, bool Set, string Owner, string Config, string Acl)>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                functions.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), r.GetBoolean(4), r.GetString(5), r.GetString(6),
                    r.GetString(7)));
            }
        }

        Assert.Equal(7, functions.Count);
        Assert.All(functions, f =>
        {
            Assert.StartsWith("sys_", f.Name);
            Assert.True(f.Definer, f.Name);
            Assert.Contains(f.Returns, new[] { "character varying", "integer" });
            Assert.Equal(f.Schema + "_system", f.Owner);
            Assert.Equal("search_path=pg_catalog, pg_temp", f.Config);
            // No PUBLIC entry ("=X/..."), EXECUTE only for the schema's runtime role
            Assert.DoesNotContain(f.Acl.Split(';'), e => e.StartsWith('='));
        });

        foreach (var (role, own, other) in new[] { ("contactsapi", "atlas", "routes"), ("echo_routes", "routes", "atlas") })
        {
            Assert.True(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, $"""
                SELECT bool_and(has_function_privilege('{role}', p.oid, 'EXECUTE'))
                  FROM pg_proc p WHERE p.pronamespace = '{own}'::regnamespace
                """));
            Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, $"""
                SELECT bool_or(has_function_privilege('{role}', p.oid, 'EXECUTE'))
                  FROM pg_proc p WHERE p.pronamespace = '{other}'::regnamespace
                """));
        }

        // contactsapi cannot run the route service's functions
        await using var api = new NpgsqlConnection(_db.As("contactsapi", pooling: false));
        await api.OpenAsync();
        await using var call = new NpgsqlCommand("SELECT * FROM routes.sys_known_owners()", api);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteReaderAsync())).SqlState);
    }

    [DockerFact]
    public async Task The_definer_functions_give_the_workers_owner_ids_and_resolve_a_token_only_to_its_owner()
    {
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        using var unscoped = scope.ServiceProvider.GetRequiredService<OwnerScope>().AllowUnscoped();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        foreach (var kind in new[] { SysOwnerKinds.Activities, SysOwnerKinds.Flights, SysOwnerKinds.UnplacedPhotos, SysOwnerKinds.PlacesToIdentify })
        {
            var owners = await db.SysOwnersAsync(kind);
            Assert.True(new[] { A, B, C }.All(owners.Contains), kind);
        }

        // Fused rows exist: nothing to backfill
        Assert.Empty(await db.SysOwnersAsync(SysOwnerKinds.FusionBackfill));
        Assert.Contains(B, await db.SysOwnersWithConnectionAsync("onedrive"));
        Assert.Empty(await db.SysOwnersWithConnectionAsync("no-such-provider"));
        await Assert.ThrowsAsync<PostgresException>(() => db.SysOwnersAsync("photos; DROP TABLE atlas.photos"));

        var tokens = new AccessTokenService(db);
        Assert.Equal(B, await tokens.ValidateAsync(RlsDatabase.TokenOf(B)));
        Assert.Equal(A, await tokens.ValidateAsync(RlsDatabase.TokenOf(A)));
        Assert.Null(await tokens.ValidateAsync("atl_not-a-token"));
        // It stamped the use, through the function (contactsapi sees no token row unscoped)
        Assert.True(await RlsDatabase.ScalarAsync<bool>(_db.Superuser,
            $"SELECT \"LastUsedAt\" IS NOT NULL FROM atlas.access_tokens WHERE \"TokenHash\" = '{RlsDatabase.Hash(RlsDatabase.TokenOf(B))}'"));

        await using var wsp = _db.Worker().BuildServiceProvider();
        using var ws = wsp.CreateScope();
        using var wunscoped = ws.ServiceProvider.GetRequiredService<OwnerScope>().AllowUnscoped();
        var routes = ws.ServiceProvider.GetRequiredService<RoutesDbContext>();
        Assert.True(new[] { A, B }.All((await routes.SysKnownOwnersAsync()).Contains));
        Assert.True(new[] { A, B }.All((await routes.SysOwnersWithOpenInteractiveJobsAsync(DateTime.UtcNow)).Contains));
        Assert.Equal(0, await routes.SysPruneFinishedJobsAsync(DateTime.UtcNow.AddDays(-7)));
    }

    // ---- 9.6 Role boundaries

    [DockerFact]
    public async Task The_runtime_roles_stay_inside_their_schemas_and_cannot_change_the_schema()
    {
        async Task<string?> Fails(string role, string sql)
        {
            await using var c = new NpgsqlConnection(_db.As(role, pooling: false));
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, c);
            try
            {
                await cmd.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        Assert.Equal("42501", await Fails("echo_routes", "SELECT 1 FROM atlas.activities LIMIT 1"));
        Assert.Equal("42501", await Fails("contactsapi",
            "INSERT INTO routes.matched_routes (\"Id\", \"OwnerId\") VALUES (gen_random_uuid(), 'x')"));
        foreach (var (role, table) in new[] { ("contactsapi", "atlas.activities"), ("echo_routes", "routes.jobs") })
        {
            Assert.Equal("42501", await Fails(role, $"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY"));
            Assert.Equal("42501", await Fails(role, $"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY"));
            Assert.Equal("42501", await Fails(role, $"DROP POLICY owner_isolation ON {table}"));
            Assert.Equal("42501", await Fails(role, $"DROP TABLE {table}"));
            Assert.Equal("42501", await Fails(role, $"TRUNCATE {table}"));
            Assert.Equal("42501", await Fails(role, "CREATE TABLE atlas.evil (x int)"));
            Assert.Equal("42501", await Fails(role, "CREATE TABLE routes.evil (x int)"));
            Assert.Equal("42501", await Fails(role, "CREATE TABLE public.evil (x int)"));
            Assert.Equal("42501", await Fails(role, "CREATE SCHEMA evil"));
        }
    }

    [DockerFact]
    public async Task The_backup_role_dumps_every_owner_and_a_runtime_role_cannot_dump()
    {
        async Task<(long Exit, string Out, string Err)> Dump(string role)
        {
            var r = await _db.Container.ExecAsync(new[]
            {
                "sh", "-c",
                $"PGPASSWORD='{_db.PasswordOf(role)}' pg_dump -h 127.0.0.1 -U {role} -d contactsdb --data-only -t atlas.activities -t routes.jobs"
            });
            return (r.ExitCode ?? -1, r.Stdout, r.Stderr);
        }

        var backup = await Dump("contacts_backup");
        Assert.True(backup.Exit == 0, backup.Err);
        Assert.Contains(A, backup.Out);
        Assert.Contains(B, backup.Out);

        var runtime = await Dump("contactsapi");
        Assert.NotEqual(0L, runtime.Exit);
        Assert.DoesNotContain(B, runtime.Out);
    }

    // ---- 9.7 Destructive paths

    [DockerFact]
    public async Task The_gone_owner_purge_deletes_only_the_absent_owners_routes_data()
    {
        var gone = "owner-gone-" + Guid.NewGuid().ToString("N")[..8];
        await _db.SeedAsync(gone);
        await using var sp = _db.Worker().BuildServiceProvider();
        var scheduler = new RoutesScheduler(sp.GetRequiredService<IServiceScopeFactory>(), null!, new WakeSignal(), new TaskPause(),
            Options.Create(new RoutesOptions()), NullLogger<RoutesScheduler>.Instance, null,
            sp.GetRequiredService<RowLevelSecurityOptions>());
        var stay = (await RlsDatabase.ScalarAsync<string>(_db.Superuser,
            "SELECT string_agg(o, ',') FROM (SELECT \"OwnerId\" o FROM routes.owner_cursors UNION SELECT \"OwnerId\" FROM routes.jobs) x"))
            .Split(',').Where(o => o != gone).ToList();
        var before = await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM routes.matched_routes WHERE \"OwnerId\" <> '{gone}'");

        await scheduler.DeleteGoneOwnersAsync(stay, default);

        foreach (var table in RlsDatabase.RoutesOwnerTables)
        {
            Assert.Equal(0L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM routes.{table} WHERE \"OwnerId\" = '{gone}'"));
        }

        Assert.Equal(before, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM routes.matched_routes WHERE \"OwnerId\" <> '{gone}'"));
    }

    [DockerFact]
    public async Task The_orphan_sweep_without_an_owner_throws_instead_of_deleting()
    {
        await using var sp = _db.Worker().BuildServiceProvider();
        using var scope = sp.CreateScope();
        var runner = new MatchRunner(scope.ServiceProvider.GetRequiredService<RoutesDbContext>(), null!, null!, null!, null!, null!,
            TimeProvider.System, Options.Create(new RoutesOptions()));
        await Assert.ThrowsAsync<OwnerScopeMissingException>(() => runner.DeleteDerivedAsync(C, new[] { _db.ActivityOf[C] }, default));
        Assert.Throws<OwnerScopeMissingException>(() => scope.ServiceProvider.GetRequiredService<OwnerScope>().Require(C));
        Assert.Equal(1L, await RlsDatabase.ScalarAsync<long>(_db.Superuser,
            $"SELECT count(*) FROM routes.matched_routes WHERE \"OwnerId\" = '{C}'"));

        // Under another Owner's scope it deletes nothing either
        scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(A);
        Assert.Equal(0, await runner.DeleteDerivedAsync(C, new[] { _db.ActivityOf[C] }, default));
        Assert.Equal(1L, await RlsDatabase.ScalarAsync<long>(_db.Superuser,
            $"SELECT count(*) FROM routes.route_quality WHERE \"OwnerId\" = '{C}'"));
    }

    [DockerFact]
    public async Task Flight_rows_are_never_made_under_the_wrong_scope()
    {
        var owner = "owner-flights-" + Guid.NewGuid().ToString("N")[..8];
        await _db.SeedAsync(owner);
        await RlsDatabase.ExecAsync(_db.Superuser, $"DELETE FROM atlas.flights WHERE \"OwnerId\" = '{owner}'");
        await using var sp = _db.Backend();

        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(B);
            var flights = new FlightService(scope.ServiceProvider.GetRequiredService<AtlasDbContext>(), null!, null!, null!, new FlightQueue(),
                NullLogger<FlightService>.Instance);
            await flights.EnsureRowsAsync(owner, default);
        }

        Assert.Equal(0L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM atlas.flights WHERE \"OwnerId\" = '{owner}'"));

        using (var scope = sp.CreateScope())
        {
            // Under the Owner's own scope the row is made (the control)
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(owner);
            var flights = new FlightService(scope.ServiceProvider.GetRequiredService<AtlasDbContext>(), null!, null!, null!, new FlightQueue(),
                NullLogger<FlightService>.Instance);
            await flights.EnsureRowsAsync(owner, default);
        }

        Assert.Equal(1L, await RlsDatabase.ScalarAsync<long>(_db.Superuser, $"SELECT count(*) FROM atlas.flights WHERE \"OwnerId\" = '{owner}'"));
    }

    [DockerFact]
    public async Task Photo_placement_runs_one_owner_at_a_time_and_touches_no_one_else()
    {
        var owner = "owner-photos-" + Guid.NewGuid().ToString("N")[..8];
        await _db.SeedAsync(owner);
        await using var sp = _db.Backend();
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(owner);
            var atlas = new AtlasService(scope.ServiceProvider.GetRequiredService<AtlasDbContext>(), NullLogger<AtlasService>.Instance);
            Assert.Equal(1, await atlas.PlacePendingPhotosAsync(owner, 500));
            // Asked for another Owner under this scope: nothing is visible, nothing changes
            Assert.Equal(0, await atlas.PlacePendingPhotosAsync(B, 500));
        }

        Assert.True(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, $"SELECT \"Placed\" FROM atlas.photos WHERE \"OwnerId\" = '{owner}'"));
        Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser, $"SELECT \"Placed\" FROM atlas.photos WHERE \"OwnerId\" = '{B}'"));
    }

    // ---- The migrate-only path and the policy gate

    [DockerFact]
    public async Task The_migrator_holds_the_policy_migration_back_until_policies_are_on()
    {
        var name = "gate_" + Guid.NewGuid().ToString("N")[..8];
        await RlsDatabase.ExecAsync(_db.Superuser, $"CREATE DATABASE {name}");
        var cs = new NpgsqlConnectionStringBuilder(_db.Superuser) { Database = name, Pooling = false }.ConnectionString;
        var options = new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql(cs, n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).Options;
        var id = Infrastructure.Routes.Migrations.RoutesRowLevelSecurity.Id;

        await using (var db = new RoutesDbContext(options))
        {
            await EchoMigrator.MigrateAsync(db, id, false, NullLogger.Instance, default);
            Assert.Equal(new[] { id }, await db.Database.GetPendingMigrationsAsync());
            Assert.Empty(await EchoMigrator.BlockingPendingAsync(db, id, false, default));
            Assert.Equal(new[] { id }, await EchoMigrator.BlockingPendingAsync(db, id, true, default));
        }

        Assert.False(await RlsDatabase.ScalarAsync<bool>(cs, "SELECT relrowsecurity FROM pg_class WHERE oid = 'routes.jobs'::regclass"));
        Assert.Equal(0, await EchoMigrator.RunMigrateOnlyAsync<RoutesDbContext>(cs, _ => options, id, true, NullLogger.Instance));
        Assert.True(await RlsDatabase.ScalarAsync<bool>(cs, "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'routes.jobs'::regclass"));
        Assert.Equal(2, await EchoMigrator.RunMigrateOnlyAsync<RoutesDbContext>(null, _ => options, id, true, NullLogger.Instance));
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception));
        }
    }
}
