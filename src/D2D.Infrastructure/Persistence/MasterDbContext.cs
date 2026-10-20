using D2D.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Persistence;

/// <summary>Central database shared by all apps: apps, identities, tokens, LINE config and webhook inbox.</summary>
public class MasterDbContext(DbContextOptions<MasterDbContext> options) : DbContext(options)
{
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppDatabase> AppDatabases => Set<AppDatabase>();
    public DbSet<AppRedirectUri> AppRedirectUris => Set<AppRedirectUri>();
    public DbSet<LineChannel> LineChannels => Set<LineChannel>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserApp> UserApps => Set<UserApp>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LineWebhookEvent> LineWebhookEvents => Set<LineWebhookEvent>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<App>(e =>
        {
            e.ToTable("apps");
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ApiKeyHash).HasMaxLength(128);
            e.HasOne(x => x.Database).WithOne().HasForeignKey<AppDatabase>(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RedirectUris).WithOne().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.LineChannels).WithOne().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppDatabase>(e =>
        {
            e.ToTable("app_databases");
            e.HasKey(x => x.AppId);
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<AppRedirectUri>(e =>
        {
            e.ToTable("app_redirect_uris");
            e.Property(x => x.Uri).HasMaxLength(500);
            e.HasIndex(x => new { x.AppId, x.Uri }).IsUnique();
        });

        b.Entity<LineChannel>(e =>
        {
            e.ToTable("line_channels");
            e.HasIndex(x => x.ChannelId).IsUnique();
            e.Property(x => x.ChannelId).HasMaxLength(32);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TokenMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AssertionKeyId).HasMaxLength(128);
            e.Property(x => x.WebhookForwardUrl).HasMaxLength(500);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.PictureUrl).HasMaxLength(1000);
            e.HasMany(x => x.Apps).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ExternalLogins).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserApp>(e =>
        {
            e.ToTable("user_apps");
            e.HasKey(x => new { x.UserId, x.AppId });
            e.Property(x => x.Roles).HasMaxLength(500);
            e.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ExternalLogin>(e =>
        {
            e.ToTable("external_logins");
            e.HasIndex(x => new { x.Provider, x.ProviderScope, x.ProviderKey }).IsUnique();
            // One identity per provider scope per user (e.g. one LINE account per LINE provider).
            e.HasIndex(x => new { x.UserId, x.Provider, x.ProviderScope }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderScope).HasMaxLength(64);
            e.Property(x => x.ChannelId).HasMaxLength(32);
            e.Property(x => x.ProviderKey).HasMaxLength(128);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.Property(x => x.CreatedByIp).HasMaxLength(64);
        });

        b.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users");
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasIndex(x => x.At);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Method).HasMaxLength(10);
            e.Property(x => x.Path).HasMaxLength(500);
            e.Property(x => x.Ip).HasMaxLength(64);
        });

        b.Entity<LineWebhookEvent>(e =>
        {
            e.ToTable("line_webhook_events");
            e.HasIndex(x => x.WebhookEventId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ChannelId).HasMaxLength(32);
            e.Property(x => x.Destination).HasMaxLength(64);
            e.Property(x => x.WebhookEventId).HasMaxLength(64);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.SourceUserId).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });
    }
}
