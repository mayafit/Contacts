using System.Text;
using System.Text.Json;
using ContactsApi.Core.Interfaces;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>
/// Stories 12.6 to 12.8 against PostgreSQL (ATLAS_TEST_DB): the read-time choice of
/// geometry in search, density and the detail view, its query count, decisions and export
/// </summary>
[Collection("routes-db")]
public class RouteGeometryDbTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 6, 0, 0, DateTimeKind.Utc);
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public RouteGeometryDbTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
    }

    private sealed record Ctx(AtlasDbContext Atlas, RoutesDbContext Routes, CommandCounter AtlasCount, CommandCounter RoutesCount) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Atlas.DisposeAsync();
            await Routes.DisposeAsync();
        }
    }

    private static async Task<Ctx?> DbAsync()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var atlasCount = new CommandCounter();
        var routesCount = new CommandCounter();
        var atlas = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).AddInterceptors(atlasCount).Options);
        await atlas.Database.MigrateAsync();
        var routes = new RoutesDbContext(new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).AddInterceptors(routesCount).Options);
        await routes.Database.MigrateAsync();
        atlasCount.Count = 0;
        routesCount.Count = 0;
        return new Ctx(atlas, routes, atlasCount, routesCount);
    }

    private static AtlasService Service(Ctx c, bool enabled = true) =>
        new(c.Atlas, NullLogger<AtlasService>.Instance, null,
            new RouteGeometryReader(c.Routes, new RouteGeometryOptions { Enabled = enabled }, NullLogger<RouteGeometryReader>.Instance));

    /// <summary>A sparse straight drive: 6 points ~550 m apart, a minute apart</summary>
    private static List<PointInput> Drive(double lat0, int start) =>
        Enumerable.Range(0, 6).Select(i => new PointInput(T0.AddMinutes(start + i), lat0 + i * 0.005, 34.85)).ToList();

    /// <summary>A "road" with a bend between every two recorded points (not the straight legs)</summary>
    private static string Road(double lat0) =>
        Geo.EncodePolyline(Enumerable.Range(0, 11).Select(i => (lat0 + i * 0.0025, 34.85 + (i % 2 == 1 ? 0.002 : 0))));

    private static async Task<List<AtlasActivity>> SeedAsync(Ctx c, AtlasService svc, string owner, int n)
    {
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = Enumerable.Range(0, n)
                .Select(i => new ActivityInput($"d{i}", "IN_PASSENGER_VEHICLE", null, T0.AddMinutes(i * 60), T0.AddMinutes(i * 60 + 5),
                    Points: Drive(32.0 + i * 0.1, i * 60)))
                .ToList()
        });
        return await c.Atlas.Activities.AsNoTracking().Where(a => a.OwnerId == owner).OrderBy(a => a.StartTime).ToListAsync();
    }

    private static async Task<string> FusedTypeAsync(Ctx c, AtlasActivity a) =>
        await c.Atlas.Fusions.AsNoTracking().Where(f => f.ActivityId == (a.ClusterId ?? a.Id)).Select(f => f.Type).FirstOrDefaultAsync() ?? a.Type;

    private static async Task MatchAsync(Ctx c, AtlasActivity a, string owner, Action<MatchedRouteRow>? change = null, string? road = null)
    {
        road ??= Road(a.MinLat ?? 32.0);
        var length = AtlasService.DecodePolyline(road).Zip(AtlasService.DecodePolyline(road).Skip(1))
            .Sum(p => Geo.Haversine(p.First.Lat, p.First.Lon, p.Second.Lat, p.Second.Lon));
        var row = new MatchedRouteRow
        {
            OwnerId = owner, ActivityId = a.Id, SourceKey = RouteKeys.SourceKey(a.Source, a.ExternalId, a.Id), InputHash = "x",
            SourceUpdatedAt = a.UpdatedAt, SourcePointCount = a.PointCount, FusedType = await FusedTypeAsync(c, a),
            Status = RouteStatuses.Matched, Confidence = 0.92, Polyline = road, MatchedDistanceM = Math.Round(length),
            Pieces = JsonSerializer.Serialize(new[]
            {
                new RoutePiece(0, Math.Round(length * 0.6, 1), PieceKinds.Observed),
                new RoutePiece(Math.Round(length * 0.6, 1), Math.Round(length, 1), PieceKinds.Inferred)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            RuleVersion = "routes.v1"
        };
        // As the service stores it: the full line and its list and density lines
        (row.ListPolyline, row.DensityPolyline) = RouteLines.Derive(road);
        change?.Invoke(row);
        c.Routes.MatchedRoutes.Add(row);
        await c.Routes.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_draws_drawable_matches_with_one_routes_query_and_no_extra_atlas_query()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 5);
        await MatchAsync(c, records[0], owner);
        // Stale: the record changed after the match (its UpdatedAt differs)
        await MatchAsync(c, records[1], owner, r => r.SourceUpdatedAt = r.SourceUpdatedAt.AddSeconds(-5));
        // Below the threshold
        await MatchAsync(c, records[2], owner, r => { r.Status = RouteStatuses.LowConfidence; r.Confidence = 0.5; });
        // "Keep the original"
        await MatchAsync(c, records[3], owner);
        c.Routes.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = RouteKeys.SourceKey(records[3].Source, records[3].ExternalId, records[3].Id) });
        await c.Routes.SaveChangesAsync();
        // A retyped record: the stored fused type no longer matches
        await MatchAsync(c, records[4], owner, r => r.FusedType = "ride");

        var query = new AtlasQuery { Visits = false };
        c.AtlasCount.Count = 0;
        c.RoutesCount.Count = 0;
        var off = await Service(c, enabled: false).SearchAsync(owner, query);
        var atlasOff = c.AtlasCount.Count;
        Assert.Equal(0, c.RoutesCount.Count);
        Assert.All(off.Activities, a => Assert.Equal(RouteGeometryKinds.Recorded, a.Geometry));

        c.AtlasCount.Count = 0;
        c.RoutesCount.Count = 0;
        c.RoutesCount.Queries.Clear();
        var on = await Service(c).SearchAsync(owner, query);
        // The query-count evidence: the choice adds exactly one set-based routes query
        _out.WriteLine($"search: atlas commands {atlasOff} without routes, {c.AtlasCount.Count} with; routes commands {c.RoutesCount.Count}");
        Assert.Equal(atlasOff, c.AtlasCount.Count);
        Assert.Equal(1, c.RoutesCount.Count);
        // The projection: the stored list line only, never the pieces or the density line
        var sql = Assert.Single(c.RoutesCount.Queries);
        Assert.Contains(@"m.""ListPolyline""", sql);
        Assert.DoesNotContain(@"m.""Pieces""", sql);
        Assert.DoesNotContain(@"m.""DensityPolyline""", sql);

        var byId = on.Activities.ToDictionary(a => a.Id);
        var drawn = byId[records[0].Id];
        Assert.Equal(RouteGeometryKinds.Matched, drawn.Geometry);
        Assert.Equal(Road(records[0].MinLat ?? 32.0), drawn.Polyline);
        // Pieces only go to the detail view
        Assert.Null(drawn.Pieces);
        foreach (var r in records.Skip(1))
        {
            Assert.Equal(RouteGeometryKinds.Recorded, byId[r.Id].Geometry);
            Assert.Equal(off.Activities.Single(a => a.Id == r.Id).Polyline, byId[r.Id].Polyline);
        }

        // Nothing about the choice is serialised but geometry
        var json = JsonSerializer.Serialize(drawn, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"geometry\":\"matched\"", json);
        Assert.DoesNotContain("recordUpdatedAt", json);
        Assert.DoesNotContain("pieces", json);
    }

    [Fact]
    public async Task Search_of_many_records_is_still_one_routes_query()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-many-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 40);
        foreach (var r in records.Where((_, i) => i % 2 == 0))
        {
            await MatchAsync(c, r, owner);
        }

        c.RoutesCount.Count = 0;
        var on = await Service(c).SearchAsync(owner, new AtlasQuery { Visits = false });
        Assert.Equal(1, c.RoutesCount.Count);
        Assert.Equal(20, on.Activities.Count(a => a.Geometry == RouteGeometryKinds.Matched));

        c.RoutesCount.Count = 0;
        c.RoutesCount.Queries.Clear();
        await Service(c).DensityAsync(owner, new AtlasQuery { Visits = false });
        Assert.Equal(1, c.RoutesCount.Count);
        // The projection: the stored density line, never the pieces
        var sql = Assert.Single(c.RoutesCount.Queries);
        Assert.Contains(@"m.""DensityPolyline""", sql);
        Assert.DoesNotContain(@"m.""Pieces""", sql);
        Assert.DoesNotContain(@"m.""ListPolyline"" IS NULL", sql);
    }

    /// <summary>A winding "road" of <paramref name="n"/> vertices (nothing simplifies it away)</summary>
    private static string LongRoad(double lat0, int n) =>
        Geo.EncodePolyline(Enumerable.Range(0, n).Select(i => (lat0 + i * 0.00005, 34.85 + Math.Sin(i * 0.7) * 0.0004)));

    [Fact]
    public async Task Search_sends_matched_lines_within_the_recorded_budget_and_the_detail_the_full_line()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-budget-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 2);
        var full = LongRoad(32.0, 1000);
        await MatchAsync(c, records[0], owner, road: full);
        // Stored before the simplified lines existed: still within the budget
        await MatchAsync(c, records[1], owner, r => (r.ListPolyline, r.DensityPolyline) = (null, null), LongRoad(32.1, 1000));

        var on = await Service(c).SearchAsync(owner, new AtlasQuery { Visits = false });
        Assert.Equal(2, on.Activities.Count(a => a.Geometry == RouteGeometryKinds.Matched));
        Assert.All(on.Activities, a => Assert.InRange(AtlasService.DecodePolyline(a.Polyline!).Count, 2, RouteLines.ListVertices));
        Assert.Equal(RouteLines.Derive(full).List, on.Activities.Single(a => a.Id == records[0].Id).Polyline);

        c.RoutesCount.Queries.Clear();
        var detail = await Service(c).GetActivityAsync(owner, records[0].Id);
        Assert.Equal(full, detail.Summary.Polyline);
        Assert.Contains(c.RoutesCount.Queries, q => q.Contains(@"m.""Pieces""") && q.Contains("matched_routes"));
    }

    [Fact]
    public async Task Density_draws_the_stored_density_line_as_it_is()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-density-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 2);
        // A marker density line: drawn verbatim, so it was not decoded and simplified again
        var marker = Geo.EncodePolyline(new[] { (32.0, 34.8), (32.0001, 34.8001), (32.0002, 34.8) });
        await MatchAsync(c, records[0], owner, r => r.DensityPolyline = marker, LongRoad(32.0, 1000));
        await MatchAsync(c, records[1], owner, r => r.DensityPolyline = null, LongRoad(32.1, 1000));

        var density = await Service(c).DensityAsync(owner, new AtlasQuery { Visits = false });

        Assert.Contains(density.Tracks, t => t.Polyline == marker);
        Assert.All(density.Tracks, t => Assert.InRange(AtlasService.DecodePolyline(t.Polyline).Count, 2, RouteLines.DensityVertices));
    }

    private sealed class Signal : IRouteChangeSignal
    {
        public readonly List<string> Owners = new();

        public void Changed(string owner) => Owners.Add(owner);
    }

    [Fact]
    public async Task A_rename_or_a_note_keeps_the_match_drawn_and_a_new_type_hides_it_and_wakes_the_service()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-edit-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 1);
        await MatchAsync(c, records[0], owner);
        var signal = new Signal();
        var svc = new AtlasService(c.Atlas, NullLogger<AtlasService>.Instance, null,
            new RouteGeometryReader(c.Routes, new RouteGeometryOptions { Enabled = true }, NullLogger<RouteGeometryReader>.Instance), signal);
        var query = new AtlasQuery { Visits = false };

        await svc.UpdateActivityAsync(owner, records[0].Id, new ActivityPatch("Morning drive", null, "to the office"));
        c.Atlas.ChangeTracker.Clear();
        Assert.Equal(RouteGeometryKinds.Matched, Assert.Single((await svc.SearchAsync(owner, query)).Activities).Geometry);
        // The same type again is no change either
        await svc.UpdateActivityAsync(owner, records[0].Id, new ActivityPatch(null, "drive", null));
        c.Atlas.ChangeTracker.Clear();
        Assert.Equal(RouteGeometryKinds.Matched, Assert.Single((await svc.SearchAsync(owner, query)).Activities).Geometry);
        Assert.Empty(signal.Owners);

        await svc.UpdateActivityAsync(owner, records[0].Id, new ActivityPatch(null, "ride", null));
        c.Atlas.ChangeTracker.Clear();
        Assert.Equal(RouteGeometryKinds.Recorded, Assert.Single((await svc.SearchAsync(owner, query)).Activities).Geometry);
        Assert.Equal(new[] { owner }, signal.Owners);
    }

    [Fact]
    public async Task Detail_returns_the_matched_line_with_pieces_and_the_source_points()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-detail-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 1);
        await MatchAsync(c, records[0], owner);
        c.Routes.Quality.Add(new RouteQualityRow
        {
            OwnerId = owner, ActivityId = records[0].Id, SourceKey = RouteKeys.SourceKey(records[0].Source, records[0].ExternalId, records[0].Id),
            Grade = "poor", Score = 31, Flags = "sparse,straight", NeedsImprovement = true, RuleVersion = "routes.v1",
            SourceUpdatedAt = records[0].UpdatedAt, PointCount = records[0].PointCount
        });
        await c.Routes.SaveChangesAsync();

        var detail = await Service(c).GetActivityAsync(owner, records[0].Id);
        Assert.Equal(RouteGeometryKinds.Matched, detail.Summary.Geometry);
        // Not drawn as the straight legs between the recorded points
        Assert.NotEqual(records[0].Polyline, detail.Summary.Polyline);
        Assert.Equal(2, detail.Summary.Pieces!.Count);
        Assert.Equal("inferred", detail.Summary.Pieces[1].Kind);
        Assert.NotNull(detail.Summary.OnRoadsM);
        // The source points stay (charts, analysis and the hover marker use them)
        Assert.Equal(records[0].PointCount, detail.Points.Count);
        Assert.Equal(new[] { records[0].Id }, detail.Route!.RecordIds);
        Assert.Equal("poor", detail.Route.Description!.Grade);
        Assert.Equal(new[] { "sparse", "straight" }, detail.Route.Description.Flags);
        Assert.True(detail.Route.Description.Current);
        Assert.Equal(0.6, detail.Route.ObservedShare!.Value, 2);
        Assert.Equal(0.4, detail.Route.InferredShare!.Value, 2);

        // Disabled: recorded, and no route section at all
        var off = await Service(c, enabled: false).GetActivityAsync(owner, records[0].Id);
        Assert.Equal(RouteGeometryKinds.Recorded, off.Summary.Geometry);
        Assert.Null(off.Route);
    }

    [Fact]
    public async Task Distance_from_the_straight_legs_is_replaced_only_while_the_match_is_drawn()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-dist-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 1);
        // Fusion took the distance from the straight legs (as for a Timeline drive with no distance)
        c.Atlas.Fusions.Add(new ActivityFusion
        {
            ActivityId = records[0].Id, OwnerId = owner, Type = records[0].Type, StartTime = records[0].StartTime, EndTime = records[0].EndTime,
            DistanceM = 2750, MemberCount = 1, Sources = records[0].Source,
            FieldSources = JsonSerializer.Serialize(new Dictionary<string, string> { ["track"] = records[0].Source, ["distanceM"] = records[0].Source, ["distance"] = "track" })
        });
        await c.Atlas.SaveChangesAsync();

        await MatchAsync(c, records[0], owner);
        var recorded = (await Service(c, enabled: false).SearchAsync(owner, new AtlasQuery { Visits = false })).Activities.Single();
        var matched = (await Service(c).SearchAsync(owner, new AtlasQuery { Visits = false })).Activities.Single();
        var row = await c.Routes.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
        Assert.Equal(row.MatchedDistanceM, matched.DistanceM);
        Assert.NotEqual(recorded.DistanceM, matched.DistanceM);

        // "Keep the original" restores the straight-line distance (nothing was overwritten)
        var data = new RouteOwnerData(c.Routes);
        await data.SetDecisionAsync(owner, new[] { row.SourceKey }, RouteDecisionKinds.Reject);
        var reverted = (await Service(c).SearchAsync(owner, new AtlasQuery { Visits = false })).Activities.Single();
        Assert.Equal(recorded.DistanceM, reverted.DistanceM);
        Assert.Equal(RouteGeometryKinds.Recorded, reverted.Geometry);
    }

    [Fact]
    public async Task Decisions_are_by_natural_key_and_try_again_clears_them()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-dec-" + Guid.NewGuid();
        var other = "geom-dec-other-" + Guid.NewGuid();
        var svc = Service(c);
        var records = await SeedAsync(c, svc, owner, 1);
        await MatchAsync(c, records[0], owner);
        var keys = (await svc.RouteRecordsAsync(owner, records[0].Id)).Select(r => r.SourceKey).ToList();
        Assert.Equal(new[] { "google-timeline|d0" }, keys);
        // Another Owner cannot address it
        await Assert.ThrowsAsync<AtlasNotFoundException>(() => svc.RouteRecordsAsync(other, records[0].Id));

        var data = new RouteOwnerData(c.Routes);
        await data.SetDecisionAsync(owner, keys, RouteDecisionKinds.Reject);
        await data.SetDecisionAsync(owner, keys, RouteDecisionKinds.Reject);
        Assert.Equal(1, await c.Routes.Decisions.CountAsync(d => d.OwnerId == owner));
        Assert.Equal(RouteGeometryKinds.Recorded, (await svc.GetActivityAsync(owner, records[0].Id)).Summary.Geometry);
        Assert.Equal("reject", (await svc.GetActivityAsync(owner, records[0].Id)).Route!.Description!.Decision);

        Assert.Equal(1, await data.ClearDecisionAsync(owner, keys));
        Assert.Equal(RouteGeometryKinds.Matched, (await svc.GetActivityAsync(owner, records[0].Id)).Summary.Geometry);
    }

    [Fact]
    public async Task Export_writes_decisions_settings_quality_and_geojson_with_pieces_for_one_owner()
    {
        await using var c = await DbAsync();
        if (c == null)
        {
            return;
        }

        var owner = "geom-exp-" + Guid.NewGuid();
        var other = "geom-exp-other-" + Guid.NewGuid();
        var records = await SeedAsync(c, Service(c), owner, 2);
        await MatchAsync(c, records[0], owner);
        await MatchAsync(c, records[1], owner);
        c.Routes.MatchedRoutes.Add(new MatchedRouteRow { OwnerId = other, ActivityId = Guid.NewGuid(), SourceKey = "x|y", Polyline = Road(31) });
        c.Routes.Settings.Add(new RouteSetting { OwnerId = owner, Json = "{\"threshold\":0.8}" });
        c.Routes.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = "google-timeline|d1" });
        c.Routes.Quality.Add(new RouteQualityRow { OwnerId = owner, ActivityId = records[0].Id, SourceKey = "google-timeline|d0", Grade = "fair", Metrics = "{\"pointsPerKm\":2.1}" });
        await c.Routes.SaveChangesAsync();

        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            await new RouteOwnerData(c.Routes).ExportAsync(owner, writer);
        }

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        var root = doc.RootElement;
        Assert.Equal(0.8, root.GetProperty("settings").GetProperty("threshold").GetDouble());
        Assert.Equal("google-timeline|d1", root.GetProperty("decisions")[0].GetProperty("sourceKey").GetString());
        Assert.Equal(2.1, root.GetProperty("quality")[0].GetProperty("metrics").GetProperty("pointsPerKm").GetDouble());
        var features = root.GetProperty("matchedRoutes").GetProperty("features");
        Assert.Equal(2, features.GetArrayLength());
        var first = features[0];
        Assert.Equal("LineString", first.GetProperty("geometry").GetProperty("type").GetString());
        // Longitude first
        Assert.Equal(34.85, first.GetProperty("geometry").GetProperty("coordinates")[0][0].GetDouble(), 3);
        Assert.Equal("inferred", first.GetProperty("properties").GetProperty("pieces")[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_missing_routes_schema_draws_the_recorded_lines()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var c = await DbAsync();
        // A context whose tables do not exist (the service never ran)
        await using var broken = new RoutesDbContext(new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(cs) { Database = "postgres" }.ConnectionString).Options);
        var owner = "geom-missing-" + Guid.NewGuid();
        var records = await SeedAsync(c!, Service(c!), owner, 1);
        var svc = new AtlasService(c!.Atlas, NullLogger<AtlasService>.Instance, null,
            new RouteGeometryReader(broken, new RouteGeometryOptions { Enabled = true }, NullLogger<RouteGeometryReader>.Instance));
        var found = await svc.SearchAsync(owner, new AtlasQuery { Visits = false });
        Assert.Equal(RouteGeometryKinds.Recorded, found.Activities.Single().Geometry);
        Assert.Equal(records[0].Id, found.Activities.Single().Id);
        var detail = await svc.GetActivityAsync(owner, records[0].Id);
        Assert.Null(detail.Route!.Description);
    }
}
