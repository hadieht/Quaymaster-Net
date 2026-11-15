using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quaymaster.Api.Http;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.IntegrationTests;

/// <summary>Red-team F-10: log lines and error envelopes are scrubbed of learned secrets and token patterns.</summary>
public sealed class RedTeamLoggingTests
{
    private const string Secret = "fx-env-password-7c1";

    [Fact]
    public void Console_Formatter_Scrubs_Message_And_Exception_Text()
    {
        var scrubber = new Scrubber();
        scrubber.Add(Secret);
        var formatter = new ScrubbingConsoleFormatter(scrubber);
        var ex = new InvalidOperationException($"docker said no: password {Secret} ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        var entry = new LogEntry<string>(LogLevel.Error, "Cat", new EventId(7), $"failed with {Secret}", ex, (s, e) => s);
        using var w = new StringWriter();

        formatter.Write(in entry, null, w);

        var text = w.ToString();
        text.Should().NotContain(Secret).And.NotContain("ghp_abcdefghijklmnopqrstuvwxyz");
        text.Should().Contain("failed with").And.Contain("fail: Cat[7]");
    }

    [Fact]
    public async Task Error_Envelope_Message_Is_Scrubbed()
    {
        var scrubber = new Scrubber();
        scrubber.Add(Secret);
        var services = new ServiceCollection().AddSingleton(scrubber).BuildServiceProvider();
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();

        await ErrorEnvelopeWriter.WriteAsync(ctx, 502, Quaymaster.Application.Errors.ErrorCode.Internal, $"Pull failed: login {Secret}");

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().NotContain(Secret).And.Contain("Pull failed");
    }
}
