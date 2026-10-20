using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using D2D.Core.Entities;
using D2D.Infrastructure.Line;
using D2D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Tests.Integration;

/// <summary>End-to-end behaviour of the real HTTP API (in memory, SQLite master DB). Every test creates its own app.</summary>
[Collection("api")]
public sealed class ApiIntegrationTests(ApiFactory factory)
{
    private const string Password = "P@ssw0rd!";
    private static readonly (string, string) Admin = ("X-Admin-Key", ApiFactory.AdminKey);

    private sealed record Reply(HttpStatusCode Status, JsonElement Json, string? Location);

    private sealed record TestApp(Guid Id, string Code, string ApiKey)
    {
        public (string, string) AppHeader => ("X-App-Id", Code);
        public (string, string) KeyHeader => ("X-Api-Key", ApiKey);
    }

    // ---- basics & admin guard ----

    [Fact]
    public async Task Health_is_ok() =>
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/health")).Status);

    [Fact]
    public async Task Admin_api_needs_the_admin_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps", headers: ("X-Admin-Key", "wrong"))).Status);

        var app = await CreateAppAsync();
        var (access, _) = await RegisterAsync(app);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Get, "/api/v1/admin/apps", headers: Bearer(access))).Status);
    }

    [Fact]
    public async Task Duplicate_app_code_conflicts()
    {
        var app = await CreateAppAsync();
        var again = await SendAsync(HttpMethod.Post, "/api/v1/admin/apps", new { code = app.Code, name = "x" }, Admin);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
    }

    // ---- authentication ----

    [Fact]
    public async Task Register_login_and_me()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        await RegisterAsync(app, email);

        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader);
        Assert.Equal(HttpStatusCode.OK, login.Status);

        var me = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(login.Json.GetProperty("accessToken").GetString()!));
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal(app.Code, me.Json.GetProperty("app").GetString());
        Assert.Equal(email, me.Json.GetProperty("email").GetString());
        Assert.Contains("user", me.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Bad_credentials_and_missing_app_are_rejected()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        await RegisterAsync(app, email);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = "wrong-password" }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = NewEmail(), password = Password }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email = NewEmail(), password = Password, displayName = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email = NewEmail(), password = "short", displayName = "x" }, app.AppHeader)).Status);
    }

    [Fact]
    public async Task Token_cannot_be_used_for_another_app()
    {
        var appA = await CreateAppAsync();
        var appB = await CreateAppAsync();
        var (access, _) = await RegisterAsync(appA);

        var wrongApp = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: [Bearer(access), appB.AppHeader]);
        Assert.Equal(HttpStatusCode.Forbidden, wrongApp.Status);
    }

    [Fact]
    public async Task Same_user_joins_a_second_app_with_self_registration()
    {
        var appA = await CreateAppAsync();
        var appB = await CreateAppAsync();
        var email = NewEmail();
        await RegisterAsync(appA, email);

        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, appB.AppHeader);
        Assert.Equal(HttpStatusCode.OK, login.Status);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_revokes_the_session()
    {
        var app = await CreateAppAsync();
        var (_, first) = await RegisterAsync(app);

        var second = await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = first }, app.AppHeader);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        var secondRefresh = second.Json.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(first, secondRefresh);

        // Replaying the rotated token looks like theft: it fails and kills the newer token too.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = first }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = secondRefresh }, app.AppHeader)).Status);
    }

    [Fact]
    public async Task Refresh_token_does_not_work_for_another_app()
    {
        var appA = await CreateAppAsync();
        var appB = await CreateAppAsync();
        var (_, refresh) = await RegisterAsync(appA);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, appB.AppHeader)).Status);
    }

    [Fact]
    public async Task Logout_all_ends_every_session_of_the_user_in_the_app()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var (access, refreshOne) = await RegisterAsync(app, email);
        var other = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader);
        var refreshTwo = other.Json.GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/api/v1/auth/logout-all", headers: Bearer(access))).Status);

        foreach (var token in new[] { refreshOne, refreshTwo })
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = token }, app.AppHeader)).Status);
        }
    }

    [Fact]
    public async Task Logout_revokes_only_that_session()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var (_, refreshOne) = await RegisterAsync(app, email);
        var other = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader);

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/logout", new { refreshToken = refreshOne }, app.AppHeader)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refreshOne }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = other.Json.GetProperty("refreshToken").GetString() }, app.AppHeader)).Status);
    }

    // ---- backend-only sign-in & introspection ----

    [Fact]
    public async Task App_can_require_its_api_key_for_sign_in_endpoints()
    {
        var app = await CreateAppAsync(requireAppKey: true);
        var email = NewEmail();
        object register = new { email, password = Password, displayName = "Eve" };
        object login = new { email, password = Password };

        // From a browser (no key) or with a wrong key: refused before any credentials are looked at.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/register", register, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/register", register, app.AppHeader, ("X-Api-Key", "wrong"))).Status);

        // From the app's backend: works, and so do refresh and logout.
        var registered = await SendAsync(HttpMethod.Post, "/api/v1/auth/register", register, app.AppHeader, app.KeyHeader);
        Assert.Equal(HttpStatusCode.OK, registered.Status);
        var refresh = registered.Json.GetProperty("refreshToken").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", login, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", login, app.AppHeader, app.KeyHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, app.AppHeader)).Status);
        var refreshed = await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, app.AppHeader, app.KeyHeader);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);

        // Users keep using their own tokens directly.
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(refreshed.Json.GetProperty("accessToken").GetString()!))).Status);

        // Another app's key does not unlock this app.
        var other = await CreateAppAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", login, app.AppHeader, other.KeyHeader)).Status);

        // Switching the setting off opens the endpoints again; other apps were never affected.
        var off = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}",
            new { name = app.Code, isActive = true, allowSelfRegistration = true, requireAppKeyForAuth = false }, Admin);
        Assert.False(off.Json.GetProperty("requireAppKeyForAuth").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", login, app.AppHeader)).Status);
    }

    [Fact]
    public async Task Introspection_reflects_changes_immediately()
    {
        var app = await CreateAppAsync(selfRegistration: false);
        var email = NewEmail();
        var add = await SendAsync(HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/members",
            new { email, displayName = "Fay", password = Password, roles = new[] { "editor" } }, Admin);
        var userId = add.Json.GetProperty("userId").GetGuid();
        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader);
        var access = login.Json.GetProperty("accessToken").GetString()!;

        async Task<Reply> Introspect(string token, params (string, string)[] headers) =>
            await SendAsync(HttpMethod.Post, "/api/v1/auth/introspect", new { token }, headers.Length > 0 ? headers : [app.AppHeader, app.KeyHeader]);

        var active = await Introspect(access);
        Assert.Equal(HttpStatusCode.OK, active.Status);
        Assert.True(active.Json.GetProperty("active").GetBoolean());
        Assert.Equal(userId, active.Json.GetProperty("userId").GetGuid());
        Assert.Equal(["editor"], active.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // The token still says "editor" but D2D answers with the current roles.
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/members/{userId}", new { roles = new[] { "admin" } }, Admin);
        Assert.Equal(["admin"], (await Introspect(access)).Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // Garbage, tokens of other apps and a missing/wrong key are not active / not allowed.
        Assert.False((await Introspect("not-a-jwt")).Json.GetProperty("active").GetBoolean());
        var other = await CreateAppAsync();
        var (otherToken, _) = await RegisterAsync(other);
        Assert.False((await Introspect(otherToken)).Json.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Introspect(access, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Introspect(access, app.AppHeader, ("X-Api-Key", "wrong"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Introspect(access, Bearer(access))).Status);

        // Disabled user and removed membership both turn the same token inactive at once.
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{userId}/active", new { isActive = false }, Admin);
        Assert.False((await Introspect(access)).Json.GetProperty("active").GetBoolean());
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{userId}/active", new { isActive = true }, Admin);
        Assert.True((await Introspect(access)).Json.GetProperty("active").GetBoolean());
        await SendAsync(HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}/members/{userId}", headers: Admin);
        Assert.False((await Introspect(access)).Json.GetProperty("active").GetBoolean());
    }

    // ---- tokens for other backends ----

    [Fact]
    public async Task Access_token_validates_with_the_published_jwks_only()
    {
        var app = await CreateAppAsync();
        var (access, _) = await RegisterAsync(app);

        var jwks = await SendAsync(HttpMethod.Get, "/.well-known/jwks.json");
        var key = jwks.Json.GetProperty("keys")[0];
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.False(key.TryGetProperty("d", out _), "JWKS must not expose private key material");

        var token = new JsonWebToken(access);
        Assert.Equal("RS256", token.Alg);
        Assert.Equal(key.GetProperty("kid").GetString(), token.Kid);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(access, new TokenValidationParameters
        {
            ValidIssuer = "http://localhost",
            ValidAudience = "d2d-apps",
            IssuerSigningKeys = new JsonWebKeySet(jwks.Json.GetRawText()).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(app.Code, token.Claims.First(c => c.Type == "app_code").Value);
    }

    [Fact]
    public async Task Discovery_document_points_to_the_jwks()
    {
        var discovery = await SendAsync(HttpMethod.Get, "/.well-known/openid-configuration");
        Assert.Equal("http://localhost", discovery.Json.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/.well-known/jwks.json", discovery.Json.GetProperty("jwks_uri").GetString());
    }

    // ---- members & roles ----

    [Fact]
    public async Task Closed_app_needs_membership_and_admin_controls_roles()
    {
        var app = await CreateAppAsync(selfRegistration: false);
        var email = NewEmail();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/register", new { email, password = Password, displayName = "Bob" }, app.AppHeader)).Status);

        var add = await SendAsync(HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/members",
            new { email, displayName = "Bob", password = Password, roles = new[] { "Editor" } }, Admin);
        Assert.Equal(HttpStatusCode.OK, add.Status);
        var userId = add.Json.GetProperty("userId").GetGuid();

        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader);
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Equal(["editor"], RolesOf(login.Json.GetProperty("accessToken").GetString()!));

        var update = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/members/{userId}", new { roles = new[] { "editor", "admin" } }, Admin);
        Assert.Equal(HttpStatusCode.OK, update.Status);
        var refreshed = await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = login.Json.GetProperty("refreshToken").GetString() }, app.AppHeader);
        Assert.Equal(["admin", "editor"], RolesOf(refreshed.Json.GetProperty("accessToken").GetString()!).Order());

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/v1/admin/apps/{app.Id}/members/{userId}", headers: Admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refreshed.Json.GetProperty("refreshToken").GetString() }, app.AppHeader)).Status);
    }

    [Fact]
    public async Task App_backend_manages_only_its_own_members()
    {
        var appA = await CreateAppAsync(selfRegistration: false);
        var appB = await CreateAppAsync(selfRegistration: false);

        var add = await SendAsync(HttpMethod.Post, "/api/v1/members",
            new { email = NewEmail(), displayName = "Carol", password = Password }, appA.AppHeader, appA.KeyHeader);
        Assert.Equal(HttpStatusCode.OK, add.Status);
        var userId = add.Json.GetProperty("userId").GetGuid();

        var listA = await SendAsync(HttpMethod.Get, "/api/v1/members", headers: [appA.AppHeader, appA.KeyHeader]);
        var listB = await SendAsync(HttpMethod.Get, "/api/v1/members", headers: [appB.AppHeader, appB.KeyHeader]);
        Assert.Equal(1, listA.Json.GetProperty("total").GetInt32());
        Assert.Equal(0, listB.Json.GetProperty("total").GetInt32());

        // App B's key cannot touch app A's member, and a wrong key is rejected outright.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Put, $"/api/v1/members/{userId}", new { roles = new[] { "x" } }, appB.AppHeader, appB.KeyHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Get, "/api/v1/members", headers: [appA.AppHeader, ("X-Api-Key", "wrong")])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Get, "/api/v1/members", headers: [appA.AppHeader])).Status);
    }

    [Fact]
    public async Task Member_list_supports_search_and_paging()
    {
        var app = await CreateAppAsync(selfRegistration: false);
        foreach (var name in new[] { "Anna", "Ben", "Cara" })
        {
            var add = await SendAsync(HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/members",
                new { email = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com", displayName = name, password = Password }, Admin);
            Assert.Equal(HttpStatusCode.OK, add.Status);
        }

        var all = await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}/members?take=2", headers: Admin);
        Assert.Equal(3, all.Json.GetProperty("total").GetInt32());
        Assert.Equal(2, all.Json.GetProperty("items").GetArrayLength());

        var found = await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}/members?search=ben", headers: Admin);
        Assert.Equal(1, found.Json.GetProperty("total").GetInt32());
        Assert.Equal("Ben", found.Json.GetProperty("items")[0].GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("super_admin")]
    [InlineData("app_server")]
    [InlineData("has space")]
    [InlineData("UPPER!")]
    public async Task Reserved_or_invalid_roles_are_rejected(string role)
    {
        var app = await CreateAppAsync(selfRegistration: false);
        var add = await SendAsync(HttpMethod.Post, "/api/v1/members",
            new { email = NewEmail(), displayName = "Dan", password = Password, roles = new[] { role } }, app.AppHeader, app.KeyHeader);
        Assert.Equal(HttpStatusCode.BadRequest, add.Status);
    }

    [Fact]
    public async Task Disabled_user_cannot_sign_in_and_sessions_end()
    {
        var app = await CreateAppAsync();
        var email = NewEmail();
        var (access, refresh) = await RegisterAsync(app, email);
        var me = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: Bearer(access));
        var userId = me.Json.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{userId}/active", new { isActive = false }, Admin)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh }, app.AppHeader)).Status);

        await SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{userId}/active", new { isActive = true }, Admin);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password }, app.AppHeader)).Status);
    }

    // ---- multi DB ----

    [Fact]
    public async Task Each_app_reads_and_writes_its_own_database()
    {
        var appA = await CreateAppAsync();
        var appB = await CreateAppAsync();
        var (tokenA, _) = await RegisterAsync(appA);
        var (tokenB, _) = await RegisterAsync(appB);

        // Before a database is configured the module reports it instead of failing.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Get, "/api/v1/notes", headers: Bearer(tokenA))).Status);

        foreach (var app in new[] { appA, appB })
        {
            var set = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/database",
                new { provider = "Sqlite", connectionString = $"Data Source={Path.Combine(factory.TempDir, app.Code + ".db")}", provision = true }, Admin);
            Assert.Equal(HttpStatusCode.NoContent, set.Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/v1/notes", new { title = "only in A" }, Bearer(tokenA))).Status);

        var notesA = await SendAsync(HttpMethod.Get, "/api/v1/notes", headers: Bearer(tokenA));
        var notesB = await SendAsync(HttpMethod.Get, "/api/v1/notes", headers: Bearer(tokenB));
        Assert.Equal(1, notesA.Json.GetArrayLength());
        Assert.Equal(0, notesB.Json.GetArrayLength());
    }

    [Fact]
    public async Task App_backend_can_fetch_its_own_connection_string_when_enabled()
    {
        var app = await CreateAppAsync();
        var other = await CreateAppAsync();
        var path = Path.Combine(factory.TempDir, app.Code + "-cred.db");
        var connection = $"Data Source={path}";
        var headers = new[] { app.AppHeader, app.KeyHeader };

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: headers)).Status);

        // Registered but not opted in: stored, yet the app cannot read it.
        await SetDatabaseAsync(app, new { provider = "Sqlite", connectionString = connection, provision = true });
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: headers)).Status);

        await SetDatabaseAsync(app, new { provider = "Sqlite", connectionString = connection, allowCredentialAccess = true });
        var creds = await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: headers);
        Assert.Equal(HttpStatusCode.OK, creds.Status);
        Assert.Equal("Sqlite", creds.Json.GetProperty("provider").GetString());
        Assert.Equal(connection, creds.Json.GetProperty("connectionString").GetString());

        // Each app only ever sees its own; no key / user token / other app's key get nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: [other.AppHeader, other.KeyHeader])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: [app.AppHeader])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: [app.AppHeader, other.KeyHeader])).Status);
        var (access, _) = await RegisterAsync(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: Bearer(access))).Status);

        // The admin API never echoes the connection string back.
        var admin = await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}", headers: Admin);
        Assert.DoesNotContain(path, admin.Json.GetRawText());
        Assert.True(admin.Json.GetProperty("databaseCredentialAccess").GetBoolean());
    }

    [Fact]
    public async Task Connection_test_can_be_skipped_for_databases_d2d_cannot_reach()
    {
        var app = await CreateAppAsync();
        var unreachable = $"Data Source={Path.Combine(factory.TempDir, "no-such-dir", "x.db")};Mode=ReadOnly";

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/database", new { provider = "Sqlite", connectionString = unreachable }, Admin)).Status);

        await SetDatabaseAsync(app, new { provider = "Sqlite", connectionString = unreachable, verify = false, allowCredentialAccess = true });
        var creds = await SendAsync(HttpMethod.Get, "/api/v1/database/credentials", headers: [app.AppHeader, app.KeyHeader]);
        Assert.Equal(unreachable, creds.Json.GetProperty("connectionString").GetString());
    }

    [Fact]
    public async Task Unreachable_database_is_rejected_when_configured()
    {
        var app = await CreateAppAsync();
        var set = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/database",
            new { provider = "Sqlite", connectionString = $"Data Source={Path.Combine(factory.TempDir, "missing-dir", "x.db")};Mode=ReadOnly" }, Admin);
        Assert.Equal(HttpStatusCode.BadRequest, set.Status);
    }

    // ---- LINE ----

    [Fact]
    public async Task Line_webhook_checks_signature_deduplicates_and_processes()
    {
        var app = await CreateAppAsync();
        var channelId = NewChannelId();
        const string secret = "channel-secret";
        var channel = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels",
            new { type = "Messaging", channelId, providerId = "P1", channelSecret = secret }, Admin);
        Assert.Equal(HttpStatusCode.OK, channel.Status);
        Assert.Equal("Stateless", channel.Json.GetProperty("tokenMode").GetString());
        Assert.Equal($"http://localhost/webhooks/line/{channelId}", channel.Json.GetProperty("webhookUrl").GetString());

        var eventId = Guid.NewGuid().ToString("N");
        var body = Encoding.UTF8.GetBytes(
            $$$"""{"destination":"Ubot","events":[{"type":"message","webhookEventId":"{{{eventId}}}","deliveryContext":{"isRedelivery":false},"source":{"type":"user","userId":"U1"},"replyToken":"r","message":{"type":"text","id":"1","text":"hi"}}]}""");
        var signature = LineSignature.Sign(secret, body);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostWebhookAsync(channelId, body, "AAAA"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWebhookAsync(channelId, body, null));
        Assert.Equal(HttpStatusCode.NotFound, await PostWebhookAsync("999", body, signature));
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(channelId, body, signature));
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(channelId, body, signature)); // redelivery

        var stored = await WaitForEventAsync(eventId, WebhookEventStatus.Processed);
        Assert.Equal(app.Id, stored.AppId);
        Assert.Equal("message", stored.EventType);
        Assert.Equal("U1", stored.SourceUserId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        Assert.Equal(1, await db.LineWebhookEvents.CountAsync(e => e.WebhookEventId == eventId));
    }

    [Fact]
    public async Task Line_verify_request_with_no_events_is_accepted()
    {
        var app = await CreateAppAsync();
        var channelId = NewChannelId();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels",
            new { type = "Messaging", channelId, providerId = "P1", channelSecret = "s" }, Admin);

        var body = Encoding.UTF8.GetBytes("""{"destination":"Ubot","events":[]}""");
        Assert.Equal(HttpStatusCode.OK, await PostWebhookAsync(channelId, body, LineSignature.Sign("s", body)));
    }

    [Fact]
    public async Task Line_settings_are_saved_together_and_the_app_reads_them_in_one_call()
    {
        var app = await CreateAppAsync();
        var messagingId = NewChannelId();
        var loginId = NewChannelId();
        var headers = new[] { app.AppHeader, app.KeyHeader };

        var m = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels", new
        {
            type = "Messaging", channelId = messagingId, providerId = "P9", channelSecret = "msg-secret", tokenMode = "Static", accessToken = "tok-1",
            jobsKey = "jobs-1", allowCredentialAccess = true,
        }, Admin);
        Assert.Equal(HttpStatusCode.OK, m.Status);
        Assert.True(m.Json.GetProperty("hasJobsKey").GetBoolean());
        Assert.False(m.Json.TryGetProperty("jobsKey", out _));   // admin answers never carry a secret

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels", new
        {
            type = "Login", channelId = loginId, providerId = "P9", channelSecret = "login-secret", liffId = $"{loginId}-AbC", allowCredentialAccess = true,
        }, Admin)).Status);

        var all = await SendAsync(HttpMethod.Get, "/api/v1/line/credentials/all", headers: headers);
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal("P9", all.Json.GetProperty("providerId").GetString());
        Assert.Equal("msg-secret", all.Json.GetProperty("messaging").GetProperty("channelSecret").GetString());
        Assert.Equal("tok-1", all.Json.GetProperty("messaging").GetProperty("accessToken").GetString());
        Assert.Equal("login-secret", all.Json.GetProperty("login").GetProperty("channelSecret").GetString());
        Assert.Equal($"{loginId}-AbC", all.Json.GetProperty("liffId").GetString());
        Assert.Equal("jobs-1", all.Json.GetProperty("jobsKey").GetString());
        Assert.Equal(2, all.Json.GetProperty("channels").GetArrayLength());

        // Editing with blank secrets keeps what is stored; the other fields change.
        var edit = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels", new
        {
            type = "Messaging", channelId = messagingId, providerId = "P9", tokenMode = "Static", allowCredentialAccess = true, webhookForwardUrl = "https://example.com/hook",
        }, Admin);
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        var again = await SendAsync(HttpMethod.Get, "/api/v1/line/credentials/all", headers: headers);
        Assert.Equal("msg-secret", again.Json.GetProperty("messaging").GetProperty("channelSecret").GetString());
        Assert.Equal("tok-1", again.Json.GetProperty("messaging").GetProperty("accessToken").GetString());
        Assert.Equal("jobs-1", again.Json.GetProperty("jobsKey").GetString());
        Assert.Equal("https://example.com/hook", again.Json.GetProperty("messaging").GetProperty("webhookForwardUrl").GetString());

        // A new channel still needs its secret.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels",
            new { type = "Messaging", channelId = NewChannelId(), providerId = "P9" }, Admin)).Status);

        // A channel that did not opt in is only named, never opened.
        var closed = NewChannelId();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels", new { type = "Login", channelId = closed, providerId = "P9", channelSecret = "x" }, Admin);
        var withheld = await SendAsync(HttpMethod.Get, "/api/v1/line/credentials/all", headers: headers);
        Assert.Equal(2, withheld.Json.GetProperty("channels").GetArrayLength());
        Assert.Contains(closed, withheld.Json.GetProperty("withheldChannelIds").EnumerateArray().Select(e => e.GetString()));

        // Another app sees nothing of it.
        var other = await CreateAppAsync();
        var none = await SendAsync(HttpMethod.Get, "/api/v1/line/credentials/all", headers: [other.AppHeader, other.KeyHeader]);
        Assert.Equal(0, none.Json.GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public async Task Line_channel_belongs_to_one_app_and_keeps_its_provider()
    {
        var appA = await CreateAppAsync();
        var appB = await CreateAppAsync();
        var channelId = NewChannelId();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{appA.Id}/line-channels",
            new { type = "Login", channelId, providerId = "P1", channelSecret = "s" }, Admin);

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{appB.Id}/line-channels",
            new { type = "Login", channelId, providerId = "P1", channelSecret = "s" }, Admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{appA.Id}/line-channels",
            new { type = "Login", channelId, providerId = "P2", channelSecret = "s" }, Admin)).Status);
    }

    [Fact]
    public async Task Line_login_redirects_to_line_with_pkce_and_checks_redirect_uri()
    {
        const string redirect = "http://localhost:3000/callback";
        var app = await CreateAppAsync(redirectUris: [redirect]);
        var channelId = NewChannelId();
        await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels",
            new { type = "Login", channelId, providerId = "P1", channelSecret = "s" }, Admin);

        var ok = await SendAsync(HttpMethod.Get, $"/api/v1/auth/line/authorize?app={app.Code}&redirect_uri={Uri.EscapeDataString(redirect)}&state=xyz");
        Assert.Equal(HttpStatusCode.Redirect, ok.Status);
        Assert.StartsWith("https://access.line.me/oauth2/v2.1/authorize", ok.Location);
        Assert.Contains("code_challenge_method=S256", ok.Location);
        Assert.Contains($"client_id={channelId}", ok.Location);

        var evil = await SendAsync(HttpMethod.Get, $"/api/v1/auth/line/authorize?app={app.Code}&redirect_uri={Uri.EscapeDataString("https://evil.example/cb")}");
        Assert.Equal(HttpStatusCode.BadRequest, evil.Status);

        var foreignChannel = await SendAsync(HttpMethod.Get,
            $"/api/v1/auth/line/authorize?app={app.Code}&redirect_uri={Uri.EscapeDataString(redirect)}&channel_id=424242");
        Assert.Equal(HttpStatusCode.BadRequest, foreignChannel.Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/api/v1/auth/line/callback?code=x&state=unknown")).Status);
    }

    [Fact]
    public async Task App_backend_can_fetch_keys_of_its_own_enabled_channels()
    {
        var app = await CreateAppAsync();
        var other = await CreateAppAsync();
        var messaging = NewChannelId();
        var login = NewChannelId();
        var closed = NewChannelId();
        await PutChannelAsync(app, new { type = "Messaging", channelId = messaging, providerId = "P1", channelSecret = "msg-secret", accessToken = "static-token", allowCredentialAccess = true });
        await PutChannelAsync(app, new { type = "Login", channelId = login, providerId = "P1", channelSecret = "login-secret", allowCredentialAccess = true });
        await PutChannelAsync(app, new { type = "Messaging", channelId = closed, providerId = "P1", channelSecret = "hidden", accessToken = "hidden-token" });

        var msg = await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{messaging}/credentials", headers: [app.AppHeader, app.KeyHeader]);
        Assert.Equal(HttpStatusCode.OK, msg.Status);
        Assert.Equal("msg-secret", msg.Json.GetProperty("channelSecret").GetString());
        Assert.Equal("static-token", msg.Json.GetProperty("accessToken").GetString());
        Assert.Equal("Static", msg.Json.GetProperty("tokenMode").GetString());
        Assert.Equal(JsonValueKind.Null, msg.Json.GetProperty("accessTokenExpiresAt").ValueKind);

        var log = await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{login}/credentials", headers: [app.AppHeader, app.KeyHeader]);
        Assert.Equal("login-secret", log.Json.GetProperty("channelSecret").GetString());
        Assert.Equal(JsonValueKind.Null, log.Json.GetProperty("accessToken").ValueKind);

        // Not enabled -> 403, someone else's channel / unknown channel -> 404, no or wrong key -> 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{closed}/credentials", headers: [app.AppHeader, app.KeyHeader])).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{messaging}/credentials", headers: [other.AppHeader, other.KeyHeader])).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/v1/line/channels/123/credentials", headers: [app.AppHeader, app.KeyHeader])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{messaging}/credentials", headers: [app.AppHeader])).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{messaging}/credentials", headers: [app.AppHeader, ("X-Api-Key", "wrong")])).Status);

        // A signed-in end user must never read channel secrets.
        var (access, _) = await RegisterAsync(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"/api/v1/line/channels/{messaging}/credentials", headers: Bearer(access))).Status);
    }

    [Fact]
    public async Task Admin_responses_never_contain_channel_secrets()
    {
        var app = await CreateAppAsync();
        var channelId = NewChannelId();
        var put = await PutChannelAsync(app, new { type = "Messaging", channelId, providerId = "P1", channelSecret = "super-secret-value", accessToken = "super-token-value", allowCredentialAccess = true });
        Assert.True(put.Json.GetProperty("allowCredentialAccess").GetBoolean());

        var get = await SendAsync(HttpMethod.Get, $"/api/v1/admin/apps/{app.Id}", headers: Admin);
        var text = get.Json.GetRawText() + put.Json.GetRawText();
        Assert.DoesNotContain("super-secret-value", text);
        Assert.DoesNotContain("super-token-value", text);
    }

    [Fact]
    public async Task Line_verify_is_for_app_backends_only()
    {
        var app = await CreateAppAsync();
        var (access, _) = await RegisterAsync(app);
        object body = new { idToken = "not-a-jwt" };

        // Browsers and signed-in users cannot call it; only the app's backend with its API key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/line/verify", body, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/line/verify", body, Bearer(access))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/auth/line/verify", body, app.AppHeader, ("X-Api-Key", "wrong"))).Status);

        // With the key a malformed token is rejected by us before LINE is called.
        var bad = await SendAsync(HttpMethod.Post, "/api/v1/auth/line/verify", body, app.AppHeader, app.KeyHeader);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.Status);
        Assert.Equal("Invalid LINE ID token", bad.Json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Line_messaging_needs_the_app_api_key()
    {
        var app = await CreateAppAsync();
        object push = new { to = "U1", messages = new[] { new { type = "text", text = "hi" } } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/line/push", push, app.AppHeader)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/v1/line/push", push, app.AppHeader, ("X-Api-Key", "wrong"))).Status);
        // Valid key, but the app has no Messaging channel yet.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/api/v1/line/push", push, app.AppHeader, app.KeyHeader)).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, "/api/v1/line/proxy/oauth2/v2.1/token", headers: [app.AppHeader, app.KeyHeader])).Status);
    }

    // ---- helpers ----

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
        return new Reply(response.StatusCode, json, response.Headers.Location?.ToString());
    }

    private async Task SetDatabaseAsync(TestApp app, object database) =>
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/database", database, Admin)).Status);

    private async Task<Reply> PutChannelAsync(TestApp app, object channel)
    {
        var reply = await SendAsync(HttpMethod.Put, $"/api/v1/admin/apps/{app.Id}/line-channels", channel, Admin);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        return reply;
    }

    private async Task<HttpStatusCode> PostWebhookAsync(string channelId, byte[] body, string? signature)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/line/{channelId}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (signature is not null) request.Headers.Add("x-line-signature", signature);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<LineWebhookEvent> WaitForEventAsync(string eventId, WebhookEventStatus status)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            var stored = await db.LineWebhookEvents.AsNoTracking().FirstOrDefaultAsync(e => e.WebhookEventId == eventId);
            if (stored?.Status == status) return stored;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"LINE event {eventId} did not reach status {status} within 10 s");
    }

    private async Task<TestApp> CreateAppAsync(bool selfRegistration = true, string[]? redirectUris = null, bool requireAppKey = false)
    {
        var code = "t" + Guid.NewGuid().ToString("N")[..12];
        var created = await SendAsync(HttpMethod.Post, "/api/v1/admin/apps",
            new { code, name = code, allowSelfRegistration = selfRegistration, redirectUris, requireAppKeyForAuth = requireAppKey }, Admin);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        return new TestApp(created.Json.GetProperty("app").GetProperty("id").GetGuid(), code, created.Json.GetProperty("apiKey").GetString()!);
    }

    private async Task<(string Access, string Refresh)> RegisterAsync(TestApp app, string? email = null)
    {
        var reply = await SendAsync(HttpMethod.Post, "/api/v1/auth/register",
            new { email = email ?? NewEmail(), password = Password, displayName = "Alice" }, app.AppHeader);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        return (reply.Json.GetProperty("accessToken").GetString()!, reply.Json.GetProperty("refreshToken").GetString()!);
    }

    private static string[] RolesOf(string accessToken) =>
        new JsonWebToken(accessToken).Claims.Where(c => c.Type == "role").Select(c => c.Value).ToArray();

    private static (string, string) Bearer(string token) => ("Authorization", $"Bearer {token}");
    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";
    private static string NewChannelId() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();
}
