using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Tests;

/// <summary>The Owner's settings documents (AD-41) against a real PostgreSQL (ATLAS_TEST_DB; skipped otherwise)</summary>
public class OwnerSettingsDbTests
{
    private static AtlasDbContext? Db()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        db.Database.Migrate();
        return db;
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task A_document_per_owner_and_kind_is_kept_as_sent_and_reset()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var svc = new OwnerSettingsService(db);
        var owner = "settings-" + Guid.NewGuid();
        var other = "settings-other-" + Guid.NewGuid();

        // None yet: an empty object
        var none = await svc.GetAsync(owner, "ui", default);
        Assert.Equal((JsonValueKind.Object, (DateTime?)null), (none.Json.ValueKind, none.UpdatedAt));
        Assert.Empty(none.Json.EnumerateObject());

        await svc.SaveAsync(owner, "ui", Json("""{"version":1,"text":{"body":18}}"""), default);
        await svc.SaveAsync(owner, "ui", Json("""{"version":1,"text":{"body":20},"future":{"x":[1,2]}}"""), default);
        var saved = await svc.GetAsync(owner, "ui", default);
        Assert.Equal(20, saved.Json.GetProperty("text").GetProperty("body").GetInt32());
        Assert.Equal(2, saved.Json.GetProperty("future").GetProperty("x").GetArrayLength());
        Assert.NotNull(saved.UpdatedAt);

        // Another Owner sees none of it
        Assert.Empty((await svc.GetAsync(other, "ui", default)).Json.EnumerateObject());

        // Refused: an unknown kind, not an object, too large
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetAsync(owner, "secrets", default));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync(owner, "ui", Json("[1,2]"), default));
        var big = "{\"pad\":\"" + new string('x', OwnerSettingsService.MaxBytes) + "\"}";
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveAsync(owner, "ui", Json(big), default));

        Assert.True(await svc.ResetAsync(owner, "ui", default));
        Assert.False(await svc.ResetAsync(owner, "ui", default));
        Assert.Empty((await svc.GetAsync(owner, "ui", default)).Json.EnumerateObject());
    }
}
