using D2D.Core;
using D2D.Core.Security;
using D2D.Core.Tenancy;

namespace D2D.Api.Tenancy;

/// <summary>
/// Resolves the app (tenant) for the request from the JWT "app_id" claim and/or the X-App-Id header.
/// A token can only be used with the app it was issued for.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenant, IAppRegistry registry)
    {
        var ct = context.RequestAborted;
        TenantInfo? app = null;

        var claim = context.User.FindFirst(AppClaimTypes.AppId)?.Value;
        if (claim is not null)
        {
            app = Guid.TryParse(claim, out var tokenAppId) ? await registry.FindByIdAsync(tokenAppId, ct) : null;
            if (app is null) throw ApiException.Unauthorized("Token app no longer exists");
        }

        var header = context.Request.Headers[AppHeaders.AppId].ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            var byHeader = await registry.FindByCodeAsync(header, ct) ?? throw ApiException.BadRequest("Unknown app", header);
            if (app is not null && app.Id != byHeader.Id) throw ApiException.Forbidden("Token was issued for a different app");
            app ??= byHeader;
        }

        if (app is not null)
        {
            if (!app.IsActive) throw ApiException.Forbidden("App is disabled");
            tenant.Set(app);
        }

        await next(context);
    }
}
