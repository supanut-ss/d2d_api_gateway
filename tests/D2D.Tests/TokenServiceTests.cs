using D2D.Core;
using D2D.Core.Entities;
using D2D.Core.Tenancy;
using D2D.Infrastructure.Auth;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace D2D.Tests;

public sealed class TokenServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MasterDbContext _db;
    private readonly TokenService _tokens;
    private readonly User _user = new() { DisplayName = "Alice" };
    private readonly TenantInfo _app = new(Guid.NewGuid(), "shop-a", "Shop A", true, true);
    private readonly TenantInfo _otherApp = new(Guid.NewGuid(), "shop-b", "Shop B", true, true);

    public TokenServiceTests()
    {
        _connection.Open();
        _db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        foreach (var app in new[] { _app, _otherApp })
            _db.Apps.Add(new App { Id = app.Id, Code = app.Code, Name = app.Name, ApiKeyHash = "x" });
        _db.Users.Add(_user);
        _db.UserApps.Add(new UserApp { UserId = _user.Id, AppId = _app.Id });
        _db.SaveChanges();

        var jwt = Microsoft.Extensions.Options.Options.Create(
            new JwtOptions { PrivateKeyPem = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem() });
        var keys = new JwtKeyStore(jwt, Microsoft.Extensions.Logging.Abstractions.NullLogger<JwtKeyStore>.Instance);
        _tokens = new TokenService(_db, jwt, keys, TimeProvider.System);
    }

    [Fact]
    public async Task Refresh_rotates_token()
    {
        var first = await _tokens.IssueAsync(_user, _app, ["user"], null, default);
        var second = await _tokens.RefreshAsync(first.RefreshToken, _app, null, default);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.False(string.IsNullOrEmpty(second.AccessToken));
    }

    [Fact]
    public async Task Reusing_rotated_token_revokes_whole_family()
    {
        var first = await _tokens.IssueAsync(_user, _app, ["user"], null, default);
        var second = await _tokens.RefreshAsync(first.RefreshToken, _app, null, default);

        var reuse = await Assert.ThrowsAsync<ApiException>(() => _tokens.RefreshAsync(first.RefreshToken, _app, null, default));
        Assert.Equal(401, reuse.StatusCode);

        // The legitimate latest token is now dead too.
        var after = await Assert.ThrowsAsync<ApiException>(() => _tokens.RefreshAsync(second.RefreshToken, _app, null, default));
        Assert.Equal(401, after.StatusCode);
    }

    [Fact]
    public async Task Token_cannot_be_refreshed_for_another_app()
    {
        var issued = await _tokens.IssueAsync(_user, _app, ["user"], null, default);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _tokens.RefreshAsync(issued.RefreshToken, _otherApp, null, default));
        Assert.Equal(401, ex.StatusCode);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
