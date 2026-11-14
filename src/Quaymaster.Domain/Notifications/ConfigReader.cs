using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Notifications;

/// <summary>
/// A tiny zod-compatible object reader: collects EVERY issue (not just the first) with zod's paths and messages, rejects
/// unknown keys last (<c>.strict()</c>) and keeps the schema's key order in the normalised output.
/// </summary>
internal sealed partial class ConfigReader
{
    private readonly JsonObject _input;
    private readonly string _prefix;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public ConfigReader(JsonObject input, string prefix = "")
    {
        _input = input;
        _prefix = prefix;
    }

    public List<ConfigIssue> Issues { get; } = [];

    public JsonObject Output { get; } = [];

    public static string Received(JsonNode? n, bool present)
    {
        if (!present)
        {
            return "undefined";
        }

        return n switch
        {
            null => "null",
            JsonArray => "array",
            JsonObject => "object",
            JsonValue v => v.GetValueKind() switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "null",
            },
            _ => "unknown",
        };
    }

    public static Func<string, string?> Min(int n) => s => s.Length < n ? $"String must contain at least {n} character(s)" : null;

    public static Func<string, string?> Max(int n) => s => s.Length > n ? $"String must contain at most {n} character(s)" : null;

    public static Func<string, string?> Re(Regex re, string message) => s => re.IsMatch(s) ? null : message;

    public static Func<string, string?> Email { get; } = s => EmailRe().IsMatch(s) ? null : "Invalid email";

    public static Func<string, string?> OutboundUrl { get; } = s =>
    {
        try
        {
            OutboundPolicy.Validate(s);
            return null;
        }
        catch (OutboundPolicyException e)
        {
            return e.Message;
        }
    };

    public string? String(string key, bool required, params Func<string, string?>[] checks)
    {
        _known.Add(key);
        var present = _input.TryGetPropertyValue(key, out var node);
        var path = Path(key);
        if (!present)
        {
            if (required)
            {
                Issues.Add(new ConfigIssue(path, "Required"));
            }

            return null;
        }

        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
        {
            Issues.Add(new ConfigIssue(path, $"Expected string, received {Received(node, true)}"));
            return null;
        }

        var s = v.GetValue<string>();
        foreach (var check in checks)
        {
            if (check(s) is { } msg)
            {
                Issues.Add(new ConfigIssue(path, msg));
            }
        }

        Output[key] = s;
        return s;
    }

    public long? Integer(string key, bool required, long min, long max)
    {
        _known.Add(key);
        var present = _input.TryGetPropertyValue(key, out var node);
        var path = Path(key);
        if (!present)
        {
            if (required)
            {
                Issues.Add(new ConfigIssue(path, "Required"));
            }

            return null;
        }

        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            Issues.Add(new ConfigIssue(path, $"Expected number, received {Received(node, true)}"));
            return null;
        }

        var d = double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        if (d != Math.Floor(d))
        {
            Issues.Add(new ConfigIssue(path, "Expected integer, received float"));
            return null;
        }

        var n = (long)d;
        if (n < min)
        {
            Issues.Add(new ConfigIssue(path, $"Number must be greater than or equal to {min}"));
        }

        if (n > max)
        {
            Issues.Add(new ConfigIssue(path, $"Number must be less than or equal to {max}"));
        }

        Output[key] = n;
        return n;
    }

    /// <summary>Optional boolean with a default that is written to the output when absent.</summary>
    public bool Boolean(string key, bool defaultValue)
    {
        _known.Add(key);
        if (!_input.TryGetPropertyValue(key, out var node))
        {
            Output[key] = defaultValue;
            return defaultValue;
        }

        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            var b = v.GetValue<bool>();
            Output[key] = b;
            return b;
        }

        Issues.Add(new ConfigIssue(Path(key), $"Expected boolean, received {Received(node, true)}"));
        return defaultValue;
    }

    public void StringList(string key, bool required, int minItems, int maxItems, params Func<string, string?>[] itemChecks)
    {
        _known.Add(key);
        var present = _input.TryGetPropertyValue(key, out var node);
        var path = Path(key);
        if (!present)
        {
            if (required)
            {
                Issues.Add(new ConfigIssue(path, "Required"));
            }

            return;
        }

        if (node is not JsonArray arr)
        {
            Issues.Add(new ConfigIssue(path, $"Expected array, received {Received(node, true)}"));
            return;
        }

        if (arr.Count < minItems)
        {
            Issues.Add(new ConfigIssue(path, $"Array must contain at least {minItems} element(s)"));
        }

        if (arr.Count > maxItems)
        {
            Issues.Add(new ConfigIssue(path, $"Array must contain at most {maxItems} element(s)"));
        }

        var outArr = new JsonArray();
        for (var i = 0; i < arr.Count; i++)
        {
            var item = arr[i];
            var ipath = $"{path}.{i}";
            if (item is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            {
                Issues.Add(new ConfigIssue(ipath, $"Expected string, received {Received(item, true)}"));
                continue;
            }

            var s = v.GetValue<string>();
            foreach (var check in itemChecks)
            {
                if (check(s) is { } msg)
                {
                    Issues.Add(new ConfigIssue(ipath, msg));
                }
            }

            outArr.Add(s);
        }

        Output[key] = outArr;
    }

    /// <summary>Optional <c>record&lt;string,string&gt;</c> with key and value rules.</summary>
    public void StringRecord(string key, Func<string, string?>[] keyChecks, Func<string, string?>[] valueChecks)
    {
        _known.Add(key);
        if (!_input.TryGetPropertyValue(key, out var node))
        {
            return;
        }

        var path = Path(key);
        if (node is not JsonObject rec)
        {
            Issues.Add(new ConfigIssue(path, $"Expected object, received {Received(node, true)}"));
            return;
        }

        var outObj = new JsonObject();
        foreach (var (k, v) in rec)
        {
            var kpath = $"{path}.{k}";
            foreach (var check in keyChecks)
            {
                if (check(k) is { } msg)
                {
                    Issues.Add(new ConfigIssue(kpath, msg));
                }
            }

            if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String)
            {
                Issues.Add(new ConfigIssue(kpath, $"Expected string, received {Received(v, true)}"));
                continue;
            }

            var s = jv.GetValue<string>();
            foreach (var check in valueChecks)
            {
                if (check(s) is { } msg)
                {
                    Issues.Add(new ConfigIssue(kpath, msg));
                }
            }

            outObj[k] = s;
        }

        Output[key] = outObj;
    }

    /// <summary>Reports unknown keys (zod strict) after all shape issues.</summary>
    public ConfigParseResult Finish()
    {
        var unknown = _input.Select(p => p.Key).Where(k => !_known.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            Issues.Add(new ConfigIssue(_prefix, $"Unrecognized key(s) in object: {string.Join(", ", unknown.Select(k => $"'{k}'"))}"));
        }

        return new ConfigParseResult(Issues.Count == 0 ? Output : null, Issues);
    }

    private string Path(string key) => _prefix.Length == 0 ? key : $"{_prefix}.{key}";

    [GeneratedRegex(@"^(?!\.)(?!.*\.\.)([A-Z0-9_'+\-\.]*)[A-Z0-9_+-]@([A-Z0-9][A-Z0-9\-]*\.)+[A-Z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRe();
}
