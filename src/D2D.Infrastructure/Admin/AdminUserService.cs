using System.ComponentModel.DataAnnotations;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Admin;

public sealed record CreateAdminRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(200)] string DisplayName,
    [Required, MinLength(AdminPasswords.MinLength), MaxLength(128)] string Password,
    AdminRole Role = AdminRole.Admin);

public sealed record UpdateAdminRequest([MaxLength(200)] string? DisplayName, AdminRole? Role, bool? IsActive);

public sealed record ResetAdminPasswordRequest([Required, MinLength(AdminPasswords.MinLength), MaxLength(128)] string NewPassword);

public sealed record AdminUserResponse(
    Guid Id, string Email, string DisplayName, AdminRole Role, bool IsActive, DateTime CreatedAt, DateTime? LastLoginAt);

/// <summary>Management of the people who can sign in to the admin console.</summary>
public sealed class AdminUserService(MasterDbContext db, AdminAuthService auth)
{
    public async Task<IReadOnlyList<AdminUserResponse>> ListAsync(CancellationToken ct) =>
        (await db.AdminUsers.AsNoTracking().OrderBy(a => a.DisplayName).ToListAsync(ct)).Select(ToResponse).ToList();

    public async Task<AdminUserResponse> CreateAsync(CreateAdminRequest request, CancellationToken ct)
    {
        var normalized = Core.Entities.User.Normalize(request.Email)!;
        if (await db.AdminUsers.AnyAsync(a => a.NormalizedEmail == normalized, ct))
            throw ApiException.Conflict("An admin with this email already exists");

        var admin = new AdminUser
        {
            Email = request.Email.Trim(),
            NormalizedEmail = normalized,
            DisplayName = request.DisplayName.Trim(),
            Role = request.Role,
        };
        admin.PasswordHash = AdminAuthService.Hasher.HashPassword(admin, request.Password);
        db.AdminUsers.Add(admin);
        await db.SaveChangesAsync(ct);
        return ToResponse(admin);
    }

    /// <param name="actorId">The admin making the change (null for the admin key). Nobody may demote or disable themselves.</param>
    public async Task<AdminUserResponse> UpdateAsync(Guid id, UpdateAdminRequest request, Guid? actorId, CancellationToken ct)
    {
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Admin not found");

        var losesPower = (request.Role is { } role && role != AdminRole.Admin) || request.IsActive == false;
        if (losesPower && admin.Role == AdminRole.Admin && admin.IsActive)
        {
            if (actorId == id) throw ApiException.Conflict("You cannot demote or disable yourself");
            var otherAdmins = await db.AdminUsers.CountAsync(a => a.Id != id && a.IsActive && a.Role == AdminRole.Admin, ct);
            if (otherAdmins == 0) throw ApiException.Conflict("At least one active admin must remain");
        }

        if (request.DisplayName is { } name && !string.IsNullOrWhiteSpace(name)) admin.DisplayName = name.Trim();
        if (request.Role is { } newRole) admin.Role = newRole;
        if (request.IsActive is { } active) admin.IsActive = active;
        await db.SaveChangesAsync(ct);

        // A demoted or disabled admin loses the sessions that carry the old rights.
        if (losesPower) await auth.RevokeAllSessionsAsync(id, ct);
        return ToResponse(admin);
    }

    public async Task ResetPasswordAsync(Guid id, ResetAdminPasswordRequest request, CancellationToken ct)
    {
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Admin not found");
        admin.PasswordHash = AdminAuthService.Hasher.HashPassword(admin, request.NewPassword);
        await db.SaveChangesAsync(ct);
        await auth.RevokeAllSessionsAsync(id, ct);
    }

    private static AdminUserResponse ToResponse(AdminUser a) =>
        new(a.Id, a.Email, a.DisplayName, a.Role, a.IsActive, a.CreatedAt, a.LastLoginAt);
}
