using FluentAssertions;
using Quaymaster.Application.Errors;

namespace Quaymaster.UnitTests.Validation;

public sealed class ErrorCodeTests
{
    // docs/API.md + shared/src/api.ts ErrorCode union of the original
    private static readonly string[] OriginalWireCodes =
    [
        "validation", "unauthorized", "forbidden", "csrf", "origin", "not_found", "conflict", "rate_limited", "read_only",
        "out_of_scope", "self_protected", "confirmation_required", "blocked_drift", "limited_capability", "ignored",
        "unsupported", "docker_unavailable", "compose_failed", "approval_stale", "window_closed", "insufficient_disk",
        "registry_error", "setup_required", "internal",
    ];

    [Fact]
    public void Wire_Codes_Are_Exactly_The_Original_Table() =>
        Enum.GetValues<ErrorCode>().Select(c => c.ToWire()).Should().BeEquivalentTo(OriginalWireCodes);

    [Fact]
    public void Rate_Limit_Errors_Carry_A_Rounded_Up_Retry_After()
    {
        var error = AppException.RateLimited("slow down", TimeSpan.FromMilliseconds(1200));

        error.Status.Should().Be(429);
        error.Headers!["Retry-After"].Should().Be("2");
        AppException.RateLimited("x", TimeSpan.Zero).Headers!["Retry-After"].Should().Be("1");
    }

    [Fact]
    public void Factories_Map_To_The_Documented_Statuses()
    {
        AppException.BadRequest("x").Status.Should().Be(400);
        AppException.Unauthorized().Status.Should().Be(401);
        AppException.Forbidden().Status.Should().Be(403);
        AppException.NotFound("User").Message.Should().Be("User not found");
        AppException.Conflict("x").Status.Should().Be(409);
    }
}
