using System.Collections.Concurrent;
using D2D.Core;
using D2D.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace D2D.Infrastructure.Persistence;

/// <summary>Creates DbContexts bound to an app's own database (provider + connection string per app).</summary>
public interface ITenantDbContextFactory
{
    /// <summary>Context for the current request's app. Caller disposes it.</summary>
    Task<TContext> CreateAsync<TContext>(CancellationToken ct = default) where TContext : DbContext;

    Task<TContext> CreateAsync<TContext>(Guid appId, CancellationToken ct = default) where TContext : DbContext;
}

public sealed class TenantDbContextFactory(ITenantContext tenant, IAppRegistry registry, IServiceProvider services)
    : ITenantDbContextFactory
{
    // Keyed by settings version so a changed connection string produces fresh options.
    private static readonly ConcurrentDictionary<(Guid AppId, Type Context, DateTime Version), DbContextOptions> OptionsCache = new();

    public Task<TContext> CreateAsync<TContext>(CancellationToken ct = default) where TContext : DbContext =>
        CreateAsync<TContext>(tenant.Required.Id, ct);

    public async Task<TContext> CreateAsync<TContext>(Guid appId, CancellationToken ct = default) where TContext : DbContext
    {
        var db = await registry.GetDatabaseAsync(appId, ct)
            ?? throw ApiException.Conflict("App database not configured", "Configure it with PUT /api/v1/admin/apps/{id}/database.");

        var options = OptionsCache.GetOrAdd((appId, typeof(TContext), db.Version), _ =>
        {
            var builder = new DbContextOptionsBuilder<TContext>();
            builder.UseProvider(db.Provider, db.ConnectionString);
            return builder.Options;
        });

        return ActivatorUtilities.CreateInstance<TContext>(services, options);
    }
}
