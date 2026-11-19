using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FreightRag.Api.Security;

/// <summary>
/// ASP.NET Core authentication for the <c>X-Api-Key</c> header. A valid key produces a user whose only
/// claim is the tenant; endpoints read the tenant from there, never from the request.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Exactly one non-empty value; anything else is treated as an invalid key.
        if (values.Count != 1 || !keys.TryResolveTenant(values[0] ?? "", out var tenantSlug))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key.")); // the key itself is never logged

        var identity = new ClaimsIdentity([new Claim(TenantClaims.TenantSlug, tenantSlug)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        return Task.CompletedTask;
    }
}

/// <summary>Claim types issued by <see cref="ApiKeyAuthenticationHandler"/>.</summary>
public static class TenantClaims
{
    public const string TenantSlug = "freightrag:tenant";

    /// <summary>The authenticated caller's tenant slug (only valid on endpoints that require authorization).</summary>
    public static string GetTenantSlug(this ClaimsPrincipal user) =>
        user.FindFirstValue(TenantSlug) ?? throw new InvalidOperationException("No tenant claim: endpoint must require authorization.");
}
