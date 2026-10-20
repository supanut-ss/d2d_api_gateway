using D2D.Core;
using D2D.Core.Entities;
using D2D.Infrastructure.Admin;
using D2D.Infrastructure.Options;
using D2D.Infrastructure.Persistence;
using D2D.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace D2D.Tests;

public sealed class AdminUserServiceTests : IDisposable
{
    private const string Password = "Admin-Pass-12345";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MasterDbContext _db;
    private readonly AdminAuthService _auth;
    private readonly AdminUserService _admins;

    public AdminUserServiceTests()
    {
        _connection.Open();
        _db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var jwt = Microsoft.Extensions.Options.Options.Create(
            new JwtOptions { PrivateKeyPem = System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem() });
        _auth = new AdminAuthService(_db, new JwtKeyStore(jwt, NullLogger<JwtKeyStore>.Instance), jwt, TimeProvider.System);
        _admins = new AdminUserService(_db, _auth);
    }

    [Fact]
    public async Task Setup_is_required_only_until_the_first_admin_exists()
    {
        Assert.True(await _auth.SetupRequiredAsync(default));
        await Create("first@example.com");
        Assert.False(await _auth.SetupRequiredAsync(default));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_disabled_or_demoted()
    {
        var only = await Create("only@example.com");

        foreach (var change in new[] { new UpdateAdminRequest(null, null, false), new UpdateAdminRequest(null, AdminRole.Viewer, null) })
        {
            var ex = await Assert.ThrowsAsync<ApiException>(() => _admins.UpdateAsync(only.Id, change, actorId: null, default));
            Assert.Equal(409, ex.StatusCode);
        }

        var second = await Create("second@example.com");
        await _admins.UpdateAsync(second.Id, new UpdateAdminRequest(null, AdminRole.Viewer, null), actorId: only.Id, default); // fine: "only" stays
        // Now "second" is a viewer, so "only" is the last admin again.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() =>
            _admins.UpdateAsync(only.Id, new UpdateAdminRequest(null, null, false), actorId: null, default))).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_demote_or_disable_themselves()
    {
        var me = await Create("me@example.com");
        await Create("other@example.com");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _admins.UpdateAsync(me.Id, new UpdateAdminRequest(null, AdminRole.Viewer, null), actorId: me.Id, default));
        Assert.Equal(409, ex.StatusCode);

        // Renaming yourself is fine.
        var renamed = await _admins.UpdateAsync(me.Id, new UpdateAdminRequest("New Name", null, null), actorId: me.Id, default);
        Assert.Equal("New Name", renamed.DisplayName);
    }

    [Fact]
    public async Task Demoting_or_resetting_an_admin_ends_their_open_sessions()
    {
        await Create("keeper@example.com");
        var target = await Create("target@example.com");

        var (_, refresh) = await _auth.LoginAsync(new AdminLoginRequest("target@example.com", Password), null, default);
        await _admins.UpdateAsync(target.Id, new UpdateAdminRequest(null, AdminRole.Viewer, null), actorId: null, default);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => _auth.RefreshAsync(refresh, null, default))).StatusCode);

        var (_, again) = await _auth.LoginAsync(new AdminLoginRequest("target@example.com", Password), null, default);
        await _admins.ResetPasswordAsync(target.Id, new ResetAdminPasswordRequest("Completely-New-Pass-1"), default);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => _auth.RefreshAsync(again, null, default))).StatusCode);
        await _auth.LoginAsync(new AdminLoginRequest("target@example.com", "Completely-New-Pass-1"), null, default);
    }

    [Fact]
    public async Task Admin_and_app_user_sessions_never_mix()
    {
        await Create("a@example.com");
        var (_, refresh) = await _auth.LoginAsync(new AdminLoginRequest("a@example.com", Password), null, default);

        // Admin sessions are stored under an app id no real app can have.
        var stored = await _db.RefreshTokens.SingleAsync();
        Assert.Equal(AdminAuthService.SessionAppId, stored.AppId);
        Assert.NotEqual(refresh, stored.TokenHash);
    }

    private Task<AdminUserResponse> Create(string email) =>
        _admins.CreateAsync(new CreateAdminRequest(email, email.Split('@')[0], Password), default);

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
