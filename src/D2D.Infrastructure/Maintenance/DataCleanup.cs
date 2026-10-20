using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2D.Infrastructure.Maintenance;

public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Processed LINE events are only kept to drop redeliveries, so a short window is enough.</summary>
    public int ProcessedWebhookRetentionDays { get; set; } = 14;

    /// <summary>Failed events are kept longer so someone can look at them.</summary>
    public int FailedWebhookRetentionDays { get; set; } = 90;

    /// <summary>Refresh tokens are removed this many days after they expire (revoked ones too: they are useless after expiry).</summary>
    public int ExpiredTokenGraceDays { get; set; } = 1;
}

public sealed record CleanupResult(int WebhookEvents, int RefreshTokens);

/// <summary>Deletes data that has no further use so the master tables do not grow forever.</summary>
public sealed class DataCleanup(MasterDbContext db, IOptions<MaintenanceOptions> options, TimeProvider clock)
{
    public async Task<CleanupResult> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var processedBefore = now.AddDays(-o.ProcessedWebhookRetentionDays);
        var failedBefore = now.AddDays(-o.FailedWebhookRetentionDays);
        var tokensBefore = now.AddDays(-o.ExpiredTokenGraceDays);

        var events = await db.LineWebhookEvents
            .Where(e => (e.Status == WebhookEventStatus.Processed && e.ProcessedAt < processedBefore) ||
                        (e.Status == WebhookEventStatus.Failed && e.ReceivedAt < failedBefore))
            .ExecuteDeleteAsync(ct);

        // A revoked token must stay until it expires: replaying it is how token theft is detected.
        var tokens = await db.RefreshTokens.Where(t => t.ExpiresAt < tokensBefore).ExecuteDeleteAsync(ct);

        return new CleanupResult(events, tokens);
    }
}

public sealed class DataMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<MaintenanceOptions> options,
    ILogger<DataMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        try
        {
            // Let startup finish before the first run.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<DataCleanup>().RunAsync(ct);
            if (result.WebhookEvents + result.RefreshTokens > 0)
                logger.LogInformation("Cleanup removed {Events} webhook events and {Tokens} refresh tokens", result.WebhookEvents, result.RefreshTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Data cleanup failed; will retry on the next interval");
        }
    }
}
