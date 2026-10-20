using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Line;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

/// <summary>Receives webhooks for every app's Messaging API channel: POST /webhooks/line/{channelId}.</summary>
[ApiController]
[Route("webhooks/line")]
[AllowAnonymous]
public sealed class LineWebhookController(IAppRegistry registry, LineWebhookIngestor ingestor) : ControllerBase
{
    [HttpPost("{channelId}")]
    [RequestSizeLimit(1_000_000)]
    public async Task<IActionResult> Receive(string channelId, CancellationToken ct)
    {
        var channel = await registry.FindLineChannelAsync(channelId, ct);
        if (channel is null || channel.Type != LineChannelType.Messaging) return NotFound();

        // The signature covers the exact raw bytes, so read the body before any JSON parsing.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        if (!LineSignature.IsValid(channel.ChannelSecret, body.Span, Request.Headers["x-line-signature"]))
            return Unauthorized();

        await ingestor.IngestAsync(channel, body, ct);
        return Ok();
    }
}
