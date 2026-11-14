using System.Globalization;
using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Validation;

/// <summary>
/// Reads query-string parameters with the messages of the original zod schemas
/// (<c>z.coerce.number().int().min().max()</c>, <c>z.enum</c>, <c>z.string().max()</c>) and reports every problem at once as
/// <c>400 validation</c> with <c>details: [{path, message}]</c>.
/// </summary>
public sealed class QueryReader(Func<string, string?> get)
{
    private readonly List<ValidationIssue> _issues = [];

    public int? NumberInRange(string name, int min, int max, int? defaultValue = null) => Number(name, min, max, defaultValue, integer: true) is { } v ? (int)v : null;

    public long? Number64InRange(string name, long min, long max, long? defaultValue = null) => Number(name, min, max, defaultValue, integer: true);

    private long? Number(string name, long min, long max, long? defaultValue, bool integer)
    {
        var raw = get(name);
        if (raw is null)
        {
            return defaultValue;
        }

        // z.coerce.number(): "" -> 0, otherwise Number(raw)
        double value;
        if (raw.Trim().Length == 0)
        {
            value = 0;
        }
        else if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            _issues.Add(new ValidationIssue(name, "Expected number, received nan"));
            return defaultValue;
        }

        if (integer && value != Math.Floor(value))
        {
            _issues.Add(new ValidationIssue(name, "Expected integer, received float"));
            return defaultValue;
        }

        if (value < min)
        {
            _issues.Add(new ValidationIssue(name, $"Number must be greater than or equal to {min}"));
            return defaultValue;
        }

        if (value > max)
        {
            _issues.Add(new ValidationIssue(name, $"Number must be less than or equal to {max}"));
            return defaultValue;
        }

        return (long)value;
    }

    public string? Enum(string name, params string[] values)
    {
        var raw = get(name);
        if (raw is null)
        {
            return null;
        }

        if (!values.Contains(raw, StringComparer.Ordinal))
        {
            _issues.Add(new ValidationIssue(name, $"Invalid enum value. Expected {string.Join(" | ", values.Select(v => $"'{v}'"))}, received '{raw}'"));
            return null;
        }

        return raw;
    }

    public string? Text(string name, int max, System.Text.RegularExpressions.Regex? pattern = null)
    {
        var raw = get(name);
        if (raw is null)
        {
            return null;
        }

        if (raw.Length > max)
        {
            _issues.Add(new ValidationIssue(name, $"String must contain at most {max} character(s)"));
            return null;
        }

        if (pattern is not null && !pattern.IsMatch(raw))
        {
            _issues.Add(new ValidationIssue(name, "Invalid"));
            return null;
        }

        return raw;
    }

    public void ThrowIfInvalid()
    {
        if (_issues.Count > 0)
        {
            throw AppException.BadRequest("Invalid request", _issues.ToList());
        }
    }
}
