namespace D2D.Core.Entities;

/// <summary>A client application (tenant) that consumes the central API.</summary>
public class App
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Public, lowercase identifier sent by clients in the X-App-Id header.</summary>
    public string Code { get; set; } = default!;

    public string Name { get; set; } = default!;

    /// <summary>SHA-256 hash of the server-to-server API key. The plain key is shown only once.</summary>
    public string ApiKeyHash { get; set; } = default!;

    public bool IsActive { get; set; } = true;

    /// <summary>When false, only users explicitly added to the app can sign in.</summary>
    public bool AllowSelfRegistration { get; set; } = true;

    /// <summary>
    /// When true, register/login/refresh/logout and the LINE token endpoints only accept calls from the app's backend
    /// (X-App-Id + X-Api-Key), so credentials are only ever handled by a registered app, never by an arbitrary client.
    /// </summary>
    public bool RequireAppKeyForAuth { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AppDatabase? Database { get; set; }
    public List<AppRedirectUri> RedirectUris { get; set; } = [];
    public List<LineChannel> LineChannels { get; set; } = [];
}

/// <summary>The app's own business database. Each app may use a different engine.</summary>
public class AppDatabase
{
    public Guid AppId { get; set; }
    public DbProvider Provider { get; set; }

    /// <summary>Connection string encrypted with <see cref="Abstractions.ISecretProtector"/>.</summary>
    public string ConnectionStringEncrypted { get; set; } = default!;

    /// <summary>
    /// When true the owning app's backend may fetch the decrypted connection string
    /// (GET /api/v1/database/credentials). Off by default so a leaked app API key cannot read it.
    /// </summary>
    public bool AllowCredentialAccess { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Allow-listed URI the API may redirect to after a browser login (e.g. LINE Login).</summary>
public class AppRedirectUri
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    public string Uri { get; set; } = default!;
}

public enum DbProvider
{
    SqlServer = 1,
    PostgreSql = 2,
    MySql = 3,
    Sqlite = 4,
}
