using System.Text.Json;
using D2D.Core.Tenancy;

namespace D2D.Core.Line;

/// <summary>A single LINE webhook event, dispatched within the owning app's tenant scope.</summary>
public sealed record LineEventContext(TenantInfo App, LineChannelConfig Channel, string EventType, JsonElement Event)
{
    public string? ReplyToken =>
        Event.TryGetProperty("replyToken", out var token) ? token.GetString() : null;

    public string? SourceUserId =>
        Event.TryGetProperty("source", out var source) && source.TryGetProperty("userId", out var userId)
            ? userId.GetString()
            : null;
}

/// <summary>
/// Handles LINE webhook events in-process. Handlers must be idempotent: a failed event is retried
/// and every matching handler runs again.
/// </summary>
public interface ILineEventHandler
{
    bool CanHandle(LineEventContext context);
    Task HandleAsync(LineEventContext context, CancellationToken ct);
}

/// <summary>Result of a Messaging API call: LINE's response body and its X-Line-Request-Id.</summary>
public sealed record LineApiResult(JsonElement? Body, string? RequestId);

/// <summary>
/// Messaging API for a channel. Access tokens are resolved per channel (static, stateless or v2.1).
/// <paramref name="retryKey"/> (X-Line-Retry-Key, a UUID) makes a retried send idempotent; one is generated when omitted.
/// </summary>
public interface ILineMessagingClient
{
    Task<LineApiResult> PushAsync(LineChannelConfig channel, string to, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default);
    Task<LineApiResult> ReplyAsync(LineChannelConfig channel, string replyToken, IReadOnlyList<JsonElement> messages, CancellationToken ct = default);
    Task<LineApiResult> MulticastAsync(LineChannelConfig channel, IReadOnlyList<string> to, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default);
    Task<LineApiResult> BroadcastAsync(LineChannelConfig channel, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default);

    /// <summary>Asynchronous on LINE's side: poll progress with the returned RequestId.</summary>
    Task<LineApiResult> NarrowcastAsync(LineChannelConfig channel, JsonElement request, string? retryKey = null, CancellationToken ct = default);

    Task<LineApiResult> GetNarrowcastProgressAsync(LineChannelConfig channel, string requestId, CancellationToken ct = default);
    Task<LineApiResult> GetProfileAsync(LineChannelConfig channel, string userId, CancellationToken ct = default);
    Task<LineApiResult> GetBotInfoAsync(LineChannelConfig channel, CancellationToken ct = default);
}

public static class LineMessages
{
    public static JsonElement Text(string text) => JsonSerializer.SerializeToElement(new { type = "text", text });
}
