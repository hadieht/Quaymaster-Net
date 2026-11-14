using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Domain.Identity;
using Quaymaster.UnitTests.Fakes;

namespace Quaymaster.UnitTests.Auth;

public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Absolute = TimeSpan.FromHours(12);

    private static Session NewSession() => new("hash", 1, "secret", T0, Absolute, "127.0.0.1", "ua");

    [Theory]
    [InlineData(0, SessionState.Valid)]
    [InlineData(29 * 60, SessionState.Valid)]
    [InlineData(30 * 60 - 1, SessionState.Valid)]
    [InlineData(30 * 60, SessionState.Expired)] // idle boundary is inclusive, like the original (>=)
    [InlineData(60 * 60, SessionState.Expired)]
    public void Idle_Timeout_Is_Measured_From_Last_Seen(int secondsLater, SessionState expected) =>
        NewSession().Evaluate(T0.AddSeconds(secondsLater), Idle).Should().Be(expected);

    [Fact]
    public void Absolute_Expiry_Wins_Even_For_An_Active_Session()
    {
        var session = NewSession();
        // keep the session "active" every 20 minutes for 12 hours
        for (var t = T0.AddMinutes(20); t < T0 + Absolute; t = t.AddMinutes(20))
        {
            session.Evaluate(t, Idle).Should().Be(SessionState.Valid);
            session.Touch(t);
        }

        session.Evaluate(T0 + Absolute, Idle).Should().Be(SessionState.Expired);
    }

    [Fact]
    public void Touch_Is_Only_Needed_After_The_Interval()
    {
        var session = NewSession();

        session.NeedsTouch(T0.AddSeconds(30)).Should().BeFalse();
        session.NeedsTouch(T0.AddSeconds(31)).Should().BeTrue();
    }

    [Fact]
    public void Idle_Expiry_Is_Capped_By_The_Absolute_Expiry()
    {
        var session = NewSession();
        session.Touch(T0 + Absolute - TimeSpan.FromMinutes(5));

        session.IdleExpiresAt(Idle).Should().Be(T0 + Absolute);
    }
}

public sealed class SessionServiceTests
{
    private static readonly SessionOptions Options = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryUserRepository _users = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly SessionRevocationBus _bus = new();
    private readonly List<SessionRevocation> _events = [];
    private readonly SessionService _service;

    public SessionServiceTests()
    {
        _bus.Subscribe(_events.Add);
        _service = new SessionService(_sessions, _users, _bus, Options, _time);
    }

    private async Task<User> UserAsync(string name = "alice", Role role = Role.Admin) =>
        await _users.AddAsync(new User(name, "hash:x", role, false, _time.GetUtcNow()), default);

    [Fact]
    public async Task Cookie_Token_Is_Random_And_Only_Its_Sha256_Is_Stored()
    {
        var user = await UserAsync();

        var (token, context) = await _service.CreateAsync(user, "10.0.0.1", "agent", default);
        var (token2, _) = await _service.CreateAsync(user, "10.0.0.1", "agent", default);

        token.Should().NotBe(token2);
        token.Length.Should().Be(43); // 32 bytes base64url, no padding
        context.Session.IdHash.Should().Be(SessionService.HashToken(token)).And.MatchRegex("^[0-9a-f]{64}$");
        _sessions.Rows.Keys.Should().NotContain(token);
    }

    [Fact]
    public async Task Valid_Token_Authenticates_And_Unknown_Or_Oversized_Tokens_Do_Not()
    {
        var user = await UserAsync();
        var (token, _) = await _service.CreateAsync(user, null, null, default);

        (await _service.AuthenticateAsync(token, default)).Should().NotBeNull();
        (await _service.AuthenticateAsync("nope", default)).Should().BeNull();
        (await _service.AuthenticateAsync(null, default)).Should().BeNull();
        (await _service.AuthenticateAsync(new string('a', 129), default)).Should().BeNull();
    }

    [Fact]
    public async Task Idle_Session_Is_Deleted_On_Access_And_A_Revocation_Is_Published()
    {
        var (token, context) = await _service.CreateAsync(await UserAsync(), null, null, default);

        _time.Advance(TimeSpan.FromMinutes(30));

        (await _service.AuthenticateAsync(token, default)).Should().BeNull();
        _sessions.Rows.Should().BeEmpty();
        _events.Should().ContainSingle().Which.SessionIdHashes.Should().Equal(context.Session.IdHash);
    }

    [Fact]
    public async Task Activity_Extends_The_Idle_Window_But_Not_The_Absolute_One()
    {
        var (token, _) = await _service.CreateAsync(await UserAsync(), null, null, default);

        for (var i = 0; i < 35; i++) // 35 x 20 min = 11h40
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            (await _service.AuthenticateAsync(token, default)).Should().NotBeNull($"step {i}");
        }

        _time.Advance(TimeSpan.FromMinutes(20)); // 12h exactly
        (await _service.AuthenticateAsync(token, default)).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_Users_Cannot_Use_Existing_Sessions()
    {
        var user = await UserAsync();
        var (token, _) = await _service.CreateAsync(user, null, null, default);
        user.Apply(new UserPatch(Disabled: true));

        (await _service.AuthenticateAsync(token, default)).Should().BeNull();
    }

    [Fact]
    public async Task Csrf_Token_Is_Session_Bound_And_Verified_In_Constant_Time_Form()
    {
        var user = await UserAsync();
        var (_, a) = await _service.CreateAsync(user, null, null, default);
        var (_, b) = await _service.CreateAsync(user, null, null, default);

        var tokenA = _service.CsrfToken(a);

        tokenA.Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(_service.CsrfToken(b));
        _service.VerifyCsrf(a, tokenA).Should().BeTrue();
        _service.VerifyCsrf(b, tokenA).Should().BeFalse();
        _service.VerifyCsrf(a, null).Should().BeFalse();
        _service.VerifyCsrf(a, tokenA[..63]).Should().BeFalse();
        _service.VerifyCsrf(a, tokenA.ToUpperInvariant()).Should().BeFalse();
    }

    [Fact]
    public async Task Session_Info_Reports_Both_Expiries_In_Javascript_Iso_Format()
    {
        var (_, context) = await _service.CreateAsync(await UserAsync(), null, null, default);

        var info = _service.ToInfo(context);

        info.ExpiresAt.Should().Be("2026-11-15T00:00:00.000Z");
        info.IdleExpiresAt.Should().Be("2026-11-14T12:30:00.000Z");
        info.User.Username.Should().Be("alice");
    }

    [Fact]
    public async Task Purge_Removes_Only_Dead_Sessions()
    {
        var user = await UserAsync();
        await _service.CreateAsync(user, null, null, default);
        _time.Advance(TimeSpan.FromMinutes(31));
        var (liveToken, _) = await _service.CreateAsync(user, null, null, default);

        await _service.PurgeExpiredAsync(default);

        _sessions.Rows.Should().ContainSingle().Which.Key.Should().Be(SessionService.HashToken(liveToken));
    }

    [Fact]
    public async Task Revoke_For_User_Keeps_The_Excepted_Session()
    {
        var user = await UserAsync();
        var (_, keep) = await _service.CreateAsync(user, null, null, default);
        await _service.CreateAsync(user, null, null, default);
        await _service.CreateAsync(user, null, null, default);

        await _service.RevokeForUserAsync(user.Id, keep.Session.IdHash, default);

        _sessions.Rows.Keys.Should().Equal(keep.Session.IdHash);
    }
}
