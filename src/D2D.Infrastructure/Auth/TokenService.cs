using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Security;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Auth;

/// <summary>Issues JWT access tokens and rotating refresh tokens scoped to one app.</summary>
public sealed class TokenService(MasterDbContext db, IOptions<JwtOptions> options, IJwtKeyStore keys, TimeProvider clock)
{
    private readonly JwtOptions _jwt = options.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Starts a new token family. Also saves any pending changes on the shared DbContext.</summary>
    public async Task<AuthTokens> IssueAsync(User user, TenantInfo app, IEnumerable<string> roles, string? ip, CancellationToken ct)
    {
        var refresh = NewRefreshToken(user.Id, app.Id, Guid.NewGuid(), ip, out var raw);
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);
        return new AuthTokens(CreateAccessToken(user, app, roles), raw, _jwt.AccessTokenMinutes * 60);
    }

    public async Task<AuthTokens> RefreshAsync(string rawToken, TenantInfo app, string? ip, CancellationToken ct)
    {
        var now = Now;
        var stored = await FindAsync(rawToken, app, ct);

        if (stored.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and end the whole session.
            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            throw ApiException.Unauthorized("Refresh token reuse detected");
        }
        if (stored.ExpiresAt <= now) throw ApiException.Unauthorized("Refresh token expired");

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == stored.UserId && x.IsActive, ct);
        var membership = await db.UserApps.FirstOrDefaultAsync(x => x.UserId == stored.UserId && x.AppId == app.Id, ct);
        if (user is null || membership is null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            throw ApiException.Unauthorized("Account is no longer active for this app");
        }

        var next = NewRefreshToken(user.Id, app.Id, stored.FamilyId, ip, out var raw);

        // Conditional update makes rotation atomic when the same token is used concurrently.
        var rotated = await db.RefreshTokens
            .Where(x => x.Id == stored.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.ReplacedById, (Guid?)next.Id), ct);
        if (rotated == 0) throw ApiException.Unauthorized("Refresh token already used");

        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        return new AuthTokens(CreateAccessToken(user, app, membership.GetRoles()), raw, _jwt.AccessTokenMinutes * 60);
    }

    /// <summary>Logout: revokes every token of the session the given refresh token belongs to.</summary>
    public async Task RevokeAsync(string rawToken, TenantInfo app, CancellationToken ct)
    {
        var stored = await FindAsync(rawToken, app, ct);
        await RevokeFamilyAsync(stored.FamilyId, Now, ct);
    }

    /// <summary>Signs the user out of this app on every device. Access tokens already issued expire on their own.</summary>
    public Task RevokeAllAsync(Guid userId, Guid appId, CancellationToken ct)
    {
        var now = Now;
        return db.RefreshTokens
            .Where(x => x.UserId == userId && x.AppId == appId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    private async Task<RefreshToken> FindAsync(string rawToken, TenantInfo app, CancellationToken ct)
    {
        var hash = TokenHasher.Sha256(rawToken);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (stored is null || stored.AppId != app.Id) throw ApiException.Unauthorized("Invalid refresh token");
        return stored;
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(x => x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    private RefreshToken NewRefreshToken(Guid userId, Guid appId, Guid familyId, string? ip, out string raw)
    {
        raw = TokenHasher.NewToken(48);
        return new RefreshToken
        {
            UserId = userId,
            AppId = appId,
            FamilyId = familyId,
            TokenHash = TokenHasher.Sha256(raw),
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(_jwt.RefreshTokenDays),
            CreatedByIp = ip,
        };
    }

    private string CreateAccessToken(User user, TenantInfo app, IEnumerable<string> roles)
    {
        var now = Now;
        var claims = new Dictionary<string, object>
        {
            [AppClaimTypes.Subject] = user.Id.ToString(),
            [AppClaimTypes.Name] = user.DisplayName,
            [AppClaimTypes.AppId] = app.Id.ToString(),
            [AppClaimTypes.AppCode] = app.Code,
            [AppClaimTypes.Role] = roles.ToArray(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };
        if (user.Email is not null) claims[AppClaimTypes.Email] = user.Email;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_jwt.AccessTokenMinutes),
            SigningCredentials = keys.SigningCredentials,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
