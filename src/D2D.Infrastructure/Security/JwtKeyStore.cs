using System.Security.Cryptography;
using System.Text.Json.Serialization;
using D2D.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Infrastructure.Security;

/// <summary>Public half of a signing key in JWK form (RFC 7517), as published in the JWKS.</summary>
public sealed record PublicJwk(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

/// <summary>
/// RS256 keys for access tokens. Other backends validate tokens with the public keys from the JWKS
/// and never need a shared secret.
/// </summary>
public interface IJwtKeyStore
{
    SigningCredentials SigningCredentials { get; }

    /// <summary>Current public key plus retired ones.</summary>
    IReadOnlyList<SecurityKey> ValidationKeys { get; }

    IReadOnlyList<PublicJwk> PublicJwks { get; }
}

public sealed class JwtKeyStore : IJwtKeyStore
{
    private readonly List<(RsaSecurityKey Key, PublicJwk Jwk)> _public = [];

    public SigningCredentials SigningCredentials { get; }
    public IReadOnlyList<SecurityKey> ValidationKeys => _public.Select(k => (SecurityKey)k.Key).ToList();
    public IReadOnlyList<PublicJwk> PublicJwks => _public.Select(k => k.Jwk).ToList();

    public JwtKeyStore(IOptions<JwtOptions> options, ILogger<JwtKeyStore> logger)
    {
        var jwt = options.Value;

        // Never disposed: the singleton lives for the whole process and signing needs the key.
        var rsa = LoadOrCreate(jwt, logger);
        var current = new RsaSecurityKey(rsa) { KeyId = ComputeKeyId(rsa) };
        SigningCredentials = new SigningCredentials(current, SecurityAlgorithms.RsaSha256)
        {
            // Signing is deterministic per key; caching the signature provider is safe and faster.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = true },
        };
        AddPublic(rsa);

        foreach (var pem in jwt.RetiredPublicKeys.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var retired = RSA.Create();
            retired.ImportFromPem(pem);
            AddPublic(retired);
        }
    }

    private void AddPublic(RSA rsa)
    {
        var kid = ComputeKeyId(rsa);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var publicKey = new RsaSecurityKey(parameters) { KeyId = kid };
        var jwk = new PublicJwk(
            "RSA", "sig", SecurityAlgorithms.RsaSha256, kid,
            Base64UrlEncoder.Encode(parameters.Modulus), Base64UrlEncoder.Encode(parameters.Exponent));
        _public.Add((publicKey, jwk));
    }

    /// <summary>kid = SHA-256 of the SubjectPublicKeyInfo, so it is stable and identical on every instance.</summary>
    private static string ComputeKeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    private static RSA LoadOrCreate(JwtOptions jwt, ILogger logger)
    {
        var rsa = RSA.Create();

        if (!string.IsNullOrWhiteSpace(jwt.PrivateKeyPem))
        {
            rsa.ImportFromPem(jwt.PrivateKeyPem);
            return rsa;
        }

        var path = jwt.PrivateKeyPath
            ?? throw new InvalidOperationException("Set Jwt:PrivateKeyPem or Jwt:PrivateKeyPath to the RS256 signing key.");

        if (!File.Exists(path))
        {
            rsa.KeySize = 2048;
            var pem = rsa.ExportPkcs8PrivateKeyPem();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                // CreateNew: if another process created the key first, use its key instead of overwriting it.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(pem);
                logger.LogWarning("Generated a new JWT signing key at {Path}. Back it up: losing it signs everyone out.", path);
                return rsa;
            }
            catch (IOException) when (File.Exists(path))
            {
                rsa.Dispose();
                rsa = RSA.Create();
            }
        }

        rsa.ImportFromPem(File.ReadAllText(path));
        return rsa;
    }
}
