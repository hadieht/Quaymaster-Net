using System.Text.RegularExpressions;
using FluentAssertions;
using Quaymaster.Application.Errors;
using Quaymaster.Infrastructure.Security;

namespace Quaymaster.UnitTests.Auth;

public sealed partial class PasswordHasherTests : IDisposable
{
    // Vectors produced by the ORIGINAL server's algorithm (node:crypto scrypt, NFKC, r=8, p=1, 64-byte key, salt 01..10).
    private const string NodeVectorSimple =
        "scrypt$1024$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$zf07uTieg/gRB8YLLpZaGRtHRypFURBaNb9IDWu4oYr2+ypwmK1txCTzwPnUYENzSLFRWCL2tYdV99a2KCNcQw==";

    private const string NodeVectorUnicode =
        "scrypt$16384$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$l7h97XWDg1VaR2S+RhTQKaWGT+HKn2Ow/HGN/+OpYP4DXYCuBYJ6IxDjBJnk1VHlxfmVb6zM4PR+zmpngW/gIg==";

    private readonly ScryptPasswordHasher _hasher = new(10);

    public void Dispose() => _hasher.Dispose();

    [GeneratedRegex(@"^scrypt\$1024\$8\$1\$[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{86}==$")]
    private static partial Regex Format();

    [Fact]
    public async Task Hash_Then_Verify_RoundTrips_And_Uses_The_Original_Format()
    {
        var hash = await _hasher.HashAsync("correct horse battery staple");

        hash.Should().MatchRegex(Format());
        (await _hasher.VerifyAsync("correct horse battery staple", hash)).Should().BeTrue();
        (await _hasher.VerifyAsync("correct horse battery stapl3", hash)).Should().BeFalse();
    }

    [Fact]
    public async Task Hashes_Use_A_Fresh_Salt_Every_Time()
    {
        var a = await _hasher.HashAsync("same password 123");
        var b = await _hasher.HashAsync("same password 123");

        a.Should().NotBe(b);
    }

    [Fact]
    public async Task Verifies_Hashes_Produced_By_The_Original_Node_Implementation()
    {
        (await _hasher.VerifyAsync("correct horse battery staple", NodeVectorSimple)).Should().BeTrue();
        (await _hasher.VerifyAsync("pässwörd-ﬁnal-12345", NodeVectorUnicode)).Should().BeTrue();
    }

    [Fact]
    public async Task Passwords_Are_Normalised_To_NFKC_Like_The_Original()
    {
        // 'ﬁ' (U+FB01 ligature) normalises to "fi" under NFKC.
        (await _hasher.VerifyAsync("pässwörd-final-12345", NodeVectorUnicode)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("bcrypt$1024$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$AAAA")]
    [InlineData("scrypt$1000$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$zf07uTieg/gRB8YLLpZaGRtHRypFURBaNb9IDWu4oYr2+ypwmK1txCTzwPnUYENzSLFRWCL2tYdV99a2KCNcQw==")] // N not a power of two
    [InlineData("scrypt$1024$8$1$AQID$zf07uTieg/gRB8YLLpZaGRtHRypFURBaNb9IDWu4oYr2+ypwmK1txCTzwPnUYENzSLFRWCL2tYdV99a2KCNcQw==")] // salt < 8 bytes
    [InlineData("scrypt$1024$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$AAAA")] // hash length
    [InlineData("scrypt$2097152$8$1$AQIDBAUGBwgJCgsMDQ4PEA==$zf07uTieg/gRB8YLLpZaGRtHRypFURBaNb9IDWu4oYr2+ypwmK1txCTzwPnUYENzSLFRWCL2tYdV99a2KCNcQw==")] // N too large
    [InlineData("scrypt$1048576$32$1$AQIDBAUGBwgJCgsMDQ4PEA==$zf07uTieg/gRB8YLLpZaGRtHRypFURBaNb9IDWu4oYr2+ypwmK1txCTzwPnUYENzSLFRWCL2tYdV99a2KCNcQw==")] // exceeds the memory cap
    public async Task Malformed_Or_Unsafe_Stored_Hashes_Are_Rejected_Not_Executed(string stored)
    {
        _hasher.IsValidHash(stored).Should().BeFalse();
        (await _hasher.VerifyAsync("anything-at-all-1", stored)).Should().BeFalse();
    }

    [Fact]
    public void Accepts_Well_Formed_Hashes()
    {
        _hasher.IsValidHash(NodeVectorSimple).Should().BeTrue();
        _hasher.IsValidHash(NodeVectorUnicode).Should().BeTrue();
    }

    [Fact]
    public void Cost_Parameter_Is_Bounded()
    {
        FluentActions.Invoking(() => new ScryptPasswordHasher(9)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ScryptPasswordHasher(21)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Floods_Of_Concurrent_Hashes_Are_Shed_With_429_Instead_Of_Queuing_Forever()
    {
        // 1 running + 8 queued is the budget; the rest must fail fast with rate_limited + Retry-After.
        using var slow = new ScryptPasswordHasher(15);
        var attempts = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            try
            {
                await slow.HashAsync("flood-password-123");
                return (AppException?)null;
            }
            catch (AppException e)
            {
                return e;
            }
        })).ToArray();

        var results = await Task.WhenAll(attempts);

        var rejected = results.Where(r => r is not null).ToList();
        rejected.Should().NotBeEmpty();
        rejected.Should().OnlyContain(e => e!.Status == 429 && e.Code == ErrorCode.RateLimited && e.Headers!["Retry-After"] == "5");
    }
}
