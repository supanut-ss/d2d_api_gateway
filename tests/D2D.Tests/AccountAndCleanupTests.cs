using D2D.Core;
using D2D.Core.Entities;
using D2D.Infrastructure.Admin;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Maintenance;
using D2D.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace D2D.Tests;

public sealed class AccountAndCleanupTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MasterDbContext _db;

    public AccountAndCleanupTests()
    {
        _connection.Open();
        _db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Cleanup_removes_only_data_that_is_no_longer_needed()
    {
        var now = DateTime.UtcNow;
        _db.LineWebhookEvents.AddRange(
            Event("old-processed", WebhookEventStatus.Processed, processedAt: now.AddDays(-20)),
            Event("new-processed", WebhookEventStatus.Processed, processedAt: now.AddDays(-1)),
            Event("old-failed", WebhookEventStatus.Failed, receivedAt: now.AddDays(-100)),
            Event("new-failed", WebhookEventStatus.Failed, receivedAt: now.AddDays(-10)),
            Event("old-pending", WebhookEventStatus.Pending, receivedAt: now.AddDays(-200)));
        _db.RefreshTokens.AddRange(
            Token("expired", expiresAt: now.AddDays(-5), revokedAt: null),
            Token("expired-and-revoked", expiresAt: now.AddDays(-5), revokedAt: now.AddDays(-6)),
            Token("revoked-but-valid", expiresAt: now.AddDays(5), revokedAt: now.AddDays(-1)),
            Token("active", expiresAt: now.AddDays(5), revokedAt: null));
        await _db.SaveChangesAsync();

        var result = await new DataCleanup(_db, Microsoft.Extensions.Options.Options.Create(new MaintenanceOptions()), TimeProvider.System)
            .RunAsync(default);

        Assert.Equal(new CleanupResult(WebhookEvents: 2, RefreshTokens: 2), result);
        Assert.Equal(["new-failed", "new-processed", "old-pending"], (await _db.LineWebhookEvents.Select(e => e.WebhookEventId).ToListAsync()).Order());
        // A revoked token must stay until it expires so replaying it can still be detected.
        Assert.Equal(["active", "revoked-but-valid"], (await _db.RefreshTokens.Select(t => t.TokenHash).ToListAsync()).Order());
    }

    [Fact]
    public async Task Unlinking_keeps_at_least_one_way_to_sign_in()
    {
        var auth = new AuthService(_db, null!, null!, TimeProvider.System);

        var withPassword = NewUser(password: true, scopes: ["P1"]);
        var lineOnly = NewUser(password: false, scopes: ["P1", "P2"]);
        _db.Users.AddRange(withPassword, lineOnly);
        await _db.SaveChangesAsync();

        await auth.UnlinkAsync(withPassword.Id, "line", "P1", default); // password remains
        await auth.UnlinkAsync(lineOnly.Id, "LINE", "P1", default);     // provider name is case-insensitive; P2 remains

        var last = await Assert.ThrowsAsync<ApiException>(() => auth.UnlinkAsync(lineOnly.Id, "line", "P2", default));
        Assert.Equal(409, last.StatusCode);
        var missing = await Assert.ThrowsAsync<ApiException>(() => auth.UnlinkAsync(lineOnly.Id, "line", "nope", default));
        Assert.Equal(404, missing.StatusCode);
        Assert.Empty(await _db.ExternalLogins.Where(l => l.UserId == withPassword.Id).ToListAsync());
        Assert.Single(await _db.ExternalLogins.Where(l => l.UserId == lineOnly.Id).ToListAsync());
    }

    [Fact]
    public void Roles_are_normalized_and_privileged_names_are_refused()
    {
        Assert.Equal(["user"], MemberService.NormalizeRoles(null));
        Assert.Equal(["user"], MemberService.NormalizeRoles(["  "]));
        Assert.Equal(["editor", "billing:admin"], MemberService.NormalizeRoles([" Editor ", "editor", "billing:admin"]));

        foreach (var bad in new[] { "super_admin", "app_server", "has space", "-lead", new string('a', 33) })
            Assert.Equal(400, Assert.Throws<ApiException>(() => MemberService.NormalizeRoles([bad])).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => MemberService.NormalizeRoles(Enumerable.Range(0, 11).Select(i => $"r{i}").ToArray())).StatusCode);
    }

    private static LineWebhookEvent Event(string id, WebhookEventStatus status, DateTime? receivedAt = null, DateTime? processedAt = null) => new()
    {
        AppId = Guid.NewGuid(), ChannelId = "1", WebhookEventId = id, EventType = "message", Payload = "{}",
        Status = status, ReceivedAt = receivedAt ?? DateTime.UtcNow, ProcessedAt = processedAt,
    };

    private static RefreshToken Token(string hash, DateTime expiresAt, DateTime? revokedAt) => new()
    {
        UserId = Guid.NewGuid(), AppId = Guid.NewGuid(), FamilyId = Guid.NewGuid(), TokenHash = hash,
        ExpiresAt = expiresAt, RevokedAt = revokedAt,
    };

    private static User NewUser(bool password, string[] scopes)
    {
        var user = new User { DisplayName = "u", PasswordHash = password ? "hash" : null };
        foreach (var scope in scopes)
            user.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = "line", ProviderScope = scope, ProviderKey = Guid.NewGuid().ToString("N"), ChannelId = "1" });
        return user;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
