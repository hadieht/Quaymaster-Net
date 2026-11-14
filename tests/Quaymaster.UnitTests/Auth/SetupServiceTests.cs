using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Users;
using Quaymaster.Domain.Identity;
using Quaymaster.UnitTests.Fakes;

namespace Quaymaster.UnitTests.Auth;

public sealed class SetupServiceTests : IDisposable
{
    private sealed class CapturingAnnouncer : ISetupAnnouncer
    {
        public List<string> Messages { get; } = [];

        public void Announce(string message) => Messages.Add(message);
    }

    private sealed class MemoryTokenStore : ISetupTokenStore
    {
        public string? Content { get; private set; }

        public string Write(string token)
        {
            Content = token;
            return "/data/setup-token";
        }

        public void Delete() => Content = null;
    }

    private sealed class MapFiles(Dictionary<string, string> files) : IFileReader
    {
        public string ReadAllText(string path) => files[path];
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUserRepository _users = new();
    private readonly InMemorySessionRepository _sessionRows = new();
    private readonly CapturingAnnouncer _announcer = new();
    private readonly MemoryTokenStore _store = new();
    private readonly RecordingAuditLog _audit = new();
    private SetupService? _setup;

    public void Dispose() => _setup?.Dispose();

    private SetupService Build(AdminBootstrap? admin = null, Dictionary<string, string>? files = null)
    {
        var hasher = new FakePasswordHasher();
        var bus = new SessionRevocationBus();
        var sessions = new SessionService(_sessionRows, _users, bus, new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12)), _time);
        var users = new UserService(_users, sessions, bus, hasher, _audit, _time);
        _setup = new SetupService(_users, users, sessions, hasher, _audit, _store, _announcer, new MapFiles(files ?? []), new SetupOptions(admin), _time);
        return _setup;
    }

    private static async Task<AppException> Failure(Func<Task> act) => (await act.Should().ThrowAsync<AppException>()).Which;

    [Fact]
    public async Task First_Run_Issues_A_Token_Announces_It_And_Persists_It()
    {
        var setup = Build();

        (await setup.InitializeAsync(default)).Should().Be(SetupOutcome.SetupTokenIssued);

        (await setup.IsRequiredAsync(default)).Should().BeTrue();
        _store.Content.Should().HaveLength(32); // 24 random bytes, base64url
        _announcer.Messages.Should().ContainSingle().Which.Should().Contain(_store.Content!).And.Contain("valid 60 minutes").And.Contain("/data/setup-token");
    }

    [Fact]
    public async Task Completing_Setup_Creates_The_Admin_Logs_In_And_Consumes_The_Token()
    {
        var setup = Build();
        await setup.InitializeAsync(default);

        var (cookie, context) = await setup.CompleteAsync(_store.Content!, "boss", "a-strong-password", "1.2.3.4", "ua", default);

        cookie.Should().NotBeNullOrEmpty();
        context.User.Role.Should().Be(Role.Admin);
        _store.Content.Should().BeNull("the token file is deleted on success");
        (await setup.IsRequiredAsync(default)).Should().BeFalse();
        _audit.Actions().Should().Contain("auth.setup");
        (await Failure(() => setup.CompleteAsync("anything", "other", "a-strong-password", "1.2.3.4", null, default)))
            .Message.Should().Be("Setup has already been completed");
    }

    [Fact]
    public async Task Wrong_Token_Is_403_And_Five_Attempts_Per_Ip_Then_429()
    {
        var setup = Build();
        await setup.InitializeAsync(default);

        for (var i = 0; i < 5; i++)
        {
            var error = await Failure(() => setup.CompleteAsync("wrong", "boss", "a-strong-password", "6.6.6.6", null, default));
            error.Status.Should().Be(403);
            error.Message.Should().Be("Invalid setup token");
        }

        var limited = await Failure(() => setup.CompleteAsync(_store.Content!, "boss", "a-strong-password", "6.6.6.6", null, default));
        limited.Status.Should().Be(429);

        // another address with the right token still works
        (await setup.CompleteAsync(_store.Content!, "boss", "a-strong-password", "7.7.7.7", null, default)).Context.User.Username.Should().Be("boss");
    }

    [Fact]
    public async Task Token_Expires_After_60_Minutes()
    {
        var setup = Build();
        await setup.InitializeAsync(default);
        var token = _store.Content!;

        _time.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1) - TimeSpan.FromSeconds(1)); // exactly 60: still valid
        var error = await Failure(() => setup.CompleteAsync("x", "boss", "a-strong-password", "1.1.1.1", null, default));
        error.Message.Should().Be("Invalid setup token");

        // the renewal timer fires after TTL + 1 s and issues a NEW token while setup is still required
        _time.Advance(TimeSpan.FromSeconds(2));
        _store.Content.Should().NotBe(token);
        _announcer.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Failed_Creation_Does_Not_Burn_The_Token()
    {
        var setup = Build();
        await setup.InitializeAsync(default);

        (await Failure(() => setup.CompleteAsync(_store.Content!, "boss", "short", "1.1.1.1", null, default))).Status.Should().Be(400);
        var again = await setup.CompleteAsync(_store.Content!, "boss", "a-strong-password", "1.1.1.1", null, default);

        again.Context.User.Username.Should().Be("boss");
    }

    [Fact]
    public async Task Concurrent_Redemptions_Of_One_Token_Create_At_Most_One_Admin()
    {
        var setup = Build();
        await setup.InitializeAsync(default);
        var token = _store.Content!;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await setup.CompleteAsync(token, $"boss{i}", "a-strong-password", $"10.0.0.{i}", null, default);
                return true;
            }
            catch (AppException)
            {
                return false;
            }
        })));

        results.Count(ok => ok).Should().Be(1);
        (await _users.CountAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task Env_Admin_With_A_Hash_Is_Created_Once_And_No_Token_Is_Issued()
    {
        var setup = Build(new AdminBootstrap("root", "hash:whatever", null));

        (await setup.InitializeAsync(default)).Should().Be(SetupOutcome.EnvAdminCreated);

        _store.Content.Should().BeNull();
        (await _users.FindByUsernameAsync("root", default))!.Role.Should().Be(Role.Admin);
        (await setup.InitializeAsync(default)).Should().Be(SetupOutcome.AlreadyInitialised);
        (await _users.CountAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task Env_Admin_With_A_Password_File_Is_Hashed_At_First_Start()
    {
        var setup = Build(new AdminBootstrap("root", null, "/run/pw"), new() { ["/run/pw"] = "file-password-123\n" });

        await setup.InitializeAsync(default);

        (await _users.FindByUsernameAsync("root", default))!.PasswordHash.Should().Be("hash:file-password-123");
    }

    [Fact]
    public async Task An_Invalid_Env_Admin_Hash_Fails_Startup()
    {
        var setup = Build(new AdminBootstrap("root", "not-a-hash", null));

        await setup.Invoking(s => s.InitializeAsync(default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("QM_ADMIN_PASSWORD_HASH*");
    }

    [Fact]
    public async Task Existing_Users_Remove_A_Stale_Token_File()
    {
        var setup = Build();
        await _users.AddAsync(new User("a", "h", Role.Admin, false, _time.GetUtcNow()), default);
        _store.Write("stale");

        (await setup.InitializeAsync(default)).Should().Be(SetupOutcome.AlreadyInitialised);

        _store.Content.Should().BeNull();
    }
}
