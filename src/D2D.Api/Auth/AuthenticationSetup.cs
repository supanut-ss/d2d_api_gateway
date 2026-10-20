using System.Security.Claims;
using D2D.Core.Security;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace D2D.Api.Auth;

public static class AuthSchemes
{
    public const string Jwt = JwtBearerDefaults.AuthenticationScheme;
    public const string AppApiKey = "AppApiKey";
    public const string AdminKey = "AdminKey";

    /// <summary>Access token of a person signed in to the admin console (its own audience).</summary>
    public const string AdminJwt = "AdminJwt";
}

public static class Policies
{
    /// <summary>End user with a JWT issued for an app.</summary>
    public const string AppUser = "AppUser";

    /// <summary>App backend calling with X-App-Id + X-Api-Key.</summary>
    public const string AppServer = "AppServer";

    /// <summary>Platform administrator who may change things: the admin key, or an admin console user with role Admin.</summary>
    public const string Admin = "Admin";

    /// <summary>Anyone allowed to look at the admin data: the above plus console users with role Viewer.</summary>
    public const string AdminRead = "AdminRead";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(AuthSchemes.Jwt)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, AppApiKeyHandler>(AuthSchemes.AppApiKey, null)
            .AddScheme<AuthenticationSchemeOptions, AdminKeyHandler>(AuthSchemes.AdminKey, null)
            .AddJwtBearer(AuthSchemes.AdminJwt, _ => { });

        // Same signing key as app tokens but another audience, so an app token never opens the admin API and vice versa.
        services.AddOptions<JwtBearerOptions>(AuthSchemes.AdminJwt)
            .Configure<IOptions<JwtOptions>, IJwtKeyStore>((o, jwtOptions, keys) =>
            {
                var jwt = jwtOptions.Value;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.AdminAudience,
                    IssuerSigningKeys = keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AppClaimTypes.Name,
                    RoleClaimType = AppClaimTypes.Role,
                };
            });

        // Configured from validated JwtOptions instead of reading config eagerly.
        services.AddOptions<JwtBearerOptions>(AuthSchemes.Jwt)
            .Configure<IOptions<JwtOptions>, IJwtKeyStore>((o, jwtOptions, keys) =>
            {
                var jwt = jwtOptions.Value;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKeys = keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AppClaimTypes.Name,
                    RoleClaimType = AppClaimTypes.Role,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.AppUser, p => p
                .AddAuthenticationSchemes(AuthSchemes.Jwt)
                .RequireAuthenticatedUser()
                .RequireClaim(AppClaimTypes.AppId))
            .AddPolicy(Policies.AppServer, p => p
                .AddAuthenticationSchemes(AuthSchemes.AppApiKey)
                .RequireRole(AppRoles.AppServer))
            .AddPolicy(Policies.Admin, p => p
                .AddAuthenticationSchemes(AuthSchemes.AdminKey, AuthSchemes.AdminJwt)
                .RequireRole(AppRoles.SuperAdmin))
            .AddPolicy(Policies.AdminRead, p => p
                .AddAuthenticationSchemes(AuthSchemes.AdminKey, AuthSchemes.AdminJwt)
                .RequireRole(AppRoles.SuperAdmin, AppRoles.PlatformViewer));

        return services;
    }

    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(AppClaimTypes.Subject) ?? throw new InvalidOperationException("Missing sub claim"));
}
