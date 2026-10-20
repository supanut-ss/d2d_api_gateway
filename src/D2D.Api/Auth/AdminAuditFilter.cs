using D2D.Core.Entities;
using D2D.Core.Security;
using D2D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace D2D.Api.Auth;

/// <summary>
/// Records who changed what on the admin API. Only successful changes (anything but GET) are logged, and only the
/// route, never the request body, so no password or key can end up in the audit trail.
/// </summary>
public sealed class AdminAuditFilter(MasterDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        var http = context.HttpContext;
        var failedWithResult = executed.Result is IStatusCodeActionResult { StatusCode: >= 400 };
        if (HttpMethods.IsGet(http.Request.Method) || executed.Exception is not null || failedWithResult) return;

        db.AuditLogs.Add(new AuditLog
        {
            // The static admin key has no email claim.
            Actor = http.User.FindFirst(AppClaimTypes.Email)?.Value ?? "admin-key",
            Method = http.Request.Method,
            Path = http.Request.Path.Value ?? "",
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(http.RequestAborted);
    }
}
