using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using D2D.Core.Entities;
using D2D.Core.Line;
using D2D.Core.Tenancy;
using Microsoft.Extensions.Logging;

namespace D2D.Infrastructure.Line;

/// <summary>Runs in-process handlers for an event, then forwards it to the app backend if configured.</summary>
public sealed class LineEventDispatcher(
    IAppRegistry registry,
    ITenantContext tenant,
    IEnumerable<ILineEventHandler> handlers,
    LineWebhookForwarder forwarder)
{
    public async Task DispatchAsync(LineWebhookEvent evt, CancellationToken ct)
    {
        var app = await registry.FindByIdAsync(evt.AppId, ct)
            ?? throw new InvalidOperationException($"App {evt.AppId} not found");
        var channel = await registry.FindLineChannelAsync(evt.ChannelId, ct)
            ?? throw new InvalidOperationException($"LINE channel {evt.ChannelId} not found or inactive");

        // Handlers can now use ITenantDbContextFactory to reach this app's own database.
        tenant.Set(app);

        using var doc = JsonDocument.Parse(evt.Payload);
        var context = new LineEventContext(app, channel, evt.EventType, doc.RootElement);
        foreach (var handler in handlers.Where(h => h.CanHandle(context)))
            await handler.HandleAsync(context, ct);

        if (!string.IsNullOrEmpty(channel.WebhookForwardUrl))
            await forwarder.ForwardAsync(channel, evt.Destination, evt.Payload, ct);
    }
}

/// <summary>
/// Re-sends an event to the app backend in LINE's own envelope, signed with the channel secret,
/// so the backend can verify it with the official LINE SDK unchanged.
/// </summary>
public sealed class LineWebhookForwarder(HttpClient http)
{
    public async Task ForwardAsync(LineChannelConfig channel, string? destination, string eventJson, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"{{\"destination\":{JsonSerializer.Serialize(destination)},\"events\":[{eventJson}]}}");

        using var request = new HttpRequestMessage(HttpMethod.Post, channel.WebhookForwardUrl) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("x-line-signature", LineSignature.Sign(channel.ChannelSecret, body));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Default handler: logs every event. Replace or add app-specific handlers.</summary>
public sealed class LoggingLineEventHandler(ILogger<LoggingLineEventHandler> logger) : ILineEventHandler
{
    public bool CanHandle(LineEventContext context) => true;

    public Task HandleAsync(LineEventContext context, CancellationToken ct)
    {
        logger.LogInformation("LINE {Type} event for app {App} from {User}", context.EventType, context.App.Code, context.SourceUserId);
        return Task.CompletedTask;
    }
}
