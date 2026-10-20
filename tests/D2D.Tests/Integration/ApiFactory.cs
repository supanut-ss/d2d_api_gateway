using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace D2D.Tests.Integration;

/// <summary>
/// Runs the real API in memory against a throw-away SQLite master DB.
/// Settings go through environment variables because several are read while the app is being configured,
/// before test-only configuration callbacks could apply.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "it-admin-key";

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), "d2d-it-" + Guid.NewGuid().ToString("N"));

    public ApiFactory()
    {
        Directory.CreateDirectory(TempDir);
        var settings = new Dictionary<string, string>
        {
            ["MasterDb__Provider"] = "Sqlite",
            ["MasterDb__ConnectionString"] = $"Data Source={Path.Combine(TempDir, "master.db")}",
            ["MasterDb__AutoCreate"] = "true",
            ["Jwt__Issuer"] = "http://localhost",
            ["Admin__ApiKey"] = AdminKey,
            ["DataProtection__KeysPath"] = Path.Combine(TempDir, "keys"),
            ["Server__PublicBaseUrl"] = "http://localhost",
            ["Line__LoginCallbackUrl"] = "http://localhost/api/v1/auth/line/callback",
            ["RateLimit__AuthPermitPerMinute"] = "100000",
            ["Maintenance__Enabled"] = "false",
        };
        foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
            // Temp files may still be locked on Windows; they live in the temp folder anyway.
        }
    }
}
