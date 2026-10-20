using System.ComponentModel.DataAnnotations;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Security;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Admin;

public sealed record AdminLoginRequest([Required, EmailAddress] string Email, [Required] string Password);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(AdminPasswords.MinLength), MaxLength(128)] string NewPassword);

public sealed record AdminProfile(Guid Id, string Email, string DisplayName, AdminRole Role);

/// <summary>The access token goes in the JSON body (kept in memory by the console); the refresh token only travels as an HttpOnly cookie.</summary>
public sealed record AdminSession(string AccessToken, int ExpiresIn, AdminProfile Admin);

public static class AdminPasswords
{
    public const int MinLength = 10;
}

/// <summary>Sign-in for the admin console: short access tokens plus a rotating refresh token that lives in a cookie.</summary>
public sealed class AdminAuthService(MasterDbContext db, IJwtKeyStore keys, IOptions<JwtOptions> options, TimeProvider clock)
{
    /// <summary>Admin sessions are rows in refresh_tokens with this app id, which no real app can have.</summary>
    public static readonly Guid SessionAppId = Guid.Empty;

    internal static readonly PasswordHasher<AdminUser> Hasher = new();

    // Verified against when the email is unknown so response time does not reveal which admins exist.
    private static readonly string DummyHash = Hasher.HashPassword(null!, Guid.NewGuid().ToString());

    private readonly JwtOptions _jwt = options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>True until the first admin exists; the console then offers a one-time setup (guarded by the admin key).</summary>
    public async Task<bool> SetupRequiredAsync(CancellationToken ct) => !await db.AdminUsers.AnyAsync(ct);

    public async Task<(AdminSession Session, string RefreshToken)> LoginAsync(AdminLoginRequest request, string? ip, CancellationToken ct)
    {
        var normalized = Core.Entities.User.Normalize(request.Email);
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.NormalizedEmail == normalized, ct);

        if (admin is null)
        {
            Hasher.VerifyHashedPassword(null!, DummyHash, request.Password);
            throw ApiException.Unauthorized("Invalid email or password");
        }

        var result = Hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed || !admin.IsActive)
            throw ApiException.Unauthorized("Invalid email or password");
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            admin.PasswordHash = Hasher.HashPassword(admin, request.Password);

        admin.LastLoginAt = Now;
        var (refresh, raw) = NewRefresh(admin.Id, Guid.NewGuid(), ip);
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);
        return (Session(admin), raw);
    }

    public async Task<(AdminSession Session, string RefreshToken)> RefreshAsync(string rawToken, string? ip, CancellationToken ct)
    {
        var now = Now;
        var hash = TokenHasher.Sha256(rawToken);
        var stored = await db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.AppId == SessionAppId, ct)
            ?? throw ApiException.Unauthorized("Session not found");

        if (stored.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and end the whole session.
            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            throw ApiException.Unauthorized("Session was reused");
        }
        if (stored.ExpiresAt <= now) throw ApiException.Unauthorized("Session expired");

        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == stored.UserId && a.IsActive, ct);
        if (admin is null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            throw ApiException.Unauthorized("Admin account is no longer active");
        }

        var (next, raw) = NewRefresh(admin.Id, stored.FamilyId, ip);
        // Conditional update makes rotation atomic when the same cookie is used concurrently.
        var rotated = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, (Guid?)next.Id), ct);
        if (rotated == 0) throw ApiException.Unauthorized("Session already used");

        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        return (Session(admin), raw);
    }

    public async Task LogoutAsync(string rawToken, CancellationToken ct)
    {
        var hash = TokenHasher.Sha256(rawToken);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash && t.AppId == SessionAppId, ct);
        if (stored is not null) await RevokeFamilyAsync(stored.FamilyId, Now, ct);
    }

    public async Task ChangePasswordAsync(Guid adminId, ChangePasswordRequest request, CancellationToken ct)
    {
        var admin = await db.AdminUsers.FirstOrDefaultAsync(a => a.Id == adminId && a.IsActive, ct)
            ?? throw ApiException.NotFound("Admin not found");
        if (Hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw ApiException.BadRequest("Current password is wrong");

        admin.PasswordHash = Hasher.HashPassword(admin, request.NewPassword);
        await db.SaveChangesAsync(ct);
        await RevokeAllSessionsAsync(adminId, ct);
    }

    public Task RevokeAllSessionsAsync(Guid adminId, CancellationToken ct)
    {
        var now = Now;
        return db.RefreshTokens
            .Where(t => t.AppId == SessionAppId && t.UserId == adminId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public static string RoleClaim(AdminRole role) => role == AdminRole.Admin ? AppRoles.SuperAdmin : AppRoles.PlatformViewer;

    private AdminSession Session(AdminUser admin)
    {
        var now = Now;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.AdminAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_jwt.AdminAccessTokenMinutes),
            Claims = new Dictionary<string, object>
            {
                [AppClaimTypes.Subject] = admin.Id.ToString(),
                [AppClaimTypes.Name] = admin.DisplayName,
                [AppClaimTypes.Email] = admin.Email,
                [AppClaimTypes.Role] = new[] { RoleClaim(admin.Role) },
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = keys.SigningCredentials,
        });
        return new AdminSession(token, _jwt.AdminAccessTokenMinutes * 60, ToProfile(admin));
    }

    internal static AdminProfile ToProfile(AdminUser admin) => new(admin.Id, admin.Email, admin.DisplayName, admin.Role);

    private (RefreshToken Token, string Raw) NewRefresh(Guid adminId, Guid familyId, string? ip)
    {
        var raw = TokenHasher.NewToken(48);
        return (new RefreshToken
        {
            UserId = adminId,
            AppId = SessionAppId,
            FamilyId = familyId,
            TokenHash = TokenHasher.Sha256(raw),
            CreatedAt = Now,
            ExpiresAt = Now.AddHours(_jwt.AdminSessionHours),
            CreatedByIp = ip,
        }, raw);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}
