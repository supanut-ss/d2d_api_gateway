using D2D.Core;
using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Admin;

public sealed record WebhookCounts(int Pending, int Processing, int Processed, int Failed);

public sealed record AppUsage(Guid AppId, string Code, string Name, bool IsActive, int Members, int Channels, int ActiveSessions);

public sealed record PlatformStats(
    int Apps, int ActiveApps, int Users, int ActiveUsers, int Admins, int LineChannels, int ActiveSessions,
    int WebhookEventsLast24h, WebhookCounts Webhooks, IReadOnlyList<AppUsage> PerApp);

public sealed record PlatformUserApp(Guid AppId, string Code, IReadOnlyList<string> Roles);

public sealed record PlatformUser(
    Guid Id, string DisplayName, string? Email, bool IsActive, DateTime CreatedAt, DateTime? LastLoginAt,
    IReadOnlyList<PlatformUserApp> Apps, IReadOnlyList<string> LoginMethods);

public sealed record PlatformUserPage(IReadOnlyList<PlatformUser> Items, int Total);

public sealed record PlatformIdentity(string Provider, string ProviderScope, string ChannelId, DateTime LinkedAt);

public sealed record PlatformUserDetail(PlatformUser User, IReadOnlyList<PlatformIdentity> Identities, int ActiveSessions);

public sealed record WebhookEventRow(
    Guid Id, Guid AppId, string AppCode, string ChannelId, string EventType, string? SourceUserId, WebhookEventStatus Status,
    int Attempts, string? LastError, bool IsRedelivery, DateTime ReceivedAt, DateTime? ProcessedAt);

public sealed record WebhookEventPage(IReadOnlyList<WebhookEventRow> Items, int Total);

public sealed record WebhookEventDetail(WebhookEventRow Event, string Payload);

public sealed record AuditRow(Guid Id, DateTime At, string Actor, string Method, string Path, string? Ip);

public sealed record AuditPage(IReadOnlyList<AuditRow> Items, int Total);

/// <summary>Read models for the admin console: overview numbers, all users, the LINE webhook inbox and the audit trail.</summary>
public sealed class PlatformQueryService(MasterDbContext db, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<PlatformStats> GetStatsAsync(CancellationToken ct)
    {
        var now = Now;
        var since = now.AddHours(-24);
        // Admin sessions live in the same table under the empty app id.
        var liveSessions = db.RefreshTokens.Where(t => t.RevokedAt == null && t.ExpiresAt > now && t.AppId != AdminAuthService.SessionAppId);

        var byStatus = await db.LineWebhookEvents.GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(WebhookEventStatus s) => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var members = (await db.UserApps.GroupBy(m => m.AppId).Select(g => new { AppId = g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.AppId, x => x.Count);
        var channels = (await db.LineChannels.GroupBy(c => c.AppId).Select(g => new { AppId = g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.AppId, x => x.Count);
        var sessions = (await liveSessions.GroupBy(t => t.AppId).Select(g => new { AppId = g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.AppId, x => x.Count);
        var apps = await db.Apps.AsNoTracking().OrderBy(a => a.Code).Select(a => new { a.Id, a.Code, a.Name, a.IsActive }).ToListAsync(ct);

        return new PlatformStats(
            Apps: apps.Count,
            ActiveApps: apps.Count(a => a.IsActive),
            Users: await db.Users.CountAsync(ct),
            ActiveUsers: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.AdminUsers.CountAsync(a => a.IsActive, ct),
            LineChannels: channels.Values.Sum(),
            ActiveSessions: sessions.Values.Sum(),
            WebhookEventsLast24h: await db.LineWebhookEvents.CountAsync(e => e.ReceivedAt >= since, ct),
            Webhooks: new WebhookCounts(
                Count(WebhookEventStatus.Pending), Count(WebhookEventStatus.Processing),
                Count(WebhookEventStatus.Processed), Count(WebhookEventStatus.Failed)),
            PerApp: apps.Select(a => new AppUsage(
                a.Id, a.Code, a.Name, a.IsActive,
                members.GetValueOrDefault(a.Id), channels.GetValueOrDefault(a.Id), sessions.GetValueOrDefault(a.Id))).ToList());
    }

    public async Task<PlatformUserPage> ListUsersAsync(string? search, int skip, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var normalized = term.ToUpperInvariant();
            query = query.Where(u => u.DisplayName.Contains(term) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized)));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id).Skip(skip).Take(take).ToListAsync(ct);
        return new PlatformUserPage(await ToUsersAsync(page, ct), total);
    }

    public async Task<PlatformUserDetail> GetUserAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User not found");
        var now = Now;
        var identities = await db.ExternalLogins.AsNoTracking().Where(l => l.UserId == id).OrderBy(l => l.CreatedAt)
            .Select(l => new PlatformIdentity(l.Provider, l.ProviderScope, l.ChannelId, l.CreatedAt)).ToListAsync(ct);
        var sessions = await db.RefreshTokens.CountAsync(t => t.UserId == id && t.RevokedAt == null && t.ExpiresAt > now, ct);
        return new PlatformUserDetail((await ToUsersAsync([user], ct))[0], identities, sessions);
    }

    public async Task<WebhookEventPage> ListWebhookEventsAsync(
        WebhookEventStatus? status, Guid? appId, string? search, int skip, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var query = db.LineWebhookEvents.AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(e => e.Status == s);
        if (appId is { } a) query = query.Where(e => e.AppId == a);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(e => e.EventType.Contains(term) || e.ChannelId.Contains(term) ||
                                     (e.SourceUserId != null && e.SourceUserId.Contains(term)) || e.WebhookEventId.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(e => e.ReceivedAt).ThenBy(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        var codes = await AppCodesAsync(page.Select(e => e.AppId), ct);
        return new WebhookEventPage(page.Select(e => ToRow(e, codes)).ToList(), total);
    }

    public async Task<WebhookEventDetail> GetWebhookEventAsync(Guid id, CancellationToken ct)
    {
        var e = await db.LineWebhookEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Event not found");
        return new WebhookEventDetail(ToRow(e, await AppCodesAsync([e.AppId], ct)), e.Payload);
    }

    /// <summary>Puts a failed (or stuck) event back in the queue so the background worker handles it again.</summary>
    public async Task RetryWebhookEventAsync(Guid id, CancellationToken ct)
    {
        var now = Now;
        var updated = await db.LineWebhookEvents
            .Where(e => e.Id == id && (e.Status == WebhookEventStatus.Failed || e.Status == WebhookEventStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, WebhookEventStatus.Pending)
                .SetProperty(e => e.Attempts, 0)
                .SetProperty(e => e.NextAttemptAt, now)
                .SetProperty(e => e.LockedUntil, (DateTime?)null), ct);
        if (updated == 0) throw ApiException.Conflict("Only failed or pending events can be retried");
    }

    public async Task<AuditPage> ListAuditAsync(string? search, int skip, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l => l.Actor.Contains(term) || l.Path.Contains(term) || l.Method.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(l => l.At).ThenBy(l => l.Id).Skip(skip).Take(take)
            .Select(l => new AuditRow(l.Id, l.At, l.Actor, l.Method, l.Path, l.Ip)).ToListAsync(ct);
        return new AuditPage(page, total);
    }

    private async Task<List<PlatformUser>> ToUsersAsync(List<User> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var memberships = (await db.UserApps.AsNoTracking().Where(m => ids.Contains(m.UserId))
                .Join(db.Apps, m => m.AppId, a => a.Id, (m, a) => new { m.UserId, m.AppId, a.Code, m.Roles }).ToListAsync(ct))
            .ToLookup(x => x.UserId);
        var providers = (await db.ExternalLogins.AsNoTracking().Where(l => ids.Contains(l.UserId))
                .Select(l => new { l.UserId, l.Provider }).ToListAsync(ct))
            .ToLookup(x => x.UserId, x => x.Provider);

        return users.Select(u =>
        {
            var methods = new List<string>();
            if (u.PasswordHash is not null) methods.Add("password");
            methods.AddRange(providers[u.Id].Distinct());
            return new PlatformUser(
                u.Id, u.DisplayName, u.Email, u.IsActive, u.CreatedAt, u.LastLoginAt,
                memberships[u.Id].OrderBy(m => m.Code)
                    .Select(m => new PlatformUserApp(m.AppId, m.Code, m.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    .ToList(),
                methods);
        }).ToList();
    }

    private async Task<Dictionary<Guid, string>> AppCodesAsync(IEnumerable<Guid> appIds, CancellationToken ct)
    {
        var ids = appIds.Distinct().ToList();
        return await db.Apps.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code, ct);
    }

    private static WebhookEventRow ToRow(LineWebhookEvent e, Dictionary<Guid, string> codes) => new(
        e.Id, e.AppId, codes.GetValueOrDefault(e.AppId, "(deleted app)"), e.ChannelId, e.EventType, e.SourceUserId,
        e.Status, e.Attempts, e.LastError, e.IsRedelivery, e.ReceivedAt, e.ProcessedAt);
}
