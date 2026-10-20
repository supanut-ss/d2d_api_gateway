using System.ComponentModel.DataAnnotations;
using D2D.Core.Security;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Auth;

public sealed record IntrospectRequest([Required, MaxLength(4096)] string Token);

/// <summary>
/// RFC 7662 style answer. When <see cref="Active"/> is false nothing else is set (and the reason is deliberately not given).
/// Roles are the current ones from the database, not those inside the token.
/// </summary>
public sealed record IntrospectionResponse(
    bool Active,
    Guid? UserId = null,
    string? DisplayName = null,
    string? Email = null,
    IReadOnlyList<string>? Roles = null,
    DateTime? ExpiresAt = null);

/// <summary>
/// Lets an app's backend ask D2D whether an access token is still good right now. Local JWT validation can lag
/// by the token lifetime (15 min); this reflects a disabled user, a removed membership and role changes immediately.
/// A single logout does not invalidate access tokens already issued.
/// </summary>
public sealed class TokenIntrospectionService(MasterDbContext db, IJwtKeyStore keys, IOptions<JwtOptions> options)
{
    private static readonly IntrospectionResponse Inactive = new(false);

    public async Task<IntrospectionResponse> IntrospectAsync(Guid appId, string token, CancellationToken ct)
    {
        var jwt = options.Value;
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken parsed) return Inactive;

        // A token issued for another app is not a token of this app.
        if (!Guid.TryParse(parsed.Claims.FirstOrDefault(c => c.Type == AppClaimTypes.AppId)?.Value, out var tokenAppId) || tokenAppId != appId)
            return Inactive;
        if (!Guid.TryParse(parsed.Subject, out var userId)) return Inactive;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        var membership = await db.UserApps.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId && m.AppId == appId, ct);
        if (user is null || membership is null) return Inactive;

        return new IntrospectionResponse(true, user.Id, user.DisplayName, user.Email, membership.GetRoles(), parsed.ValidTo);
    }
}
