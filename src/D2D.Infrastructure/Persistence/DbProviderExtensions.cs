using System.Collections.Concurrent;
using D2D.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace D2D.Infrastructure.Persistence;

public static class DbProviderExtensions
{
    // AutoDetect opens a connection, so resolve each MySQL server version only once.
    private static readonly ConcurrentDictionary<string, ServerVersion> MySqlVersions = new();

    /// <summary>Configures the EF Core provider chosen at runtime (master DB or per-app DB).</summary>
    public static DbContextOptionsBuilder UseProvider(this DbContextOptionsBuilder builder, DbProvider provider, string connectionString) =>
        provider switch
        {
            DbProvider.SqlServer => builder.UseSqlServer(connectionString, o => o.EnableRetryOnFailure()),
            DbProvider.PostgreSql => builder.UseNpgsql(connectionString, o => o.EnableRetryOnFailure()),
            DbProvider.MySql => builder.UseMySql(
                connectionString,
                MySqlVersions.GetOrAdd(connectionString, ServerVersion.AutoDetect),
                o => o.EnableRetryOnFailure()),
            DbProvider.Sqlite => builder.UseSqlite(connectionString),
            _ => throw new NotSupportedException($"Database provider '{provider}' is not supported."),
        };
}
