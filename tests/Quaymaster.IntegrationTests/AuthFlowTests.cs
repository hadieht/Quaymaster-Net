using System.Runtime.Versioning;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class AuthFlowTests : IDisposable
{
    private readonly QmFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Status_Is_Public_And_Reports_Setup_Required_On_A_Fresh_Install()
    {
        var client = _factory.NewClient();

        var status = await client.GetAsync("/auth/status", auth: false);

        status.Status.Should().Be(200);
        status.Json.GetRawText().Should().Be("{\"setupRequired\":true,\"authenticated\":false}");
    }

    [Fact]
    public async Task Full_Flow_Setup_Then_Me_Then_Logout_Then_Login_Again()
    {
        var client = _factory.NewClient();

        // setup consumes the one-time token and logs in
        var setup = await client.SetupAsync("admin", QmFactory.AdminPassword);
        setup.Status.Should().Be(200);
        setup.Json.GetProperty("user").GetProperty("username").GetString().Should().Be("admin");
        setup.Json.GetProperty("user").GetProperty("role").GetString().Should().Be("admin");
        setup.Json.GetProperty("user").GetProperty("logsRead").GetBoolean().Should().BeTrue();
        setup.Json.GetProperty("csrfToken").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        setup.Json.GetProperty("expiresAt").GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        setup.Json.GetProperty("idleExpiresAt").GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");

        (await client.GetAsync("/auth/status", auth: true)).Json.GetRawText().Should().Be("{\"setupRequired\":false,\"authenticated\":true}");
        (await client.GetAsync("/auth/status", auth: false)).Json.GetRawText().Should().Be("{\"setupRequired\":false,\"authenticated\":false}");

        // me returns SessionInfo with the same csrf token
        var me = await client.GetAsync("/auth/me");
        me.Status.Should().Be(200);
        me.Json.GetProperty("csrfToken").GetString().Should().Be(client.Csrf);
        me.Json.GetProperty("user").GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        me.Json.GetProperty("user").GetProperty("disabled").GetBoolean().Should().BeFalse();

        // logout kills the session and clears the cookie
        var cookie = client.Cookie;
        var logout = await client.PostAsync("/auth/logout");
        logout.Status.Should().Be(200);
        logout.Json.GetRawText().Should().Be("{\"ok\":true}");
        logout.SetCookie.Should().StartWith("qm_session=;").And.Contain("Max-Age=0").And.Contain("HttpOnly");
        client.Cookie = cookie;
        (await client.GetAsync("/auth/me")).Status.Should().Be(401);

        // login with the same credentials rotates the session id
        var login = await client.LoginAsync("admin", QmFactory.AdminPassword);
        login.Status.Should().Be(200);
        client.Cookie.Should().NotBe(cookie);
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);
    }

    [Fact]
    public async Task Session_Cookie_Has_The_Original_Attributes()
    {
        var client = _factory.NewClient();

        var setup = await client.SetupAsync("admin", QmFactory.AdminPassword);

        var cookie = setup.SetCookie!;
        cookie.Should().MatchRegex(@"^qm_session=[A-Za-z0-9_-]{43}; Path=/; HttpOnly; SameSite=Strict; Max-Age=43200$");
        cookie.Should().NotContain("Secure");
    }

    [Fact]
    public async Task Setup_Rejects_A_Wrong_Token_A_Weak_Password_And_A_Second_Run()
    {
        var client = _factory.NewClient();

        var wrong = await client.SetupAsync("admin", QmFactory.AdminPassword, token: "not-the-token");
        wrong.Status.Should().Be(403);
        wrong.ErrorCode.Should().Be("forbidden");
        wrong.ErrorMessage.Should().Be("Invalid setup token");

        var weak = await client.SetupAsync("admin", "short");
        weak.Status.Should().Be(400);
        weak.ErrorCode.Should().Be("validation");
        weak.ErrorMessage.Should().Be("Password must be at least 12 characters long");

        var token = _factory.ReadSetupToken();
        (await client.SetupAsync("admin", QmFactory.AdminPassword, token)).Status.Should().Be(200, "a failed attempt does not burn the token");

        var again = await _factory.NewClient().SetupAsync("other", QmFactory.AdminPassword, token);
        again.Status.Should().Be(409);
        again.ErrorCode.Should().Be("conflict");
        again.ErrorMessage.Should().Be("Setup has already been completed");
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Setup_Token_File_Is_0600_And_Removed_After_Use_And_The_Data_Dir_Is_Private()
    {
        var client = _factory.NewClient();
        var tokenFile = Path.Combine(_factory.DataDir, "setup-token");

        File.Exists(tokenFile).Should().BeTrue();
        File.GetUnixFileMode(tokenFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_factory.DataDir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(_factory.DatabaseFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (await client.SetupAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);

        File.Exists(tokenFile).Should().BeFalse();
    }

    [Fact]
    public async Task Login_With_Wrong_Credentials_Is_A_Uniform_401_That_Does_Not_Reveal_Accounts()
    {
        var admin = await _factory.NewAdminAsync();
        var client = _factory.NewClient();

        var wrongPassword = await client.LoginAsync("admin", "not the password");
        var unknownUser = await client.LoginAsync("nobody", "not the password");

        wrongPassword.Status.Should().Be(401);
        wrongPassword.ErrorCode.Should().Be("unauthorized");
        wrongPassword.ErrorMessage.Should().Be("Invalid username or password");
        unknownUser.Body.Should().Be(wrongPassword.Body);
        wrongPassword.SetCookie.Should().BeNull();
        admin.Cookie.Should().NotBeNull();
    }

    [Fact]
    public async Task Usernames_Are_Case_Insensitive_At_Login()
    {
        await _factory.NewAdminAsync();

        (await _factory.NewClient().LoginAsync("ADMIN", QmFactory.AdminPassword)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Logout_All_Ends_Every_Session_Of_The_User()
    {
        var first = await _factory.NewAdminAsync();
        var second = _factory.NewClient();
        (await second.LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);

        (await first.PostAsync("/auth/logout-all")).Status.Should().Be(200);

        (await first.GetAsync("/auth/me")).Status.Should().Be(401);
        (await second.GetAsync("/auth/me")).Status.Should().Be(401);
    }

    [Fact]
    public async Task Password_Change_Needs_The_Current_Password_And_Signs_Out_Other_Sessions()
    {
        var current = await _factory.NewAdminAsync();
        var other = _factory.NewClient();
        await other.LoginAsync("admin", QmFactory.AdminPassword);

        var wrong = await current.PostAsync("/auth/password", new { current = "wrong password!!", next = "a brand new password" });
        wrong.Status.Should().Be(403);
        wrong.ErrorCode.Should().Be("forbidden");
        wrong.ErrorMessage.Should().Be("Current password is incorrect");

        var ok = await current.PostAsync("/auth/password", new { current = QmFactory.AdminPassword, next = "a brand new password" });
        ok.Status.Should().Be(200);
        ok.Json.GetRawText().Should().Be("{\"ok\":true,\"note\":\"All other sessions were signed out\"}");

        (await current.GetAsync("/auth/me")).Status.Should().Be(200);
        (await other.GetAsync("/auth/me")).Status.Should().Be(401);
        (await _factory.NewClient().LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(401);
        (await _factory.NewClient().LoginAsync("admin", "a brand new password")).Status.Should().Be(200);
    }

    [Fact]
    public async Task Idle_Sessions_Expire_And_Activity_Keeps_Them_Alive()
    {
        var client = await _factory.NewAdminAsync();

        _factory.Time.Advance(TimeSpan.FromMinutes(25));
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);
        _factory.Time.Advance(TimeSpan.FromMinutes(25));
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);

        _factory.Time.Advance(TimeSpan.FromMinutes(31));
        (await client.GetAsync("/auth/me")).Status.Should().Be(401);
    }

    [Fact]
    public async Task Sessions_Expire_At_The_Absolute_Limit_Even_When_Active()
    {
        using var factory = new QmFactory(new() { ["QM_SESSION_ABSOLUTE_HOURS"] = "1" });
        var client = await factory.NewAdminAsync();

        factory.Time.Advance(TimeSpan.FromMinutes(25));
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);
        factory.Time.Advance(TimeSpan.FromMinutes(25));
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);
        factory.Time.Advance(TimeSpan.FromMinutes(11));

        (await client.GetAsync("/auth/me")).Status.Should().Be(401);
    }

    [Fact]
    public async Task Secure_Mode_Uses_The_Host_Prefixed_Secure_Cookie_And_Hsts()
    {
        using var factory = new QmFactory(new() { ["QM_COOKIE_SECURE"] = "true", ["QM_PUBLIC_ORIGIN"] = "https://localhost" });
        var client = factory.NewClient();
        client.Origin = "https://localhost";

        var setup = await client.SetupAsync("admin", QmFactory.AdminPassword);

        setup.Status.Should().Be(200);
        setup.SetCookie.Should().MatchRegex(@"^__Host-qm_session=[A-Za-z0-9_-]{43}; Path=/; HttpOnly; SameSite=Strict; Max-Age=43200; Secure$");
        setup.Header("Strict-Transport-Security").Should().Be("max-age=15552000; includeSubDomains");
        (await client.GetAsync("/auth/me")).Status.Should().Be(200);
        client.CookieName.Should().Be("__Host-qm_session");
    }

    [Fact]
    public async Task Env_Provisioned_Admin_Skips_The_Setup_Token()
    {
        var hash = await new Quaymaster.Infrastructure.Security.ScryptPasswordHasher(10).HashAsync("provisioned password");
        using var factory = new QmFactory(new() { ["QM_ADMIN_USER"] = "root", ["QM_ADMIN_PASSWORD_HASH"] = hash });
        var client = factory.NewClient();

        (await client.GetAsync("/auth/status", auth: false)).Json.GetProperty("setupRequired").GetBoolean().Should().BeFalse();
        File.Exists(Path.Combine(factory.DataDir, "setup-token")).Should().BeFalse();
        (await client.LoginAsync("root", "provisioned password")).Status.Should().Be(200);
    }
}
