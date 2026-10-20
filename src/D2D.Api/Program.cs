using System.Text.Json.Serialization;
using D2D.Api.Auth;
using D2D.Api.Setup;
using D2D.Api.Tenancy;
using D2D.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiAuthentication();
builder.Services.AddScoped<AdminAuditFilter>();
builder.Services.AddApiRateLimiting();
builder.Services.AddApiSwagger();
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

await app.Services.InitializeAsync();

app.UseExceptionHandler();
app.UseMiddleware<ApiExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// The admin console (wwwroot/admin): strict headers, and no inline script or style is allowed to run.
app.Use(async (context, next) =>
{
    // Not an endpoint: a matched endpoint would stop the static file middleware from serving /admin/ itself.
    if (context.Request.Path == "/admin")
    {
        context.Response.Redirect($"{context.Request.PathBase}/admin/");
        return;
    }
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-cache"; // always revalidate, so a new deployment shows up at once
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>(); // needs the authenticated user, runs before authorization
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
