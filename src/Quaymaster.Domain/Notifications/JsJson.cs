using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quaymaster.Domain.Notifications;

/// <summary>
/// <c>JSON.stringify</c> / <c>String(v)</c> as a JavaScript engine produces them. The original builds every channel body
/// with JSON.stringify, so byte-for-byte parity (and the webhook HMAC over the body) needs the same escaping: non-ASCII
/// is emitted raw, only quotes, backslash, control characters and lone surrogates are escaped, no whitespace.
/// </summary>
public static class JsJson
{
    public static string Stringify(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>JavaScript <c>String(value)</c> for a JSON primitive (objects/arrays are not used as data values).</summary>
    public static string ToJsString(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.String:
                    return v.GetValue<string>();
                case JsonValueKind.Number:
                    return Number(v);
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return "null";
            }
        }

        return node is JsonArray ? string.Join(',', ((JsonArray)node).Select(ToJsString)) : "[object Object]";
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        WriteString(sb, s);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, k);
                    sb.Append(':');
                    Write(sb, v);
                }

                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    Write(sb, a[i]);
                }

                sb.Append(']');
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(sb, v.GetValue<string>());
                        break;
                    case JsonValueKind.Number:
                        sb.Append(Number(v));
                        break;
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    default:
                        sb.Append("null");
                        break;
                }

                break;
        }
    }

    private static string Number(JsonValue v)
    {
        if (v.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (v.TryGetValue<double>(out var d))
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                return "null";
            }

            if (d == Math.Floor(d) && Math.Abs(d) < 1e21)
            {
                return d.ToString("F0", CultureInfo.InvariantCulture);
            }

            return d.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
        }

        return v.ToJsonString();
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
