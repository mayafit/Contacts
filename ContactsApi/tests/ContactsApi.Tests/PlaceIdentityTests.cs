using System.Net;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class PlaceIdentityRuleTests
{
    private static LocationCandidate C(string name, string kind, double distance) =>
        new($"osm:node/{name.GetHashCode() & 0xffffff}", name, null, kind, kind.Split('=')[1], 31.8, 34.8, distance, null, null, null, null, new());

    [Fact]
    public void One_establishment_close_by_and_none_other_around_is_clear()
    {
        var d = PlaceIdentity.Decide(new[] { C("Dor Alon", "amenity=fuel", 4), C("Yesodot East", "highway=bus_stop", 20), C("Parking", "amenity=parking", 30) });
        Assert.Equal("Dor Alon", d.Choice?.Name);
        Assert.Empty(d.Options);
    }

    [Fact]
    public void Two_establishments_near_each_other_need_the_user()
    {
        var d = PlaceIdentity.Decide(new[] { C("Dor Alon", "amenity=fuel", 4), C("Alonit", "shop=convenience", 18) });
        Assert.Null(d.Choice);
        Assert.Equal(new[] { "Dor Alon", "Alonit" }, d.Options.Select(o => o.Name));

        // One close, the other within 80 m: still ambiguous
        Assert.Null(PlaceIdentity.Decide(new[] { C("A", "shop=bakery", 10), C("B", "amenity=cafe", 70) }).Choice);
        // The other farther than 80 m: clear
        Assert.Equal("A", PlaceIdentity.Decide(new[] { C("A", "shop=bakery", 10), C("B", "amenity=cafe", 95) }).Choice?.Name);
    }

    [Fact]
    public void A_home_next_to_a_shop_is_not_named_after_it()
    {
        var shop = new[] { C("Kiosk", "shop=kiosk", 15) };
        Assert.Equal("Kiosk", PlaceIdentity.Decide(shop).Choice?.Name);
        var home = PlaceIdentity.Decide(shop, longStay: true);
        Assert.Null(home.Choice);
        Assert.Equal("Kiosk", Assert.Single(home.Options).Name);   // offered, not applied
    }

    [Fact]
    public void An_empty_answer_proves_no_single_identity()
    {
        Assert.False(PlaceIdentity.SingleIdentity(Array.Empty<LocationCandidate>(), "osm:node/1"));
    }

    [Fact]
    public void Nothing_nameable_around_is_nothing()
    {
        var d = PlaceIdentity.Decide(new[] { C("Stop", "highway=bus_stop", 5), C("Far", "shop=bakery", 150) });
        Assert.Null(d.Choice);
        Assert.Empty(d.Options);
        Assert.True(PlaceIdentity.SingleIdentity(new[] { C("Dor Alon", "amenity=fuel", 4), C("Stop", "highway=bus_stop", 20) },
            C("Dor Alon", "amenity=fuel", 4).Ref));
        Assert.False(PlaceIdentity.SingleIdentity(new[] { C("Dor Alon", "amenity=fuel", 4), C("Alonit", "shop=convenience", 18) },
            C("Dor Alon", "amenity=fuel", 4).Ref));
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise), OpenStreetMap stubbed</summary>
public class PlaceIdentityDbTests
{
    private const double Lat = 31.8106;
    private const double Lon = 34.8603;

    /// <summary>Photon answers: only the fuel station, or the station and a shop next to it</summary>
    private sealed class FakePhoton : HttpMessageHandler
    {
        public bool WithShop { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var shop = WithShop
                ? """,{"type":"Feature","properties":{"osm_type":"W","osm_id":1192121299,"osm_key":"shop","osm_value":"convenience","name":"Alonit"},"geometry":{"type":"Point","coordinates":[34.8603441,31.8107615]}}"""
                : "";
            var json = request.RequestUri!.Host.Contains("photon")
                ? """{"features":[{"type":"Feature","properties":{"osm_type":"N","osm_id":2671441002,"osm_key":"amenity","osm_value":"fuel","name":"Dor Alon"},"geometry":{"type":"Point","coordinates":[34.860262,31.8105843]}}""" + shop + "]}"
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    [Fact]
    public async Task Identifies_spreads_asks_and_keeps_trip_visits()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        // Other tests' pending places would use up the batches
        await db.Places.Where(p => p.IdentifyState == "pending").ExecuteUpdateAsync(s => s.SetProperty(p => p.IdentifyState, "none"));
        var owner = "identity-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var handler = new FakePhoton();
        var geo = new GeoLookupClient(new HttpClient(handler));
        var t0 = DateTime.UtcNow.Date.AddDays(-5).AddHours(5);

        // Two unnamed places 30 m apart at the station (visited during a ride), one 3 km away (not), one named
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Places =
            {
                new PlaceInput("a", "Place", Lat, Lon), new PlaceInput("b", "Place", Lat + 0.00027, Lon),
                new PlaceInput("far", "Place", Lat + 0.03, Lon), new PlaceInput("cafe", "Cafe Nimrod", 31.9, 34.9)
            },
            Visits =
            {
                new VisitInput("va", t0.AddMinutes(40), t0.AddMinutes(50), Lat, Lon, "a"),
                new VisitInput("vb", t0.AddDays(1), t0.AddDays(1).AddMinutes(5), Lat + 0.00027, Lon, "b"),
                new VisitInput("vfar", t0.AddDays(2), t0.AddDays(2).AddHours(1), Lat + 0.03, Lon, "far"),
                new VisitInput("vcafe", t0.AddDays(3), t0.AddDays(3).AddHours(1), 31.9, 34.9, "cafe")
            }
        });
        var ride = Enumerable.Range(0, 61).Select(i => new PointInput(t0.AddMinutes(i), Lat - 0.01 + i * 0.0003, Lon, null, null)).ToList();
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities = { new ActivityInput("ride", "cycling", "Morning ride", null, null, Points: ride) }
        });
        var places = await db.Places.Where(p => p.OwnerId == owner).ToDictionaryAsync(p => p.ExternalId!, p => p);
        var visits = await db.Visits.Where(v => v.OwnerId == owner).ToDictionaryAsync(v => v.ExternalId!, v => v);
        Assert.Equal("user", places["cafe"].IdentifyState);           // a real name needs nothing

        // Visits of rides: only the one during the ride
        var rides = await svc.SearchAsync(owner, new AtlasQuery { Types = new List<string> { "ride" } });
        Assert.Equal(new[] { visits["va"].Id }, rides.Visits.Select(v => v.Id));
        Assert.Equal(4, (await svc.SearchAsync(owner, new AtlasQuery())).Visits.Count);

        // Ambiguous where the shop is next to the station
        handler.WithShop = true;
        GeoLookupClient.ClearCaches();
        while (await svc.IdentifyPendingPlacesAsync(geo, 10) > 0)
        {
        }

        var a = await svc.GetPlaceAsync(owner, places["a"].Id);
        Assert.Equal("ambiguous", a.IdentifyState);
        Assert.Equal(new[] { "Dor Alon", "Alonit" }, a.Candidates!.Select(c => c.Name));
        // 3 km away the station is out of reach: nothing to name it after
        Assert.Equal("none", (await svc.GetPlaceAsync(owner, places["far"].Id)).IdentifyState);

        // The user picks the station for "a": the area has two identities, so "b" is suggested, not applied
        var picked = a.Candidates![0];
        var result = await svc.AssociateVisitPlaceAsync(owner, visits["va"].Id, new PlaceAssociation(picked.Name, null, picked.Label, picked.Ref), geo: geo);
        Assert.Equal("user", result.Place.IdentifyState);
        Assert.Empty(result.Applied!);
        Assert.Equal(places["b"].Id, Assert.Single(result.Suggested!).PlaceId);
        Assert.InRange(result.Suggested![0].DistanceM, 25, 35);

        var applied = await svc.ApplyIdentityAsync(owner, places["a"].Id, new[] { places["b"].Id });
        Assert.Equal("Dor Alon", (await svc.GetPlaceAsync(owner, places["b"].Id)).Name);
        Assert.Single(applied);

        // Where only the station is around, a neighbour gets the identity at once
        await db.Places.Where(p => p.Id == places["b"].Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IdentifyState, "pending").SetProperty(p => p.Name, "Place"));
        db.ChangeTracker.Clear();                                       // each request has its own context
        handler.WithShop = false;
        GeoLookupClient.ClearCaches();
        var again = await svc.AssociateVisitPlaceAsync(owner, visits["va"].Id, new PlaceAssociation(picked.Name, null, picked.Label, picked.Ref),
            geo: new GeoLookupClient(new HttpClient(handler)));
        var spread = Assert.Single(again.Applied!);
        Assert.Equal("Dor Alon", spread.Name);
        var b = await svc.GetPlaceAsync(owner, places["b"].Id);
        Assert.Equal(("auto", "nearby"), (b.IdentifyState, b.IdentifiedFrom));
        Assert.Equal("user", (await svc.ConfirmPlaceAsync(owner, places["b"].Id)).IdentifyState);

        // The density map: the ride's track and the visits
        var density = await svc.DensityAsync(owner, new AtlasQuery());
        Assert.Equal("ride", Assert.Single(density.Tracks).Type);
        Assert.Equal(4, density.Visits.Count);
        Assert.False(density.Truncated);
        Assert.Single((await svc.DensityAsync(owner, new AtlasQuery { Types = new List<string> { "ride" } })).Visits);
    }
}
