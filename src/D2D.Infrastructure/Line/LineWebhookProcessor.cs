using System.Threading.Channels;
using D2D.Core.Entities;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace D2D.Infrastructure.Line;

/// <summary>Wakes the processor as soon as new events are stored instead of waiting for the next poll.</summary>
public sealed class WebhookSignal
{
    private readonly Channel<bool> _channel =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Poll interval elapsed.
        }
    }
}

/// <summary>
/// Processes the webhook inbox with retries and exponential backoff. Safe to run on several
/// instances: each event is claimed with a conditional update before it is handled.
/// </summary>
public sealed class LineWebhookProcessor(
    IServiceScopeFactory scopeFactory,
    WebhookSignal signal,
    TimeProvider clock,
    ILogger<LineWebhookProcessor> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ProcessBatchAsync(stoppingToken);
                if (count < BatchSize) await signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LINE webhook processing loop failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        var ids = await Due(db.LineWebhookEvents, now)
            .OrderBy(x => x.ReceivedAt)
            .Take(BatchSize)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var lockedUntil = now + LockDuration;
            var claimed = await Due(db.LineWebhookEvents.Where(x => x.Id == id), now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, WebhookEventStatus.Processing)
                    .SetProperty(x => x.LockedUntil, lockedUntil)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);

            if (claimed == 1) await ProcessOneAsync(id, ct);
        }

        return ids.Count;
    }

    /// <summary>Pending events whose retry time has come, plus events abandoned by a crashed worker.</summary>
    private static IQueryable<LineWebhookEvent> Due(IQueryable<LineWebhookEvent> events, DateTime now) =>
        events.Where(x =>
            (x.Status == WebhookEventStatus.Pending && x.NextAttemptAt <= now) ||
            (x.Status == WebhookEventStatus.Processing && x.LockedUntil < now));

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        // A scope per event gives handlers a clean tenant context and DbContext.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var evt = await db.LineWebhookEvents.FirstAsync(x => x.Id == id, ct);

        try
        {
            await scope.ServiceProvider.GetRequiredService<LineEventDispatcher>().DispatchAsync(evt, ct);
            evt.Status = WebhookEventStatus.Processed;
            evt.ProcessedAt = clock.GetUtcNow().UtcDateTime;
            evt.LastError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "LINE event {EventId} ({Type}) failed on attempt {Attempt}", evt.WebhookEventId, evt.EventType, evt.Attempts);
            evt.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            if (evt.Attempts >= MaxAttempts)
            {
                evt.Status = WebhookEventStatus.Failed;
            }
            else
            {
                evt.Status = WebhookEventStatus.Pending;
                evt.NextAttemptAt = clock.GetUtcNow().UtcDateTime + Backoff(evt.Attempts);
            }
        }

        evt.LockedUntil = null;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // 30s, 1m, 2m, 4m
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(15 * Math.Pow(2, attempt));
}
