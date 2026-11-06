using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Tests;

/// <summary>
/// AD-49 rule 9.1 without a database: every table of both models is an owner table with
/// OwnerId and is listed by its schema's policy migration (track_points by its parent
/// until rule 10). A new entity without a policy fails here, before any Docker run.
/// </summary>
public class RowLevelSecurityModelTests
{
    [Fact]
    public void Every_atlas_table_has_an_owner_column_and_a_policy()
    {
        using var db = new AtlasDbContextFactory().CreateDbContext(Array.Empty<string>());
        var tables = db.Model.GetEntityTypes().ToDictionary(e => e.GetTableName()!, e => e.FindProperty("OwnerId") != null);
        Assert.Equal(tables.Keys.Where(t => t != "track_points").OrderBy(t => t),
            Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.OwnerTables.OrderBy(t => t));
        Assert.All(tables.Where(t => t.Key != "track_points"), t => Assert.True(t.Value, $"atlas.{t.Key} has no OwnerId"));
        Assert.Contains("track_points", tables.Keys);
    }

    [Fact]
    public void Every_routes_table_has_an_owner_column_and_a_policy()
    {
        using var db = new RoutesDbContextFactory().CreateDbContext(Array.Empty<string>());
        var tables = db.Model.GetEntityTypes().ToDictionary(e => e.GetTableName()!, e => e.FindProperty("OwnerId") != null);
        Assert.Equal(tables.Keys.OrderBy(t => t), Infrastructure.Routes.Migrations.RoutesRowLevelSecurity.OwnerTables.OrderBy(t => t));
        Assert.All(tables, t => Assert.True(t.Value, $"routes.{t.Key} has no OwnerId"));
    }
}
