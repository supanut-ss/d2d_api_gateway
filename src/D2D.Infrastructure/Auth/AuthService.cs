using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Auth;

/// <summary>Email/password authentication and app membership.</summary>
public sealed class AuthService(MasterDbContext db, TokenService tokens, ITenantContext tenant, TimeProvider clock)
{
    private static readonly PasswordHasher<User> Hasher = new();

    // Verified against when the email is unknown so response time does not reveal registered emails.
    private static readonly string DummyHash = Hasher.HashPassword(null!, Guid.NewGuid().ToString());

    public async Task<AuthTokens> RegisterAsync(RegisterRequest request, string? ip, CancellationToken ct)
    {
        var app = tenant.Required;
        if (!app.AllowSelfRegistration) throw ApiException.Forbidden("Registration is disabled for this app");

        var normalized = User.Normalize(request.Email)!;
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, ct))
        {
            throw ApiException.Conflict(
                "Email already registered",
                "Log in with the existing account; it is linked to this app automatically.");
        }

        var user = new User { Email = request.Email.Trim(), NormalizedEmail = normalized, DisplayName = request.DisplayName.Trim() };
        user.PasswordHash = Hasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        return await SignInAsync(user, app, ip, ct);
    }

    public async Task<AuthTokens> LoginAsync(LoginRequest request, string? ip, CancellationToken ct)
    {
        var app = tenant.Required;
        var normalized = User.Normalize(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);

        if (user?.PasswordHash is null)
        {
            Hasher.VerifyHashedPassword(null!, DummyHash, request.Password);
            throw ApiException.Unauthorized("Invalid email or password");
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed || !user.IsActive)
            throw ApiException.Unauthorized("Invalid email or password");
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = Hasher.HashPassword(user, request.Password);

        return await SignInAsync(user, app, ip, ct);
    }

    /// <summary>Ensures membership in the app, then issues tokens. Shared by all login methods.</summary>
    public async Task<AuthTokens> SignInAsync(User user, TenantInfo app, string? ip, CancellationToken ct)
    {
        if (!user.IsActive) throw ApiException.Forbidden("Account disabled");

        var membership = await EnsureMembershipAsync(user, app, ct);
        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        return await tokens.IssueAsync(user, app, membership.GetRoles(), ip, ct);
    }

    public async Task<MeResponse> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var app = tenant.Required;
        var user = await db.Users.AsNoTracking()
            .Include(x => x.ExternalLogins)
            .FirstOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw ApiException.NotFound("User not found");
        var membership = await db.UserApps.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.AppId == app.Id, ct);

        return new MeResponse(
            user.Id,
            user.DisplayName,
            user.Email,
            user.PictureUrl,
            app.Code,
            membership?.GetRoles() ?? [],
            user.ExternalLogins
                .OrderBy(x => x.CreatedAt)
                .Select(x => new LinkedIdentity(x.Provider, x.ProviderScope, x.ChannelId, x.CreatedAt))
                .ToList());
    }

    /// <summary>
    /// Detaches an external identity (e.g. a LINE account). The last remaining way to sign in cannot be removed,
    /// otherwise the user would lock themselves out.
    /// </summary>
    public async Task UnlinkAsync(Guid userId, string provider, string scope, CancellationToken ct)
    {
        var user = await db.Users.Include(x => x.ExternalLogins).FirstOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw ApiException.NotFound("User not found");
        var login = user.ExternalLogins.FirstOrDefault(x =>
            x.Provider == provider.ToLowerInvariant() && x.ProviderScope == scope)
            ?? throw ApiException.NotFound("Linked account not found");

        var otherMethods = user.ExternalLogins.Count - 1 + (user.PasswordHash is null ? 0 : 1);
        if (otherMethods < 1)
            throw ApiException.Conflict("Cannot remove the only sign-in method", "Add another sign-in method first.");

        db.ExternalLogins.Remove(login);
        await db.SaveChangesAsync(ct);
    }

    private async Task<UserApp> EnsureMembershipAsync(User user, TenantInfo app, CancellationToken ct)
    {
        var membership = await db.UserApps.FirstOrDefaultAsync(x => x.UserId == user.Id && x.AppId == app.Id, ct);
        if (membership is not null) return membership;
        if (!app.AllowSelfRegistration) throw ApiException.Forbidden("Not a member of this app");

        membership = new UserApp { UserId = user.Id, AppId = app.Id };
        db.UserApps.Add(membership);
        return membership;
    }
}
