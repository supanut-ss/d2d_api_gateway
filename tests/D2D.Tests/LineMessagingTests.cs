using System.Net;
using System.Security.Cryptography;
using System.Text;
using D2D.Core.Abstractions;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Line;
using D2D.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Tests;

public sealed class LineMessagingTests : IDisposable
{
    private const string ChannelId = "1234567890";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly FakeLine _line = new();

    public LineMessagingTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MasterDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        db.Database.EnsureCreated();
        var app = new App { Code = "payyet", Name = "Payyet", ApiKeyHash = "x" };
        app.LineChannels.Add(new LineChannel
        {
            AppId = app.Id, Type = LineChannelType.Messaging, ChannelId = ChannelId, ProviderId = "P1",
            ChannelSecretEncrypted = "secret", TokenMode = LineTokenMode.JwtAssertion,
        });
        db.Apps.Add(app);
        db.SaveChanges();
    }

    [Fact]
    public async Task Stateless_token_is_issued_once_and_cached()
    {
        var provider = NewProvider();
        var channel = Channel(LineTokenMode.Stateless);

        var first = await provider.GetAccessTokenAsync(channel, default);
        var second = await provider.GetAccessTokenAsync(channel, default);

        Assert.Equal(first, second);
        Assert.Equal(1, _line.Count("oauth2/v3/token"));
    }

    [Fact]
    public async Task Issued_token_reports_when_it_expires_and_static_token_never_does()
    {
        var before = DateTime.UtcNow;
        var stateless = await NewProvider().GetIssuedAccessTokenAsync(Channel(LineTokenMode.Stateless), default);
        var v21 = await NewProvider().GetIssuedAccessTokenAsync(Channel(LineTokenMode.JwtAssertion, NewRsaJwk()), default);
        var fixedToken = await NewProvider().GetIssuedAccessTokenAsync(Channel(LineTokenMode.Static) with { StaticAccessToken = "abc" }, default);

        Assert.InRange(stateless.ExpiresAt!.Value, before.AddSeconds(890), before.AddSeconds(910));
        Assert.InRange(v21.ExpiresAt!.Value, before.AddDays(6.9), before.AddDays(7.1));
        Assert.Equal(new IssuedAccessToken("abc", null), fixedToken);
    }

    [Fact]
    public async Task V21_token_is_stored_and_reused_by_other_instances()
    {
        var channel = Channel(LineTokenMode.JwtAssertion, NewRsaJwk());

        var issued = await NewProvider().GetAccessTokenAsync(channel, default);
        var reused = await NewProvider().GetAccessTokenAsync(channel, default); // fresh memory cache, same DB

        Assert.Equal(issued, reused);
        Assert.Equal(1, _line.Count("oauth2/v2.1/token"));
    }

    [Fact]
    public async Task Rejected_issued_token_is_renewed_and_call_retried_once()
    {
        var provider = NewProvider();
        var client = new LineApiClient(new HttpClient(_line), provider, NullLogger<LineApiClient>.Instance);
        _line.RejectNextBotCall = true;

        var result = await client.GetBotInfoAsync(Channel(LineTokenMode.Stateless));

        Assert.NotNull(result.Body);
        Assert.Equal(2, _line.Count("oauth2/v3/token"));
        Assert.Equal(2, _line.Count("v2/bot/info"));
    }

    [Fact]
    public async Task Jwt_assertion_matches_LINE_spec()
    {
        using var rsa = RSA.Create(2048);
        var channel = Channel(LineTokenMode.JwtAssertion, NewRsaJwk(rsa));

        var assertion = LineChannelTokenProvider.CreateAssertion(channel, TimeProvider.System);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = ChannelId,
            ValidAudience = "https://api.line.me/",
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
        });
        Assert.True(result.IsValid, result.Exception?.Message);

        var token = (JsonWebToken)result.SecurityToken;
        Assert.Equal("kid-1", token.Kid);
        Assert.Equal("RS256", token.Alg);
        Assert.Equal(ChannelId, token.Subject);
        Assert.Equal(604800L, token.GetPayloadValue<long>("token_exp"));
    }

    [Theory]
    [InlineData("v2/bot/richmenu/list", true)]
    [InlineData("v2/bot/message/123/content", true)]
    [InlineData("v2/bot/../oauth2/v2.1/token", false)]
    [InlineData("oauth2/v2.1/token", false)]
    [InlineData("v2/bot/info?x=1", false)]
    public void Proxy_allows_only_messaging_api_paths(string path, bool allowed) =>
        Assert.Equal(allowed, LineApiClient.IsAllowedProxyPath(path));

    [Theory]
    [InlineData("v2/bot/message/123/content", "https://api-data.line.me/")]
    [InlineData("v2/bot/richmenu/abc/content", "https://api-data.line.me/")]
    [InlineData("v2/bot/richmenu/list", "https://api.line.me/")]
    public async Task Proxy_routes_binary_endpoints_to_data_host(string path, string host)
    {
        var client = new LineApiClient(new HttpClient(_line), NewProvider(), NullLogger<LineApiClient>.Instance);

        using var _ = await client.ProxyAsync(Channel(LineTokenMode.Stateless), HttpMethod.Get, path, "", null, null, null, default);

        Assert.StartsWith(host, _line.LastBotUrl);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private LineChannelTokenProvider NewProvider() => new(
        new FakeHttpClientFactory(_line),
        new MemoryCache(new MemoryCacheOptions()),
        _services.GetRequiredService<IServiceScopeFactory>(),
        new PlainSecrets(),
        TimeProvider.System,
        NullLogger<LineChannelTokenProvider>.Instance);

    private static LineChannelConfig Channel(LineTokenMode mode, string? privateJwk = null) =>
        new(Guid.NewGuid(), LineChannelType.Messaging, ChannelId, "P1", "secret", null, mode,
            AssertionKeyId: privateJwk is null ? null : "kid-1", AssertionPrivateKey: privateJwk);

    private static string NewRsaJwk(RSA? rsa = null)
    {
        rsa ??= RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        static string B(byte[]? v) => Base64UrlEncoder.Encode(v);
        return $$"""
            {"kty":"RSA","alg":"RS256","n":"{{B(p.Modulus)}}","e":"{{B(p.Exponent)}}","d":"{{B(p.D)}}","p":"{{B(p.P)}}","q":"{{B(p.Q)}}","dp":"{{B(p.DP)}}","dq":"{{B(p.DQ)}}","qi":"{{B(p.InverseQ)}}"}
            """;
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Fake LINE: issues numbered tokens and answers bot calls; can reject one bot call with 401.</summary>
    private sealed class FakeLine : HttpMessageHandler
    {
        private readonly List<string> _calls = [];
        private int _issued;

        public bool RejectNextBotCall { get; set; }
        public string LastBotUrl { get; private set; } = "";

        public int Count(string path) => _calls.Count(c => c.Contains(path, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (_calls) _calls.Add(url);

            if (url.Contains("/oauth2/"))
            {
                var token = $"token-{Interlocked.Increment(ref _issued)}";
                // Stateless tokens live 15 minutes; v2.1 tokens live as long as the requested token_exp (7 days).
                var expiresIn = url.Contains("/v2.1/") ? 604800 : 900;
                return Json($$"""{"access_token":"{{token}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""");
            }

            LastBotUrl = url;
            if (RejectNextBotCall)
            {
                RejectNextBotCall = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            return Json("""{"userId":"Ubot","basicId":"@payyet","displayName":"Payyet"}""");
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
