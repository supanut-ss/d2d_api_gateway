using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Security;
using D2D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Admin;

public sealed record MemberResponse(
    Guid UserId,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTime JoinedAt,
    DateTime? LastLoginAt,
    IReadOnlyList<string> LoginMethods);

public sealed record MemberPage(IReadOnlyList<MemberResponse> Items, int Total);

/// <summary>Adds a user to an app. An unknown email creates the user (password required); a known one is attached.</summary>
public sealed record AddMemberRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [MaxLength(200)] string? DisplayName,
    [MinLength(8), MaxLength(128)] string? Password,
    string[]? Roles);

public sealed record SetRolesRequest([Required] string[] Roles);

public sealed record SetActiveRequest(bool IsActive);

/// <summary>Membership and role management for one app. Callers pass the app id they are allowed to manage.</summary>
public sealed partial class MemberService(MasterDbContext db, TimeProvider clock)
{
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly HashSet<string> ReservedRoles = [AppRoles.AppServer, AppRoles.SuperAdmin];

    public async Task EnsureAppExistsAsync(Guid appId, CancellationToken ct)
    {
        if (!await db.Apps.AnyAsync(a => a.Id == appId, ct)) throw ApiException.NotFound("App not found");
    }

    public async Task<MemberPage> ListAsync(Guid appId, string? search, int skip, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var query = db.UserApps.AsNoTracking()
            .Where(m => m.AppId == appId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { Membership = m, User = u });

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var normalized = term.ToUpperInvariant();
            query = query.Where(x => x.User.DisplayName.Contains(term) ||
                                     (x.User.NormalizedEmail != null && x.User.NormalizedEmail.Contains(normalized)));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(x => x.User.DisplayName).ThenBy(x => x.User.Id).Skip(skip).Take(take).ToListAsync(ct);

        var ids = page.Select(x => x.User.Id).ToList();
        var providers = (await db.ExternalLogins.AsNoTracking()
                .Where(l => ids.Contains(l.UserId))
                .Select(l => new { l.UserId, l.Provider })
                .ToListAsync(ct))
            .ToLookup(l => l.UserId, l => l.Provider);

        return new MemberPage(page.Select(x => ToResponse(x.User, x.Membership, providers[x.User.Id])).ToList(), total);
    }

    public async Task<MemberResponse> AddAsync(Guid appId, AddMemberRequest request, CancellationToken ct)
    {
        var roles = NormalizeRoles(request.Roles);
        var normalized = User.Normalize(request.Email)!;
        var user = await db.Users.Include(u => u.ExternalLogins).FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null)
        {
            if (string.IsNullOrEmpty(request.Password)) throw ApiException.BadRequest("Password is required to create a new user");
            user = new User
            {
                Email = request.Email.Trim(),
                NormalizedEmail = normalized,
                DisplayName = (string.IsNullOrWhiteSpace(request.DisplayName) ? request.Email : request.DisplayName).Trim(),
            };
            user.PasswordHash = Hasher.HashPassword(user, request.Password);
            db.Users.Add(user);
        }
        else if (await db.UserApps.AnyAsync(m => m.UserId == user.Id && m.AppId == appId, ct))
        {
            throw ApiException.Conflict("User is already a member of this app");
        }

        var membership = new UserApp { UserId = user.Id, AppId = appId, Roles = string.Join(',', roles) };
        db.UserApps.Add(membership);
        await db.SaveChangesAsync(ct);
        return ToResponse(user, membership, user.ExternalLogins.Select(l => l.Provider));
    }

    /// <summary>Roles take effect in new access tokens; tokens already issued keep theirs until they expire.</summary>
    public async Task<MemberResponse> SetRolesAsync(Guid appId, Guid userId, string[] roles, CancellationToken ct)
    {
        var membership = await db.UserApps.FirstOrDefaultAsync(m => m.AppId == appId && m.UserId == userId, ct)
            ?? throw ApiException.NotFound("User is not a member of this app");
        membership.Roles = string.Join(',', NormalizeRoles(roles));
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().Include(u => u.ExternalLogins).FirstAsync(u => u.Id == userId, ct);
        return ToResponse(user, membership, user.ExternalLogins.Select(l => l.Provider));
    }

    /// <summary>Removes the membership and ends the user's sessions in this app (access tokens expire on their own).</summary>
    public async Task RemoveAsync(Guid appId, Guid userId, CancellationToken ct)
    {
        var removed = await db.UserApps.Where(m => m.AppId == appId && m.UserId == userId).ExecuteDeleteAsync(ct);
        if (removed == 0) throw ApiException.NotFound("User is not a member of this app");

        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.AppId == appId && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    /// <summary>Platform-wide switch: a disabled user cannot sign in to any app and all their sessions end.</summary>
    public async Task SetUserActiveAsync(Guid userId, bool isActive, CancellationToken ct)
    {
        var updated = await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, isActive), ct);
        if (updated == 0) throw ApiException.NotFound("User not found");
        if (isActive) return;

        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    /// <summary>Sets a new password for an app user (admin action) and signs them out everywhere.</summary>
    public async Task ResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        if (newPassword.Length is < 8 or > 128) throw ApiException.BadRequest("Password must be 8–128 characters");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw ApiException.NotFound("User not found");
        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(ct);
        await RevokeSessionsAsync(userId, ct);
    }

    /// <summary>Ends every refresh-token session of the user in all apps (access tokens expire on their own).</summary>
    public async Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var revoked = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (revoked == 0 && !await db.Users.AnyAsync(u => u.Id == userId, ct)) throw ApiException.NotFound("User not found");
    }

    public static string[] NormalizeRoles(string[]? roles)
    {
        var result = (roles ?? []).Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct().ToArray();
        if (result.Length == 0) return [AppRoles.User];
        if (result.Length > 10) throw ApiException.BadRequest("At most 10 roles per user");

        foreach (var role in result)
        {
            if (!RolePattern().IsMatch(role) || ReservedRoles.Contains(role))
                throw ApiException.BadRequest("Invalid role", role);
        }
        return result;
    }

    private static MemberResponse ToResponse(User user, UserApp membership, IEnumerable<string> providers)
    {
        var methods = new List<string>();
        if (user.PasswordHash is not null) methods.Add("password");
        methods.AddRange(providers.Distinct());

        return new MemberResponse(
            user.Id, user.DisplayName, user.Email, membership.GetRoles(), user.IsActive,
            membership.JoinedAt, user.LastLoginAt, methods);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_:.-]{0,31}$")]
    private static partial Regex RolePattern();
}
