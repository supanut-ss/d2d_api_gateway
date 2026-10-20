using System.Threading.RateLimiting;
using D2D.Core;
using D2D.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;

namespace D2D.Api.Setup;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}

public static class ApiSetup
{
    /// <summary>
    /// Throttles login-type endpoints per client IP and app to slow down credential stuffing.
    /// RateLimit:AuthPermitPerMinute (default 20) is read lazily from the final configuration.
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((o, config) =>
        {
            var permit = config.GetValue("RateLimit:AuthPermitPerMinute", 20);
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                $"{ctx.Connection.RemoteIpAddress}|{ctx.Request.Headers[AppHeaders.AppId]}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1) }));
        });
        return services;
    }

    public static IServiceCollection AddApiSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        return services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo { Title = "D2D Central API", Version = "v1" });
            o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
            AddHeaderScheme(o, "AppId", AppHeaders.AppId);
            AddHeaderScheme(o, "ApiKey", AppHeaders.ApiKey);
            AddHeaderScheme(o, "AdminKey", AppHeaders.AdminKey);

            // Swagger UI sends whichever of these the user filled in.
            o.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [Ref("Bearer")] = Array.Empty<string>(),
                [Ref("AppId")] = Array.Empty<string>(),
                [Ref("ApiKey")] = Array.Empty<string>(),
                [Ref("AdminKey")] = Array.Empty<string>(),
            });
        });
    }

    private static void AddHeaderScheme(Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions o, string id, string header) =>
        o.AddSecurityDefinition(id, new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = header });

    private static OpenApiSecurityScheme Ref(string id) =>
        new() { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = id } };
}

/// <summary>
/// Turns <see cref="ApiException"/> into RFC 7807 problem responses. Expected errors are handled here,
/// before UseExceptionHandler, so they are not logged as unhandled; anything else still becomes a logged 500.
/// </summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = ex.StatusCode;
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails { Status = ex.StatusCode, Title = ex.Title, Detail = ex.Detail },
            });
        }
    }
}
