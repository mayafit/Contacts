using System.Reflection;
using System.Text.Encodings.Web;
using ContactsApi.Infrastructure.Routes;
using ContactsApi.WebApi.Controllers;
using ContactsApi.WebApi.Services;
using EchoRoutes.Worker.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>The EchoService scheme: the routes token works on /internal/routes/* and nowhere else (AD-3, AD-33 rule 2)</summary>
public class InternalRoutesAuthTests
{
    private const string RoutesToken = "routes-token-for-tests";
    private const string BackendToken = "backend-token-for-tests";

    private static async Task<AuthenticateResult> AuthenticateAsync(string path, string? header, string? configured = RoutesToken)
    {
        var options = new EchoServiceOptions { RoutesToken = configured };
        var monitor = new StaticMonitor(options);
        var handler = new EchoServiceHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (header != null)
        {
            context.Request.Headers.Authorization = header;
        }

        await handler.InitializeAsync(new AuthenticationScheme(EchoServiceDefaults.Scheme, null, typeof(EchoServiceHandler)), context);
        return await handler.AuthenticateAsync();
    }

    [Theory]
    [InlineData("/internal/routes/owners")]
    [InlineData("/internal/routes/changes")]
    [InlineData("/internal/routes/tracks")]
    [InlineData("/internal/routes/exists")]
    [InlineData("/internal/routes/range")]
    public async Task The_routes_token_is_accepted_on_internal_routes(string path)
    {
        var result = await AuthenticateAsync(path, "Bearer " + RoutesToken);

        Assert.True(result.Succeeded);
        Assert.Equal(EchoServiceDefaults.RoutesService, result.Principal!.FindFirst(EchoServiceDefaults.ServiceClaim)!.Value);
    }

    [Theory]
    [InlineData("/internal/v1/assess")]
    [InlineData("/internal/faces/jobs")]
    [InlineData("/internal/routesx/owners")]
    [InlineData("/api/atlas/search")]
    [InlineData("/api/mcp")]
    [InlineData("/health")]
    public async Task The_routes_token_is_refused_on_every_other_path(string path)
    {
        var result = await AuthenticateAsync(path, "Bearer " + RoutesToken);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Wrong_missing_or_unconfigured_tokens_are_refused()
    {
        Assert.False((await AuthenticateAsync("/internal/routes/owners", "Bearer " + BackendToken)).Succeeded);
        Assert.False((await AuthenticateAsync("/internal/routes/owners", "Bearer " + RoutesToken, configured: null)).Succeeded);
        Assert.False((await AuthenticateAsync("/internal/routes/owners", "Bearer ")).Succeeded);
        Assert.True((await AuthenticateAsync("/internal/routes/owners", null)).None);
    }

    [Fact]
    public void Every_internal_endpoint_requires_the_routes_policy_and_none_is_anonymous()
    {
        // The AD-3 endpoint test, for /internal/**: every controller routed there has the policy
        var controllers = typeof(InternalRoutesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetCustomAttribute<RouteAttribute>()?.Template.StartsWith("internal", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        var controller = Assert.Single(controllers);
        Assert.Equal(EchoServiceDefaults.RoutesPolicy, controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        // owners, changes, tracks, exists (GET and POST), range
        Assert.Equal(6, actions.Length);
        Assert.All(actions, a => Assert.Null(a.GetCustomAttribute<AllowAnonymousAttribute>()));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        // And no other controller is routed under /internal or carries the service policy
        var others = typeof(InternalRoutesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t != controller)
            .SelectMany(t => t.GetCustomAttributes<AuthorizeAttribute>().Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())));
        Assert.DoesNotContain(others, a => a.Policy == EchoServiceDefaults.RoutesPolicy);
    }

    [Fact]
    public void The_service_accepts_only_the_backend_token()
    {
        Assert.True(InternalApi.Accepts("Bearer " + BackendToken, BackendToken));
        Assert.False(InternalApi.Accepts("Bearer " + RoutesToken, BackendToken));
        Assert.False(InternalApi.Accepts(BackendToken, BackendToken));
        Assert.False(InternalApi.Accepts(null, BackendToken));
        Assert.False(InternalApi.Accepts("Bearer ", ""));
        Assert.False(InternalApi.Accepts("Bearer " + BackendToken, null));
    }

    private sealed class StaticMonitor : IOptionsMonitor<EchoServiceOptions>
    {
        private readonly EchoServiceOptions _options;

        public StaticMonitor(EchoServiceOptions options) => _options = options;

        public EchoServiceOptions CurrentValue => _options;

        public EchoServiceOptions Get(string? name) => _options;

        public IDisposable? OnChange(Action<EchoServiceOptions, string?> listener) => null;
    }
}

/// <summary>The 429 rule: batch calls wait while map or timeline reads are in flight (AD-33 rule 5)</summary>
public class InteractiveLoadTests
{
    [Theory]
    [InlineData("POST", "/api/atlas/search", true)]
    [InlineData("POST", "/api/atlas/histogram", true)]
    [InlineData("GET", "/api/atlas/histogram", true)]
    [InlineData("POST", "/api/atlas/density", true)]
    [InlineData("GET", "/api/atlas/activities/0d4c7a52-4b51-4b8e-9d0e-3f1c2b8a9e10", true)]
    [InlineData("GET", "/api/atlas/activities/0d4c7a52-4b51-4b8e-9d0e-3f1c2b8a9e10/photos", false)]
    [InlineData("PATCH", "/api/atlas/activities/0d4c7a52-4b51-4b8e-9d0e-3f1c2b8a9e10", false)]
    [InlineData("GET", "/api/routes/status", false)]
    [InlineData("GET", "/api/routes/jobs/0d4c7a52-4b51-4b8e-9d0e-3f1c2b8a9e10", false)]
    [InlineData("GET", "/internal/routes/changes", false)]
    [InlineData("POST", "/api/atlas/ingest", false)]
    public void Only_map_and_timeline_reads_count_as_load(string method, string path, bool counts)
    {
        Assert.Equal(counts, InteractiveLoadMiddleware.IsInteractiveRead(method, path));
    }

    [Fact]
    public async Task The_middleware_counts_a_read_only_while_it_runs()
    {
        var load = new InteractiveLoad();
        var seen = -1;
        var middleware = new InteractiveLoadMiddleware(_ =>
        {
            seen = load.InFlight;
            throw new InvalidOperationException("the read failed");
        });
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/atlas/search";

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, load));
        Assert.Equal(1, seen);
        Assert.False(load.Busy);

        // Status polling never counts
        context.Request.Method = "GET";
        context.Request.Path = "/api/routes/status";
        await new InteractiveLoadMiddleware(_ =>
        {
            seen = load.InFlight;
            return Task.CompletedTask;
        }).InvokeAsync(context, load);
        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task Batch_calls_get_429_with_retry_after_and_interactive_calls_pass()
    {
        var load = new InteractiveLoad();
        using (load.Begin())
        {
            var (batch, batchRan) = await RunFilterAsync(load, null);
            Assert.False(batchRan);
            var result = Assert.IsType<ObjectResult>(batch.Result);
            Assert.Equal(429, result.StatusCode);
            Assert.Equal("2", batch.HttpContext.Response.Headers.RetryAfter.ToString());

            var (interactive, interactiveRan) = await RunFilterAsync(load, EchoPriority.Interactive);
            Assert.True(interactiveRan);
            Assert.Null(interactive.Result);
        }

        // Nothing in flight: batch calls pass
        var (_, ran) = await RunFilterAsync(load, null);
        Assert.True(ran);
    }

    private static async Task<(ActionExecutingContext Context, bool Ran)> RunFilterAsync(InteractiveLoad load, string? priority)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(load).BuildServiceProvider()
        };
        if (priority != null)
        {
            http.Request.Headers[EchoPriority.Header] = priority;
        }

        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        var ran = false;
        await new YieldToInteractiveAttribute().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });
        return (context, ran);
    }
}
