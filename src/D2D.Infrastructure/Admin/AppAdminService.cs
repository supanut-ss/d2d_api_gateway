using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using D2D.Core;
using D2D.Core.Abstractions;
using D2D.Core.Entities;
using D2D.Core.Line;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Persistence.AppData;
using D2D.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Admin;

public sealed record CreateAppRequest(
    [Required, RegularExpression("^[a-z0-9][a-z0-9-]{1,62}$")] string Code,
    [Required, MaxLength(200)] string Name,
    bool AllowSelfRegistration = true,
    string[]? RedirectUris = null,
    bool RequireAppKeyForAuth = false);

public sealed record UpdateAppRequest(
    [Required, MaxLength(200)] string Name,
    bool IsActive,
    bool AllowSelfRegistration,
    string[]? RedirectUris,
    bool? RequireAppKeyForAuth = null);

/// <param name="Provision">Creates the sample schema (only for modules that run inside D2D).</param>
/// <param name="AllowCredentialAccess">Lets the app's backend fetch the connection string from D2D.</param>
/// <param name="Verify">Test the connection from D2D before saving. Turn off when D2D cannot reach the database itself.</param>
public sealed record SetDatabaseRequest(
    DbProvider Provider,
    [Required] string ConnectionString,
    bool Provision = false,
    bool AllowCredentialAccess = false,
    bool Verify = true);

public sealed record UpsertLineChannelRequest(
    LineChannelType Type,
    [Required, MaxLength(32)] string ChannelId,
    [Required, MaxLength(64)] string ProviderId,
    string? ChannelSecret,
    string? AccessToken,
    [Url] string? WebhookForwardUrl,
    LineTokenMode? TokenMode = null,
    string? AssertionKeyId = null,
    string? AssertionPrivateKey = null,
    bool AllowCredentialAccess = false,
    [MaxLength(64)] string? LiffId = null,
    string? JobsKey = null,
    bool IsActive = true);

public sealed record LineChannelSummary(
    string ChannelId,
    string ProviderId,
    LineChannelType Type,
    LineTokenMode TokenMode,
    bool HasAccessToken,
    bool AllowCredentialAccess,
    DateTime? IssuedTokenExpiresAt,
    string? WebhookForwardUrl,
    string? WebhookPath,
    string? WebhookUrl,
    bool IsActive,
    string? AssertionKeyId,
    bool HasAssertionPrivateKey,
    string? LiffId,
    bool HasJobsKey,
    DateTime UpdatedAt);

public sealed record AppResponse(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    bool AllowSelfRegistration,
    bool RequireAppKeyForAuth,
    IReadOnlyList<string> RedirectUris,
    DbProvider? DatabaseProvider,
    bool DatabaseCredentialAccess,
    IReadOnlyList<LineChannelSummary> LineChannels);

public sealed record AppWithKeyResponse(AppResponse App, string ApiKey);

/// <summary>Platform administration of apps: registration, database, LINE channels, API keys.</summary>
public sealed class AppAdminService(
    MasterDbContext db,
    ISecretProtector secrets,
    IAppRegistry registry,
    ILineMessagingClient messaging,
    IOptions<ServerOptions> server,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<AppResponse>> ListAsync(CancellationToken ct)
    {
        var apps = await WithDetails(db.Apps.AsNoTracking()).OrderBy(a => a.Code).ToListAsync(ct);
        return apps.Select(ToResponse).ToList();
    }

    public async Task<AppResponse> GetAsync(Guid id, CancellationToken ct) => ToResponse(await LoadAsync(id, ct));

    public async Task<AppWithKeyResponse> CreateAsync(CreateAppRequest request, CancellationToken ct)
    {
        var code = request.Code.ToLowerInvariant();
        if (await db.Apps.AnyAsync(a => a.Code == code, ct)) throw ApiException.Conflict("App code already exists");

        var apiKey = NewApiKey();
        var app = new App
        {
            Code = code,
            Name = request.Name.Trim(),
            AllowSelfRegistration = request.AllowSelfRegistration,
            RequireAppKeyForAuth = request.RequireAppKeyForAuth,
            ApiKeyHash = TokenHasher.Sha256(apiKey),
        };
        foreach (var uri in ValidateRedirectUris(request.RedirectUris))
            app.RedirectUris.Add(new AppRedirectUri { AppId = app.Id, Uri = uri });

        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);
        return new AppWithKeyResponse(ToResponse(app), apiKey);
    }

    public async Task<AppResponse> UpdateAsync(Guid id, UpdateAppRequest request, CancellationToken ct)
    {
        var app = await LoadAsync(id, ct);
        app.Name = request.Name.Trim();
        app.IsActive = request.IsActive;
        app.AllowSelfRegistration = request.AllowSelfRegistration;
        if (request.RequireAppKeyForAuth is { } requireKey) app.RequireAppKeyForAuth = requireKey;

        if (request.RedirectUris is not null)
        {
            var wanted = ValidateRedirectUris(request.RedirectUris);
            db.AppRedirectUris.RemoveRange(app.RedirectUris.Where(r => !wanted.Contains(r.Uri)));
            foreach (var uri in wanted.Where(u => app.RedirectUris.All(r => r.Uri != u)))
                db.AppRedirectUris.Add(new AppRedirectUri { AppId = app.Id, Uri = uri });
        }

        await db.SaveChangesAsync(ct);
        registry.Invalidate(id);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Deletes the app with its database settings, redirect URIs, LINE channels, memberships, sessions and webhook inbox.
    /// The people (users) stay: they may belong to other apps.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) throw ApiException.NotFound("App not found");

        // These tables have no foreign key to apps, so they are not covered by the cascade.
        await db.RefreshTokens.Where(t => t.AppId == id).ExecuteDeleteAsync(ct);
        await db.LineWebhookEvents.Where(e => e.AppId == id).ExecuteDeleteAsync(ct);
        await db.UserApps.Where(m => m.AppId == id).ExecuteDeleteAsync(ct);
        await db.Apps.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        registry.Invalidate(id);
    }

    public async Task<string> RotateApiKeyAsync(Guid id, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("App not found");
        var apiKey = NewApiKey();
        app.ApiKeyHash = TokenHasher.Sha256(apiKey);
        await db.SaveChangesAsync(ct);
        registry.Invalidate(id);
        return apiKey;
    }

    public async Task SetDatabaseAsync(Guid id, SetDatabaseRequest request, CancellationToken ct)
    {
        var app = await db.Apps.Include(a => a.Database).FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw ApiException.NotFound("App not found");

        // Verify (or provision) before saving so a bad connection string never reaches the registry.
        if (request.Verify || request.Provision) await PrepareDatabaseAsync(request, ct);

        app.Database ??= new AppDatabase { AppId = app.Id };
        app.Database.Provider = request.Provider;
        app.Database.ConnectionStringEncrypted = secrets.Protect(request.ConnectionString);
        app.Database.AllowCredentialAccess = request.AllowCredentialAccess;
        app.Database.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        registry.Invalidate(id);
    }

    /// <summary>The stored connection string in clear text, for the admin console's edit form. The caller must be an admin.</summary>
    public async Task<string?> RevealDatabaseConnectionAsync(Guid id, CancellationToken ct)
    {
        var app = await LoadAsync(id, ct);
        return app.Database is null ? null : secrets.Unprotect(app.Database.ConnectionStringEncrypted);
    }

    public async Task<LineChannelSummary> UpsertLineChannelAsync(Guid id, UpsertLineChannelRequest request, CancellationToken ct)
    {
        if (!await db.Apps.AnyAsync(a => a.Id == id, ct)) throw ApiException.NotFound("App not found");

        var channel = await db.LineChannels.FirstOrDefaultAsync(x => x.ChannelId == request.ChannelId, ct);
        if (channel is not null && channel.AppId != id) throw ApiException.Conflict("LINE channel belongs to another app");
        // LINE cannot move a channel to another provider, and user identities are keyed by provider.
        if (channel is not null && channel.ProviderId != request.ProviderId)
            throw ApiException.Conflict("ProviderId of an existing channel cannot change");
        if (channel is null)
        {
            channel = new LineChannel { AppId = id, ChannelId = request.ChannelId, ProviderId = request.ProviderId };
            db.LineChannels.Add(channel);
        }

        // Secrets are never shown again, so a blank value on an existing channel keeps what is stored.
        var existing = channel.ChannelSecretEncrypted is not null;
        if (string.IsNullOrEmpty(request.ChannelSecret) && !existing) throw ApiException.BadRequest("ChannelSecret is required");
        var keepToken = string.IsNullOrEmpty(request.AccessToken) && channel.AccessTokenEncrypted is not null;
        var keepKey = string.IsNullOrEmpty(request.AssertionPrivateKey) && channel.AssertionPrivateKeyEncrypted is not null;
        var mode = ResolveTokenMode(request, keepToken, keepKey);

        channel.Type = request.Type;
        if (!string.IsNullOrEmpty(request.ChannelSecret)) channel.ChannelSecretEncrypted = secrets.Protect(request.ChannelSecret);
        channel.TokenMode = mode;
        if (mode != LineTokenMode.Static) channel.AccessTokenEncrypted = null;
        else if (!string.IsNullOrEmpty(request.AccessToken)) channel.AccessTokenEncrypted = secrets.Protect(request.AccessToken);
        if (mode != LineTokenMode.JwtAssertion)
        {
            channel.AssertionKeyId = null;
            channel.AssertionPrivateKeyEncrypted = null;
        }
        else
        {
            channel.AssertionKeyId = string.IsNullOrEmpty(request.AssertionKeyId) ? channel.AssertionKeyId : request.AssertionKeyId;
            if (!string.IsNullOrEmpty(request.AssertionPrivateKey)) channel.AssertionPrivateKeyEncrypted = secrets.Protect(request.AssertionPrivateKey);
        }
        // Credentials changed: never reuse a token issued with the old ones.
        channel.IssuedAccessTokenEncrypted = null;
        channel.IssuedAccessTokenExpiresAt = null;
        channel.WebhookForwardUrl = string.IsNullOrWhiteSpace(request.WebhookForwardUrl) ? null : request.WebhookForwardUrl;
        channel.AllowCredentialAccess = request.AllowCredentialAccess;
        channel.LiffId = string.IsNullOrWhiteSpace(request.LiffId) ? null : request.LiffId.Trim();
        if (!string.IsNullOrEmpty(request.JobsKey)) channel.JobsKeyEncrypted = secrets.Protect(request.JobsKey);
        channel.IsActive = request.IsActive;
        channel.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        registry.Invalidate(id);
        return ToSummary(channel);
    }

    /// <summary>Obtains the channel's access token and calls LINE's bot info endpoint to prove the setup works.</summary>
    public async Task<JsonElement?> VerifyLineChannelAsync(Guid id, string channelId, CancellationToken ct)
    {
        var channel = await registry.FindLineChannelAsync(channelId, ct);
        if (channel is null || channel.AppId != id || channel.Type != LineChannelType.Messaging)
            throw ApiException.NotFound("LINE Messaging channel not found for this app");

        return (await messaging.GetBotInfoAsync(channel, ct)).Body;
    }

    public async Task DeleteLineChannelAsync(Guid id, string channelId, CancellationToken ct)
    {
        var deleted = await db.LineChannels.Where(x => x.AppId == id && x.ChannelId == channelId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw ApiException.NotFound("LINE channel not found");
        registry.Invalidate(id);
    }

    private async Task<App> LoadAsync(Guid id, CancellationToken ct) =>
        await WithDetails(db.Apps).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("App not found");

    private static IQueryable<App> WithDetails(IQueryable<App> apps) =>
        apps.Include(a => a.Database).Include(a => a.RedirectUris).Include(a => a.LineChannels).AsSplitQuery();

    private static async Task PrepareDatabaseAsync(SetDatabaseRequest request, CancellationToken ct)
    {
        bool ok;
        try
        {
            var builder = new DbContextOptionsBuilder<AppDataDbContext>();
            builder.UseProvider(request.Provider, request.ConnectionString);
            await using var probe = new AppDataDbContext(builder.Options);

            if (request.Provision)
            {
                // Sample module only: creates the database if missing. Real modules ship provider-specific migrations.
                await probe.Database.EnsureCreatedAsync(ct);
                ok = true;
            }
            else
            {
                ok = await probe.Database.CanConnectAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ApiException.BadRequest("Cannot connect to database", ex.Message);
        }

        if (!ok) throw ApiException.BadRequest("Cannot connect to database", "Server unreachable or database does not exist.");
    }

    private static HashSet<string> ValidateRedirectUris(IEnumerable<string>? uris)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in uris ?? [])
        {
            // Absolute URIs only; custom schemes (myapp://callback) are allowed for mobile apps.
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment))
                throw ApiException.BadRequest("Invalid redirect URI", uri);
            result.Add(uri);
        }
        return result;
    }

    private static string NewApiKey() => "d2dk_" + TokenHasher.NewToken(32);

    private AppResponse ToResponse(App app) => new(
        app.Id,
        app.Code,
        app.Name,
        app.IsActive,
        app.AllowSelfRegistration,
        app.RequireAppKeyForAuth,
        app.RedirectUris.Select(r => r.Uri).OrderBy(u => u).ToList(),
        app.Database?.Provider,
        app.Database?.AllowCredentialAccess ?? false,
        app.LineChannels.Select(ToSummary).ToList());

    /// <summary>
    /// Messaging channels default to Static when a token is supplied, otherwise Stateless (needs only id + secret).
    /// </summary>
    private static LineTokenMode ResolveTokenMode(UpsertLineChannelRequest request, bool keepToken, bool keepKey)
    {
        if (request.Type == LineChannelType.Login) return LineTokenMode.Static;

        var mode = request.TokenMode
            ?? (string.IsNullOrEmpty(request.AccessToken) && !keepToken ? LineTokenMode.Stateless : LineTokenMode.Static);

        switch (mode)
        {
            case LineTokenMode.Static when string.IsNullOrEmpty(request.AccessToken) && !keepToken:
                throw ApiException.BadRequest("AccessToken is required for Static token mode");
            case LineTokenMode.JwtAssertion:
                if (string.IsNullOrEmpty(request.AssertionKeyId) || (string.IsNullOrEmpty(request.AssertionPrivateKey) && !keepKey))
                    throw ApiException.BadRequest("AssertionKeyId and AssertionPrivateKey are required for JwtAssertion token mode");
                if (!string.IsNullOrEmpty(request.AssertionPrivateKey) && !IsPrivateJwk(request.AssertionPrivateKey))
                    throw ApiException.BadRequest("AssertionPrivateKey must be the private key as JWK JSON");
                break;
        }
        return mode;
    }

    private static bool IsPrivateJwk(string json)
    {
        try
        {
            var key = new JsonWebKey(json);
            return key.Kty == "RSA" && !string.IsNullOrEmpty(key.D);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            return false;
        }
    }

    private LineChannelSummary ToSummary(LineChannel channel)
    {
        var webhookPath = channel.Type == LineChannelType.Messaging ? $"/webhooks/line/{channel.ChannelId}" : null;
        var publicBase = server.Value.PublicBaseUrl?.TrimEnd('/');

        return new LineChannelSummary(
            channel.ChannelId,
            channel.ProviderId,
            channel.Type,
            channel.TokenMode,
            channel.AccessTokenEncrypted is not null,
            channel.AllowCredentialAccess,
            channel.IssuedAccessTokenExpiresAt,
            channel.WebhookForwardUrl,
            webhookPath,
            // The URL to paste into the LINE console (includes the IIS application path).
            webhookPath is not null && !string.IsNullOrEmpty(publicBase) ? publicBase + webhookPath : null,
            channel.IsActive,
            channel.AssertionKeyId,
            channel.AssertionPrivateKeyEncrypted is not null,
            channel.LiffId,
            channel.JobsKeyEncrypted is not null,
            channel.UpdatedAt);
    }
}
