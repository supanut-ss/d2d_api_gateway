using System.Security.Cryptography;
using System.Text.Json;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Tests;

public sealed class JwtKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d2d-jwt-" + Guid.NewGuid().ToString("N"));

    private static JwtKeyStore NewStore(JwtOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), NullLogger<JwtKeyStore>.Instance);

    [Fact]
    public void Generates_key_file_on_first_start_and_reuses_it()
    {
        var options = new JwtOptions { PrivateKeyPath = Path.Combine(_dir, "sub", "jwt-signing.pem") };

        var first = NewStore(options);
        Assert.True(File.Exists(options.PrivateKeyPath));
        var second = NewStore(options); // simulates a restart or a second instance

        Assert.Equal(first.PublicJwks[0].Kid, second.PublicJwks[0].Kid);
        Assert.Equal(first.PublicJwks[0].N, second.PublicJwks[0].N);
    }

    [Fact]
    public void Jwks_contains_only_public_parameters()
    {
        var keys = NewStore(new JwtOptions { PrivateKeyPath = Path.Combine(_dir, "k.pem") });

        var json = JsonSerializer.Serialize(new { keys = keys.PublicJwks });
        using var doc = JsonDocument.Parse(json);
        var jwk = doc.RootElement.GetProperty("keys")[0];

        Assert.Equal("RSA", jwk.GetProperty("kty").GetString());
        Assert.Equal("RS256", jwk.GetProperty("alg").GetString());
        Assert.Equal("sig", jwk.GetProperty("use").GetString());
        foreach (var secret in new[] { "d", "p", "q", "dp", "dq", "qi" })
            Assert.False(jwk.TryGetProperty(secret, out _), $"JWKS must not contain private parameter '{secret}'");
    }

    [Fact]
    public async Task Token_signed_by_store_validates_against_published_jwks_only()
    {
        var keys = NewStore(new JwtOptions { PrivateKeyPath = Path.Combine(_dir, "k.pem") });
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "iss",
            Audience = "aud",
            Claims = new Dictionary<string, object> { ["sub"] = "u1" },
            SigningCredentials = keys.SigningCredentials,
        });

        // What a backend does: build keys from the JWKS document alone.
        var jwks = new JsonWebKeySet(JsonSerializer.Serialize(new { keys = keys.PublicJwks }));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "iss",
            ValidAudience = "aud",
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(keys.PublicJwks[0].Kid, ((JsonWebToken)result.SecurityToken).Kid);
    }

    [Fact]
    public async Task Tokens_from_other_keys_and_hmac_tokens_are_rejected()
    {
        var keys = NewStore(new JwtOptions { PrivateKeyPath = Path.Combine(_dir, "k.pem") });
        var handler = new JsonWebTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = "iss", ValidAudience = "aud",
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };

        var foreign = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "iss", Audience = "aud",
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaSha256),
        });
        // Algorithm-confusion attempt: HS256 signed with the public key bytes as the secret.
        var hmac = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "iss", Audience = "aud",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Base64UrlEncoder.DecodeBytes(keys.PublicJwks[0].N)), SecurityAlgorithms.HmacSha256),
        });

        Assert.False((await handler.ValidateTokenAsync(foreign, parameters)).IsValid);
        Assert.False((await handler.ValidateTokenAsync(hmac, parameters)).IsValid);
    }

    [Fact]
    public async Task Retired_public_key_still_validates_old_tokens_after_rotation()
    {
        var oldPath = Path.Combine(_dir, "old.pem");
        var oldStore = NewStore(new JwtOptions { PrivateKeyPath = oldPath });
        var oldToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "iss", Audience = "aud", SigningCredentials = oldStore.SigningCredentials,
        });

        using var oldRsa = RSA.Create();
        oldRsa.ImportFromPem(File.ReadAllText(oldPath));
        var rotated = NewStore(new JwtOptions
        {
            PrivateKeyPath = Path.Combine(_dir, "new.pem"),
            RetiredPublicKeys = [oldRsa.ExportSubjectPublicKeyInfoPem()],
        });

        Assert.Equal(2, rotated.PublicJwks.Count);
        Assert.NotEqual(oldStore.PublicJwks[0].Kid, rotated.SigningCredentials.Kid);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(oldToken, new TokenValidationParameters
        {
            ValidIssuer = "iss", ValidAudience = "aud",
            IssuerSigningKeys = rotated.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public void Missing_key_configuration_fails_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => NewStore(new JwtOptions()));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
