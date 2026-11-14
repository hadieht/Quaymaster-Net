using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Security;
using Quaymaster.Application.Users;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;
using Quaymaster.UnitTests.Fakes;

namespace Quaymaster.UnitTests.Auth;

public sealed class UserAndAuthServiceTests
{
    private static readonly Actor Admin = new("root", "10.0.0.1");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUserRepository _users = new();
    private readonly InMemorySessionRepository _sessionRows = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly SessionRevocationBus _bus = new();
    private readonly List<SessionRevocation> _events = [];
    private readonly SessionService _sessions;
    private readonly UserService _userService;
    private readonly AuthService _auth;

    public UserAndAuthServiceTests()
    {
        _bus.Subscribe(_events.Add);
        var hasher = new FakePasswordHasher();
        _sessions = new SessionService(_sessionRows, _users, _bus, new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12)), _time);
        _userService = new UserService(_users, _sessions, _bus, hasher, _audit, _time);
        _auth = new AuthService(_users, _sessions, hasher, new LoginLimiter(_time), _audit, _time);
    }

    private async Task<UserInfoDto> AddAsync(string name, Role role = Role.Viewer, string password = "a-long-password!") =>
        await _userService.CreateAsync(name, password, role, false, Admin, default);

    private static async Task<AppException> Failure(Func<Task> act) => (await act.Should().ThrowAsync<AppException>()).Which;

    // ---- users ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_Validates_The_Username_And_Password_Strength()
    {
        (await Failure(() => AddAsync("bad name!"))).Message.Should().Be("Username must be 1-64 characters: letters, digits, . _ @ -");
        (await Failure(() => AddAsync("ok", password: "short"))).Message.Should().Be("Password must be at least 12 characters long");
        (await Failure(() => AddAsync(new string('x', 65)))).Status.Should().Be(400);
    }

    [Fact]
    public async Task Duplicate_Usernames_Conflict_Case_Insensitively()
    {
        await AddAsync("Alice");

        var error = await Failure(() => AddAsync("alice"));

        error.Status.Should().Be(409);
        error.Code.Should().Be(ErrorCode.Conflict);
        error.Message.Should().Be("Username already exists");
    }

    [Fact]
    public async Task Create_Is_Audited_Without_Any_Password()
    {
        await AddAsync("bob", Role.Viewer, "super-secret-password");

        var record = _audit.Records.Should().ContainSingle().Which;
        record.Action.Should().Be("user.create");
        record.Target.Should().Be("bob");
        record.Actor.Should().Be("root");
        record.Ip.Should().Be("10.0.0.1");
        System.Text.Json.JsonSerializer.Serialize(record).Should().NotContain("super-secret");
    }

    [Fact]
    public async Task The_Last_Active_Admin_Cannot_Be_Demoted_Disabled_Or_Deleted()
    {
        var only = await AddAsync("admin", Role.Admin);

        var demote = await Failure(() => _userService.UpdateAsync(only.Id, new UserPatch(Role: Role.Viewer), Admin, default));
        var disable = await Failure(() => _userService.UpdateAsync(only.Id, new UserPatch(Disabled: true), Admin, default));
        var delete = await Failure(() => _userService.DeleteAsync(only.Id, Admin, default));

        demote.Status.Should().Be(409);
        demote.Message.Should().Be("Cannot demote or disable the last active admin");
        disable.Message.Should().Be("Cannot demote or disable the last active admin");
        delete.Message.Should().Be("Cannot delete the last active admin");
        delete.Code.Should().Be(ErrorCode.Conflict);
    }

    [Fact]
    public async Task With_A_Second_Active_Admin_Demotion_And_Deletion_Are_Allowed_Again()
    {
        var first = await AddAsync("admin1", Role.Admin);
        var second = await AddAsync("admin2", Role.Admin);

        (await _userService.UpdateAsync(first.Id, new UserPatch(Role: Role.Viewer), Admin, default)).Role.Should().Be("viewer");
        await Failure(() => _userService.DeleteAsync(second.Id, Admin, default)); // now the last one again
        await _userService.UpdateAsync(first.Id, new UserPatch(Role: Role.Admin), Admin, default);
        await _userService.DeleteAsync(second.Id, Admin, default);

        (await _users.CountAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_Disabled_Admin_Does_Not_Count_As_Active()
    {
        var a = await AddAsync("admin1", Role.Admin);
        var b = await AddAsync("admin2", Role.Admin);
        await _userService.UpdateAsync(b.Id, new UserPatch(Disabled: true), Admin, default);

        (await Failure(() => _userService.UpdateAsync(a.Id, new UserPatch(Disabled: true), Admin, default))).Status.Should().Be(409);
        await _userService.DeleteAsync(b.Id, Admin, default); // deleting the disabled one is fine
    }

    [Fact]
    public async Task Privilege_Changes_Revoke_That_Users_Sessions_And_Notify_Streams()
    {
        await AddAsync("admin", Role.Admin);
        var viewer = await AddAsync("viewer");
        var user = (await _users.FindByIdAsync(viewer.Id, default))!;
        await _sessions.CreateAsync(user, null, null, default);
        await _sessions.CreateAsync(user, null, null, default);

        var updated = await _userService.UpdateAsync(viewer.Id, new UserPatch(LogsRead: true), Admin, default);

        updated.LogsRead.Should().BeTrue();
        _sessionRows.Rows.Should().BeEmpty();
        _events.Should().Contain(e => e.UserId == viewer.Id);
        _audit.Actions().Should().Contain("user.update");
    }

    [Fact]
    public async Task An_Empty_Patch_Revokes_Nothing()
    {
        await AddAsync("admin", Role.Admin);
        var viewer = await AddAsync("viewer");
        await _sessions.CreateAsync((await _users.FindByIdAsync(viewer.Id, default))!, null, null, default);

        await _userService.UpdateAsync(viewer.Id, new UserPatch(), Admin, default);

        _sessionRows.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task Unknown_Users_Are_404()
    {
        (await Failure(() => _userService.UpdateAsync(99, new UserPatch(), Admin, default))).Status.Should().Be(404);
        (await Failure(() => _userService.DeleteAsync(99, Admin, default))).Message.Should().Be("User not found");
        (await Failure(() => _userService.SetPasswordAsync(99, "a-long-password!", Admin, default))).Code.Should().Be(ErrorCode.NotFound);
    }

    [Fact]
    public async Task Admin_Password_Reset_Signs_The_User_Out_Everywhere()
    {
        await AddAsync("admin", Role.Admin);
        var viewer = await AddAsync("viewer");
        await _sessions.CreateAsync((await _users.FindByIdAsync(viewer.Id, default))!, null, null, default);

        await _userService.SetPasswordAsync(viewer.Id, "brand-new-password", Admin, default);

        _sessionRows.Rows.Should().BeEmpty();
        (await _users.FindByIdAsync(viewer.Id, default))!.PasswordHash.Should().Be("hash:brand-new-password");
        (await Failure(() => _userService.SetPasswordAsync(viewer.Id, "short", Admin, default))).Status.Should().Be(400);
    }

    // ---- login / password change ---------------------------------------------------------------------------

    [Fact]
    public async Task Login_Succeeds_With_Right_Credentials_And_Rotates_The_Session_Id()
    {
        await AddAsync("carol", Role.Admin, "carol-password-1");

        var (t1, _) = await _auth.LoginAsync("carol", "carol-password-1", "5.5.5.5", "ua", default);
        var (t2, _) = await _auth.LoginAsync("CAROL", "carol-password-1", "5.5.5.5", "ua", default);

        t1.Should().NotBe(t2);
        _audit.Records.Where(r => r.Action == "auth.login").Should().OnlyContain(r => r.Outcome == AuditOutcome.Success && r.Actor == "carol");
    }

    [Fact]
    public async Task Failed_Login_Is_401_And_Never_Stores_The_Typed_Username()
    {
        await AddAsync("dave", Role.Admin, "dave-password-12");

        var typo = await Failure(() => _auth.LoginAsync("hunter2-oops-a-password", "x", "5.5.5.5", null, default));
        var wrong = await Failure(() => _auth.LoginAsync("dave", "wrong", "5.5.5.5", null, default));

        typo.Status.Should().Be(401);
        typo.Message.Should().Be("Invalid username or password");
        wrong.Message.Should().Be(typo.Message, "no account enumeration");
        _audit.Records.Where(r => r.Action == "auth.login").Select(r => r.Actor).Should().Contain("dave");
        _audit.Records.Select(r => r.Actor).Should().NotContain(a => a.Contains("hunter2", StringComparison.Ordinal));
        _audit.Records.First(r => r.Actor.StartsWith("unknown:", StringComparison.Ordinal)).Actor.Should().MatchRegex("^unknown:[0-9a-f]{12}$");
    }

    [Fact]
    public async Task Disabled_Accounts_Cannot_Log_In_Even_With_The_Right_Password()
    {
        await AddAsync("admin", Role.Admin);
        var viewer = await AddAsync("eve", Role.Viewer, "eve-password-123");
        await _userService.UpdateAsync(viewer.Id, new UserPatch(Disabled: true), Admin, default);

        (await Failure(() => _auth.LoginAsync("eve", "eve-password-123", "1.1.1.1", null, default))).Status.Should().Be(401);
    }

    [Fact]
    public async Task Throttled_Login_Is_Audited_As_Denied_Before_Any_Hashing()
    {
        await AddAsync("frank", Role.Admin, "frank-password-1");
        for (var i = 0; i < 20; i++)
        {
            await Failure(() => _auth.LoginAsync($"user{i}", "x", "7.7.7.7", null, default));
        }

        var error = await Failure(() => _auth.LoginAsync("frank", "frank-password-1", "7.7.7.7", null, default));

        error.Status.Should().Be(429);
        error.Headers.Should().ContainKey("Retry-After");
        _audit.Records.Last().Outcome.Should().Be(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Failed_Logins_Beyond_The_Free_Ones_Are_Delayed_By_The_Progressive_Account_Delay()
    {
        await AddAsync("gina", Role.Admin, "gina-password-12");
        // 3rd failure for the account => 500 ms delay, applied via the (fake) clock
        await Failure(() => _auth.LoginAsync("gina", "x", "1.0.0.1", null, default));
        await Failure(() => _auth.LoginAsync("gina", "x", "1.0.0.2", null, default));
        var third = _auth.LoginAsync("gina", "x", "1.0.0.3", null, default);

        third.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await Failure(() => third)).Status.Should().Be(401);
    }

    [Fact]
    public async Task Password_Change_Needs_The_Current_Password_And_Signs_Out_Other_Sessions()
    {
        await AddAsync("hank", Role.Admin, "hank-password-12");
        var (_, current) = await _auth.LoginAsync("hank", "hank-password-12", "2.2.2.2", null, default);
        await _auth.LoginAsync("hank", "hank-password-12", "3.3.3.3", null, default);

        var wrong = await Failure(() => _auth.ChangePasswordAsync(current, "not-the-password", "a-new-password-1", Admin, default));
        await _auth.ChangePasswordAsync(current, "hank-password-12", "a-new-password-1", Admin, default);

        wrong.Status.Should().Be(403);
        wrong.Message.Should().Be("Current password is incorrect");
        _sessionRows.Rows.Keys.Should().Equal(current.Session.IdHash);
        (await _users.FindByUsernameAsync("hank", default))!.PasswordHash.Should().Be("hash:a-new-password-1");
        _audit.Actions().Should().Contain("auth.password_change");
    }

    [Fact]
    public async Task Password_Change_Rejects_Weak_New_Passwords_Before_Checking_The_Current_One()
    {
        await AddAsync("ivy", Role.Admin, "ivy-password-123");
        var (_, current) = await _auth.LoginAsync("ivy", "ivy-password-123", "2.2.2.2", null, default);

        (await Failure(() => _auth.ChangePasswordAsync(current, "whatever", "short", Admin, default))).Status.Should().Be(400);
    }

    [Fact]
    public async Task Current_Password_Guessing_Is_Throttled_Per_Session()
    {
        await AddAsync("jon", Role.Admin, "jon-password-123");
        var (_, ctx) = await _auth.LoginAsync("jon", "jon-password-123", "2.2.2.2", null, default);

        var statuses = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var attempt = Failure(() => _auth.ChangePasswordAsync(ctx, "wrong-guess-" + i, "a-new-password-1", Admin, default));
            while (!attempt.IsCompleted)
            {
                _time.Advance(TimeSpan.FromMilliseconds(250)); // well inside the 30 s pair lock
                await Task.Yield();
            }

            statuses.Add((await attempt).Status);
        }

        statuses.Should().Equal(403, 403, 403, 403, 403, 429);
    }

    [Fact]
    public async Task Logout_And_Logout_All_Remove_Sessions_And_Are_Audited()
    {
        await AddAsync("kim", Role.Admin, "kim-password-123");
        var (_, one) = await _auth.LoginAsync("kim", "kim-password-123", "9.9.9.9", null, default);
        await _auth.LoginAsync("kim", "kim-password-123", "9.9.9.9", null, default);

        await _auth.LogoutAsync(one, Admin, default);
        _sessionRows.Rows.Should().HaveCount(1);
        await _auth.LogoutAllAsync(one, Admin, default);

        _sessionRows.Rows.Should().BeEmpty();
        _audit.Actions().Should().Contain(["auth.logout", "auth.logout_all"]);
    }
}
