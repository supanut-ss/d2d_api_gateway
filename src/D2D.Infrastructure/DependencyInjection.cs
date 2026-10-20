using System.Text;
using D2D.Core.Abstractions;
using D2D.Core.Line;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Admin;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Line;
using D2D.Infrastructure.Maintenance;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using D2D.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace D2D.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<MasterDbOptions>().Bind(config.GetSection(MasterDbOptions.Section))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "MasterDb:ConnectionString is required")
            .ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
            .PostConfigure(o => o.PrivateKeyPath ??= Path.Combine(config["DataProtection:KeysPath"] ?? "keys", "jwt-signing.pem"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "Jwt:Issuer is required")
            .ValidateOnStart();
        services.AddOptions<ServerOptions>().Bind(config.GetSection(ServerOptions.Section));
        services.AddOptions<LineOptions>().Bind(config.GetSection(LineOptions.Section));
        services.AddOptions<AdminOptions>().Bind(config.GetSection(AdminOptions.Section));

        services.AddDbContext<MasterDbContext>((sp, o) =>
        {
            var master = sp.GetRequiredService<IOptions<MasterDbOptions>>().Value;
            o.UseProvider(master.Provider, master.ConnectionString);
        });
        services.AddHealthChecks().AddDbContextCheck<MasterDbContext>("master-db");

        // Key ring must be shared by every instance (and backed up) or stored secrets cannot be decrypted.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("d2d-api")
            .PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeysPath"] ?? "keys"));
        // Windows only: encrypts the key ring at rest for the app pool identity. Not portable between machines.
        if (config.GetValue<bool>("DataProtection:ProtectWithDpapi"))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DataProtection:ProtectWithDpapi is only supported on Windows.");
            dataProtection.ProtectKeysWithDpapi();
        }

        services.AddSingleton<IJwtKeyStore, JwtKeyStore>();

        services.AddMemoryCache();
        services.AddDistributedMemoryCache(); // Replace with Redis when running more than one instance.
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IAppRegistry, AppRegistry>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();

        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<TokenIntrospectionService>();
        services.AddScoped<LineLoginService>();
        services.AddScoped<AppAdminService>();
        services.AddScoped<MemberService>();
        services.AddScoped<AdminAuthService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<PlatformQueryService>();

        services.AddOptions<MaintenanceOptions>().Bind(config.GetSection(MaintenanceOptions.Section));
        services.AddScoped<DataCleanup>();
        services.AddHostedService<DataMaintenanceService>();

        services.AddHttpClient(LineChannelTokenProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<ILineChannelTokenProvider, LineChannelTokenProvider>();
        services.AddHttpClient<LineApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddTransient<ILineMessagingClient>(sp => sp.GetRequiredService<LineApiClient>());
        services.AddHttpClient<LineWebhookForwarder>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<WebhookSignal>();
        services.AddScoped<LineWebhookIngestor>();
        services.AddScoped<LineEventDispatcher>();
        services.AddScoped<ILineEventHandler, LoggingLineEventHandler>();
        services.AddHostedService<LineWebhookProcessor>();

        return services;
    }

    /// <summary>
    /// Loads the JWT signing key (so a bad key fails at startup, not on the first login) and creates the
    /// master schema when MasterDb:AutoCreate is on.
    /// </summary>
    public static async Task InitializeAsync(this IServiceProvider services)
    {
        services.GetRequiredService<IJwtKeyStore>();

        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<MasterDbOptions>>().Value;
        if (!options.AutoCreate) return;

        await scope.ServiceProvider.GetRequiredService<MasterDbContext>().Database.EnsureCreatedAsync();
    }
}
