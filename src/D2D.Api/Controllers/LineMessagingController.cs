using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using D2D.Api.Auth;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Line;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Line;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

public sealed record PushMessageRequest(
    [Required] string To,
    [Required, MinLength(1), MaxLength(5)] JsonElement[] Messages);

public sealed record ReplyMessageRequest(
    [Required] string ReplyToken,
    [Required, MinLength(1), MaxLength(5)] JsonElement[] Messages);

public sealed record MulticastMessageRequest(
    [Required, MinLength(1), MaxLength(500)] string[] To,
    [Required, MinLength(1), MaxLength(5)] JsonElement[] Messages);

public sealed record BroadcastMessageRequest([Required, MinLength(1), MaxLength(5)] JsonElement[] Messages);

/// <summary>
/// Messaging API for app backends (X-App-Id + X-Api-Key). Uses the app's Messaging channel;
/// X-Line-Channel-Id picks one when the app has several. X-Line-Retry-Key is passed to LINE for idempotent retries.
/// </summary>
[ApiController]
[Route("api/v1/line")]
[Authorize(Policy = Policies.AppServer)]
public sealed class LineMessagingController(IAppRegistry registry, ITenantContext tenant, LineApiClient line) : ControllerBase
{
    private const string ChannelHeader = "X-Line-Channel-Id";
    private const string RetryKeyHeader = "X-Line-Retry-Key";
    private const string RequestIdHeader = "X-Line-Request-Id";

    [HttpPost("push")]
    public async Task<IActionResult> Push(PushMessageRequest request, CancellationToken ct) =>
        LineResult(await line.PushAsync(await ResolveChannelAsync(ct), request.To, request.Messages, RetryKey, ct));

    [HttpPost("reply")]
    public async Task<IActionResult> Reply(ReplyMessageRequest request, CancellationToken ct) =>
        LineResult(await line.ReplyAsync(await ResolveChannelAsync(ct), request.ReplyToken, request.Messages, ct));

    [HttpPost("multicast")]
    public async Task<IActionResult> Multicast(MulticastMessageRequest request, CancellationToken ct) =>
        LineResult(await line.MulticastAsync(await ResolveChannelAsync(ct), request.To, request.Messages, RetryKey, ct));

    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast(BroadcastMessageRequest request, CancellationToken ct) =>
        LineResult(await line.BroadcastAsync(await ResolveChannelAsync(ct), request.Messages, RetryKey, ct));

    /// <summary>Body is LINE's narrowcast request as-is (messages, recipient, filter, limit). Returns the requestId to poll.</summary>
    [HttpPost("narrowcast")]
    public async Task<IActionResult> Narrowcast([FromBody] JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("messages", out _))
            throw ApiException.BadRequest("Narrowcast request must contain messages");

        var result = await line.NarrowcastAsync(await ResolveChannelAsync(ct), request, RetryKey, ct);
        return Accepted(new { requestId = result.RequestId });
    }

    [HttpGet("narrowcast/{requestId}/progress")]
    public async Task<IActionResult> NarrowcastProgress(string requestId, CancellationToken ct) =>
        LineResult(await line.GetNarrowcastProgressAsync(await ResolveChannelAsync(ct), requestId, ct));

    [HttpGet("profile/{userId}")]
    public async Task<IActionResult> Profile(string userId, CancellationToken ct) =>
        LineResult(await line.GetProfileAsync(await ResolveChannelAsync(ct), userId, ct));

    [HttpGet("bot-info")]
    public async Task<IActionResult> BotInfo(CancellationToken ct) =>
        LineResult(await line.GetBotInfoAsync(await ResolveChannelAsync(ct), ct));

    /// <summary>
    /// Any other Messaging API endpoint with the channel's token: rich menus, message content download,
    /// audiences, insights, etc. Example: GET /api/v1/line/proxy/v2/bot/message/{messageId}/content.
    /// LINE's status, content type and body are returned unchanged.
    /// </summary>
    [AcceptVerbs("GET", "POST", "PUT", "DELETE")]
    [Route("proxy/{**path}")]
    [RequestSizeLimit(10_000_000)]
    public async Task Proxy(string path, CancellationToken ct)
    {
        if (!LineApiClient.IsAllowedProxyPath(path)) throw ApiException.BadRequest("Path is not a LINE Messaging API path", path);
        var channel = await ResolveChannelAsync(ct);

        byte[]? body = null;
        if (Request.ContentLength > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        using var response = await line.ProxyAsync(
            channel, new HttpMethod(Request.Method), path, Request.QueryString.Value ?? "", body, Request.ContentType, RetryKey, ct);

        Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType) Response.ContentType = contentType.ToString();
        if (response.Headers.TryGetValues(RequestIdHeader, out var ids)) Response.Headers[RequestIdHeader] = ids.FirstOrDefault();
        await response.Content.CopyToAsync(Response.Body, ct);
    }

    private string? RetryKey => Request.Headers.TryGetValue(RetryKeyHeader, out var key) ? key.ToString() : null;

    private IActionResult LineResult(LineApiResult result)
    {
        if (result.RequestId is not null) Response.Headers[RequestIdHeader] = result.RequestId;
        return Ok(result.Body ?? JsonSerializer.SerializeToElement(new { }));
    }

    private async Task<LineChannelConfig> ResolveChannelAsync(CancellationToken ct)
    {
        var app = tenant.Required;
        var channelId = Request.Headers[ChannelHeader].ToString();
        var channel = string.IsNullOrEmpty(channelId)
            ? await registry.GetLineChannelAsync(app.Id, LineChannelType.Messaging, ct)
            : await registry.FindLineChannelAsync(channelId, ct);

        if (channel is null || channel.AppId != app.Id || channel.Type != LineChannelType.Messaging)
            throw ApiException.NotFound("LINE Messaging channel not found for this app");
        return channel;
    }
}
