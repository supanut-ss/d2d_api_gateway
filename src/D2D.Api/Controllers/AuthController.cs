using D2D.Api.Auth;
using D2D.Api.Setup;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Line;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace D2D.Api.Controllers;

/// <summary>Email/password login, token refresh and logout. Requires X-App-Id.</summary>
[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(AuthService auth, TokenService tokens, ITenantContext tenant) : ControllerBase
{
    [HttpPost("register")]
    [AppKeyIfRequired]
    public Task<AuthTokens> Register(RegisterRequest request, CancellationToken ct) =>
        auth.RegisterAsync(request, ClientIp, ct);

    [HttpPost("login")]
    [AppKeyIfRequired]
    public Task<AuthTokens> Login(LoginRequest request, CancellationToken ct) =>
        auth.LoginAsync(request, ClientIp, ct);

    [HttpPost("refresh")]
    [AppKeyIfRequired]
    public Task<AuthTokens> Refresh(RefreshRequest request, CancellationToken ct) =>
        tokens.RefreshAsync(request.RefreshToken, tenant.Required, ClientIp, ct);

    [HttpPost("logout")]
    [AppKeyIfRequired]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await tokens.RevokeAsync(request.RefreshToken, tenant.Required, ct);
        return NoContent();
    }

    /// <summary>Ends all sessions of the signed-in user in this app.</summary>
    [HttpPost("logout-all")]
    [Authorize(Policy = Policies.AppUser)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await tokens.RevokeAllAsync(User.GetUserId(), tenant.Required.Id, ct);
        return NoContent();
    }

    /// <summary>Unlinks an external account, e.g. DELETE /auth/identities/line/{lineProviderId}.</summary>
    [HttpDelete("identities/{provider}/{scope}")]
    [Authorize(Policy = Policies.AppUser)]
    public async Task<IActionResult> Unlink(string provider, string scope, CancellationToken ct)
    {
        await auth.UnlinkAsync(User.GetUserId(), provider, scope, ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = Policies.AppUser)]
    [DisableRateLimiting]
    public Task<MeResponse> Me(CancellationToken ct) => auth.GetMeAsync(User.GetUserId(), ct);

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}

/// <summary>LINE Login for all apps (LIFF/SDK ID token flow and web redirect flow).</summary>
[ApiController]
[Route("api/v1/auth/line")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class LineAuthController(LineLoginService lineLogin) : ControllerBase
{
    /// <summary>LIFF / LINE SDK: exchange a LINE ID token for API tokens. Requires X-App-Id.</summary>
    [HttpPost("token")]
    [AppKeyIfRequired]
    public Task<AuthTokens> LoginWithIdToken(LineIdTokenLoginRequest request, CancellationToken ct) =>
        lineLogin.LoginWithIdTokenAsync(request, ClientIp, ct);

    /// <summary>
    /// Web flow step 1: browser navigates here; redirects to LINE.
    /// channel_id picks one of the app's Login channels (default: the first one).
    /// </summary>
    [HttpGet("authorize")]
    public async Task<IActionResult> Authorize(
        [FromQuery] string app,
        [FromQuery(Name = "redirect_uri")] string redirectUri,
        [FromQuery] string? state,
        [FromQuery(Name = "channel_id")] string? channelId,
        CancellationToken ct) =>
        Redirect(await lineLogin.BuildAuthorizeUrlAsync(app, redirectUri, state, channelId, ct));

    /// <summary>Web flow step 2: LINE redirects here; we redirect to the app with a one-time code.</summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct) =>
        Redirect(await lineLogin.HandleCallbackAsync(code, state, error, errorDescription, ct));

    /// <summary>Web flow step 3: the app exchanges the one-time code for tokens. Requires X-App-Id.</summary>
    [HttpPost("exchange")]
    [AppKeyIfRequired]
    public Task<AuthTokens> Exchange(ExchangeCodeRequest request, CancellationToken ct) =>
        lineLogin.ExchangeCodeAsync(request.Code, ClientIp, ct);

    /// <summary>
    /// For apps without user accounts: the app's backend (X-App-Id + X-Api-Key) sends the LINE ID token it received
    /// from its LIFF page and gets back the verified LINE userId. Nothing is created or stored. Call it from the
    /// backend, never trust a userId sent by the browser.
    /// </summary>
    [HttpPost("verify")]
    [Authorize(Policy = Policies.AppServer)]
    [DisableRateLimiting]
    public Task<LineIdentityResponse> Verify(LineIdTokenLoginRequest request, CancellationToken ct) =>
        lineLogin.VerifyIdTokenOnlyAsync(request, ct);

    /// <summary>Links a LINE account to the signed-in user using an ID token from any registered Login channel.</summary>
    [HttpPost("link")]
    [Authorize(Policy = Policies.AppUser)]
    public async Task<IActionResult> Link(LineIdTokenLoginRequest request, CancellationToken ct)
    {
        await lineLogin.LinkWithIdTokenAsync(User.GetUserId(), request, ct);
        return NoContent();
    }

    /// <summary>
    /// Starts a browser link flow on any registered Login channel (e.g. another LINE provider's app).
    /// Navigate to the returned URL; the browser comes back to redirectUri with linked=line or error=link_failed.
    /// </summary>
    [HttpPost("link/start")]
    [Authorize(Policy = Policies.AppUser)]
    public Task<StartLineLinkResponse> StartLink(StartLineLinkRequest request, CancellationToken ct) =>
        lineLogin.StartLinkAsync(User.GetUserId(), request, ct);

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
