using System.Reflection;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ModelContextProtocol.Server;

namespace ContactsApi.Tests;

/// <summary>MCP tokens start read-only and are rate limited (AD-50 rule 9)</summary>
public class AccessTokenTests
{
    /// <summary>The tools that change data: they need the write scope</summary>
    private static readonly HashSet<string> WriteTools = new()
    {
        "atlas_merge_activities", "atlas_split_activity", "atlas_fuse", "atlas_update_activity", "atlas_delete_activity",
        "atlas_create_activity", "atlas_create_place", "atlas_update_place", "atlas_delete_place", "atlas_delete_visit",
        "atlas_add_health", "atlas_delete_import"
    };

    private static (MethodInfo Method, string Name)[] Tools() =>
        typeof(AtlasMcpTools).GetMethods()
            .Select(m => (m, m.GetCustomAttribute<McpServerToolAttribute>()?.Name))
            .Where(t => t.Name != null)
            .Select(t => (t.m, t.Name!))
            .ToArray();

    private static Exception? Invoke(MethodInfo method, bool canWrite)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Items[AtlasMcpTools.OwnerItem] = "owner-1";
        http.HttpContext.Items[AtlasMcpTools.CanWriteItem] = canWrite;
        // No Atlas service: a call that gets past the scope check fails on it instead
        var tools = new AtlasMcpTools(null!, http, null!);
        var args = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        try
        {
            var result = method.Invoke(tools, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
            }

            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Every_tool_that_changes_data_is_listed_as_a_write_tool()
    {
        var names = Tools().Select(t => t.Name).ToHashSet();
        Assert.Subset(names, WriteTools);
        // Read tools are named for what they read; a new mutating tool must be added above
        foreach (var name in names.Except(WriteTools))
        {
            Assert.DoesNotMatch("(create|update|delete|merge|split|add|fuse)", name);
        }
    }

    [Fact]
    public void A_read_only_token_cannot_use_any_write_tool()
    {
        foreach (var (method, name) in Tools().Where(t => WriteTools.Contains(t.Name)))
        {
            var error = Invoke(method, canWrite: false);
            Assert.True(error is ModelContextProtocol.McpException, $"{name}: {error?.GetType().Name}");
            Assert.Contains("read-only", error!.Message);
        }
    }

    [Fact]
    public void A_write_token_gets_past_the_scope_check_and_read_tools_need_no_scope()
    {
        foreach (var (method, name) in Tools().Where(t => WriteTools.Contains(t.Name)))
        {
            Assert.False(Invoke(method, canWrite: true) is ModelContextProtocol.McpException, name);
        }

        foreach (var (method, name) in Tools().Where(t => !WriteTools.Contains(t.Name)))
        {
            Assert.False(Invoke(method, canWrite: false) is ModelContextProtocol.McpException, name);
        }
    }

    [Fact]
    public void The_rate_limit_holds_per_minute_and_per_day_and_per_token()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        var limiter = new PatRateLimiter(new PatRateLimitOptions { PerMinute = 3, PerDay = 5 }, clock);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(a, out _));
        }

        Assert.False(limiter.TryAcquire(a, out var wait));
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.True(limiter.TryAcquire(b, out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(a, out _));
        Assert.True(limiter.TryAcquire(a, out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.TryAcquire(a, out wait));
        Assert.True(wait > TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(limiter.TryAcquire(a, out _));
    }
}

/// <summary>Token expiry and the write scope against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class AccessTokenDbTests
{
    private static AtlasDbContext? Db()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        return string.IsNullOrEmpty(cs)
            ? null
            : new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
                .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
    }

    [Fact]
    public async Task New_tokens_are_read_only_for_90_days_and_expired_tokens_are_refused()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        await db.Database.MigrateAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var svc = new AccessTokenService(db, clock);
        var owner = "test-" + Guid.NewGuid();

        var (readOnly, info) = await svc.CreateAsync(owner, "client");
        var (writer, writeInfo) = await svc.CreateAsync(owner, "writer", allowWrite: true, days: 1000);

        Assert.False(info.CanWrite);
        Assert.Equal(90, Math.Round((info.ExpiresAt - info.CreatedAt).TotalDays));
        Assert.True(writeInfo.CanWrite);
        Assert.Equal(365, Math.Round((writeInfo.ExpiresAt - writeInfo.CreatedAt).TotalDays));
        Assert.False((await svc.CheckAsync(readOnly))!.CanWrite);
        Assert.True((await svc.CheckAsync(writer))!.CanWrite);
        Assert.NotNull((await svc.ListAsync(owner)).Single(t => t.Id == info.Id).LastUsedAt);

        clock.Advance(TimeSpan.FromDays(91));
        Assert.Null(await svc.CheckAsync(readOnly));
        Assert.NotNull(await svc.CheckAsync(writer));

        await db.AccessTokens.Where(t => t.OwnerId == owner).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Existing_tokens_get_90_days_and_keep_write_access_with_a_notice()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261030163812_Flights");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO atlas.access_tokens ("Id", "OwnerId", "Name", "TokenHash", "Prefix", "CreatedAt") VALUES ({id}, 'legacy-owner', 'old', {Guid.NewGuid().ToString()}, 'atl_xxxx', now())""");

        await db.Database.MigrateAsync();

        var row = await db.AccessTokens.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.InRange((row.ExpiresAt - DateTime.UtcNow).TotalDays, 89.9, 90.1);
        var listed = (await new AccessTokenService(db).ListAsync("legacy-owner")).Single(t => t.Id == id);
        Assert.True(listed.CanWrite);
        Assert.True(listed.Legacy);
        await db.AccessTokens.Where(t => t.Id == id).ExecuteDeleteAsync();
    }
}
