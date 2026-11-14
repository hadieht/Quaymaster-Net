using FluentAssertions;
using Quaymaster.Application.Security;

namespace Quaymaster.UnitTests.Security;

public sealed class MutationGuardTests
{
    private static readonly AllowedOrigins Allowed = AllowedOrigins.From(["https://qm.example.org"], "127.0.0.1", 8787);
    private const string Good = "https://qm.example.org";

    private static MutationDecision Decide(string method, string? origin, string? csrf, bool session, bool csrfValid = true) =>
        MutationGuard.Evaluate(method, origin, Allowed, csrf, session, _ => csrfValid);

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("get")]
    public void Safe_Methods_Need_Neither_Origin_Nor_Token(string method) =>
        Decide(method, null, null, session: true, csrfValid: false).Should().Be(MutationDecision.Allowed);

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("post")]
    public void Unsafe_Methods_Need_An_Exact_Origin(string method)
    {
        Decide(method, null, "t", true).Should().Be(MutationDecision.OriginRejected);
        Decide(method, "", "t", true).Should().Be(MutationDecision.OriginRejected);
        Decide(method, "https://evil.example", "t", true).Should().Be(MutationDecision.OriginRejected);
        Decide(method, "https://qm.example.org/", "t", true).Should().Be(MutationDecision.OriginRejected, "trailing slash is not an exact match");
        Decide(method, "HTTPS://QM.EXAMPLE.ORG", "t", true).Should().Be(MutationDecision.OriginRejected, "comparison is exact, like the original");
        Decide(method, "http://qm.example.org", "t", true).Should().Be(MutationDecision.OriginRejected, "scheme matters");
        Decide(method, "null", "t", true).Should().Be(MutationDecision.OriginRejected);
    }

    [Fact]
    public void Matching_Origin_Without_A_Session_Is_Enough()
    {
        // public endpoints (login/setup): no session exists yet
        Decide("POST", Good, null, session: false).Should().Be(MutationDecision.Allowed);
    }

    [Fact]
    public void Authenticated_Mutations_Also_Need_A_Valid_Csrf_Token()
    {
        Decide("POST", Good, null, true, csrfValid: false).Should().Be(MutationDecision.CsrfRejected);
        Decide("POST", Good, "wrong", true, csrfValid: false).Should().Be(MutationDecision.CsrfRejected);
        Decide("POST", Good, "right", true, csrfValid: true).Should().Be(MutationDecision.Allowed);
    }

    [Fact]
    public void Origin_Is_Checked_Before_The_Csrf_Token()
    {
        Decide("POST", "https://evil.example", "right", true, csrfValid: true).Should().Be(MutationDecision.OriginRejected);
    }

    [Theory]
    [InlineData("127.0.0.1", 8787, "http://127.0.0.1:8787,http://localhost:8787")]
    [InlineData("localhost", 9000, "http://localhost:9000,http://127.0.0.1:9000")]
    [InlineData("0.0.0.0", 8787, "http://0.0.0.0:8787,http://127.0.0.1:8787,http://localhost:8787")]
    [InlineData("::1", 8787, "http://[::1]:8787,http://127.0.0.1:8787,http://localhost:8787")]
    [InlineData("192.168.1.50", 8787, "http://192.168.1.50:8787")]
    public void Default_Origins_Are_Derived_From_The_Bind_Address(string host, int port, string expected) =>
        AllowedOrigins.From([], host, port).Origins.Should().BeEquivalentTo(expected.Split(','));

    [Fact]
    public void Explicit_Public_Origins_Replace_The_Derived_Ones() =>
        AllowedOrigins.From(["https://a.example", "http://b.example:8080"], "127.0.0.1", 8787)
            .Origins.Should().BeEquivalentTo("https://a.example", "http://b.example:8080");
}
