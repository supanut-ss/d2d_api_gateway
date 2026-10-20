namespace D2D.Core.Entities;

/// <summary>Inbox row for a LINE webhook event. Stored first, processed asynchronously.</summary>
public class LineWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    public string ChannelId { get; set; } = default!;

    /// <summary>Bot user id from the webhook envelope ("destination").</summary>
    public string? Destination { get; set; }

    /// <summary>LINE's unique event id; used to drop redelivered duplicates.</summary>
    public string WebhookEventId { get; set; } = default!;

    public string EventType { get; set; } = default!;
    public string? SourceUserId { get; set; }
    public bool IsRedelivery { get; set; }

    /// <summary>Raw JSON of the single event.</summary>
    public string Payload { get; set; } = default!;

    public WebhookEventStatus Status { get; set; } = WebhookEventStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public enum WebhookEventStatus
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3,
}
