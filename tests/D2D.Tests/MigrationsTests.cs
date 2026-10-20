using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace D2D.Tests;

/// <summary>
/// Guards the per-provider migrations. None of these tests connects to a database.
/// If one fails after you changed an entity or MasterDbContext, add a migration: scripts/add-migration.ps1 &lt;Name&gt;.
/// </summary>
public class MigrationsTests
{
    public static TheoryData<DbProvider> Providers() => new(Enum.GetValues<DbProvider>());

    [Theory]
    [MemberData(nameof(Providers))]
    public void Migrations_are_in_sync_with_the_model(DbProvider provider)
    {
        using var db = Create(provider);

        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(
            db.Database.HasPendingModelChanges(),
            $"The {provider} migrations do not match the model. Run scripts/add-migration.ps1 <Name> to add a migration for every provider.");
    }

    [Fact]
    public void Every_provider_has_the_same_migrations()
    {
        // The id is "<timestamp>_<Name>"; the timestamps differ per provider, the names must not.
        var byProvider = Enum.GetValues<DbProvider>().ToDictionary(
            p => p,
            p =>
            {
                using var db = Create(p);
                return db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]).ToList();
            });

        var expected = byProvider[DbProvider.SqlServer];
        foreach (var (provider, names) in byProvider)
            Assert.True(expected.SequenceEqual(names), $"{provider} has migrations [{string.Join(", ", names)}] but SqlServer has [{string.Join(", ", expected)}]");
    }

    [Fact]
    public void Each_provider_has_its_own_migrations_assembly()
    {
        foreach (var provider in Enum.GetValues<DbProvider>())
        {
            using var db = Create(provider);
            var assembly = MigrationsAssemblies.For(provider);
            Assert.Equal($"D2D.Migrations.{provider}", assembly);
            Assert.All(db.Database.GetMigrations(), id => Assert.NotNull(id));
        }
    }

    [Fact]
    public async Task Migrations_build_a_working_database_from_scratch()
    {
        // Applies the real SQLite migrations to an empty database and uses the result.
        var path = Path.Combine(Path.GetTempPath(), "d2d-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<MasterDbContext>()
                .UseSqlite($"Data Source={path}", o => o.MigrationsAssembly(MigrationsAssemblies.For(DbProvider.Sqlite)))
                .Options;

            await using (var db = new MasterDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());

                db.Apps.Add(new App { Code = "demo", Name = "Demo", ApiKeyHash = "x" });
                db.AdminUsers.Add(new AdminUser { Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM", DisplayName = "A", PasswordHash = "h" });
                await db.SaveChangesAsync();
            }

            await using (var again = new MasterDbContext(options))
            {
                await again.Database.MigrateAsync(); // running it twice is harmless
                Assert.Equal(1, await again.Apps.CountAsync());
                Assert.Equal(1, await again.AdminUsers.CountAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static MasterDbContext Create(DbProvider provider)
    {
        var assembly = MigrationsAssemblies.For(provider);
        var builder = new DbContextOptionsBuilder<MasterDbContext>();
        switch (provider)
        {
            case DbProvider.SqlServer: builder.UseSqlServer("Server=.;Database=none", o => o.MigrationsAssembly(assembly)); break;
            case DbProvider.PostgreSql: builder.UseNpgsql("Host=localhost;Database=none", o => o.MigrationsAssembly(assembly)); break;
            // A fixed server version, so building the options never connects.
            case DbProvider.MySql: builder.UseMySql("Server=localhost;Database=none", new MySqlServerVersion(new Version(8, 0, 36)), o => o.MigrationsAssembly(assembly)); break;
            case DbProvider.Sqlite: builder.UseSqlite("Data Source=none.db", o => o.MigrationsAssembly(assembly)); break;
            default: throw new NotSupportedException(provider.ToString());
        }
        return new MasterDbContext(builder.Options);
    }
}
