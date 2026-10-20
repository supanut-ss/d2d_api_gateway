using D2D.Api.Auth;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

public sealed record AdminResetPasswordRequest([System.ComponentModel.DataAnnotations.Required] string NewPassword);

/// <summary>Platform admin: manage members and roles of any app, and disable users globally.</summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = Policies.AdminRead)]
[ServiceFilter(typeof(AdminAuditFilter))]
public sealed class AdminMembersController(MemberService members) : ControllerBase
{
    [HttpGet("apps/{appId:guid}/members")]
    public async Task<MemberPage> List(Guid appId, [FromQuery] string? search, [FromQuery] int skip, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        await members.EnsureAppExistsAsync(appId, ct);
        return await members.ListAsync(appId, search, skip, take, ct);
    }

    [HttpPost("apps/{appId:guid}/members")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<MemberResponse> Add(Guid appId, AddMemberRequest request, CancellationToken ct)
    {
        await members.EnsureAppExistsAsync(appId, ct);
        return await members.AddAsync(appId, request, ct);
    }

    [HttpPut("apps/{appId:guid}/members/{userId:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<MemberResponse> SetRoles(Guid appId, Guid userId, SetRolesRequest request, CancellationToken ct)
    {
        await members.EnsureAppExistsAsync(appId, ct);
        return await members.SetRolesAsync(appId, userId, request.Roles, ct);
    }

    [HttpDelete("apps/{appId:guid}/members/{userId:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Remove(Guid appId, Guid userId, CancellationToken ct)
    {
        await members.EnsureAppExistsAsync(appId, ct);
        await members.RemoveAsync(appId, userId, ct);
        return NoContent();
    }

    [HttpPut("users/{userId:guid}/active")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> SetActive(Guid userId, SetActiveRequest request, CancellationToken ct)
    {
        await members.SetUserActiveAsync(userId, request.IsActive, ct);
        return NoContent();
    }

    /// <summary>Sets a new password for an app user and signs them out everywhere.</summary>
    [HttpPost("users/{userId:guid}/password")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> ResetPassword(Guid userId, AdminResetPasswordRequest request, CancellationToken ct)
    {
        await members.ResetPasswordAsync(userId, request.NewPassword, ct);
        return NoContent();
    }

    [HttpPost("users/{userId:guid}/revoke-sessions")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> RevokeSessions(Guid userId, CancellationToken ct)
    {
        await members.RevokeSessionsAsync(userId, ct);
        return NoContent();
    }
}

/// <summary>
/// An app's own backend manages members and roles of its own app only (X-App-Id + X-Api-Key),
/// e.g. to provision accounts for an app that has self-registration turned off.
/// </summary>
[ApiController]
[Route("api/v1/members")]
[Authorize(Policy = Policies.AppServer)]
public sealed class AppMembersController(MemberService members, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public Task<MemberPage> List([FromQuery] string? search, [FromQuery] int skip, [FromQuery] int take = 50, CancellationToken ct = default) =>
        members.ListAsync(tenant.Required.Id, search, skip, take, ct);

    [HttpPost]
    public Task<MemberResponse> Add(AddMemberRequest request, CancellationToken ct) =>
        members.AddAsync(tenant.Required.Id, request, ct);

    [HttpPut("{userId:guid}")]
    public Task<MemberResponse> SetRoles(Guid userId, SetRolesRequest request, CancellationToken ct) =>
        members.SetRolesAsync(tenant.Required.Id, userId, request.Roles, ct);

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct)
    {
        await members.RemoveAsync(tenant.Required.Id, userId, ct);
        return NoContent();
    }
}
