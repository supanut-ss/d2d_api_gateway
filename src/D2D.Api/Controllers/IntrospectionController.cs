using D2D.Api.Auth;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2D.Api.Controllers;

/// <summary>An app's backend checks an access token against D2D's current data (X-App-Id + X-Api-Key).</summary>
[ApiController]
[Route("api/v1/auth/introspect")]
[Authorize(Policy = Policies.AppServer)]
public sealed class IntrospectionController(TokenIntrospectionService introspection, ITenantContext tenant) : ControllerBase
{
    [HttpPost]
    public async Task<IntrospectionResponse> Introspect(IntrospectRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await introspection.IntrospectAsync(tenant.Required.Id, request.Token, ct);
    }
}
