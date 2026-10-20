using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace D2D.Migrations.MySql;

/// <summary>
/// Used only by "dotnet ef" to build the model and migrations for this provider. The connection string is a placeholder:
/// generating a migration never connects to a database.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseMySql("Server=localhost;Database=d2d_design;User=design;Password=design", new MySqlServerVersion(new Version(8, 0, 36)), b => b.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options;
        return new MasterDbContext(options);
    }
}
