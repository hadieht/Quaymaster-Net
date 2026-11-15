using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Abstractions;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>Red-team F-03 (lost update) and F-04 (last-admin race): the repros as deterministic / repeated regressions.</summary>
public sealed class RedTeamAccountRaceTests : IDisposable
{
    private readonly GateHasher _gate = new();
    private readonly QmFactory _factory;

    public RedTeamAccountRaceTests()
    {
        _factory = new QmFactory(null, services =>
        {
            var inner = services.Single(d => d.ServiceType == typeof(IPasswordHasher)).ImplementationInstance as IPasswordHasher
                ?? throw new InvalidOperationException("hasher registration changed");
            _gate.Inner = inner;
            services.AddSingleton<IPasswordHasher>(_gate);
        });
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<long> CreateAdminAsync(ApiClient admin, string name)
    {
        var r = await admin.PostAsync("/users", new { username = name, password = "second admin pass 1", role = "admin" });
        r.Status.Should().Be(201);
        return r.Json.GetProperty("id").GetInt64();
    }

    private static async Task<string> RoleOfAsync(ApiClient admin, long id)
    {
        var users = await admin.GetAsync("/users");
        return users.Json.EnumerateArray().Single(u => u.GetProperty("id").GetInt64() == id).GetProperty("role").GetString()!;
    }

    [Fact]
    public async Task Demotion_Is_Not_Undone_By_A_Concurrent_Own_Password_Change()
    {
        var admin = await _factory.NewAdminAsync();
        var id = await CreateAdminAsync(admin, "admin2");
        var admin2 = _factory.NewClient();
        (await admin2.LoginAsync("admin2", "second admin pass 1")).Status.Should().Be(200);

        var entered = _gate.Arm();
        var change = admin2.PostAsync("/auth/password", new { current = "second admin pass 1", next = "a brand new password 2" });
        await entered.WaitAsync(TimeSpan.FromSeconds(10));

        (await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { role = "viewer" })).Status.Should().Be(200);
        _gate.Release();
        await change;

        (await RoleOfAsync(admin, id)).Should().Be("viewer", "the slow hash must write only the password columns");
        var relogin = await _factory.NewClient().LoginAsync("admin2", "a brand new password 2");
        relogin.Status.Should().Be(200);
        relogin.Json.GetProperty("user").GetProperty("role").GetString().Should().Be("viewer");
    }

    [Fact]
    public async Task Disable_Is_Not_Undone_By_A_Concurrent_Admin_Password_Reset()
    {
        var admin = await _factory.NewAdminAsync();
        var id = await CreateAdminAsync(admin, "admin2");

        var entered = _gate.Arm();
        var reset = admin.PostAsync($"/users/{id}/password", new { password = "reset by admin pass 3" });
        await entered.WaitAsync(TimeSpan.FromSeconds(10));

        (await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { disabled = true })).Status.Should().Be(200);
        _gate.Release();
        (await reset).Status.Should().Be(200);

        var users = await admin.GetAsync("/users");
        users.Json.EnumerateArray().Single(u => u.GetProperty("id").GetInt64() == id).GetProperty("disabled").GetBoolean().Should().BeTrue();
        (await _factory.NewClient().LoginAsync("admin2", "reset by admin pass 3")).Status.Should().Be(401);
    }

    [Fact]
    public async Task Two_Admins_Disabling_Each_Other_Concurrently_Never_Leave_Zero_Admins()
    {
        var admin = await _factory.NewAdminAsync();
        var id2 = await CreateAdminAsync(admin, "admin2");
        var admin2 = _factory.NewClient();
        (await admin2.LoginAsync("admin2", "second admin pass 1")).Status.Should().Be(200);
        var id1 = (await admin.GetAsync("/users")).Json.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin").GetProperty("id").GetInt64();

        // both fire at the same instant; exactly one may win, the other must get 409
        var go = new TaskCompletionSource();
        var a = Task.Run(async () => { await go.Task; return await admin.SendAsync(HttpMethod.Patch, $"/users/{id2}", new { disabled = true }); });
        var b = Task.Run(async () => { await go.Task; return await admin2.SendAsync(HttpMethod.Patch, $"/users/{id1}", new { disabled = true }); });
        go.SetResult();
        var statuses = (await Task.WhenAll(a, b)).Select(r => r.Status).Order().ToArray();

        statuses[0].Should().Be(200);
        statuses[1].Should().NotBe(200, "exactly one of the two mutual disables may win (the loser: 409 last-admin, or 401 once the winner revoked its session)");
        var viewer = _factory.NewClient();
        var survivors = 0;
        foreach (var (name, pw) in new[] { ("admin", AdminPw), ("admin2", "second admin pass 1") })
        {
            if ((await _factory.NewClient().LoginAsync(name, pw)).Status == 200)
            {
                survivors++;
            }
        }

        survivors.Should().Be(1);
        _ = viewer;
    }

    private const string AdminPw = QmFactory.AdminPassword;

    private sealed class GateHasher : IPasswordHasher
    {
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;

        public IPasswordHasher Inner { get; set; } = null!;

        public Task Arm()
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _entered.Task;
        }

        public void Release() => _release!.SetResult();

        public async Task<string> HashAsync(string password, CancellationToken ct = default)
        {
            var (entered, release) = (_entered, _release);
            if (entered is not null && release is not null && !entered.Task.IsCompleted)
            {
                entered.SetResult();
                await release.Task;
            }

            return await Inner.HashAsync(password, ct);
        }

        public Task<bool> VerifyAsync(string password, string storedHash, CancellationToken ct = default) => Inner.VerifyAsync(password, storedHash, ct);

        public Task DummyVerifyAsync(string password, CancellationToken ct = default) => Inner.DummyVerifyAsync(password, ct);

        public bool IsValidHash(string storedHash) => Inner.IsValidHash(storedHash);
    }
}
