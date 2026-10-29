using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Matching;

namespace ContactsApi.Tests;

/// <summary>The roads of a matched line (AD-37, rule routes.v4), on synthetic answers</summary>
public class RouteRoadsTests
{
    private static readonly Func<int, SyntheticTrace.Label> M = v => new SyntheticTrace.Label("matched", v);

    [Fact]
    public void The_trace_request_asks_for_edge_names_and_the_answer_keeps_them()
    {
        Assert.Contains("edge.names", ValhallaTrace.Attributes);

        using var doc = SyntheticTrace.Answer(9, new[] { M(0), M(8) }, names: e => e switch
        {
            0 => new[] { " 431 ", "431", "" },
            _ => null
        });
        var answer = ValhallaTrace.Parse(doc.RootElement);

        // Trimmed, once each, blanks dropped; an edge with no names has none
        Assert.Equal(new[] { "431" }, answer.Edges[0].Names);
        Assert.Null(answer.Edges[1].Names);
    }

    [Fact]
    public void The_line_lists_its_roads_in_travel_order_with_their_metres()
    {
        // 25 vertices of 111 m: route 4 for 8, then a street that also carries 431 for 8, then 4 again
        var vertices = Enumerable.Range(0, 13).Select(i => i * 2).ToList();
        var edges = new[] { (0, 4), (4, 8), (8, 12), (12, 16), (16, 20), (20, 24) };
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(25, vertices.Select(v => M(v)), edges: edges,
            names: e => e switch
            {
                0 or 1 or 4 or 5 => new[] { "4" },
                _ => new[] { "431", "דרך נמיר" }
            }));

        Assert.Equal(new[] { "4", "431", "דרך נמיר" }, r.Roads!.Select(x => x.Name));
        Assert.Equal(new[] { true, true, false }, r.Roads!.Select(x => x.Ref));
        // Route 4 twice (summed, first taken at the start); the shared edges count for both names
        Assert.InRange(r.Roads![0].M, 1770, 1790);
        Assert.Equal(0, r.Roads[0].FromM);
        Assert.InRange(r.Roads[1].M, 885, 895);
        Assert.InRange(r.Roads[1].FromM, 885, 895);
        Assert.Equal(r.Roads[1].FromM, r.Roads[2].FromM);
    }

    [Fact]
    public void A_line_with_no_names_has_no_roads_and_a_failed_match_none_at_all()
    {
        var vertices = new List<int> { 0, 2, 4, 6 };
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(7, vertices.Select(v => M(v))));
        Assert.Empty(r.Roads!);

        var failed = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => null);
        Assert.Equal(RouteStatuses.Failed, failed.Status);
        Assert.Null(failed.Roads);
    }

    [Fact]
    public void Short_roads_are_dropped_and_the_longest_kept_in_travel_order()
    {
        var many = Enumerable.Range(0, RouteRoads.Max + 5).Select(i => ($"r{i}", 200.0 + i, 1000.0 - i)).ToList();
        many.Add(("slip", RouteRoads.MinM - 1, 0));

        var kept = RouteRoads.Keep(many);

        Assert.Equal(RouteRoads.Max, kept.Count);
        Assert.DoesNotContain(kept, k => k.Name == "slip");
        // The 5 shortest of the long ones are the ones left out
        Assert.DoesNotContain(kept, k => k.Name is "r0" or "r4");
        Assert.Equal(kept.OrderBy(k => k.FromM).Select(k => k.Name), kept.Select(k => k.Name));
    }

    [Theory]
    [InlineData("431", true)]
    [InlineData("1", true)]
    [InlineData("20A", true)]
    [InlineData("איילון צפון", false)]
    [InlineData("12345", false)]
    public void Route_numbers_are_told_from_street_names(string name, bool isRef) => Assert.Equal(isRef, RouteRoads.IsRef(name));

    [Theory]
    [InlineData(" 431 ", "431")]
    [InlineData("road 431", "431")]
    [InlineData("Route 4", "4")]
    [InlineData("כביש 431", "431")]
    [InlineData("אבן גבירול", "אבן גבירול")]
    [InlineData("דרך נמיר", "דרך נמיר")]
    [InlineData("road 12a", "12A")]
    [InlineData("highway 6", "6")]
    [InlineData("road", "road")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void What_the_owner_types_is_searched_without_a_road_prefix(string? typed, string? searched) =>
        Assert.Equal(searched, RouteRoads.Normalize(typed));

    [Fact]
    public void Stored_roads_round_trip_and_unreadable_ones_are_empty()
    {
        var roads = RouteRoads.Keep(new[] { ("431", 900.0, 12.0) });
        Assert.Equal(roads, RouteRoads.Parse(RouteRoads.Serialize(roads)));
        Assert.Contains("\"fromM\":12", RouteRoads.Serialize(roads));
        Assert.Empty(RouteRoads.Parse("{not json"));
        Assert.Empty(RouteRoads.Parse(null));
    }

    [Fact]
    public void The_road_query_is_owner_scoped_and_matches_numbers_exactly_and_names_in_part()
    {
        var byNumber = RouteGeometryReader.OnRoadSql(true);
        var byName = RouteGeometryReader.OnRoadSql(false);
        Assert.Contains("m.\"OwnerId\" = @owner", byNumber);
        Assert.Contains("@> ARRAY[@road]", byNumber);
        Assert.Contains("ILIKE @road", byName);
        Assert.Contains("<> 'failed'", byName);
    }
}
