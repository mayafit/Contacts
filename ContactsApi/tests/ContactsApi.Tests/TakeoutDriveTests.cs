using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>Reviews from a scheduled Takeout on Drive (AD-40): the zip, the opt-in, the check</summary>
public class TakeoutDriveTests
{
    /// <summary>Shaped like Takeout's Maps (your places)/Reviews.json</summary>
    internal const string Reviews = """
    {"type":"FeatureCollection","features":[
      {"type":"Feature","geometry":{"type":"Point","coordinates":[34.7818,32.0853]},
       "properties":{"date":"2025-09-03T07:12:00Z","five_star_rating_published":5,"review_text_published":"Great hummus",
         "google_maps_url":"http://maps.google.com/?cid=111","location":{"name":"Hummus Abu Hassan","address":"Tel Aviv","country_code":"IL"},
         "questions":[{"question":"Price per person","selected_option":"₪40–60"},{"question":"Meal type","selected_options":["Lunch","Brunch"]},
                      {"question":"Food","rating":5},{"question":"Empty"}]}},
      {"type":"Feature","geometry":{"type":"Point","coordinates":[34.8,32.1]},
       "properties":{"five_star_rating_draft":3,"review_text_draft":"Not sure","google_maps_url":"javascript:alert(1)",
         "location":[{"name":"קפה לנדוור"}]}},
      {"type":"Feature","geometry":{"type":"Point","coordinates":[0,0]},"properties":{"five_star_rating_published":4,"location":{"name":"Nowhere"}}},
      {"type":"Feature","geometry":{"type":"Point","coordinates":[34.9,32.2]},"properties":{"location":{"name":"Nothing written"}}}
    ]}
    """;

    internal static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                w.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static (List<ReviewInput>? Reviews, string? Note) Read(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return GoogleReviewsParser.FromZip(zip);
    }

    [Fact]
    public void The_reviews_file_is_read_out_of_a_takeout_zip_with_the_browser_s_rules()
    {
        var (reviews, note) = Read(Zip(("Takeout/archive_browser.html", "<html/>"), ("Takeout/Maps (your places)/Saved Places.json",
            """{"type":"FeatureCollection","features":[{"properties":{"location":{"name":"Home"}}}]}"""),
            ("Takeout/Maps (your places)/Reviews.json", Reviews)));

        Assert.Null(note);
        Assert.Equal(2, reviews!.Count);
        var first = reviews[0];
        Assert.Equal(("Hummus Abu Hassan", 32.0853, 34.7818, (int?)5, "Great hummus", true, "http://maps.google.com/?cid=111"),
            (first.Name, first.Lat, first.Lon, first.Rating, first.Text, first.Published, first.MapsUrl));
        Assert.Equal(new DateTime(2025, 9, 3, 7, 12, 0, DateTimeKind.Utc), first.ReviewedAt);
        Assert.Equal(new[] { "₪40–60", "Lunch, Brunch", "5/5" }, first.Answers!.Select(a => a.Answer));
        // The draft: no date, and its unsafe link dropped
        Assert.Equal(("קפה לנדוור", (int?)3, false, (string?)null, (DateTime?)null),
            (reviews[1].Name, reviews[1].Rating, reviews[1].Published, reviews[1].MapsUrl, reviews[1].ReviewedAt));
    }

    [Fact]
    public void A_localized_export_is_read_and_a_zip_without_reviews_says_so()
    {
        var (reviews, _) = Read(Zip(("Takeout/מפות (המקומות שלך)/ביקורות.json", Reviews)));
        Assert.Equal(2, reviews!.Count);

        var (none, note) = Read(Zip(("Takeout/Fit/All Data/x.json", "{}"), ("Takeout/Maps/Saved/list.json", Reviews)));
        Assert.Null(none);
        Assert.Contains("no Maps (your places) reviews", note);
        Assert.Contains("not readable JSON", Read(Zip(("Takeout/Maps (your places)/Reviews.json", "{oops"))).Note);

        Assert.True(GoogleReviewsParser.Candidate("Takeout\\Maps (your places)\\Reviews.json"));
        Assert.False(GoogleReviewsParser.Candidate("Takeout/Maps/Saved/list.json"));
    }

    [Fact]
    public void The_takeout_opt_in_asks_for_drive_read_only()
    {
        var scopes = ScopePreferences.ScopesFor(new[] { "openid" }, new OptIns { Takeout = true });
        Assert.Equal(new[] { "openid", TakeoutScopes.Drive }, scopes);
        Assert.DoesNotContain(TakeoutScopes.Drive, ScopePreferences.ScopesFor(new[] { "openid" }, new OptIns { Activity = true }));
        Assert.True(ScopePreferences.FromScopes(new[] { TakeoutScopes.Drive }).Takeout);
        Assert.True(new OptIns { Activity = true }.With(new OptIns { Takeout = true }).Takeout);
    }
}

/// <summary>The Drive check against a fake Drive and a real PostgreSQL (ATLAS_TEST_DB; skipped otherwise)</summary>
public class TakeoutDriveDbTests
{
    private sealed class Tokens : ITakeoutTokens
    {
        public bool Granted = true;
        public string? Token = "token";
        public bool Refused;
        public Task<bool> GrantedAsync(string owner) => Task.FromResult(Granted);
        public Task<(string? Token, bool Refused)> AccessTokenAsync(string owner) => Task.FromResult((Token, Refused));
    }

    /// <summary>A Drive holding some files; downloads counted; failures on demand</summary>
    private sealed class Drive : HttpMessageHandler
    {
        public readonly List<(string Id, string Name, DateTime Modified, byte[] Bytes, long? Size)> Files = new();
        public readonly List<string> Downloads = new();
        public readonly Dictionary<string, (HttpStatusCode Status, string Body)> DownloadFails = new();
        public (HttpStatusCode Status, string Body)? ListFail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer token", request.Headers.Authorization?.ToString());
            var url = request.RequestUri!.ToString();
            if (url.Contains("alt=media"))
            {
                var id = request.RequestUri!.AbsolutePath.Split('/').Last();
                Downloads.Add(id);
                if (DownloadFails.TryGetValue(id, out var fail))
                {
                    return Task.FromResult(new HttpResponseMessage(fail.Status) { Content = new StringContent(fail.Body) });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files.Single(f => f.Id == id).Bytes) });
            }

            if (ListFail is { } lf)
            {
                return Task.FromResult(new HttpResponseMessage(lf.Status) { Content = new StringContent(lf.Body) });
            }

            // Only the Owner's own files, never shared ones
            var query = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Assert.Contains("'me' in owners", query);
            Assert.Contains("name contains 'takeout-'", query);
            var body = JsonSerializer.Serialize(new
            {
                files = Files.OrderByDescending(f => f.Modified).Select(f => new
                {
                    id = f.Id, name = f.Name, size = (f.Size ?? f.Bytes.Length).ToString(), modifiedTime = f.Modified.ToString("o")
                })
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, false);
    }

    private const string ScopeDenied = """{"error":{"code":403,"details":[{"reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}""";
    private const string FileDenied = """{"error":{"code":403,"errors":[{"reason":"cannotDownloadFile"}]}}""";

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

    private static byte[] WithReviews() => TakeoutDriveTests.Zip(("Takeout/Maps (your places)/Reviews.json", TakeoutDriveTests.Reviews));

    private static byte[] Without() => TakeoutDriveTests.Zip(("Takeout/Fit/x.json", "{}"));

    [Fact]
    public async Task Exports_are_picked_up_once_and_failures_are_told_apart()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "takeout-" + Guid.NewGuid();
        var tokens = new Tokens { Granted = false };
        var drive = new Drive();
        var queue = new TakeoutDriveQueue();
        var svc = new TakeoutDriveService(db, new AtlasService(db, NullLogger<AtlasService>.Instance), tokens, new Factory(drive), queue,
            Options.Create(new TakeoutOptions { MaxZipMb = 1 }), NullLogger<TakeoutDriveService>.Instance);
        async Task<TakeoutDriveStatus> Status() => await svc.StatusAsync(owner, default);

        // Not allowed yet: no connection
        Assert.False((await Status()).Granted);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.ConnectAsync(owner, "me@example.com", default));
        tokens.Granted = true;
        var connected = await svc.ConnectAsync(owner, "me@example.com", default);
        Assert.Equal(("idle", "me@example.com"), (connected.Connection!.Status, connected.Connection.Account));
        Assert.Equal(owner, await queue.NextAsync(TimeSpan.Zero, default));

        // The first check takes only the newest export (both its parts), not an older one
        var t0 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        drive.Files.Add(("old", "takeout-20260401T000000Z-001.zip", t0.AddDays(-60), WithReviews(), null));
        drive.Files.Add(("a1", "takeout-20260601T000000Z-001.zip", t0, WithReviews(), null));
        drive.Files.Add(("a2", "takeout-20260601T000000Z-002.zip", t0.AddMinutes(1), Without(), null));
        // Not a zip by name: never listed as an export
        drive.Files.Add(("tgz", "takeout-20260601T000000Z-001.tgz", t0, new byte[3], null));
        await svc.CheckAsync(owner, default);

        Assert.Equal(new[] { "a1", "a2" }, drive.Downloads);
        Assert.Equal(2, await db.Reviews.CountAsync(r => r.OwnerId == owner));
        Assert.Equal("Drive: takeout-20260601T000000Z-001.zip",
            (await db.Imports.SingleAsync(i => i.OwnerId == owner && i.Source == "google-maps")).FileName);
        var status = await Status();
        Assert.Equal(("idle", 2), (status.Connection!.Status, status.Connection.ItemsImported));
        Assert.Equal("takeout-20260601T000000Z-002.zip: no Maps (your places) reviews in it", status.Connection.LastError);
        Assert.Equal("a2", status.LastExport!.Id);
        Assert.True(status.Stale);

        // Nothing new: nothing downloaded, and the old note goes
        await svc.CheckAsync(owner, default);
        Assert.Equal(2, drive.Downloads.Count);
        Assert.Null((await Status()).Connection!.LastError);

        // Next: too large (skipped, seen), a passing failure (left for later), a file not downloadable
        drive.Files.Add(("big", "takeout-20260801T000000Z-001.zip", t0.AddDays(60), new byte[10], 5L * 1024 * 1024));
        drive.Files.Add(("busy", "takeout-20260901T000000Z-001.zip", t0.AddDays(90), WithReviews(), null));
        drive.Files.Add(("denied", "takeout-20261001T000000Z-001.zip", t0.AddDays(120), WithReviews(), null));
        drive.DownloadFails["busy"] = (HttpStatusCode.ServiceUnavailable, "{}");
        drive.DownloadFails["denied"] = (HttpStatusCode.Forbidden, FileDenied);
        await svc.CheckAsync(owner, default);
        status = await Status();
        Assert.Equal("error", status.Connection!.Status);
        Assert.Contains("takeout-20260901T000000Z-001.zip: Google Drive answered 503", status.Connection.LastError);
        Assert.DoesNotContain("big", drive.Downloads);
        Assert.DoesNotContain("denied", drive.Downloads);

        // The passing failure is tried again; the same reviews again add none; the denied file is noted, not lost access
        drive.DownloadFails.Remove("busy");
        await svc.CheckAsync(owner, default);
        status = await Status();
        Assert.Equal(("idle", 2), (status.Connection!.Status, status.Connection.ItemsImported));
        Assert.Contains("busy", drive.Downloads);
        Assert.Contains("takeout-20261001T000000Z-001.zip: Google Drive answered 403", status.Connection.LastError);
        Assert.False(status.Stale);

        // Google not reachable to renew the sign-in: an error, not lost access
        drive.Files.Add(("n1", "takeout-20261101T000000Z-001.zip", DateTime.UtcNow, WithReviews(), null));
        tokens.Token = null;
        await svc.CheckAsync(owner, default);
        Assert.Equal("error", (await Status()).Connection!.Status);

        // Drive no longer allowed: expired, and not checked again until connected
        tokens.Token = "token";
        drive.ListFail = (HttpStatusCode.Forbidden, ScopeDenied);
        await svc.CheckAsync(owner, default);
        status = await Status();
        Assert.Equal(("expired", TakeoutDriveService.SignInAgain), (status.Connection!.Status, status.Connection.LastError));
        drive.ListFail = null;
        await svc.CheckAsync(owner, default);
        Assert.DoesNotContain("n1", drive.Downloads);
        await svc.ConnectAsync(owner, "me@example.com", default);
        await svc.CheckAsync(owner, default);
        Assert.Contains("n1", drive.Downloads);

        // A refused sign-in expires it too
        tokens.Token = null;
        tokens.Refused = true;
        await svc.CheckAsync(owner, default);
        Assert.Equal("expired", (await Status()).Connection!.Status);

        // A check already running (another pod) is not run twice; a claim left by a crash is taken after an hour
        await svc.ConnectAsync(owner, "me@example.com", default);
        tokens.Token = "token";
        tokens.Refused = false;
        await db.Connections.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "syncing"));
        var downloads = drive.Downloads.Count;
        drive.Files.Add(("n2", "takeout-20261102T000000Z-001.zip", DateTime.UtcNow, Without(), null));
        await svc.CheckAsync(owner, default);
        Assert.Equal(downloads, drive.Downloads.Count);
        await db.Connections.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTime.UtcNow.AddHours(-2)));
        await svc.CheckAsync(owner, default);
        Assert.Contains("n2", drive.Downloads);

        // Another Owner has nothing to check
        await Assert.ThrowsAsync<AtlasNotFoundException>(() => svc.SyncAsync("takeout-other-" + Guid.NewGuid(), default));
        Assert.True(await svc.DisconnectAsync(owner, default));
        Assert.Null((await Status()).Connection);
    }

    [Fact]
    public void An_export_s_parts_belong_to_one_set()
    {
        Assert.Equal("takeout-20260601T000000Z", TakeoutDriveService.ExportSet("takeout-20260601T000000Z-001.zip"));
        Assert.Equal("takeout-20260601T000000Z", TakeoutDriveService.ExportSet("takeout-20260601T000000Z-012.zip"));
        Assert.Equal("takeout", TakeoutDriveService.ExportSet("takeout.zip"));
    }
}
