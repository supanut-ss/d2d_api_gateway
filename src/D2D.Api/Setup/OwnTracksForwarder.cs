using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Setup;

/// <summary>
/// Keeps the short public address <c>/owntracks/...</c> on the gateway while the OwnTracks API lives in the payyet application
/// (<c>/payyet/owntracks/...</c>, a separate IIS application). Requests are passed on unchanged: method, query string, body and the
/// headers OwnTracks and the tracker page use. Nothing is stored or logged here.
/// </summary>
public static class OwnTracksForwarder
{
    public const string HttpClientName = "owntracks-forward";
    private const string DefaultTarget = "https://gateway.drivetodev.online/payyet/owntracks";
    private const long MaxBodyBytes = 1_048_576;   // location reports are tiny

    private static readonly string[] RequestHeaders = ["Authorization", "Content-Type", "X-Limit-U", "X-Limit-D", "User-Agent", "Origin", "Accept"];
    private static readonly string[] ResponseHeaders = ["Content-Type", "Cache-Control", "Access-Control-Allow-Origin", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers", "Access-Control-Max-Age", "Vary"];

    public static IServiceCollection AddOwnTracksForwarder(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }

    public static IEndpointRouteBuilder MapOwnTracksForwarder(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/owntracks/{**rest}", ["GET", "POST", "OPTIONS"], Forward)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        app.MapMethods("/owntracks", ["GET", "POST", "OPTIONS"], Forward)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        return app;
    }

    private static async Task Forward(HttpContext ctx, IHttpClientFactory factory, IConfiguration config, ILogger<Program> log)
    {
        var target = (config["Forward:OwnTracksUrl"] ?? DefaultTarget).TrimEnd('/');
        var rest = ctx.GetRouteValue("rest") as string;
        var url = target + (string.IsNullOrEmpty(rest) ? "" : "/" + rest) + ctx.Request.QueryString.Value;

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            req.Content = new StreamContent(ctx.Request.Body);
        foreach (var name in RequestHeaders)
        {
            if (!ctx.Request.Headers.TryGetValue(name, out var value)) continue;
            if (!req.Headers.TryAddWithoutValidation(name, value.ToArray()))
                req.Content?.Headers.TryAddWithoutValidation(name, value.ToArray());
        }

        try
        {
            using var res = await factory.CreateClient(HttpClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)res.StatusCode;
            foreach (var name in ResponseHeaders)
            {
                if (res.Headers.TryGetValues(name, out var v) || res.Content.Headers.TryGetValues(name, out v))
                    ctx.Response.Headers[name] = v.ToArray();   // assignment, so the gateway's own CORS headers are replaced, not doubled
            }
            await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogWarning(ex, "OwnTracks forward to the payyet application failed");
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }
}
