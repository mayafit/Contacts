using System.Text.Json;
using ContactsApi.WebApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>The backend's client of the route service: the Owner and token it sends, and ROUTES_UNAVAILABLE</summary>
public class RoutesServiceClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? Body)> Seen = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"success":true,"data":{"jobId":"00000000-0000-0000-0000-000000000001"}}""")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));
            return Answer(request);
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public Factory(HttpMessageHandler h) => _h = h;

        public HttpClient CreateClient(string name)
        {
            var c = new HttpClient(_h, false) { BaseAddress = new Uri("http://contacts-routes:8080/") };
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "backend-token");
            return c;
        }
    }

    private static RoutesServiceClient Client(Handler handler, bool configured = true) =>
        new(new Factory(handler), new ConfigurationBuilder().AddInMemoryCollection(configured
                ? new Dictionary<string, string?> { ["Routes:ServiceUrl"] = "http://contacts-routes:8080", ["Routes:BackendToken"] = "backend-token" }
                : new Dictionary<string, string?>()).Build(),
            NullLogger<RoutesServiceClient>.Instance);

    [Fact]
    public async Task Queues_with_the_session_owner_and_the_backend_token()
    {
        var handler = new Handler();
        var id = Guid.NewGuid();

        var answer = await Client(handler).QueueAsync("improve", "owner-a", new { activityIds = new[] { id } }, true, false, default);

        Assert.Equal(202, answer.Status);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("/internal/v1/improve", request.RequestUri!.AbsolutePath);
        Assert.Equal("backend-token", request.Headers.Authorization!.Parameter);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("owner-a", json.GetProperty("ownerId").GetString());
        Assert.True(json.GetProperty("interactive").GetBoolean());
        Assert.Equal(id, json.GetProperty("scope").GetProperty("activityIds")[0].GetGuid());
    }

    [Fact]
    public async Task An_unreachable_or_disabled_service_is_routes_unavailable_and_a_wake_never_throws()
    {
        var handler = new Handler { Answer = _ => throw new HttpRequestException("no route to host") };
        await Assert.ThrowsAsync<RoutesUnavailableException>(() => Client(handler).StatusAsync("o", default));
        await Assert.ThrowsAsync<RoutesUnavailableException>(() => Client(new Handler(), configured: false).JobAsync("o", Guid.NewGuid(), default));

        handler.Answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<RoutesUnavailableException>(() => Client(handler).StatusAsync("o", default));

        Client(handler).Wake("o");
        var disabled = new Handler();
        Client(disabled, configured: false).Wake("o");
        await Task.Delay(200);
        Assert.Empty(disabled.Seen);
    }
}
