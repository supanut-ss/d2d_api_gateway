using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Persistence.AppData;

/// <summary>
/// Sample per-app business database. Real app modules add their own DbContext and
/// obtain it through <see cref="ITenantDbContextFactory"/>.
/// </summary>
public class AppDataDbContext(DbContextOptions<AppDataDbContext> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => x.OwnerUserId);
        });
    }
}

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public string Title { get; set; } = default!;
    public string? Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
