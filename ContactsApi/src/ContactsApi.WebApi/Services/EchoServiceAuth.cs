/**
 * @fileoverview The EchoService authentication scheme (AD-3 rule 3, AD-33 rule 2):
 * a service token identifies the calling service, never an Owner, and is accepted
 * only on that service's own internal paths
 * @module ContactsApi.WebApi.Services
 */
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ContactsApi.WebApi.Models.Responses;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Names of the EchoService scheme</summary>
public static class EchoServiceDefaults
{
    /// <summary>The scheme</summary>
    public const string Scheme = "EchoService";

    /// <summary>The claim naming the calling service</summary>
    public const string ServiceClaim = "echo:service";

    /// <summary>The route-quality service</summary>
    public const string RoutesService = "routes";

    /// <summary>The policy of /internal/routes/*</summary>
    public const string RoutesPolicy = "EchoService.Routes";

    /// <summary>Where the route-quality service's token is accepted, and nowhere else</summary>
    public const string RoutesPrefix = "/internal/routes";
}

/// <summary>The service tokens (from Vault: Routes__ServiceToken)</summary>
public class EchoServiceOptions : AuthenticationSchemeOptions
{
    /// <summary>The route-quality service's token; empty switches its internal API off</summary>
    public string? RoutesToken { get; set; }
}

/// <summary>
/// Checks a Bearer service token. A token is valid only under its own path prefix:
/// the routes token anywhere else fails, so it can never reach another endpoint.
/// </summary>
public class EchoServiceHandler : AuthenticationHandler<EchoServiceOptions>
{
    /// <summary>Creates the handler</summary>
    public EchoServiceHandler(IOptionsMonitor<EchoServiceOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header[7..].Trim();
        var service = Identify(token);
        if (service == null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown service token."));
        }

        if (!Request.Path.StartsWithSegments(service.Value.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("The service token is not valid on this path."));
        }

        var identity = new ClaimsIdentity(new[] { new Claim(EchoServiceDefaults.ServiceClaim, service.Value.Name) }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(ApiResponse<object>.ErrorResponse("UNAUTHORIZED", "A service token for this path is required."));
    }

    /// <inheritdoc />
    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(ApiResponse<object>.ErrorResponse("FORBIDDEN", "This service may not call this path."));
    }

    private (string Name, string Prefix)? Identify(string token)
    {
        var routes = Options.RoutesToken;
        if (!string.IsNullOrEmpty(routes) && FixedEquals(token, routes))
        {
            return (EchoServiceDefaults.RoutesService, EchoServiceDefaults.RoutesPrefix);
        }

        return null;
    }

    /// <summary>Compares in constant time (no timing hint about the token)</summary>
    internal static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
