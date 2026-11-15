using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Api.Http;

/// <summary>
/// Console log formatter that runs every line (message, scope-free) and its exception text through the secret scrubber
/// and the text redaction rules (red-team F-10: the original logger scrubbed messages and stacks; raw exception text such
/// as a Docker or registry error can carry a learned secret or a token).
/// </summary>
public sealed class ScrubbingConsoleFormatter(Scrubber scrubber) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "qm";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var text = $"{Level(logEntry.LogLevel)}: {logEntry.Category}[{logEntry.EventId.Id}]{Environment.NewLine}      {message}";
        if (logEntry.Exception is not null)
        {
            text += Environment.NewLine + logEntry.Exception;
        }

        textWriter.WriteLine(scrubber.Clean(text));
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}
