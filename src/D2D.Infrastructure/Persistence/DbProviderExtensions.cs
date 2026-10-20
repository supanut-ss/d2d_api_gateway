using System.Collections.Concurrent;
using D2D.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Persistence;

public static class DbProviderExtensions
{
    // AutoDetect opens a connection, so resolve each MySQL server version only once.
    private static readonly ConcurrentDictionary<string, ServerVersion> MySqlVersions = new();

    /// <summary>
    /// Configures the EF Core provider chosen at runtime (master DB or per-app DB).
    /// <paramref name="migrationsAssembly"/> is the project holding that provider's migrations (see <see cref="MigrationsAssemblies"/>).
    /// </summary>
    public static DbContextOptionsBuilder UseProvider(
        this DbContextOptionsBuilder builder, DbProvider provider, string connectionString, string? migrationsAssembly = null) =>
        provider switch
        {
            DbProvider.SqlServer => builder.UseSqlServer(connectionString, o =>
            {
                o.EnableRetryOnFailure();
                if (migrationsAssembly is not null) o.MigrationsAssembly(migrationsAssembly);
            }),
            DbProvider.PostgreSql => builder.UseNpgsql(connectionString, o =>
            {
                o.EnableRetryOnFailure();
                if (migrationsAssembly is not null) o.MigrationsAssembly(migrationsAssembly);
            }),
            DbProvider.MySql => builder.UseMySql(
                connectionString,
                MySqlVersions.GetOrAdd(connectionString, ServerVersion.AutoDetect),
                o =>
                {
                    o.EnableRetryOnFailure();
                    if (migrationsAssembly is not null) o.MigrationsAssembly(migrationsAssembly);
                }),
            DbProvider.Sqlite => builder.UseSqlite(connectionString, o =>
            {
                if (migrationsAssembly is not null) o.MigrationsAssembly(migrationsAssembly);
            }),
            _ => throw new NotSupportedException($"Database provider '{provider}' is not supported."),
        };
}

/// <summary>
/// Migrations are provider specific (types, quoting, features), so each engine has its own project
/// (D2D.Migrations.SqlServer, .PostgreSql, .MySql, .Sqlite) holding the migrations of the master database.
/// </summary>
public static class MigrationsAssemblies
{
    public static string For(DbProvider provider) => $"D2D.Migrations.{provider}";
}
