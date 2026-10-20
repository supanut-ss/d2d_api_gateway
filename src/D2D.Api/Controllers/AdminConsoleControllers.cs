using D2D.Api.Auth;
using D2D.Api.Setup;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Security;
using D2D.Infrastructure.Admin;
using D2D.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace D2D.Api.Controllers;

public sealed record AdminMe(Guid? Id, string DisplayName, string? Email, AdminRole Role, bool ViaAdminKey);

/// <summary>Sign-in for the admin console. The refresh token only ever travels as an HttpOnly cookie.</summary>
[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController(AdminAuthService auth, IOptions<JwtOptions> jwt) : ControllerBase
{
    private const string CookieName = "d2d_admin_session";
    private const string AjaxHeader = "X-Requested-With";
    public const string AjaxValue = "d2d-admin";

    /// <summary>Tells the console whether to show the first-run setup (no admin exists yet).</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<object> Status(CancellationToken ct) => new { setupRequired = await auth.SetupRequiredAsync(ct) };

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<AdminSession> Login(AdminLoginRequest request, CancellationToken ct)
    {
        var (session, refresh) = await auth.LoginAsync(request, Ip, ct);
        SetCookie(refresh);
        return session;
    }

    /// <summary>Exchanges the session cookie for a new access token (and rotates the cookie).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<AdminSession> Refresh(CancellationToken ct)
    {
        RequireAjaxHeader();
        var raw = Request.Cookies[CookieName] ?? throw ApiException.Unauthorized("No session");
        var (session, refresh) = await auth.RefreshAsync(raw, Ip, ct);
        SetCookie(refresh);
        return session;
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        RequireAjaxHeader();
        if (Request.Cookies[CookieName] is { } raw) await auth.LogoutAsync(raw, ct);
        Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = Policies.AdminRead)]
    public AdminMe Me()
    {
        var role = User.IsInRole(AppRoles.SuperAdmin) ? AdminRole.Admin : AdminRole.Viewer;
        return Guid.TryParse(User.FindFirst(AppClaimTypes.Subject)?.Value, out var id)
            ? new AdminMe(id, User.FindFirst(AppClaimTypes.Name)?.Value ?? "", User.FindFirst(AppClaimTypes.Email)?.Value, role, false)
            : new AdminMe(null, "Admin key", null, role, true);
    }

    [HttpPost("password")]
    [Authorize(Policy = Policies.AdminRead)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(AppClaimTypes.Subject)?.Value, out var id))
            throw ApiException.BadRequest("The admin key has no password to change");
        await auth.ChangePasswordAsync(id, request, ct);
        Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath });
        return NoContent();
    }

    private string CookiePath => Request.PathBase + "/api/v1/admin/auth";
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private void SetCookie(string refresh) => Response.Cookies.Append(CookieName, refresh, new CookieOptions
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        MaxAge = TimeSpan.FromHours(jwt.Value.AdminSessionHours),
        IsEssential = true,
    });

    // A cross-site page cannot add a custom header without a CORS preflight, which we do not allow for this origin.
    private void RequireAjaxHeader()
    {
        if (Request.Headers[AjaxHeader] != AjaxValue) throw ApiException.BadRequest($"Missing {AjaxHeader} header");
    }
}

/// <summary>People who can sign in to the admin console.</summary>
[ApiController]
[Route("api/v1/admin/admins")]
[Authorize(Policy = Policies.AdminRead)]
[ServiceFilter(typeof(AdminAuditFilter))]
public sealed class AdminUsersController(AdminUserService admins) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AdminUserResponse>> List(CancellationToken ct) => admins.ListAsync(ct);

    /// <summary>
    /// Creates an admin. The very first one is created with the static admin key (X-Admin-Key), which is what the console's
    /// first-run setup does; after that console admins create each other.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Policies.Admin)]
    public Task<AdminUserResponse> Create(CreateAdminRequest request, CancellationToken ct) => admins.CreateAsync(request, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public Task<AdminUserResponse> Update(Guid id, UpdateAdminRequest request, CancellationToken ct) =>
        admins.UpdateAsync(id, request, Guid.TryParse(User.FindFirst(AppClaimTypes.Subject)?.Value, out var actor) ? actor : null, ct);

    [HttpPost("{id:guid}/password")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetAdminPasswordRequest request, CancellationToken ct)
    {
        await admins.ResetPasswordAsync(id, request, ct);
        return NoContent();
    }
}

/// <summary>Read-only views over the whole platform for the console: overview, users, LINE inbox, audit trail.</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = Policies.AdminRead)]
[ServiceFilter(typeof(AdminAuditFilter))]
public sealed class AdminPlatformController(PlatformQueryService platform) : ControllerBase
{
    [HttpGet("stats")]
    public Task<PlatformStats> Stats(CancellationToken ct) => platform.GetStatsAsync(ct);

    [HttpGet("users")]
    public Task<PlatformUserPage> Users([FromQuery] string? search, [FromQuery] int skip, [FromQuery] int take = 50, CancellationToken ct = default) =>
        platform.ListUsersAsync(search, skip, take, ct);

    [HttpGet("users/{id:guid}")]
    public Task<PlatformUserDetail> UserDetail(Guid id, CancellationToken ct) => platform.GetUserAsync(id, ct);

    [HttpGet("webhook-events")]
    public Task<WebhookEventPage> WebhookEvents(
        [FromQuery] WebhookEventStatus? status, [FromQuery] Guid? appId, [FromQuery] string? search,
        [FromQuery] int skip, [FromQuery] int take = 50, CancellationToken ct = default) =>
        platform.ListWebhookEventsAsync(status, appId, search, skip, take, ct);

    [HttpGet("webhook-events/{id:guid}")]
    public Task<WebhookEventDetail> WebhookEvent(Guid id, CancellationToken ct) => platform.GetWebhookEventAsync(id, ct);

    [HttpPost("webhook-events/{id:guid}/retry")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        await platform.RetryWebhookEventAsync(id, ct);
        return NoContent();
    }

    [HttpGet("audit")]
    public Task<AuditPage> Audit([FromQuery] string? search, [FromQuery] int skip, [FromQuery] int take = 50, CancellationToken ct = default) =>
        platform.ListAuditAsync(search, skip, take, ct);
}
