using D2D.Infrastructure.Options;
using D2D.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace D2D.Api.Controllers;

/// <summary>
/// Lets backends of other apps validate access tokens without a shared secret. In ASP.NET Core:
/// <c>AddJwtBearer(o => o.Authority = "https://host/d2d")</c> discovers everything from here.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class WellKnownController(IJwtKeyStore keys, IOptions<JwtOptions> jwt, IOptions<ServerOptions> server) : ControllerBase
{
    [HttpGet(".well-known/jwks.json")]
    public IActionResult Jwks()
    {
        CacheBriefly();
        return Ok(new { keys = keys.PublicJwks });
    }

    [HttpGet(".well-known/openid-configuration")]
    public IActionResult Configuration()
    {
        CacheBriefly();
        var baseUrl = (server.Value.PublicBaseUrl ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}").TrimEnd('/');
        return Ok(new
        {
            issuer = jwt.Value.Issuer,
            jwks_uri = $"{baseUrl}/.well-known/jwks.json",
            id_token_signing_alg_values_supported = new[] { "RS256" },
            subject_types_supported = new[] { "public" },
            response_types_supported = new[] { "token" },
        });
    }

    // Short cache: backends refetch quickly after a key rotation, but do not hit us on every request.
    private void CacheBriefly() => Response.Headers.CacheControl = "public, max-age=300";
}
