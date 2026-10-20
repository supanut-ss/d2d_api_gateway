using System.Net;
using System.Text;
using System.Text.Json;
using D2D.Core;
using D2D.Core.Abstractions;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Line;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using D2D.Infrastructure.Tenancy;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;

namespace D2D.Tests;

/// <summary>
/// payyet and booksabuy share LINE provider P1; joddaily is on provider P2.
/// LINE's verify endpoint is faked: it echoes the unsigned test token's claims.
/// </summary>
public sealed class LineLoginServiceTests : IDisposable
{
    private const string PayyetChannel = "1000000001";
    private const string JoddailyChannel = "2000000001";
    private const string BooksabuyChannel = "1000000002";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly Dictionary<string, TenantInfo> _apps = new();
    private int _verifyCalls;

    public LineLoginServiceTests()
    {
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MasterDbContext>(o => o.UseSqlite(_connection));
        services.AddMemoryCache();
        services.AddDistributedMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, PlainSecrets>();
        services.AddSingleton<IAppRegistry, AppRegistry>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<LineLoginService>();
        services.Configure<JwtOptions>(o => o.PrivateKeyPem = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem());
        services.AddSingleton<IJwtKeyStore, JwtKeyStore>();
        services.Configure<LineOptions>(o => o.LoginCallbackUrl = "http://localhost/callback");
        services.AddScoped(_ => new LineApiClient(
            new HttpClient(new FakeLineVerify(() => Interlocked.Increment(ref _verifyCalls))), null!, NullLogger<LineApiClient>.Instance));
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        db.Database.EnsureCreated();
        AddApp(db, "payyet", "P1", PayyetChannel);
        AddApp(db, "joddaily", "P2", JoddailyChannel);
        AddApp(db, "booksabuy", "P1", BooksabuyChannel);
        db.SaveChanges();
    }

    [Fact]
    public async Task Apps_on_same_LINE_provider_share_one_user()
    {
        var payyet = await LoginAsync("payyet", IdToken(PayyetChannel, "U-alice-p1"));
        var booksabuy = await LoginAsync("booksabuy", IdToken(BooksabuyChannel, "U-alice-p1"));

        Assert.Equal(payyet, booksabuy);
    }

    [Fact]
    public async Task Apps_on_different_LINE_providers_get_separate_users()
    {
        var payyet = await LoginAsync("payyet", IdToken(PayyetChannel, "U-alice-p1"));
        var joddaily = await LoginAsync("joddaily", IdToken(JoddailyChannel, "U-alice-p2"));

        Assert.NotEqual(payyet, joddaily);
    }

    [Fact]
    public async Task Linking_joins_identities_across_providers()
    {
        var userId = await LoginAsync("payyet", IdToken(PayyetChannel, "U-alice-p1"));

        await WithScope<LineLoginService>(s => s.LinkWithIdTokenAsync(userId, new(IdToken(JoddailyChannel, "U-alice-p2"), null), default));

        Assert.Equal(userId, await LoginAsync("joddaily", IdToken(JoddailyChannel, "U-alice-p2")));
    }

    [Fact]
    public async Task Linking_identity_owned_by_another_user_conflicts()
    {
        var alice = await LoginAsync("payyet", IdToken(PayyetChannel, "U-alice-p1"));
        await LoginAsync("joddaily", IdToken(JoddailyChannel, "U-alice-p2")); // creates a separate user

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            WithScope<LineLoginService>(s => s.LinkWithIdTokenAsync(alice, new(IdToken(JoddailyChannel, "U-alice-p2"), null), default)));
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task Verify_only_returns_the_line_identity_without_creating_a_user()
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps["payyet"]);

        var identity = await scope.ServiceProvider.GetRequiredService<LineLoginService>()
            .VerifyIdTokenOnlyAsync(new LineIdTokenLoginRequest(IdToken(PayyetChannel, "U-visitor"), null), default);

        Assert.Equal("U-visitor", identity.UserId);
        Assert.Equal("P1", identity.ProviderId);
        Assert.Equal(PayyetChannel, identity.ChannelId);
        Assert.Equal("Alice", identity.DisplayName);

        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        Assert.Empty(db.Users);
        Assert.Empty(db.ExternalLogins);
        Assert.Empty(db.RefreshTokens);
    }

    [Fact]
    public async Task Verify_only_remembers_tokens_that_have_an_expiry_so_every_request_can_call_it()
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps["payyet"]);
        var service = scope.ServiceProvider.GetRequiredService<LineLoginService>();
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var withExpiry = new LineIdTokenLoginRequest(IdToken(PayyetChannel, "U-a", exp), null);
        var withoutExpiry = new LineIdTokenLoginRequest(IdToken(PayyetChannel, "U-b"), null);

        var first = await service.VerifyIdTokenOnlyAsync(withExpiry, default);
        var second = await service.VerifyIdTokenOnlyAsync(withExpiry, default);

        Assert.Equal(first, second);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime, first.ExpiresAt);
        Assert.Equal(1, _verifyCalls); // the second call came from the cache

        await service.VerifyIdTokenOnlyAsync(withoutExpiry, default);
        await service.VerifyIdTokenOnlyAsync(withoutExpiry, default);
        Assert.Equal(3, _verifyCalls); // no known expiry -> never cached
    }

    [Fact]
    public async Task Verify_only_cache_does_not_leak_between_apps()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = new LineIdTokenLoginRequest(IdToken(PayyetChannel, "U-a", exp), null);

        using (var owner = _services.CreateScope())
        {
            owner.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps["payyet"]);
            await owner.ServiceProvider.GetRequiredService<LineLoginService>().VerifyIdTokenOnlyAsync(token, default);
        }

        using var other = _services.CreateScope();
        other.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps["joddaily"]);
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            other.ServiceProvider.GetRequiredService<LineLoginService>().VerifyIdTokenOnlyAsync(token, default));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Verify_only_rejects_tokens_of_other_apps_and_garbage()
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps["payyet"]);
        var service = scope.ServiceProvider.GetRequiredService<LineLoginService>();

        var otherApp = await Assert.ThrowsAsync<ApiException>(() =>
            service.VerifyIdTokenOnlyAsync(new LineIdTokenLoginRequest(IdToken(JoddailyChannel, "U-x"), null), default));
        var garbage = await Assert.ThrowsAsync<ApiException>(() =>
            service.VerifyIdTokenOnlyAsync(new LineIdTokenLoginRequest("not-a-jwt", null), default));
        Assert.Equal(401, otherApp.StatusCode);
        Assert.Equal(401, garbage.StatusCode);
    }

    [Fact]
    public async Task Id_token_from_another_apps_channel_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => LoginAsync("payyet", IdToken(JoddailyChannel, "U-x")));
        Assert.Equal(401, ex.StatusCode);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> LoginAsync(string app, string idToken)
    {
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(_apps[app]);
        var tokens = await scope.ServiceProvider.GetRequiredService<LineLoginService>()
            .LoginWithIdTokenAsync(new LineIdTokenLoginRequest(idToken, null), null, default);
        return Guid.Parse(new JsonWebToken(tokens.AccessToken).Subject);
    }

    private async Task WithScope<T>(Func<T, Task> action) where T : notnull
    {
        using var scope = _services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    private void AddApp(MasterDbContext db, string code, string providerId, string loginChannel)
    {
        var app = new App { Code = code, Name = code, ApiKeyHash = "x" };
        app.LineChannels.Add(new LineChannel
        {
            AppId = app.Id,
            Type = LineChannelType.Login,
            ChannelId = loginChannel,
            ProviderId = providerId,
            ChannelSecretEncrypted = "secret",
        });
        db.Apps.Add(app);
        _apps[code] = new TenantInfo(app.Id, code, code, true, true);
    }

    private static string IdToken(string aud, string sub, long? exp = null) =>
        "e30." + WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { aud, sub, name = "Alice", exp })) + ".sig";

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    /// <summary>Accepts a token only when client_id matches its aud, like LINE does.</summary>
    private sealed class FakeLineVerify(Action onCall) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onCall();
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            var payload = WebEncoders.Base64UrlDecode(form["id_token"].ToString().Split('.')[1]);
            var aud = JsonDocument.Parse(payload).RootElement.GetProperty("aud").GetString();

            return aud == form["client_id"]
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) { Headers = { { "Content-Type", "application/json" } } } }
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_request\"}", Encoding.UTF8) };
        }
    }
}
