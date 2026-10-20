namespace D2D.Core.Entities;

/// <summary>A global identity. Access to each app is granted through <see cref="UserApp"/>.</summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>Null for users that only sign in with an external provider (e.g. LINE).</summary>
    public string? PasswordHash { get; set; }

    public string DisplayName { get; set; } = default!;
    public string? PictureUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public List<UserApp> Apps { get; set; } = [];
    public List<ExternalLogin> ExternalLogins { get; set; } = [];

    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();
}

/// <summary>Membership of a user in an app, with roles scoped to that app.</summary>
public class UserApp
{
    public Guid UserId { get; set; }
    public Guid AppId { get; set; }

    /// <summary>Comma-separated roles, e.g. "user,admin".</summary>
    public string Roles { get; set; } = "user";

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public string[] GetRoles() =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Link between a user and an external identity provider account.</summary>
public class ExternalLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>Provider name, e.g. "line".</summary>
    public string Provider { get; set; } = default!;

    /// <summary>Provider-side user id (LINE "sub").</summary>
    public string ProviderKey { get; set; } = default!;

    /// <summary>
    /// Scope in which <see cref="ProviderKey"/> is unique. For LINE this is the LINE provider id,
    /// so every channel under one LINE provider resolves to the same user.
    /// </summary>
    public string ProviderScope { get; set; } = default!;

    /// <summary>Channel used when the identity was first linked (informational).</summary>
    public string ChannelId { get; set; } = default!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
