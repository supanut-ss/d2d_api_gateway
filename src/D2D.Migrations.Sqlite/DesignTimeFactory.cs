using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace D2D.Migrations.Sqlite;

/// <summary>
/// Used only by "dotnet ef" to build the model and migrations for this provider. The connection string is a placeholder:
/// generating a migration never connects to a database.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseSqlite("Data Source=d2d_design.db", b => b.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options;
        return new MasterDbContext(options);
    }
}
