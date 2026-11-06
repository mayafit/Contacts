using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Tenancy;
using ContactsApi.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContactsApi.Tests.Rls;

/// <summary>
/// MCP tokens under row-level security (AD-50 rule 9 with AD-49): the definer function refuses
/// expired tokens, and the token's rights are read under its Owner, as contactsapi with the
/// interceptor enforcing. Skipped where Docker is not available.
/// </summary>
[Collection(RlsCollection.Name)]
public class AccessTokenRlsDbTests
{
    private readonly RlsDatabase _db;

    public AccessTokenRlsDbTests(RlsDatabase db) => _db = db;

    private async Task<string> AddTokenAsync(string owner, DateTime expiresAt, string scopes)
    {
        var token = "atl_rls-" + Guid.NewGuid().ToString("N");
        await using var migrator = _db.MigratorAtlas();
        migrator.AccessTokens.Add(new AccessToken
        {
            OwnerId = owner, Name = "t", TokenHash = RlsDatabase.Hash(token), Prefix = token[..8], ExpiresAt = expiresAt, Scopes = scopes
        });
        await migrator.SaveChangesAsync();
        return token;
    }

    [DockerFact]
    public async Task A_valid_token_resolves_to_its_owner_with_its_rights_and_sets_the_request_owner()
    {
        var owner = "owner-pat-" + Guid.NewGuid().ToString("N")[..8];
        var reader = await AddTokenAsync(owner, DateTime.UtcNow.AddDays(30), "atlas");
        var writer = await AddTokenAsync(owner, DateTime.UtcNow.AddDays(30), "atlas write");
        await using var sp = _db.Backend();

        foreach (var (token, canWrite) in new[] { (reader, false), (writer, true) })
        {
            // As the /api/mcp middleware: a fresh request scope with no Owner yet
            using var scope = sp.CreateScope();
            var owners = scope.ServiceProvider.GetRequiredService<OwnerScope>();
            var tokens = new AccessTokenService(scope.ServiceProvider.GetRequiredService<AtlasDbContext>(), owners);

            var result = await tokens.CheckAsync(token);

            Assert.NotNull(result);
            Assert.Equal(owner, result!.OwnerId);
            Assert.Equal(canWrite, result.CanWrite);
            Assert.NotEqual(Guid.Empty, result.Id);
            Assert.Equal(owner, owners.OwnerId);
        }
    }

    [DockerFact]
    public async Task An_expired_token_is_refused_by_the_definer_function_itself()
    {
        var owner = "owner-exp-" + Guid.NewGuid().ToString("N")[..8];
        var expired = await AddTokenAsync(owner, DateTime.UtcNow.AddMinutes(-1), "atlas write");
        await using var sp = _db.Backend();
        using var scope = sp.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<OwnerScope>();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        Assert.Null(await new AccessTokenService(db, owners).CheckAsync(expired));
        Assert.Null(owners.OwnerId);
        using (owners.AllowUnscoped())
        {
            Assert.Null(await db.SysResolveAccessTokenAsync(RlsDatabase.Hash(expired)));
        }

        // Not even stamped as used
        Assert.False(await RlsDatabase.ScalarAsync<bool>(_db.Superuser,
            $"SELECT \"LastUsedAt\" IS NOT NULL FROM atlas.access_tokens WHERE \"TokenHash\" = '{RlsDatabase.Hash(expired)}'"));
    }

    [DockerFact]
    public async Task The_owners_of_source_connections_come_from_the_definer_function_and_each_reads_only_its_own()
    {
        await using var sp = _db.Backend();
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        List<string> owners;
        using (var unscoped = scopes.CreateUnscopedScope())
        {
            owners = await unscoped.ServiceProvider.GetRequiredService<AtlasDbContext>().SysOwnersAsync(SysOwnerKinds.SourceConnections);
        }

        Assert.True(new[] { RlsDatabase.A, RlsDatabase.B, RlsDatabase.C }.All(owners.Contains));
        foreach (var owner in new[] { RlsDatabase.A, RlsDatabase.B })
        {
            using var scope = scopes.CreateOwnerScope(owner);
            var rows = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Connections.AsNoTracking().ToListAsync();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(owner, r.OwnerId));
        }
    }

    [DockerFact]
    public async Task The_key_ring_rotation_reads_and_replaces_connection_secrets_under_each_owner()
    {
        var owner = "owner-sec-" + Guid.NewGuid().ToString("N")[..8];
        await using (var migrator = _db.MigratorAtlas())
        {
            migrator.Connections.Add(new SourceConnection { OwnerId = owner, Provider = "sports-tracker", SecretProtected = "old-secret", Status = "expired" });
            await migrator.SaveChangesAsync();
        }

        await using var sp = _db.Backend();
        var source = new ContactsApi.WebApi.Services.KeyRing.SourceSecretValues(sp.GetRequiredService<IServiceScopeFactory>());
        var values = new List<ContactsApi.WebApi.Services.KeyRing.ProtectedValue>();
        await foreach (var v in source.ReadAllAsync(CancellationToken.None))
        {
            values.Add(v);
        }

        // Every Owner's secrets, an expired connection's included, each tagged with its Owner
        Assert.True(new[] { RlsDatabase.A, RlsDatabase.B, owner }.All(o => values.Any(v => v.Owner == o)));
        var mine = Assert.Single(values, v => v.Owner == owner);
        Assert.Equal("old-secret", mine.Protected);

        Assert.True(await source.ReplaceAsync(mine, "new-secret", CancellationToken.None));
        // Compare-and-set: the stale value no longer matches
        Assert.False(await source.ReplaceAsync(mine, "newer-secret", CancellationToken.None));
        Assert.Equal("new-secret", await RlsDatabase.ScalarAsync<string>(_db.Superuser,
            $"SELECT \"SecretProtected\" FROM atlas.source_connections WHERE \"OwnerId\" = '{owner}'"));
    }
}
