using D2D.Api.Auth;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

public sealed record DatabaseCredentials(DbProvider Provider, string ConnectionString, DateTime UpdatedAt);

/// <summary>
/// For apps that run their own backend against their own database: the connection string is kept (encrypted) in D2D
/// and handed to the owning app's backend on request. Only enabled when the database was registered with
/// allowCredentialAccess=true.
/// </summary>
[ApiController]
[Route("api/v1/database/credentials")]
[Authorize(Policy = Policies.AppServer)]
public sealed class DatabaseCredentialsController(
    IAppRegistry registry,
    ITenantContext tenant,
    ILogger<DatabaseCredentialsController> logger) : ControllerBase
{
    /// <summary>
    /// The calling app is identified by X-App-Id + X-Api-Key, so there is nothing to name. Fetch it at startup (or when a
    /// connection fails) and keep it in memory; <c>updatedAt</c> changes whenever the connection string is replaced.
    /// </summary>
    [HttpGet]
    public async Task<DatabaseCredentials> Get(CancellationToken ct)
    {
        var app = tenant.Required;
        var database = await registry.GetDatabaseAsync(app.Id, ct)
            ?? throw ApiException.NotFound("No database is registered for this app");
        if (!database.AllowCredentialAccess)
        {
            throw ApiException.Forbidden(
                "Credential access is disabled for this database",
                "Enable it with allowCredentialAccess=true when registering the database.");
        }

        logger.LogInformation("App {App} read its database connection string", app.Code);

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return new DatabaseCredentials(database.Provider, database.ConnectionString, database.Version);
    }
}
