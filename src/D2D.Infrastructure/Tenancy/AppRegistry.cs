using System.Collections.Concurrent;
using D2D.Core.Abstractions;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace D2D.Infrastructure.Tenancy;

/// <summary>
/// Reads app configuration from the master DB and caches it in memory for a short time.
/// Every cache entry of an app shares one eviction token so <see cref="Invalidate"/> drops them all.
/// </summary>
public sealed class AppRegistry(IServiceScopeFactory scopeFactory, IMemoryCache cache, ISecretProtector secrets) : IAppRegistry
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _evictions = new();

    private sealed record AppEntry(TenantInfo Info, string ApiKeyHash);

    public async Task<TenantInfo?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        (await GetEntryByCodeAsync(code, ct))?.Info;

    public async Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await GetOrLoadAsync($"app:id:{id}", id, (db, token) => ProjectApps(db.Apps.Where(a => a.Id == id)).FirstOrDefaultAsync(token), ct);
        return entry?.Info;
    }

    public async Task<bool> VerifyApiKeyAsync(string code, string apiKey, CancellationToken ct = default)
    {
        var entry = await GetEntryByCodeAsync(code, ct);
        return entry is not null && TokenHasher.FixedTimeEquals(entry.ApiKeyHash, TokenHasher.Sha256(apiKey));
    }

    public Task<DatabaseConfig?> GetDatabaseAsync(Guid appId, CancellationToken ct = default) =>
        GetOrLoadAsync($"app:db:{appId}", appId, async (db, token) =>
        {
            var row = await db.AppDatabases.AsNoTracking().FirstOrDefaultAsync(x => x.AppId == appId, token);
            return row is null ? null : new DatabaseConfig(row.Provider, secrets.Unprotect(row.ConnectionStringEncrypted), row.UpdatedAt, row.AllowCredentialAccess);
        }, ct);

    public async Task<IReadOnlyList<string>> GetRedirectUrisAsync(Guid appId, CancellationToken ct = default) =>
        await GetOrLoadAsync<IReadOnlyList<string>>($"app:redirects:{appId}", appId, async (db, token) =>
            await db.AppRedirectUris.AsNoTracking().Where(x => x.AppId == appId).Select(x => x.Uri).ToListAsync(token), ct)
        ?? [];

    public async Task<LineChannelConfig?> FindLineChannelAsync(string channelId, CancellationToken ct = default)
    {
        var key = $"line:channel:{channelId}";
        if (cache.TryGetValue(key, out LineChannelConfig? cached)) return cached;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var row = await db.LineChannels.AsNoTracking().FirstOrDefaultAsync(x => x.ChannelId == channelId && x.IsActive, ct);
        if (row is null) return null;

        var config = ToConfig(row);
        Store(key, config, row.AppId);
        return config;
    }

    public Task<LineChannelConfig?> GetLineChannelAsync(Guid appId, LineChannelType type, CancellationToken ct = default) =>
        GetOrLoadAsync($"line:app:{appId}:{type}", appId, async (db, token) =>
        {
            var row = await db.LineChannels.AsNoTracking()
                .Where(x => x.AppId == appId && x.Type == type && x.IsActive)
                .OrderBy(x => x.UpdatedAt)
                .FirstOrDefaultAsync(token);
            return row is null ? null : ToConfig(row);
        }, ct);

    public void Invalidate(Guid appId)
    {
        if (_evictions.TryRemove(appId, out var cts)) cts.Cancel();
    }

    private Task<AppEntry?> GetEntryByCodeAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToLowerInvariant();
        var key = $"app:code:{normalized}";
        if (cache.TryGetValue(key, out AppEntry? cached)) return Task.FromResult(cached);
        return LoadEntryByCodeAsync(key, normalized, ct);
    }

    private async Task<AppEntry?> LoadEntryByCodeAsync(string key, string code, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var entry = await ProjectApps(db.Apps.Where(a => a.Code == code)).FirstOrDefaultAsync(ct);
        if (entry is not null) Store(key, entry, entry.Info.Id);
        return entry;
    }

    private static IQueryable<AppEntry> ProjectApps(IQueryable<App> apps) =>
        apps.AsNoTracking().Select(a => new AppEntry(
            new TenantInfo(a.Id, a.Code, a.Name, a.IsActive, a.AllowSelfRegistration, a.RequireAppKeyForAuth),
            a.ApiKeyHash));

    private async Task<T?> GetOrLoadAsync<T>(string key, Guid appId, Func<MasterDbContext, CancellationToken, Task<T?>> load, CancellationToken ct)
        where T : class
    {
        if (cache.TryGetValue(key, out T? cached)) return cached;

        await using var scope = scopeFactory.CreateAsyncScope();
        var value = await load(scope.ServiceProvider.GetRequiredService<MasterDbContext>(), ct);
        if (value is not null) Store(key, value, appId);
        return value;
    }

    private void Store<T>(string key, T value, Guid appId)
    {
        var token = _evictions.GetOrAdd(appId, _ => new CancellationTokenSource()).Token;
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl }
            .AddExpirationToken(new CancellationChangeToken(token));
        cache.Set(key, value, options);
    }

    private LineChannelConfig ToConfig(LineChannel row) => new(
        row.AppId,
        row.Type,
        row.ChannelId,
        row.ProviderId,
        secrets.Unprotect(row.ChannelSecretEncrypted),
        row.WebhookForwardUrl,
        row.TokenMode,
        Decrypt(row.AccessTokenEncrypted),
        row.AssertionKeyId,
        Decrypt(row.AssertionPrivateKeyEncrypted),
        row.AllowCredentialAccess);

    private string? Decrypt(string? ciphertext) => ciphertext is null ? null : secrets.Unprotect(ciphertext);
}
