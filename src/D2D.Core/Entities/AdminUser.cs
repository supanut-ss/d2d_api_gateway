namespace D2D.Core.Entities;

/// <summary>
/// Someone who signs in to the admin console. Separate from <see cref="User"/> on purpose:
/// no app user (even one who self-registered) can ever become a platform admin.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = default!;
    public string NormalizedEmail { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public AdminRole Role { get; set; } = AdminRole.Admin;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public enum AdminRole
{
    /// <summary>Can view and change everything.</summary>
    Admin = 1,

    /// <summary>Can view everything, cannot change anything.</summary>
    Viewer = 2,
}

/// <summary>A change made through the admin API. Request bodies are never stored, so no secret can end up here.</summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>The admin's email, or "admin-key" when the static admin key was used.</summary>
    public string Actor { get; set; } = default!;

    public string Method { get; set; } = default!;
    public string Path { get; set; } = default!;
    public string? Ip { get; set; }
}
