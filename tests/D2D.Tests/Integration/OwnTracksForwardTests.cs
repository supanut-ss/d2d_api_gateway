using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace D2D.Tests.Integration;

/// <summary>/owntracks/* on the gateway is passed unchanged to the payyet application.</summary>
[Collection("api")]
public sealed class OwnTracksForwardTests(ApiFactory factory)
{
    private static async Task<(WebApplication Upstream, string Url)> StartUpstreamAsync()
    {
        var upstream = WebApplication.CreateBuilder().Build();
        upstream.Urls.Add("http://127.0.0.1:0");
        upstream.Map("/{**path}", async (HttpContext c) =>
        {
            var body = await new StreamReader(c.Request.Body).ReadToEndAsync();
            c.Response.Headers["Access-Control-Allow-Origin"] = "https://drivetodev.online";
            c.Response.StatusCode = c.Request.Query.ContainsKey("deny") ? 401 : 200;
            await c.Response.WriteAsync($"{c.Request.Method}|{c.Request.Path}|{c.Request.QueryString}|{c.Request.Headers.Authorization}|{c.Request.Headers["X-Limit-U"]}|{body}");
        });
        await upstream.StartAsync();
        var url = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (upstream, url);
    }

    private HttpClient ClientFor(string upstreamUrl) =>
        factory.WithWebHostBuilder(b => b.UseSetting("Forward:OwnTracksUrl", upstreamUrl + "/payyet/owntracks")).CreateClient();

    [Fact]
    public async Task Post_goes_through_with_body_query_and_headers()
    {
        var (upstream, url) = await StartUpstreamAsync();
        await using var _ = upstream;
        var client = ClientFor(url);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/owntracks?key=k1")
        {
            Content = new StringContent("{\"_type\":\"location\"}", Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Basic abc");
        req.Headers.TryAddWithoutValidation("X-Limit-U", "mom");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("POST|/payyet/owntracks|?key=k1|Basic abc|mom|{\"_type\":\"location\"}", await res.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "https://drivetodev.online" }, res.Headers.GetValues("Access-Control-Allow-Origin").ToArray());   // one value, not doubled
    }

    [Fact]
    public async Task Sub_paths_and_upstream_status_are_kept()
    {
        var (upstream, url) = await StartUpstreamAsync();
        await using var _ = upstream;
        var res = await ClientFor(url).GetAsync("/owntracks/latest?deny=1");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.StartsWith("GET|/payyet/owntracks/latest|?deny=1|", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unreachable_upstream_gives_502()
    {
        var res = await ClientFor("http://127.0.0.1:1").GetAsync("/owntracks/latest");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }
}
