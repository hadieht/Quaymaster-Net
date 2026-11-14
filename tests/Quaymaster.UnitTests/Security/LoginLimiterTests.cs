using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Security;

namespace Quaymaster.UnitTests.Security;

public sealed class LoginLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly LoginLimiter _limiter;

    public LoginLimiterTests() => _limiter = new LoginLimiter(_time);

    private static AppException Rejected(Action act) => act.Should().Throw<AppException>().Which;

    [Fact]
    public void Ip_Budget_Is_20_Attempts_Per_5_Minutes_With_A_Retry_After()
    {
        for (var i = 0; i < 20; i++)
        {
            _limiter.BeforeAttempt($"user{i}", "1.2.3.4");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var error = Rejected(() => _limiter.BeforeAttempt("another", "1.2.3.4"));

        error.Status.Should().Be(429);
        error.Code.Should().Be(ErrorCode.RateLimited);
        error.Message.Should().Be("Too many attempts from this address");
        // oldest hit was 20 s ago -> 300 - 20 = 280 s
        error.Headers!["Retry-After"].Should().Be("280");
    }

    [Fact]
    public void Ip_Budget_Is_Per_Address_And_Recovers_After_The_Window()
    {
        for (var i = 0; i < 20; i++)
        {
            _limiter.BeforeAttempt("u", "1.1.1.1");
        }

        _limiter.BeforeAttempt("u", "2.2.2.2"); // other address unaffected
        Rejected(() => _limiter.BeforeAttempt("u", "1.1.1.1"));

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1));

        _limiter.BeforeAttempt("u", "1.1.1.1");
    }

    [Fact]
    public void Five_Failures_For_One_Account_And_Ip_Lock_That_Pair_With_Exponential_Backoff()
    {
        for (var i = 0; i < 4; i++)
        {
            _limiter.BeforeAttempt("Alice", "9.9.9.9");
            _limiter.OnFailure("Alice", "9.9.9.9");
        }

        _limiter.BeforeAttempt("Alice", "9.9.9.9"); // 5th attempt still allowed...
        _limiter.OnFailure("Alice", "9.9.9.9"); // ...and its failure locks for 30 s

        var locked = Rejected(() => _limiter.BeforeAttempt("alice", "9.9.9.9")); // account name is case-insensitive
        locked.Message.Should().Be("Too many failed attempts; try again later");
        locked.Headers!["Retry-After"].Should().Be("30");

        _limiter.BeforeAttempt("alice", "8.8.8.8"); // other IP is not locked: the real admin is never locked out remotely

        _time.Advance(TimeSpan.FromSeconds(30));
        _limiter.BeforeAttempt("alice", "9.9.9.9");
        _limiter.OnFailure("alice", "9.9.9.9"); // 6th failure -> 60 s

        Rejected(() => _limiter.BeforeAttempt("alice", "9.9.9.9")).Headers!["Retry-After"].Should().Be("60");
    }

    [Fact]
    public void Pair_Lock_Is_Capped_At_15_Minutes()
    {
        for (var i = 0; i < 30; i++)
        {
            _limiter.OnFailure("a", "ip");
        }

        Rejected(() => _limiter.BeforeAttempt("a", "ip")).Headers!["Retry-After"].Should().Be("900");
    }

    [Fact]
    public void Failed_Responses_Get_A_Progressive_Capped_Delay_After_Two_Failures()
    {
        _limiter.OnFailure("bob", "ip1").Should().Be(TimeSpan.Zero);
        _limiter.OnFailure("bob", "ip2").Should().Be(TimeSpan.Zero);
        _limiter.OnFailure("bob", "ip3").Should().Be(TimeSpan.FromMilliseconds(500));
        _limiter.OnFailure("bob", "ip4").Should().Be(TimeSpan.FromMilliseconds(1000));
        _limiter.OnFailure("bob", "ip5").Should().Be(TimeSpan.FromMilliseconds(2000));

        for (var i = 0; i < 20; i++)
        {
            _limiter.OnFailure("bob", $"x{i}");
        }

        _limiter.OnFailure("bob", "last").Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Account_Delay_Decays_After_15_Minutes_And_Success_Resets()
    {
        for (var i = 0; i < 5; i++)
        {
            _limiter.OnFailure("carol", "ip");
        }

        _time.Advance(TimeSpan.FromMinutes(16));
        _limiter.OnFailure("carol", "ip2").Should().Be(TimeSpan.Zero);

        _limiter.OnFailure("carol", "ip2");
        _limiter.OnFailure("carol", "ip2");
        _limiter.OnSuccess("carol", "ip2");
        _limiter.OnFailure("carol", "ip2").Should().Be(TimeSpan.Zero);
        _limiter.BeforeAttempt("carol", "ip2"); // pair lock cleared too
    }

    [Fact]
    public void Setup_Budget_Allows_5_Attempts_Per_Ip_Per_Window()
    {
        var budget = new AttemptBudget(5, TimeSpan.FromMinutes(60), _time);
        for (var i = 0; i < 5; i++)
        {
            budget.Take("1.1.1.1");
        }

        var error = Rejected(() => budget.Take("1.1.1.1"));
        error.Status.Should().Be(429);
        error.Headers!["Retry-After"].Should().Be("3600");
        budget.Take("2.2.2.2");

        _time.Advance(TimeSpan.FromMinutes(61));
        budget.Take("1.1.1.1");
    }
}
