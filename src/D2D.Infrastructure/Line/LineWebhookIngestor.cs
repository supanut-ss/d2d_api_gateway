using System.Text.Json;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace D2D.Infrastructure.Line;

/// <summary>
/// Stores verified webhook events in the inbox table so the HTTP response returns fast;
/// <see cref="LineWebhookProcessor"/> handles them afterwards.
/// </summary>
public sealed class LineWebhookIngestor(MasterDbContext db, WebhookSignal signal, ILogger<LineWebhookIngestor> logger)
{
    /// <returns>Number of new events stored.</returns>
    public async Task<int> IngestAsync(LineChannelConfig channel, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // The console "Verify" button sends an empty events array.
        if (!root.TryGetProperty("events", out var events) || events.GetArrayLength() == 0) return 0;

        var destination = root.TryGetProperty("destination", out var d) ? d.GetString() : null;
        var stored = 0;

        foreach (var e in events.EnumerateArray())
        {
            var eventId = e.TryGetProperty("webhookEventId", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(eventId)) continue;

            // LINE redelivers on failure; the unique index is the final guard against races.
            if (await db.LineWebhookEvents.AnyAsync(x => x.WebhookEventId == eventId, ct)) continue;

            db.LineWebhookEvents.Add(new LineWebhookEvent
            {
                AppId = channel.AppId,
                ChannelId = channel.ChannelId,
                Destination = destination,
                WebhookEventId = eventId,
                EventType = e.TryGetProperty("type", out var type) ? type.GetString() ?? "unknown" : "unknown",
                SourceUserId = e.TryGetProperty("source", out var s) && s.TryGetProperty("userId", out var u) ? u.GetString() : null,
                IsRedelivery = e.TryGetProperty("deliveryContext", out var dc)
                    && dc.TryGetProperty("isRedelivery", out var r) && r.ValueKind == JsonValueKind.True,
                Payload = e.GetRawText(),
            });

            try
            {
                await db.SaveChangesAsync(ct);
                stored++;
            }
            catch (DbUpdateException ex)
            {
                logger.LogInformation(ex, "Duplicate LINE webhook event {EventId} ignored", eventId);
                db.ChangeTracker.Clear();
            }
        }

        if (stored > 0) signal.Notify();
        return stored;
    }
}
