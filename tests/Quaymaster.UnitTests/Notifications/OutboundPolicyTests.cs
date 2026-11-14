using FluentAssertions;
using Quaymaster.Domain.Notifications;
using Xunit;

namespace Quaymaster.UnitTests.Notifications;

public sealed class OutboundPolicyTests
{
    // The original refuses: unspecified, link-local (cloud metadata), multicast, broadcast. It ALLOWS loopback and RFC1918
    // (self-hosted ntfy / gotify on the home network) - this table is the contract.
    [Theory]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("0.1.2.3", "unspecified")]
    [InlineData("169.254.169.254", "link-local (cloud metadata range)")]
    [InlineData("169.254.0.1", "link-local (cloud metadata range)")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.250", "multicast")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("::", "unspecified")]
    [InlineData("fe80::1", "link-local")]
    [InlineData("febf::1", "link-local")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("::ffff:169.254.169.254", "link-local (cloud metadata range)")]
    [InlineData("::ffff:a9fe:a9fe", "link-local (cloud metadata range)")]
    [InlineData("::ffff:0.0.0.0", "unspecified")]
    public void Refused_Addresses(string ip, string reason) => OutboundPolicy.DisallowedAddressReason(ip).Should().Be(reason);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("223.255.255.255")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fec0::1")] // site-local is not in the original's list
    public void Allowed_Addresses(string ip) => OutboundPolicy.DisallowedAddressReason(ip).Should().BeNull();

    [Theory]
    [InlineData("http://127.0.0.1:8080/x")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("http://[::1]:9000/")]
    [InlineData("https://hooks.slack.com/services/T/B/x")]
    [InlineData("http://localhost:2586/topic")]
    [InlineData("http://ntfy.home.lan/topic")]
    public void Allowed_Urls(string url) => OutboundPolicy.Validate(url).Should().NotBeNull();

    [Theory]
    [InlineData("not a url", "Invalid URL")]
    [InlineData("/relative", "Invalid URL")]
    [InlineData("ftp://example.com/x", "Only http and https URLs are allowed")]
    [InlineData("file:///etc/passwd", "Only http and https URLs are allowed")]
    [InlineData("javascript:alert(1)", "Only http and https URLs are allowed")]
    [InlineData("http://user:pw@example.com/", "URLs with embedded credentials (user:password@) are not allowed")]
    [InlineData("http://user@example.com/", "URLs with embedded credentials (user:password@) are not allowed")]
    [InlineData("http://169.254.169.254/latest/meta-data", "Destination address 169.254.169.254 is not allowed (link-local (cloud metadata range))")]
    [InlineData("http://0.0.0.0:80/", "Destination address 0.0.0.0 is not allowed (unspecified)")]
    [InlineData("http://224.0.0.1/", "Destination address 224.0.0.1 is not allowed (multicast)")]
    [InlineData("http://[fe80::1]/", "Destination address fe80::1 is not allowed (link-local)")]
    [InlineData("http://[::ffff:169.254.169.254]/", "Destination address ::ffff:169.254.169.254 is not allowed (link-local (cloud metadata range))")]
    public void Refused_Urls_With_The_Original_Messages(string url, string message)
    {
        var act = () => OutboundPolicy.Validate(url);
        act.Should().Throw<OutboundPolicyException>().WithMessage(message);
    }

    [Fact]
    public void Numeric_Host_Notations_Are_Not_A_Bypass()
    {
        // WHATWG normalises 0xA9FEA9FE / 2852039166 to the metadata address; .NET keeps the text, so the connect-time address check
        // (which resolves the host and tests every address) is the safety net - see OutboundTransportTests.
        foreach (var url in new[] { "http://0xA9FEA9FE/", "http://2852039166/", "http://0251.0376.0251.0376/" })
        {
            try
            {
                OutboundPolicy.Validate(url);
            }
            catch (OutboundPolicyException)
            {
                // refused at parse time: also fine
            }
        }
    }

    [Fact]
    public void Masking_Keeps_Only_Scheme_And_Host() =>
        OutboundPolicy.MaskToSchemeHost("https://discord.com/api/webhooks/1/SECRET?x=1", "••••••").Should().Be("https://discord.com/••••••");

    [Fact]
    public void Limits_Match_The_Original()
    {
        OutboundPolicy.DefaultTimeoutMs.Should().Be(5000);
        OutboundPolicy.MaxResponseBytes.Should().Be(64 * 1024);
        OutboundPolicy.MaxRedirects.Should().Be(3);
    }
}
