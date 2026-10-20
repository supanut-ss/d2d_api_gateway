using D2D.Core.Entities;

namespace D2D.Infrastructure.Options;

public sealed class MasterDbOptions
{
    public const string Section = "MasterDb";

    public DbProvider Provider { get; set; } = DbProvider.SqlServer;
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Applies pending EF Core migrations of the master database when the app starts (creating the database schema on the first run).
    /// Fine for a single instance; with several instances apply migrations from a script or one-off job instead.
    /// </summary>
    public bool AutoMigrate { get; set; }
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>"iss" claim. Use the public base URL of this API so OpenID discovery matches the tokens.</summary>
    public string Issuer { get; set; } = "d2d-api";

    public string Audience { get; set; } = "d2d-apps";

    /// <summary>RSA private key (PEM) that signs access tokens (RS256). Takes precedence over <see cref="PrivateKeyPath"/>.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// PEM file holding the signing key; generated on first start when missing.
    /// Defaults to jwt-signing.pem inside DataProtection:KeysPath. Keep it outside the web root and back it up.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Public keys (PEM) of previous signing keys, still published in the JWKS so tokens signed before a rotation stay valid.</summary>
    public string[] RetiredPublicKeys { get; set; } = [];

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Audience of admin console tokens. Different from <see cref="Audience"/>, so the two kinds never work for each other.</summary>
    public string AdminAudience { get; set; } = "d2d-admin";

    public int AdminAccessTokenMinutes { get; set; } = 15;

    /// <summary>An admin session ends this many hours after the last refresh.</summary>
    public int AdminSessionHours { get; set; } = 12;
}

public sealed class ServerOptions
{
    public const string Section = "Server";

    /// <summary>
    /// Public URL of this API including any IIS application path, e.g. https://api.example.com/d2d.
    /// Used in OpenID discovery and admin responses. When empty it is derived from the request.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

public sealed class LineOptions
{
    public const string Section = "Line";

    /// <summary>Callback URL of this API, registered in every LINE Login channel.</summary>
    public string LoginCallbackUrl { get; set; } = "";

    public string Scope { get; set; } = "profile openid email";
    public int AuthStateMinutes { get; set; } = 10;
}

public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Platform admin key for /api/v1/admin. Empty disables the admin API.</summary>
    public string ApiKey { get; set; } = "";
}
