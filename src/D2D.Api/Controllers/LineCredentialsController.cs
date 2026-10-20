using D2D.Api.Auth;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Line;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

/// <summary>Keys of one LINE channel, ready to use. <see cref="AccessToken"/> is null for Login channels.</summary>
public sealed record LineChannelCredentials(
    string ChannelId,
    string ProviderId,
    LineChannelType Type,
    LineTokenMode? TokenMode,
    string ChannelSecret,
    string? AccessToken,
    DateTime? AccessTokenExpiresAt);

/// <summary>
/// For apps that talk to LINE themselves: send the channel id, get the channel's keys from the database.
/// Only the owning app (X-App-Id + X-Api-Key) and only for channels that were enabled with allowCredentialAccess.
/// </summary>
[ApiController]
[Route("api/v1/line/channels/{channelId}/credentials")]
[Authorize(Policy = Policies.AppServer)]
public sealed class LineCredentialsController(
    IAppRegistry registry,
    ITenantContext tenant,
    ILineChannelTokenProvider tokens,
    ILogger<LineCredentialsController> logger) : ControllerBase
{
    /// <summary>
    /// Returns the channel secret and, for Messaging channels, an access token that is valid now
    /// (issued and renewed by D2D). Re-fetch when <c>accessTokenExpiresAt</c> is within a minute; Static tokens have no expiry.
    /// A returned token is valid for at least 2 minutes (stateless) or 1 day (v2.1). The v2.1 signing key is never returned.
    /// </summary>
    [HttpGet]
    public async Task<LineChannelCredentials> Get(string channelId, CancellationToken ct)
    {
        var app = tenant.Required;
        var channel = await registry.FindLineChannelAsync(channelId, ct);
        // Another app's channel looks exactly like a missing one.
        if (channel is null || channel.AppId != app.Id) throw ApiException.NotFound("LINE channel not found for this app");
        if (!channel.AllowCredentialAccess)
        {
            throw ApiException.Forbidden(
                "Credential access is disabled for this channel",
                "Enable it with allowCredentialAccess=true when registering the channel.");
        }

        IssuedAccessToken? token = channel.Type == LineChannelType.Messaging
            ? await tokens.GetIssuedAccessTokenAsync(channel, ct)
            : null;

        logger.LogInformation("App {App} read credentials of LINE channel {ChannelId}", app.Code, channel.ChannelId);

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return new LineChannelCredentials(
            channel.ChannelId,
            channel.ProviderId,
            channel.Type,
            channel.Type == LineChannelType.Messaging ? channel.TokenMode : null,
            channel.ChannelSecret,
            token?.Token,
            token?.ExpiresAt);
    }

    /// <summary>
    /// Same as the call above for the app's own channel of a type (default Messaging), so the app does not need to know its channel id.
    /// </summary>
    [HttpGet("~/api/v1/line/credentials")]
    public async Task<LineChannelCredentials> GetByType([FromQuery] LineChannelType type = LineChannelType.Messaging, CancellationToken ct = default)
    {
        var channel = await registry.GetLineChannelAsync(tenant.Required.Id, type, ct)
            ?? throw ApiException.NotFound($"No {type} LINE channel for this app");
        return await Get(channel.ChannelId, ct);
    }
}
