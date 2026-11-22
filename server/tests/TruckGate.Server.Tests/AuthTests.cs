using TruckGate.Application;

namespace TruckGate.Server.Tests;

[Collection("db")]
public class AuthTests(DatabaseFixture fx)
{
    private async Task<(AuthService Auth, AdminService Admin, string Username, int Id)> NewUserAsync(string role, string secret)
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var admin = new AdminService(db);
        var username = role.ToLowerInvariant() + fx.NextId();
        var id = await admin.CreateUserAsync(username, "Salim K.", role, "ar", DatabaseFixture.Tester);
        await admin.SetUserPinAsync(id, secret, DatabaseFixture.Tester);
        return (new AuthService(db), admin, username, id);
    }

    [Fact]
    public async Task Gatekeeper_logs_in_with_PIN()
    {
        var (auth, _, username, id) = await NewUserAsync("GATEKEEPER", "5937");
        var user = await auth.LoginAsync(username, "5937");
        Assert.Equal(id, user.Id);
        Assert.Equal("GATEKEEPER", user.Role);
    }

    [Fact]
    public async Task Five_wrong_secrets_lock_the_account_even_for_the_right_one()
    {
        var (auth, admin, username, id) = await NewUserAsync("GATEKEEPER", "5937");
        for (int i = 0; i < 4; i++)
            Assert.Equal("LOGIN_FAILED", (await Assert.ThrowsAsync<TruckGateException>(() => auth.LoginAsync(username, "0000"))).Code);
        Assert.Equal("ACCOUNT_LOCKED", (await Assert.ThrowsAsync<TruckGateException>(() => auth.LoginAsync(username, "0000"))).Code);
        Assert.Equal("ACCOUNT_LOCKED", (await Assert.ThrowsAsync<TruckGateException>(() => auth.LoginAsync(username, "5937"))).Code);

        // an administrator setting a new PIN unlocks the account
        await admin.SetUserPinAsync(id, "6048", DatabaseFixture.Tester);
        Assert.Equal(id, (await auth.LoginAsync(username, "6048")).Id);
    }

    [Fact]
    public async Task Unknown_user_and_quarry_operator_cannot_log_in_to_the_web()
    {
        var (auth, _, username, _) = await NewUserAsync("QUARRY_OPERATOR", "4821");
        Assert.Equal("LOGIN_FAILED", (await Assert.ThrowsAsync<TruckGateException>(() => auth.LoginAsync(username, "4821"))).Code);
        Assert.Equal("LOGIN_FAILED", (await Assert.ThrowsAsync<TruckGateException>(() => auth.LoginAsync("nobody", "4821"))).Code);
    }

    [Theory]
    [InlineData("ADMIN", "short")]
    [InlineData("ADMIN", "1234567890123")]
    [InlineData("GATEKEEPER", "1234")]
    [InlineData("GATEKEEPER", "letters")]
    public async Task Weak_secrets_are_refused_per_role(string role, string secret)
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var admin = new AdminService(db);
        var id = await admin.CreateUserAsync("u" + fx.NextId(), "U", role, "fr", DatabaseFixture.Tester);
        await Assert.ThrowsAsync<TruckGateException>(() => admin.SetUserPinAsync(id, secret, DatabaseFixture.Tester));
    }

    [Fact]
    public async Task Only_one_first_administrator_can_be_created_from_the_console()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var auth = new AuthService(db);
        await auth.CreateFirstAdminAsync("admin", "Admin", "a-long-password");
        var e = await Assert.ThrowsAsync<TruckGateException>(() => auth.CreateFirstAdminAsync("admin2", "Admin", "a-long-password"));
        Assert.Equal("ADMIN_EXISTS", e.Code);
        Assert.Equal("ADMIN", (await auth.LoginAsync("admin", "a-long-password")).Role);
    }
}
