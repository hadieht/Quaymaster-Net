using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Redaction;

/// <summary>
/// Removes literal secret values (and their URL-encoded / JSON-escaped forms) from free text: captured CLI output,
/// error messages, event attributes, log lines. Fed by every container / project that is loaded. Thread-safe.
/// </summary>
public sealed partial class Scrubber
{
    private static readonly HashSet<string> Trivial = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "none", "yes", "info", "warn", "error", "debug", "trace",
    };

    private readonly Lock _gate = new();
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private string[] _sorted = [];

    public int Size
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    /// <summary>
    /// Whether a secret-named value may be learned for GLOBAL literal scrubbing (audit H-01). A short or common value
    /// (<c>2026</c>, <c>alpine</c>, <c>unhealthy</c>) would corrupt unrelated data.
    /// </summary>
    public static bool Learnable(string v)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (v.Length < 8)
        {
            return false;
        }

        if (NumbersAndDatesRe().IsMatch(v))
        {
            return false;
        }

        return !(PlainWordRe().IsMatch(v) && v.Length < 20);
    }

    public void Add(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !Learnable(secret))
        {
            return;
        }

        var forms = new HashSet<string>(StringComparer.Ordinal)
        {
            secret,
            Uri.EscapeDataString(secret),
            System.Text.Json.JsonSerializer.Serialize(secret)[1..^1],
        };
        lock (_gate)
        {
            var changed = false;
            foreach (var f in forms)
            {
                if (Trivial.Contains(f) || f == Redactor.Mask || _values.Contains(f))
                {
                    continue;
                }

                _values.Add(f);
                changed = true;
            }

            if (changed)
            {
                _sorted = [.. _values.OrderByDescending(x => x.Length)];
            }
        }
    }

    public void AddAll(IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        foreach (var s in secrets)
        {
            Add(s);
        }
    }

    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] sorted;
        lock (_gate)
        {
            sorted = _sorted;
        }

        var o = text;
        foreach (var s in sorted)
        {
            if (o.Contains(s, StringComparison.Ordinal))
            {
                o = o.Replace(s, Redactor.Mask, StringComparison.Ordinal);
            }
        }

        return o;
    }

    /// <summary>Scrubs, then applies the text rules. Use for anything free-form.</summary>
    public string Clean(string text) => Redactor.RedactText(Scrub(text));

    [GeneratedRegex(@"^[\d\s:.,/\-T+Z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumbersAndDatesRe();

    [GeneratedRegex(@"^[A-Za-z_ -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainWordRe();
}
