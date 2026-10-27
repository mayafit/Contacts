using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>Parsing OpenStreetMap answers (trimmed from real ones for 31.8106, 34.8603 — Yesodot junction)</summary>
public class GeoLookupTests
{
    private const double Lat = 31.8106;
    private const double Lon = 34.8603;

    private const string NominatimJson = """
        {"place_id":47376889,"osm_type":"node","osm_id":2671441002,"lat":"31.8105843","lon":"34.8602620",
         "category":"amenity","type":"fuel","name":"Dor Alon",
         "display_name":"Dor Alon, 3, Nahal Sorek Regional Council, Rehovot Subdistrict, Center District, 7680400, Israel",
         "address":{"amenity":"Dor Alon","road":"3","city":"Nahal Sorek Regional Council","state":"Center District",
                    "postcode":"7680400","country":"Israel","country_code":"il"},
         "extratags":{"brand:en":"Dor Alon","brand:he":"דור אלון","wheelchair":"yes","opening_hours":"24/7"},
         "namedetails":{"name":"דור אלון","name:en":"Dor Alon","name:he":"דור אלון"}}
        """;

    private const string OverpassJson = """
        {"version":0.6,"elements":[
          {"type":"node","id":1803053522,"lat":31.8097802,"lon":34.8595446,
           "tags":{"highway":"bus_stop","public_transport":"platform","name":"צומת יסודות למזרח","name:en":"Yesodot Junction East"}},
          {"type":"node","id":2671441002,"lat":31.8105843,"lon":34.8602620,
           "tags":{"addr:city":"יסודות","amenity":"fuel","brand":"דור אלון","brand:en":"Dor Alon","name":"דור אלון","name:en":"Dor Alon",
                   "opening_hours":"24/7","phone":"+972 8-123-4567"}},
          {"type":"way","id":555,"center":{"lat":31.8112,"lon":34.8611},
           "tags":{"shop":"convenience","name":"Yellow","website":"https://www.yellow.co.il","addr:street":"Derech HaYam","addr:housenumber":"4"}},
          {"type":"node","id":7,"lat":31.81,"lon":34.86,"tags":{"amenity":"bench"}}
        ]}
        """;

    [Fact]
    public void Reads_the_address_and_the_named_object_at_the_point()
    {
        var (address, at) = GeoLookupClient.ParseNominatim(NominatimJson, Lat, Lon);
        Assert.NotNull(address);
        Assert.Equal("3, Nahal Sorek Regional Council, Israel", address!.Display);
        Assert.Equal("il", address.CountryCode);
        Assert.Equal("7680400", address.Postcode);

        Assert.NotNull(at);
        Assert.Equal("osm:node/2671441002", at!.Ref);
        Assert.Equal("Dor Alon", at.Name);
        Assert.Equal("דור אלון", at.LocalName);
        Assert.Equal("amenity=fuel", at.Kind);
        Assert.Equal("Fuel", at.Label);
        Assert.Equal("24/7", at.OpeningHours);
        Assert.Equal("Dor Alon", at.Details["brand"]);
        Assert.InRange(at.DistanceM, 0, 10);
    }

    [Fact]
    public void Reads_named_places_around_nearest_first_with_their_facts()
    {
        var found = GeoLookupClient.ParseOverpass(OverpassJson, Lat, Lon);
        Assert.Equal(3, found.Count);                      // the nameless bench is left out
        Assert.Equal("Dor Alon", found[0].Name);
        Assert.Equal("+972 8-123-4567", found[0].Phone);
        Assert.Equal("Bus stop", Assert.Single(found, c => c.Ref == "osm:node/1803053522").Label);
        var shop = Assert.Single(found, c => c.Ref == "osm:way/555");
        Assert.Equal("Convenience", shop.Label);
        Assert.Equal("4 Derech HaYam", shop.Address);
        Assert.Equal("https://www.yellow.co.il", shop.Website);
        Assert.Null(shop.LocalName);
        Assert.True(found.Zip(found.Skip(1)).All(p => p.First.DistanceM <= p.Second.DistanceM));
    }

    [Fact]
    public void Survives_errors_and_empty_answers()
    {
        Assert.Equal((null, null), GeoLookupClient.ParseNominatim("""{"error":"Unable to geocode"}""", Lat, Lon));
        Assert.Empty(GeoLookupClient.ParseOverpass("""{"elements":[]}""", Lat, Lon));
        Assert.Throws<GeoLookupException>(() => GeoLookupClient.ParseOverpass("<html>busy</html>", Lat, Lon));
        Assert.Equal("Fast food", GeoLookupClient.Humanize("fast_food"));
    }

    /// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
    [Fact]
    public async Task Associating_names_the_place_for_every_visit_or_creates_one()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "place-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var t0 = DateTime.UtcNow.Date.AddDays(-3).AddHours(5);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Places = { new PlaceInput("p1", "Place", Lat, Lon) },
            Visits =
            {
                new VisitInput("v1", t0, t0.AddMinutes(12), Lat, Lon, "p1"),
                new VisitInput("v2", t0.AddDays(1), t0.AddDays(1).AddMinutes(30), Lat, Lon, "p1"),
                new VisitInput("v3", t0.AddDays(2), t0.AddDays(2).AddHours(1), 31.9, 34.9, null)
            }
        });
        var visits = await db.Visits.Where(v => v.OwnerId == owner).ToDictionaryAsync(v => v.ExternalId!, v => v);

        var result = await svc.AssociateVisitPlaceAsync(owner, visits["v1"].Id, new PlaceAssociation(
            "Dor Alon", "Yesodot junction", "Fuel", "osm:node/2671441002", null, "+972 8-123-4567", "24/7",
            new Dictionary<string, string> { ["brand"] = "Dor Alon" }));
        Assert.Equal("Dor Alon", result.Visit.PlaceName);
        Assert.Equal(visits["v1"].PlaceId, result.Place.Id);
        Assert.Equal(2, result.Place.Visits);
        Assert.Equal("osm:node/2671441002", result.Place.LinkedRef);
        Assert.Equal("Dor Alon", result.Place.Details["brand"]);
        Assert.Equal(Lat, result.Place.Lat);                          // where the user was

        // The other visit to the place shows the name too
        var found = await svc.SearchAsync(owner, new AtlasQuery { Activities = false });
        Assert.Equal(2, found.Visits.Count(v => v.PlaceName == "Dor Alon"));

        // A visit without a place gets a new one where it was
        var lone = await svc.AssociateVisitPlaceAsync(owner, visits["v3"].Id, new PlaceAssociation("Grandma's"));
        Assert.Equal(31.9, lone.Place.Lat);
        Assert.Equal(1, lone.Place.Visits);
        Assert.Null(lone.Place.LinkedRef);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.AssociateVisitPlaceAsync(owner, visits["v3"].Id, new PlaceAssociation(" ")));
        await Assert.ThrowsAsync<AtlasNotFoundException>(() => svc.AssociateVisitPlaceAsync("someone-else", visits["v3"].Id, new PlaceAssociation("x")));

        // Clearing a field through PATCH
        await svc.UpdatePlaceAsync(owner, result.Place.Id, new PlacePatch(Phone: ""));
        Assert.Null((await svc.GetPlaceAsync(owner, result.Place.Id)).Phone);
    }
}
