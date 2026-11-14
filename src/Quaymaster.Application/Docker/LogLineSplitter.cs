using System.Text;
using System.Text.RegularExpressions;

namespace Quaymaster.Application.Docker;

public sealed record LogLine(string Stream, string? Ts, string Text);

/// <summary>Incremental line splitter over demultiplexed log frames (port of the original <c>LogDemuxer</c>).</summary>
public sealed partial class LogLineSplitter(bool timestamps, Action<LogLine> onLine)
{
    public const int MaxLineChars = 16 * 1024;

    private readonly Decoder _outDecoder = Encoding.UTF8.GetDecoder();
    private readonly Decoder _errDecoder = Encoding.UTF8.GetDecoder();
    private string _outPartial = string.Empty;
    private string _errPartial = string.Empty;

    public void Push(LogFrame frame)
    {
        var decoder = frame.Stderr ? _errDecoder : _outDecoder;
        var span = frame.Data.Span;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(span.Length)];
        var n = decoder.GetChars(span, chars, flush: false);
        Text(frame.Stderr, new string(chars, 0, n));
    }

    public void Flush()
    {
        EmitPartial(false);
        EmitPartial(true);
    }

    private void EmitPartial(bool stderr)
    {
        var partial = stderr ? _errPartial : _outPartial;
        if (partial.Length > 0)
        {
            Emit(stderr, partial);
        }

        if (stderr)
        {
            _errPartial = string.Empty;
        }
        else
        {
            _outPartial = string.Empty;
        }
    }

    private void Text(bool stderr, string s)
    {
        var data = (stderr ? _errPartial : _outPartial) + s;
        var parts = data.Split('\n');
        var partial = parts[^1];
        var lines = parts.AsSpan(0, parts.Length - 1).ToArray().ToList();
        if (partial.Length > MaxLineChars)
        {
            lines.Add(partial);
            partial = string.Empty;
        }

        if (stderr)
        {
            _errPartial = partial;
        }
        else
        {
            _outPartial = partial;
        }

        foreach (var line in lines)
        {
            Emit(stderr, line.EndsWith('\r') ? line[..^1] : line);
        }
    }

    private void Emit(bool stderr, string line)
    {
        string? ts = null;
        var text = line;
        if (timestamps)
        {
            var m = TimestampRe().Match(line);
            if (m.Success)
            {
                ts = m.Groups[1].Value;
                text = m.Groups[2].Value;
            }
        }

        onLine(new LogLine(stderr ? "stderr" : "stdout", ts, text.Length > MaxLineChars ? text[..MaxLineChars] + "…" : text));
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T[0-9:.]+Z) ([\s\S]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRe();
}
