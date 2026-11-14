using System.Text.Json.Nodes;

namespace Quaymaster.Application.Docker;

/// <summary>Null-tolerant accessors for the Engine's JSON (fields are frequently absent or null).</summary>
public static class JsonX
{
    public static string? Str(this JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? Str(this JsonNode? node, string key) => (node as JsonObject)?[key].Str();

    public static long? Num(this JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var whole))
        {
            return whole;
        }

        return v.TryGetValue<double>(out var fraction) ? (long)fraction : null;
    }

    public static long? Num(this JsonNode? node, string key) => (node as JsonObject)?[key].Num();

    public static bool? Bool(this JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static JsonObject? Obj(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonObject;

    public static JsonArray? Arr(this JsonNode? node, string key) => (node as JsonObject)?[key] as JsonArray;

    public static IEnumerable<JsonObject> Objects(this JsonArray? array) =>
        array is null ? [] : array.OfType<JsonObject>();

    public static Dictionary<string, string> StringMap(this JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (v.Str() is { } s)
                {
                    map[k] = s;
                }
            }
        }

        return map;
    }
}
