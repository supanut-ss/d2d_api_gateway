using D2D.Api.Auth;
using D2D.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

/// <summary>
/// Platform administration of apps. Reading needs any admin (key or console user); changing needs the Admin role.
/// </summary>
[ApiController]
[Route("api/v1/admin/apps")]
[Authorize(Policy = Policies.AdminRead)]
[ServiceFilter(typeof(AdminAuditFilter))]
public sealed class AdminAppsController(AppAdminService admin) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AppResponse>> List(CancellationToken ct) => admin.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public Task<AppResponse> Get(Guid id, CancellationToken ct) => admin.GetAsync(id, ct);

    /// <summary>Registers an app. The returned API key is shown only once.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<AppWithKeyResponse>> Create(CreateAppRequest request, CancellationToken ct)
    {
        var created = await admin.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.App.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public Task<AppResponse> Update(Guid id, UpdateAppRequest request, CancellationToken ct) =>
        admin.UpdateAsync(id, request, ct);

    /// <summary>Deletes the app and everything that belongs only to it (channels, sessions, webhook inbox). Users stay.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await admin.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/api-key/rotate")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<object> RotateApiKey(Guid id, CancellationToken ct) =>
        new { apiKey = await admin.RotateApiKeyAsync(id, ct) };

    /// <summary>Sets the app's own database (any supported engine). Provision=true creates the sample schema.</summary>
    [HttpPut("{id:guid}/database")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SetDatabase(Guid id, SetDatabaseRequest request, CancellationToken ct)
    {
        await admin.SetDatabaseAsync(id, request, ct);
        return NoContent();
    }

    /// <summary>Returns the stored connection string so the console can show it on edit. POST (not GET) so the audit trail records every read.</summary>
    [HttpPost("{id:guid}/database/reveal")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> RevealDatabase(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var cs = await admin.RevealDatabaseConnectionAsync(id, ct);
        return cs is null ? NotFound() : Ok(new { connectionString = cs });
    }

    [HttpPut("{id:guid}/line-channels")]
    [Authorize(Policy = Policies.Admin)]
    public Task<LineChannelSummary> UpsertLineChannel(Guid id, UpsertLineChannelRequest request, CancellationToken ct) =>
        admin.UpsertLineChannelAsync(id, request, ct);

    /// <summary>Gets an access token the configured way and calls LINE (bot info) to confirm the channel works.</summary>
    [HttpPost("{id:guid}/line-channels/{channelId}/verify")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> VerifyLineChannel(Guid id, string channelId, CancellationToken ct) =>
        Ok(await admin.VerifyLineChannelAsync(id, channelId, ct));

    [HttpDelete("{id:guid}/line-channels/{channelId}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> DeleteLineChannel(Guid id, string channelId, CancellationToken ct)
    {
        await admin.DeleteLineChannelAsync(id, channelId, ct);
        return NoContent();
    }
}
