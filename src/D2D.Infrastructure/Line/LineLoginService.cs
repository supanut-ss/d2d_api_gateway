using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace D2D.Infrastructure.Line;

/// <summary>
/// LINE Login for every app. Flows:
/// 1. ID token (LIFF / LINE SDK): the client posts its LINE ID token, we verify it and issue our tokens.
/// 2. Web redirect: authorize → LINE → callback → app redirect URI with a one-time code → exchange.
/// 3. Linking: attach a LINE account from another LINE provider to the signed-in user.
///
/// LINE user ids are unique per LINE provider, so identities are keyed by (provider id, sub):
/// apps under the same LINE provider share users automatically; apps under different
/// providers share a user only after the user links the accounts.
/// </summary>
public sealed class LineLoginService(
    MasterDbContext db,
    IAppRegistry registry,
    ITenantContext tenant,
    LineApiClient line,
    AuthService auth,
    IDistributedCache cache,
    IMemoryCache memory,
    TimeProvider clock,
    IOptions<LineOptions> options)
{
    private const string Provider = "line";
    private static readonly TimeSpan MaxVerifyCache = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    private readonly LineOptions _options = options.Value;

    /// <param name="LinkUserId">Set when the flow links an account instead of signing in.</param>
    private sealed record PendingLogin(
        Guid AppId, string ChannelId, string RedirectUri, string? ClientState, string Nonce, string CodeVerifier, Guid? LinkUserId);

    private sealed record PendingCode(Guid UserId, Guid AppId);

    public async Task<AuthTokens> LoginWithIdTokenAsync(LineIdTokenLoginRequest request, string? ip, CancellationToken ct)
    {
        var app = tenant.Required;
        var channel = await ResolveIdTokenChannelAsync(request.IdToken, app.Id, ct);
        var profile = await line.VerifyIdTokenAsync(request.IdToken, channel.ChannelId, request.Nonce, ct);
        var user = await FindOrCreateUserAsync(channel, profile, ct);
        return await auth.SignInAsync(user, app, ip, ct);
    }

    /// <summary>
    /// For apps that need to know who a LINE visitor is but have no accounts: verifies the ID token with LINE and
    /// returns the identity. Nothing is stored and no tokens are issued. The token must belong to one of the calling app's Login channels.
    /// </summary>
    public async Task<LineIdentityResponse> VerifyIdTokenOnlyAsync(LineIdTokenLoginRequest request, CancellationToken ct)
    {
        var app = tenant.Required;
        // Cheap local check first: the token must belong to one of the calling app's Login channels.
        var channel = await ResolveIdTokenChannelAsync(request.IdToken, app.Id, ct);

        // Apps with no login send the ID token on every request, so remember what LINE already vouched for.
        var key = $"line:verify:{channel.ChannelId}:{TokenHasher.Sha256(request.IdToken)}:{request.Nonce}";
        if (memory.TryGetValue(key, out LineIdentityResponse? cached)) return cached!;

        var profile = await line.VerifyIdTokenAsync(request.IdToken, channel.ChannelId, request.Nonce, ct);
        DateTime? expiresAt = profile.Exp is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null;
        var identity = new LineIdentityResponse(
            profile.Sub, channel.ProviderId, channel.ChannelId, profile.Name, profile.Picture, profile.Email, expiresAt);

        // Only tokens with a known expiry are kept, never longer than the token itself (minus a margin) or 5 minutes.
        if (expiresAt is not null)
        {
            var ttl = expiresAt.Value - clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(30);
            if (ttl > TimeSpan.Zero) memory.Set(key, identity, ttl < MaxVerifyCache ? ttl : MaxVerifyCache);
        }
        return identity;
    }

    /// <summary>Links a LINE account (ID token from any registered Login channel) to the signed-in user.</summary>
    public async Task LinkWithIdTokenAsync(Guid userId, LineIdTokenLoginRequest request, CancellationToken ct)
    {
        var channel = await ResolveIdTokenChannelAsync(request.IdToken, appId: null, ct);
        var profile = await line.VerifyIdTokenAsync(request.IdToken, channel.ChannelId, request.Nonce, ct);
        await LinkAsync(userId, channel, profile, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Builds the LINE authorize URL (with PKCE) the browser is redirected to.</summary>
    public async Task<string> BuildAuthorizeUrlAsync(
        string appCode, string redirectUri, string? clientState, string? channelId, CancellationToken ct)
    {
        var app = await registry.FindByCodeAsync(appCode, ct) ?? throw ApiException.BadRequest("Unknown app");
        if (!app.IsActive) throw ApiException.Forbidden("App is disabled");
        await EnsureRedirectAllowedAsync(app.Id, redirectUri, ct);

        var channel = channelId is null
            ? await RequireLoginChannelAsync(app.Id, ct)
            : await RequireLoginChannelAsync(channelId, app.Id, ct);

        return await CreateAuthorizeUrlAsync(new PendingLogin(
            app.Id, channel.ChannelId, redirectUri, clientState, TokenHasher.NewToken(), TokenHasher.NewToken(), LinkUserId: null), ct);
    }

    /// <summary>Starts the browser link flow for the signed-in user; the channel may belong to any app.</summary>
    public async Task<StartLineLinkResponse> StartLinkAsync(Guid userId, StartLineLinkRequest request, CancellationToken ct)
    {
        var app = tenant.Required;
        await EnsureRedirectAllowedAsync(app.Id, request.RedirectUri, ct);
        var channel = await RequireLoginChannelAsync(request.ChannelId, appId: null, ct);

        var url = await CreateAuthorizeUrlAsync(new PendingLogin(
            app.Id, channel.ChannelId, request.RedirectUri, request.State, TokenHasher.NewToken(), TokenHasher.NewToken(), userId), ct);
        return new StartLineLinkResponse(url);
    }

    /// <summary>Handles LINE's redirect and returns the app URL to send the browser to.</summary>
    public async Task<string> HandleCallbackAsync(string? code, string? state, string? error, string? errorDescription, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state)) throw ApiException.BadRequest("Missing state");
        var pending = await TakeAsync<PendingLogin>(StateKey(state), ct)
            ?? throw ApiException.BadRequest("Login session expired or state is invalid");

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return AppRedirect(pending, ("error", error ?? "access_denied"), ("error_description", errorDescription));

        var channel = await RequireLoginChannelAsync(pending.ChannelId, appId: null, ct);
        var token = await line.ExchangeCodeAsync(code, _options.LoginCallbackUrl, channel.ChannelId, channel.ChannelSecret, pending.CodeVerifier, ct);
        var profile = await line.VerifyIdTokenAsync(
            token.IdToken ?? throw ApiException.Unauthorized("LINE did not return an ID token", "Add the openid scope."),
            channel.ChannelId, pending.Nonce, ct);

        if (pending.LinkUserId is { } linkUserId)
        {
            try
            {
                await LinkAsync(linkUserId, channel, profile, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (ApiException ex)
            {
                // The browser is mid-redirect: report the failure to the app instead of showing a raw error.
                return AppRedirect(pending, ("error", "link_failed"), ("error_description", ex.Title));
            }
            return AppRedirect(pending, ("linked", Provider));
        }

        var user = await FindOrCreateUserAsync(channel, profile, ct);
        await db.SaveChangesAsync(ct);

        // Tokens never travel in the URL: the app swaps this short-lived code for them server-side.
        var authCode = TokenHasher.NewToken();
        await cache.SetStringAsync(CodeKey(authCode), JsonSerializer.Serialize(new PendingCode(user.Id, pending.AppId)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CodeLifetime }, ct);

        return AppRedirect(pending, ("code", authCode));
    }

    public async Task<AuthTokens> ExchangeCodeAsync(string code, string? ip, CancellationToken ct)
    {
        var app = tenant.Required;
        var pending = await TakeAsync<PendingCode>(CodeKey(code), ct) ?? throw ApiException.Unauthorized("Invalid or expired code");
        if (pending.AppId != app.Id) throw ApiException.Unauthorized("Code was issued for a different app");

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == pending.UserId, ct)
            ?? throw ApiException.Unauthorized("User not found");
        return await auth.SignInAsync(user, app, ip, ct);
    }

    private async Task<User> FindOrCreateUserAsync(LineChannelConfig channel, LineIdTokenPayload profile, CancellationToken ct)
    {
        var login = await FindIdentityAsync(channel.ProviderId, profile.Sub, ct);
        if (login is not null)
        {
            var existing = await db.Users.FirstAsync(x => x.Id == login.UserId, ct);
            existing.DisplayName = profile.Name ?? existing.DisplayName;
            existing.PictureUrl = profile.Picture ?? existing.PictureUrl;
            return existing;
        }

        // No automatic linking to an existing account by email: that would let whoever controls
        // a LINE account with the same email take over the account. Linking must be explicit.
        var user = new User { DisplayName = profile.Name ?? "LINE user", PictureUrl = profile.Picture };
        var normalized = User.Normalize(profile.Email);
        if (normalized is not null && !await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, ct))
        {
            user.Email = profile.Email;
            user.NormalizedEmail = normalized;
        }

        user.ExternalLogins.Add(NewIdentity(user.Id, channel, profile));
        db.Users.Add(user);
        return user;
    }

    private async Task LinkAsync(Guid userId, LineChannelConfig channel, LineIdTokenPayload profile, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, ct)) throw ApiException.Unauthorized("User not found");

        var existing = await FindIdentityAsync(channel.ProviderId, profile.Sub, ct);
        if (existing is not null)
        {
            if (existing.UserId == userId) return; // already linked
            throw ApiException.Conflict(
                "LINE account is linked to another user",
                "Merging two existing users is not supported.");
        }

        var hasOtherAccount = await db.ExternalLogins.AnyAsync(
            x => x.UserId == userId && x.Provider == Provider && x.ProviderScope == channel.ProviderId, ct);
        if (hasOtherAccount) throw ApiException.Conflict("Another LINE account from this LINE provider is already linked");

        db.ExternalLogins.Add(NewIdentity(userId, channel, profile));
    }

    private Task<ExternalLogin?> FindIdentityAsync(string providerId, string sub, CancellationToken ct) =>
        db.ExternalLogins.FirstOrDefaultAsync(
            x => x.Provider == Provider && x.ProviderScope == providerId && x.ProviderKey == sub, ct);

    private static ExternalLogin NewIdentity(Guid userId, LineChannelConfig channel, LineIdTokenPayload profile) => new()
    {
        UserId = userId,
        Provider = Provider,
        ProviderScope = channel.ProviderId,
        ProviderKey = profile.Sub,
        ChannelId = channel.ChannelId,
    };

    /// <summary>
    /// Picks the Login channel from the token's "aud" claim, so an app may own several Login channels
    /// (e.g. different LIFF apps). The claim is read unverified only to choose the channel;
    /// LINE's verify endpoint then checks the signature and that aud equals that channel.
    /// </summary>
    private async Task<LineChannelConfig> ResolveIdTokenChannelAsync(string idToken, Guid? appId, CancellationToken ct)
    {
        var channelId = ReadAudience(idToken) ?? throw ApiException.Unauthorized("Invalid LINE ID token");
        var channel = await registry.FindLineChannelAsync(channelId, ct);
        if (channel is null || channel.Type != LineChannelType.Login)
            throw ApiException.Unauthorized("ID token was issued for an unknown LINE Login channel");
        if (appId is not null && channel.AppId != appId)
            throw ApiException.Unauthorized("ID token was issued for another app's LINE channel");
        return channel;
    }

    private static string? ReadAudience(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            return payload.RootElement.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String
                ? aud.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private async Task<string> CreateAuthorizeUrlAsync(PendingLogin pending, CancellationToken ct)
    {
        var state = TokenHasher.NewToken();
        await cache.SetStringAsync(StateKey(state), JsonSerializer.Serialize(pending),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_options.AuthStateMinutes) }, ct);

        var codeChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(pending.CodeVerifier)));
        return QueryHelpers.AddQueryString(LineApiClient.AuthorizeEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = pending.ChannelId,
            ["redirect_uri"] = _options.LoginCallbackUrl,
            ["state"] = state,
            ["scope"] = _options.Scope,
            ["nonce"] = pending.Nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });
    }

    private async Task EnsureRedirectAllowedAsync(Guid appId, string redirectUri, CancellationToken ct)
    {
        var allowed = await registry.GetRedirectUrisAsync(appId, ct);
        if (!allowed.Contains(redirectUri, StringComparer.Ordinal))
            throw ApiException.BadRequest("redirect_uri is not registered for this app");
    }

    private async Task<LineChannelConfig> RequireLoginChannelAsync(Guid appId, CancellationToken ct) =>
        await registry.GetLineChannelAsync(appId, LineChannelType.Login, ct)
        ?? throw ApiException.Conflict("LINE Login is not configured for this app");

    /// <param name="appId">When set, the channel must belong to this app.</param>
    private async Task<LineChannelConfig> RequireLoginChannelAsync(string channelId, Guid? appId, CancellationToken ct)
    {
        var channel = await registry.FindLineChannelAsync(channelId, ct);
        if (channel is null || channel.Type != LineChannelType.Login || (appId is not null && channel.AppId != appId))
            throw ApiException.BadRequest("Unknown LINE Login channel", channelId);
        return channel;
    }

    // Read-then-delete makes codes single use. With Redis prefer an atomic GETDEL.
    private async Task<T?> TakeAsync<T>(string key, CancellationToken ct) where T : class
    {
        var json = await cache.GetStringAsync(key, ct);
        if (json is null) return null;
        await cache.RemoveAsync(key, ct);
        return JsonSerializer.Deserialize<T>(json);
    }

    private static string AppRedirect(PendingLogin pending, params (string Key, string? Value)[] query) =>
        QueryHelpers.AddQueryString(pending.RedirectUri, query
            .Append((Key: "state", Value: pending.ClientState))
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value));

    private static string StateKey(string state) => $"line-login:state:{state}";
    private static string CodeKey(string code) => $"line-login:code:{code}";
}
