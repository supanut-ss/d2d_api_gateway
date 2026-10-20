using D2D.Core;
using D2D.Core.Security;
using D2D.Core.Tenancy;

namespace D2D.Infrastructure.Tenancy;

public sealed class TenantContext : ITenantContext
{
    public TenantInfo? Current { get; private set; }

    public TenantInfo Required => Current ?? throw ApiException.BadRequest(
        "App not specified",
        $"Send the {AppHeaders.AppId} header or use a token issued for an app.");

    public void Set(TenantInfo tenant) => Current = tenant;
}
