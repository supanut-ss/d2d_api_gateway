using D2D.Core;
using D2D.Core.Security;
using D2D.Core.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Filters;

namespace D2D.Api.Auth;

/// <summary>
/// On sign-in endpoints: if the app is configured with RequireAppKeyForAuth, the call must carry the app's own
/// API key (X-App-Id + X-Api-Key), i.e. it comes from the app's backend. Other apps are unaffected.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AppKeyIfRequiredAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // The app comes from X-App-Id (resolved by the tenant middleware). No app: the action reports it as before.
        var app = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().Current;
        if (app is not { RequireAppKeyForAuth: true }) return;

        var result = await context.HttpContext.AuthenticateAsync(AuthSchemes.AppApiKey);
        var keyAppId = result.Principal?.FindFirst(AppClaimTypes.AppId)?.Value;
        if (result.Succeeded && keyAppId == app.Id.ToString()) return;

        throw ApiException.Unauthorized(
            "This app signs users in through its backend",
            $"Send {AppHeaders.ApiKey} together with {AppHeaders.AppId} from the app's backend.");
    }
}
