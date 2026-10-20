using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace D2D.Tests.Integration;

/// <summary>The admin console API: admin sign-in, roles, audit trail and the platform-wide views.</summary>
[Collection("api")]
public sealed class AdminConsoleTests(ApiFactory factory)
{
    private const string AdminPassword = "Admin-Pass-12345";
    private const string UserPassword = "P@ssw0rd!";
    private const string CookieName = "d2d_admin_session";
    private static readonly (string, string) AdminKey = ("X-Admin-Key", ApiFactory.AdminKey);
    private static readonly (string, string) Ajax = ("X-Requested-With", "d2d-admin");

    private sealed record Reply(HttpStatusCode Status, JsonElement Json, string? SessionCookie)
    {
        public string? CookieValue => SessionCookie?.Split(';')[0][(CookieName.Length + 1)..];
    }

    [Fact]
    public async Task Admin_signs_in_and_the_two_kinds_of_token_stay_apart()
    {
        var (email, _) = await CreateAdminAsync();
        var login = await LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, login.Status);
        var access = login.Json.GetProperty("accessToken").GetString()!;
        Assert.Equal(email, login.Json.GetProperty("admin").GetProperty("email").GetString());

        // The refresh token only travels as a locked-down cookie, never in the body.
        Assert.False(login.Json.TryGetProperty("refreshToken", out _));
        var cookie = login.SessionCookie!.ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/v1/admin/auth", cookie);

        var me = await SendAsync(HttpMethod.Get, "/api/v1/admin/auth/me", headers: Bearer(access));
        Assert.Equal(email, me.Json.GetProperty("email").GetString());
        Assert.Equal("Admin", me.Json.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps", headers: Bearer(access))).Status);

        // An app user's token does not open the admin API, and an admin token does not act as an app user.
        var app = await CreateAppAsync();
        var appToken = (await SendAsync(HttpMethod.Post, "/api/v1/auth/register",
            new { email = NewEmail(), password = UserPassword, displayName = "U" }, ("X-App-Id", app.Code))).Json.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps", headers: Bearer(appToken))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(access))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps")).Status);

        // The static admin key keeps working as a break-glass option.
        var viaKey = await SendAsync(HttpMethod.Get, "/api/v1/admin/auth/me", headers: AdminKey);
        Assert.True(viaKey.Json.GetProperty("viaAdminKey").GetBoolean());
    }

    [Fact]
    public async Task Wrong_password_unknown_email_and_disabled_admin_are_rejected()
    {
        var (email, _) = await CreateAdminAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, "wrong-password-123")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(NewEmail())).Status);

        var admin = (await SendAsync(HttpMethod.Get, "/api/v1/admin/admins", headers: AdminKey)).Json
            .EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
        var id = admin.GetProperty("id").GetGuid();
        var session = await LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/admins/{id}", new { isActive = false }, AdminKey)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email)).Status);
        // Disabling also ends the sessions that were already open.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.CookieValue!)).Status);
    }

    [Fact]
    public async Task Viewer_can_look_at_everything_but_change_nothing()
    {
        var (email, _) = await CreateAdminAsync("Viewer");
        var viewer = Bearer((await LoginAsync(email)).Json.GetProperty("accessToken").GetString()!);
        var app = await CreateAppAsync();

        foreach (var path in new[] { "apps", $"apps/{app.Id}", $"apps/{app.Id}/members", "stats", "users", "webhook-events", "audit", "admins" })
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/{path}", headers: viewer)).Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/v1/admin/apps", new { code = "x" + Guid.NewGuid().ToString("N")[..8], name = "n" }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}", new { name = "n", isActive = true, allowSelfRegistration = true }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}", headers: viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/api-key/rotate", headers: viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/v1/admin/admins", new { email = NewEmail(), displayName = "x", password = AdminPassword }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{Guid.NewGuid()}/active", new { isActive = false }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/webhook-events/{Guid.NewGuid()}/retry", headers: viewer)).Status);

        // Nothing changed.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}", headers: AdminKey)).Status);
    }

    [Fact]
    public async Task Session_cookie_rotates_and_reuse_ends_the_session()
    {
        var (email, _) = await CreateAdminAsync();
        var login = await LoginAsync(email);
        var first = login.CookieValue!;

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/admin/auth/refresh", headers: Cookie(first))).Status); // no X-Requested-With

        var second = await RefreshAsync(first);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.NotEqual(first, second.CookieValue);
        Assert.False(string.IsNullOrEmpty(second.Json.GetProperty("accessToken").GetString()));

        // Replaying the old cookie looks like theft: refused, and the newer cookie dies with it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.CookieValue!)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/admin/auth/refresh", headers: Ajax)).Status); // no cookie
    }

    [Fact]
    public async Task Logout_and_password_change_end_the_session()
    {
        var (email, _) = await CreateAdminAsync();
        var one = await LoginAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/api/v1/admin/auth/logout", headers: [Ajax, Cookie(one.CookieValue!)])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(one.CookieValue!)).Status);

        var two = await LoginAsync(email);
        var access = Bearer(two.Json.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/admin/auth/password",
            new { currentPassword = "wrong-current-pass", newPassword = "Another-Pass-12345" }, access)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/api/v1/admin/auth/password",
            new { currentPassword = AdminPassword, newPassword = "Another-Pass-12345" }, access)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(two.CookieValue!)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email)).Status);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, "Another-Pass-12345")).Status);
    }

    [Fact]
    public async Task Admin_accounts_validate_input_and_reject_duplicates()
    {
        var (email, _) = await CreateAdminAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/api/v1/admin/admins",
            new { email = email.ToUpperInvariant(), displayName = "Dup", password = AdminPassword }, AdminKey)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/admin/admins",
            new { email = NewEmail(), displayName = "Short", password = "short" }, AdminKey)).Status);
        Assert.True((await SendAsync(HttpMethod.Get, "/api/v1/admin/auth/status")).Json.TryGetProperty("setupRequired", out _));
    }

    [Fact]
    public async Task Audit_trail_records_who_changed_what_but_not_reads_or_failures()
    {
        var (email, _) = await CreateAdminAsync();
        var admin = Bearer((await LoginAsync(email)).Json.GetProperty("accessToken").GetString()!);

        var code = "a" + Guid.NewGuid().ToString("N")[..10];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/apps", new { code, name = code }, admin);
        var id = created.Json.GetProperty("app").GetProperty("id").GetGuid();
        await SendAsync(HttpMethod.Get, "/api/v1/admin/apps", headers: admin);                           // a read
        await SendAsync(HttpMethod.Post, "/api/v1/admin/apps", new { code, name = "again" }, admin);      // fails with 409
        await SendAsync(HttpMethod.Post, $"/api/v1/admin/apps/{id}/api-key/rotate", headers: AdminKey);   // via the admin key

        var mine = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/audit?search={Uri.EscapeDataString(email)}", headers: admin)).Json;
        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        var row = mine.GetProperty("items")[0];
        Assert.Equal("POST", row.GetProperty("method").GetString());
        Assert.Equal("/api/v1/admin/apps", row.GetProperty("path").GetString());

        var viaKey = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/audit?search={id}", headers: admin)).Json;
        Assert.Equal("admin-key", viaKey.GetProperty("items")[0].GetProperty("actor").GetString());
        // The API key that the rotation returned is nowhere in the trail.
        Assert.DoesNotContain("d2dk_", viaKey.GetRawText() + mine.GetRawText());
    }

    // ---- platform-wide views ----

    [Fact]
    public async Task Stats_users_and_user_detail_describe_the_platform()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var register = await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email, password = UserPassword, displayName = "Zed Zeta" }, ("X-App-Id", app.Code));
        var userId = (await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(register.Json.GetProperty("accessToken").GetString()!))).Json.GetProperty("id").GetGuid();

        var stats = (await SendAsync(HttpMethod.Get, "/api/v1/admin/stats", headers: AdminKey)).Json;
        Assert.True(stats.GetProperty("apps").GetInt32() >= 1);
        Assert.True(stats.GetProperty("activeSessions").GetInt32() >= 1);
        var usage = stats.GetProperty("perApp").EnumerateArray().First(a => a.GetProperty("code").GetString() == app.Code);
        Assert.Equal(1, usage.GetProperty("members").GetInt32());
        Assert.Equal(1, usage.GetProperty("activeSessions").GetInt32());

        var found = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/users?search={Uri.EscapeDataString(email)}", headers: AdminKey)).Json;
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        var item = found.GetProperty("items")[0];
        Assert.Equal(userId, item.GetProperty("id").GetGuid());
        Assert.Equal(app.Code, item.GetProperty("apps")[0].GetProperty("code").GetString());
        Assert.Equal("password", item.GetProperty("loginMethods")[0].GetString());

        var detail = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/users/{userId}", headers: AdminKey)).Json;
        Assert.Equal(1, detail.GetProperty("activeSessions").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/users/{Guid.NewGuid()}", headers: AdminKey)).Status);
    }

    [Fact]
    public async Task Admin_can_reset_a_user_password_and_end_their_sessions()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var register = await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email, password = UserPassword, displayName = "Y" }, ("X-App-Id", app.Code));
        var refresh = register.Json.GetProperty("refreshToken").GetString();
        var userId = (await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(register.Json.GetProperty("accessToken").GetString()!))).Json.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{userId}/password", new { newPassword = "short" }, AdminKey)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{userId}/password", new { newPassword = "Brand-New-Pass-1" }, AdminKey)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, ("X-App-Id", app.Code))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = UserPassword }, ("X-App-Id", app.Code))).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = "Brand-New-Pass-1" }, ("X-App-Id", app.Code))).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{userId}/revoke-sessions", headers: AdminKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{Guid.NewGuid()}/revoke-sessions", headers: AdminKey)).Status);
    }

    [Fact]
    public async Task Webhook_inbox_can_be_browsed_and_failed_events_retried()
    {
        var app = await CreateAppAsync();
        var failed = NewEvent(app.Id, WebhookEventStatus.Failed, "boom");
        var done = NewEvent(app.Id, WebhookEventStatus.Processed, null);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            db.LineWebhookEvents.AddRange(failed, done);
            await db.SaveChangesAsync();
        }

        var failedList = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/webhook-events?appId={app.Id}&status=Failed", headers: AdminKey)).Json;
        Assert.Equal(1, failedList.GetProperty("total").GetInt32());
        Assert.Equal("boom", failedList.GetProperty("items")[0].GetProperty("lastError").GetString());
        Assert.Equal(app.Code, failedList.GetProperty("items")[0].GetProperty("appCode").GetString());
        Assert.Equal(2, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/webhook-events?appId={app.Id}", headers: AdminKey)).Json.GetProperty("total").GetInt32());

        var detail = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/webhook-events/{failed.Id}", headers: AdminKey)).Json;
        Assert.Equal("{\"type\":\"message\"}", detail.GetProperty("payload").GetString());

        // A processed event is final; a failed one goes back to the queue.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/webhook-events/{done.Id}/retry", headers: AdminKey)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/api/v1/admin/webhook-events/{failed.Id}/retry", headers: AdminKey)).Status);
        var after = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/webhook-events/{failed.Id}", headers: AdminKey)).Json.GetProperty("event");
        Assert.NotEqual("Failed", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Deleting_an_app_removes_what_belongs_to_it_and_keeps_the_people()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var register = await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email, password = UserPassword, displayName = "Keep Me" }, ("X-App-Id", app.Code));
        var refresh = register.Json.GetProperty("refreshToken").GetString();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels",
            new { type = "Messaging", channelId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(), providerId = "P1", channelSecret = "s" }, AdminKey);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            db.LineWebhookEvents.Add(NewEvent(app.Id, WebhookEventStatus.Processed, null));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}", headers: AdminKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}", headers: AdminKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}", headers: AdminKey)).Status);
        Assert.Equal(0, (await SendAsync(HttpMethod.Get, $"/api/v1/admin/webhook-events?appId={app.Id}", headers: AdminKey)).Json.GetProperty("total").GetInt32());

        // The app's code no longer works, but the person is still known to the platform.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, ("X-App-Id", app.Code))).Status);
        var user = (await SendAsync(HttpMethod.Get, $"/api/v1/admin/users?search={Uri.EscapeDataString(email)}", headers: AdminKey)).Json;
        Assert.Equal(1, user.GetProperty("total").GetInt32());
        Assert.Equal(0, user.GetProperty("items")[0].GetProperty("apps").GetArrayLength());
    }

    // ---- helpers ----

    private async Task<(string Email, string Password)> CreateAdminAsync(string role = "Admin")
    {
        var email = NewEmail();
        var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/admins",
            new { email, displayName = "Admin " + email[..6], password = AdminPassword, role }, AdminKey);
        Assert.Equal(HttpStatusCode.OK, created.Status);
        return (email, AdminPassword);
    }

    private Task<Reply> LoginAsync(string email, string password = AdminPassword) =>
        SendAsync(HttpMethod.Post, "/api/v1/admin/auth/login", new { email, password });

    private Task<Reply> RefreshAsync(string cookie) =>
        SendAsync(HttpMethod.Post, "/api/v1/admin/auth/refresh", headers: [Ajax, Cookie(cookie)]);

    private async Task<(Guid Id, string Code)> CreateAppAsync()
    {
        var code = "t" + Guid.NewGuid().ToString("N")[..12];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/apps", new { code, name = code }, AdminKey);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        return (created.Json.GetProperty("app").GetProperty("id").GetGuid(), code);
    }

    private static LineWebhookEvent NewEvent(Guid appId, WebhookEventStatus status, string? error) => new()
    {
        AppId = appId, ChannelId = "1", WebhookEventId = Guid.NewGuid().ToString("N"), EventType = "message",
        Payload = "{\"type\":\"message\"}", Status = status, LastError = error, Attempts = status == WebhookEventStatus.Failed ? 5 : 1,
        ProcessedAt = status == WebhookEventStatus.Processed ? DateTime.UtcNow : null,
    };

    private async Task<Reply> SendAsync(HttpMethod method, string path, object? body = null, params (string Name, string Value)[] headers)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            json = JsonDocument.Parse(text).RootElement.Clone();
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : null;
        return new Reply(response.StatusCode, json, cookie);
    }

    private static (string, string) Bearer(string token) => ("Authorization", $"Bearer {token}");
    private static (string, string) Cookie(string value) => ("Cookie", $"{CookieName}={value}");
    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";
}
