using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using D2D.Core;
using D2D.Core.Abstractions;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Line;

/// <summary>Resolves the Messaging API access token of a channel according to its <see cref="LineTokenMode"/>.</summary>
/// <summary>An access token that can be used now. <see cref="ExpiresAt"/> is null for long-lived (Static) tokens.</summary>
public sealed record IssuedAccessToken(string Token, DateTime? ExpiresAt);

public interface ILineChannelTokenProvider
{
    Task<string> GetAccessTokenAsync(LineChannelConfig channel, CancellationToken ct);

    /// <summary>
    /// Same token plus when it expires. A returned issued token stays valid for at least the renewal margin
    /// (2 minutes for stateless, 1 day for v2.1), because it is renewed before that.
    /// </summary>
    Task<IssuedAccessToken> GetIssuedAccessTokenAsync(LineChannelConfig channel, CancellationToken ct);

    /// <summary>Drops an issued token LINE rejected (401) so the next call obtains a fresh one.</summary>
    Task InvalidateAsync(LineChannelConfig channel, CancellationToken ct);
}

public sealed class LineChannelTokenProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    ISecretProtector secrets,
    TimeProvider clock,
    ILogger<LineChannelTokenProvider> logger) : ILineChannelTokenProvider
{
    public const string HttpClientName = "line-oauth";

    private const string StatelessEndpoint = "https://api.line.me/oauth2/v3/token";
    private const string V21Endpoint = "https://api.line.me/oauth2/v2.1/token";
    private const string AssertionAudience = "https://api.line.me/";

    // v2.1 tokens are capped at 30 active per channel: issue 7-day tokens and renew one day early,
    // so at most ~7 exist at once even if several instances renew concurrently.
    private static readonly TimeSpan V21Lifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan V21RenewBefore = TimeSpan.FromDays(1);
    private static readonly TimeSpan StatelessRenewBefore = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);

    public async Task<string> GetAccessTokenAsync(LineChannelConfig channel, CancellationToken ct) =>
        (await GetIssuedAccessTokenAsync(channel, ct)).Token;

    public async Task<IssuedAccessToken> GetIssuedAccessTokenAsync(LineChannelConfig channel, CancellationToken ct)
    {
        if (channel.TokenMode == LineTokenMode.Static)
        {
            return new IssuedAccessToken(
                channel.StaticAccessToken
                    ?? throw ApiException.Conflict("LINE channel has no access token", $"Channel {channel.ChannelId}"),
                null);
        }

        var key = CacheKey(channel);
        if (cache.TryGetValue(key, out IssuedAccessToken? cached)) return cached!;

        // One issuance per channel at a time inside this instance.
        var gate = _locks.GetOrAdd(channel.ChannelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached!;

            var (token, expiresAt, renewBefore) = channel.TokenMode == LineTokenMode.Stateless
                ? await IssueStatelessAsync(channel, ct)
                : await GetOrIssueV21Async(channel, ct);

            var issued = new IssuedAccessToken(token, expiresAt);
            var now = clock.GetUtcNow();
            var cacheUntil = new DateTimeOffset(expiresAt, TimeSpan.Zero) - renewBefore;
            cache.Set(key, issued, cacheUntil > now ? cacheUntil : now.AddSeconds(30));
            return issued;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InvalidateAsync(LineChannelConfig channel, CancellationToken ct)
    {
        cache.Remove(CacheKey(channel));
        if (channel.TokenMode != LineTokenMode.JwtAssertion) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MasterDbContext>().LineChannels
            .Where(x => x.ChannelId == channel.ChannelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IssuedAccessTokenEncrypted, (string?)null)
                .SetProperty(x => x.IssuedAccessTokenExpiresAt, (DateTime?)null), ct);
    }

    private async Task<(string Token, DateTime ExpiresAt, TimeSpan RenewBefore)> IssueStatelessAsync(
        LineChannelConfig channel, CancellationToken ct)
    {
        var response = await RequestTokenAsync(channel, StatelessEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = channel.ChannelId,
            ["client_secret"] = channel.ChannelSecret,
        }, ct);
        return (response.AccessToken, Now.AddSeconds(response.ExpiresIn), StatelessRenewBefore);
    }

    /// <summary>Reuses the v2.1 token stored in the master DB while it is fresh; otherwise issues and stores a new one.</summary>
    private async Task<(string Token, DateTime ExpiresAt, TimeSpan RenewBefore)> GetOrIssueV21Async(
        LineChannelConfig channel, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var row = await db.LineChannels.FirstAsync(x => x.ChannelId == channel.ChannelId, ct);

        if (row is { IssuedAccessTokenEncrypted: not null, IssuedAccessTokenExpiresAt: { } storedExpiry })
        {
            var expiresAt = DateTime.SpecifyKind(storedExpiry, DateTimeKind.Utc);
            if (expiresAt - V21RenewBefore > Now)
                return (secrets.Unprotect(row.IssuedAccessTokenEncrypted), expiresAt, V21RenewBefore);
        }

        var response = await RequestTokenAsync(channel, V21Endpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = CreateAssertion(channel, clock),
        }, ct);

        var issuedExpiry = Now.AddSeconds(response.ExpiresIn);
        row.IssuedAccessTokenEncrypted = secrets.Protect(response.AccessToken);
        row.IssuedAccessTokenExpiresAt = issuedExpiry;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Issued LINE v2.1 channel access token for {ChannelId}, expires {ExpiresAt:u}", channel.ChannelId, issuedExpiry);
        return (response.AccessToken, issuedExpiry, V21RenewBefore);
    }

    /// <summary>JWT assertion signed with the channel's registered key pair (RS256, kid in header).</summary>
    public static string CreateAssertion(LineChannelConfig channel, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(channel.AssertionKeyId) || string.IsNullOrEmpty(channel.AssertionPrivateKey))
            throw ApiException.Conflict("JWT assertion key is not configured", $"Channel {channel.ChannelId}");

        var key = new JsonWebKey(channel.AssertionPrivateKey) { KeyId = channel.AssertionKeyId };
        var now = clock.GetUtcNow().UtcDateTime;
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = channel.ChannelId,
            Audience = AssertionAudience,
            Expires = now.AddMinutes(30),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = channel.ChannelId,
                ["token_exp"] = (long)V21Lifetime.TotalSeconds,
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<TokenResponse> RequestTokenAsync(
        LineChannelConfig channel, string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await httpFactory.CreateClient(HttpClientName).PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("LINE token issuance failed for channel {ChannelId}: {Status} {Error}", channel.ChannelId, (int)response.StatusCode, error);
            throw new ApiException(502, "Cannot obtain LINE channel access token", error);
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new ApiException(502, "Cannot obtain LINE channel access token");
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // Includes the credentials' fingerprint so changed credentials never reuse an old cached token.
    private static string CacheKey(LineChannelConfig channel) =>
        $"line:token:{channel.ChannelId}:{channel.TokenMode}:{channel.AssertionKeyId}:{TokenHasher.Sha256(channel.ChannelSecret)[..16]}";
}
