using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Line;
using D2D.Core.Tenancy;
using Microsoft.Extensions.Logging;

namespace D2D.Infrastructure.Line;

public sealed record LineIdTokenPayload(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("aud")] string Aud,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picture")] string? Picture,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("exp")] long? Exp = null);

public sealed record LineTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>Client over the LINE Login and Messaging APIs.</summary>
public sealed partial class LineApiClient(
    HttpClient http,
    ILineChannelTokenProvider tokens,
    ILogger<LineApiClient> logger) : ILineMessagingClient
{
    public const string AuthorizeEndpoint = "https://access.line.me/oauth2/v2.1/authorize";
    private const string TokenEndpoint = "https://api.line.me/oauth2/v2.1/token";
    private const string VerifyEndpoint = "https://api.line.me/oauth2/v2.1/verify";
    private const string ApiHost = "https://api.line.me/";
    private const string DataHost = "https://api-data.line.me/";

    // ---- LINE Login ----

    /// <summary>LINE validates signature, audience (channel id), expiry and nonce for us.</summary>
    public async Task<LineIdTokenPayload> VerifyIdTokenAsync(string idToken, string channelId, string? nonce, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["id_token"] = idToken, ["client_id"] = channelId };
        if (!string.IsNullOrEmpty(nonce)) form["nonce"] = nonce;

        using var response = await http.PostAsync(VerifyEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw ApiException.Unauthorized("Invalid LINE ID token", await response.Content.ReadAsStringAsync(ct));

        return await response.Content.ReadFromJsonAsync<LineIdTokenPayload>(ct)
            ?? throw ApiException.Unauthorized("Invalid LINE ID token");
    }

    public async Task<LineTokenResponse> ExchangeCodeAsync(
        string code, string redirectUri, string channelId, string channelSecret, string codeVerifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = channelId,
            ["client_secret"] = channelSecret,
            ["code_verifier"] = codeVerifier,
        };

        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
            throw ApiException.Unauthorized("LINE code exchange failed", await response.Content.ReadAsStringAsync(ct));

        return await response.Content.ReadFromJsonAsync<LineTokenResponse>(ct)
            ?? throw ApiException.Unauthorized("LINE code exchange failed");
    }

    // ---- Messaging API ----

    public Task<LineApiResult> PushAsync(LineChannelConfig channel, string to, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Post, "v2/bot/message/push", new { to, messages }, retryKey ?? NewRetryKey(), ct);

    public Task<LineApiResult> ReplyAsync(LineChannelConfig channel, string replyToken, IReadOnlyList<JsonElement> messages, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Post, "v2/bot/message/reply", new { replyToken, messages }, retryKey: null, ct);

    public Task<LineApiResult> MulticastAsync(LineChannelConfig channel, IReadOnlyList<string> to, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Post, "v2/bot/message/multicast", new { to, messages }, retryKey ?? NewRetryKey(), ct);

    public Task<LineApiResult> BroadcastAsync(LineChannelConfig channel, IReadOnlyList<JsonElement> messages, string? retryKey = null, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Post, "v2/bot/message/broadcast", new { messages }, retryKey ?? NewRetryKey(), ct);

    public Task<LineApiResult> NarrowcastAsync(LineChannelConfig channel, JsonElement request, string? retryKey = null, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Post, "v2/bot/message/narrowcast", request, retryKey ?? NewRetryKey(), ct);

    public Task<LineApiResult> GetNarrowcastProgressAsync(LineChannelConfig channel, string requestId, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Get, $"v2/bot/message/progress/narrowcast?requestId={Uri.EscapeDataString(requestId)}", body: null, retryKey: null, ct);

    public Task<LineApiResult> GetProfileAsync(LineChannelConfig channel, string userId, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Get, $"v2/bot/profile/{Uri.EscapeDataString(userId)}", body: null, retryKey: null, ct);

    public Task<LineApiResult> GetBotInfoAsync(LineChannelConfig channel, CancellationToken ct = default) =>
        SendJsonAsync(channel, HttpMethod.Get, "v2/bot/info", body: null, retryKey: null, ct);

    // ---- Generic proxy for the rest of the Messaging API (rich menus, content, audiences, insights...) ----

    /// <summary>Only Messaging API paths, plain characters, no traversal.</summary>
    public static bool IsAllowedProxyPath(string path) =>
        ProxyPathPattern().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Forwards a request to the Messaging API with the channel's token. The caller owns the response
    /// and streams it back; LINE's status code is returned unchanged.
    /// </summary>
    public Task<HttpResponseMessage> ProxyAsync(
        LineChannelConfig channel, HttpMethod method, string path, string query, byte[]? body, string? contentType, string? retryKey, CancellationToken ct)
    {
        if (!IsAllowedProxyPath(path)) throw ApiException.BadRequest("Path is not a LINE Messaging API path", path);

        var url = (DataHostPathPattern().IsMatch(path) ? DataHost : ApiHost) + path + query;
        return SendAsync(channel, () =>
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (contentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            if (retryKey is not null) request.Headers.Add("X-Line-Retry-Key", retryKey);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<LineApiResult> SendJsonAsync(
        LineChannelConfig channel, HttpMethod method, string path, object? body, string? retryKey, CancellationToken ct)
    {
        using var response = await SendAsync(channel, () =>
        {
            var request = new HttpRequestMessage(method, ApiHost + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            // Lets LINE de-duplicate a send that is retried after a timeout.
            if (retryKey is not null) request.Headers.Add("X-Line-Retry-Key", retryKey);
            return request;
        }, HttpCompletionOption.ResponseContentRead, ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        var requestId = response.Headers.TryGetValues("X-Line-Request-Id", out var ids) ? ids.FirstOrDefault() : null;

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            logger.LogWarning("LINE API {Path} failed for channel {ChannelId}: {Status} {Error}", path, channel.ChannelId, status, text);
            // Problems in the caller's request keep LINE's status; credential or LINE-side failures are a gateway error.
            var mapped = status is 400 or 404 or 409 or 429 ? status : 502;
            throw new ApiException(mapped, "LINE Messaging API error", text);
        }

        JsonElement? json = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var doc = JsonDocument.Parse(text);
            json = doc.RootElement.Clone();
        }
        return new LineApiResult(json, requestId);
    }

    /// <summary>Sends with the channel's token. An issued (non-static) token LINE rejects is dropped and the call retried once.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        LineChannelConfig channel, Func<HttpRequestMessage> createRequest, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(channel, ct));

            var response = await http.SendAsync(request, completion, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || channel.TokenMode == LineTokenMode.Static || attempt == 2)
                return response;

            response.Dispose();
            await tokens.InvalidateAsync(channel, ct);
        }
    }

    private static string NewRetryKey() => Guid.NewGuid().ToString();

    [GeneratedRegex(@"^v2/bot/[A-Za-z0-9/_\-.:]+$")]
    private static partial Regex ProxyPathPattern();

    // Endpoints served from api-data.line.me (binary content and file uploads).
    [GeneratedRegex(@"^v2/bot/(message/[^/]+/content(/preview|/transcoding)?|richmenu/[^/]+/content|audienceGroup/upload/byFile)$")]
    private static partial Regex DataHostPathPattern();
}
