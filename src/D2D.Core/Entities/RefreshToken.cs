namespace D2D.Core.Entities;

/// <summary>Opaque refresh token, stored as a hash and rotated on every use.</summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid AppId { get; set; }
    public string TokenHash { get; set; } = default!;

    /// <summary>All tokens rotated from the same sign-in share a family, so reuse can revoke them together.</summary>
    public Guid FamilyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? CreatedByIp { get; set; }
}
