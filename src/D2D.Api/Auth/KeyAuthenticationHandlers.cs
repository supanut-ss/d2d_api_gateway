using System.Security.Claims;
using System.Text.Encodings.Web;
using D2D.Core.Security;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace D2D.Api.Auth;

/// <summary>Authenticates an app backend by X-App-Id + X-Api-Key.</summary>
public sealed class AppApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAppRegistry registry) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var code = Request.Headers[AppHeaders.AppId].ToString();
        var key = Request.Headers[AppHeaders.ApiKey].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(key)) return AuthenticateResult.NoResult();

        if (!await registry.VerifyApiKeyAsync(code, key, Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid API key");

        var app = await registry.FindByCodeAsync(code, Context.RequestAborted);
        if (app is null || !app.IsActive) return AuthenticateResult.Fail("App is disabled");

        Claim[] claims =
        [
            new(AppClaimTypes.Subject, $"app:{app.Code}"),
            new(AppClaimTypes.Name, app.Name),
            new(AppClaimTypes.AppId, app.Id.ToString()),
            new(AppClaimTypes.AppCode, app.Code),
            new(AppClaimTypes.Role, AppRoles.AppServer),
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name, AppClaimTypes.Name, AppClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>Authenticates the platform administrator by X-Admin-Key.</summary>
public sealed class AdminKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AdminOptions> admin) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = admin.Value.ApiKey;
        var provided = Request.Headers[AppHeaders.AdminKey].ToString();
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Compare hashes so the comparison is constant-time regardless of key length.
        if (!TokenHasher.FixedTimeEquals(TokenHasher.Sha256(configured), TokenHasher.Sha256(provided)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin key"));

        Claim[] claims =
        [
            new(AppClaimTypes.Subject, "admin"),
            new(AppClaimTypes.Name, "Platform admin"),
            new(AppClaimTypes.Role, AppRoles.SuperAdmin),
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name, AppClaimTypes.Name, AppClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
