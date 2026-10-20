using D2D.Core.Entities;

namespace D2D.Core.Tenancy;

/// <summary>Cached, read-only view of an app used during a request.</summary>
public sealed record TenantInfo(
    Guid Id, string Code, string Name, bool IsActive, bool AllowSelfRegistration, bool RequireAppKeyForAuth = false);

/// <summary>Decrypted database settings of an app. <see cref="Version"/> changes whenever settings change.</summary>
public sealed record DatabaseConfig(DbProvider Provider, string ConnectionString, DateTime Version, bool AllowCredentialAccess = false);

/// <summary>Decrypted LINE channel settings.</summary>
public sealed record LineChannelConfig(
    Guid AppId,
    LineChannelType Type,
    string ChannelId,
    string ProviderId,
    string ChannelSecret,
    string? WebhookForwardUrl,
    LineTokenMode TokenMode = LineTokenMode.Static,
    string? StaticAccessToken = null,
    string? AssertionKeyId = null,
    string? AssertionPrivateKey = null,
    bool AllowCredentialAccess = false);

/// <summary>The app (tenant) the current request or background job is running for.</summary>
public interface ITenantContext
{
    TenantInfo? Current { get; }

    /// <summary>Current app; throws 400 when the request did not identify one.</summary>
    TenantInfo Required { get; }

    void Set(TenantInfo tenant);
}

/// <summary>Cached lookups of app configuration stored in the master database.</summary>
public interface IAppRegistry
{
    Task<TenantInfo?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> VerifyApiKeyAsync(string code, string apiKey, CancellationToken ct = default);
    Task<DatabaseConfig?> GetDatabaseAsync(Guid appId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRedirectUrisAsync(Guid appId, CancellationToken ct = default);
    Task<LineChannelConfig?> FindLineChannelAsync(string channelId, CancellationToken ct = default);
    Task<LineChannelConfig?> GetLineChannelAsync(Guid appId, LineChannelType type, CancellationToken ct = default);

    /// <summary>Drops every cached entry of an app after its configuration changed.</summary>
    void Invalidate(Guid appId);
}
