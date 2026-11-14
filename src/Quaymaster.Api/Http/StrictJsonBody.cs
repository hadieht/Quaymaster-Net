using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Validation;

namespace Quaymaster.Api.Http;

/// <summary>
/// Reads and validates a request body the way the original zod schemas did: JSON object only, unknown properties are
/// rejected (strict), wrong types and missing fields are reported as <c>400 validation</c> with
/// <c>details: [{path, message}]</c>. An empty body is treated as <c>{}</c>.
/// </summary>
public static partial class StrictJsonBody
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<T> ReadAsync<T>(HttpContext context, IValidator<T> validator)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);
        var model = await ParseAsync<T>(context);
        validator.EnsureValid(model);
        return model;
    }

    public static async Task<T> ParseAsync<T>(HttpContext context)
        where T : class
    {
        // Kestrel enforces MaxRequestBodySize too; this check keeps the contract independent of the server.
        if (context.Request.ContentLength > RequestLimits.MaxBodyBytes)
        {
            throw new AppException(413, ErrorCode.Validation, "Request body too large");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > RequestLimits.MaxBodyBytes)
            {
                throw new AppException(413, ErrorCode.Validation, "Request body too large");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0 || IsBlank(buffer))
        {
            return Deserialize<T>("{}");
        }

        if (!IsJson(context.Request.ContentType))
        {
            throw new AppException(415, ErrorCode.Validation, "Content-Type must be application/json");
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Null => Deserialize<T>("{}"),
                JsonValueKind.Object => Deserialize<T>(document.RootElement.GetRawText()),
                _ => throw Invalid("", "Expected object"),
            };
        }
        catch (JsonException)
        {
            throw AppException.BadRequest("Bad request");
        }
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw Invalid("", "Expected object");
        }
        catch (JsonException e) when (UnmappedRegex().Match(e.Message) is { Success: true } unmapped)
        {
            throw Invalid("", $"Unrecognized key(s) in object: '{unmapped.Groups[1].Value}'");
        }
        catch (JsonException e)
        {
            var path = (e.Path ?? string.Empty).TrimStart('$').TrimStart('.');
            throw Invalid(path, "Invalid type");
        }
    }

    private static AppException Invalid(string path, string message) =>
        AppException.BadRequest("Invalid request", new[] { new ValidationIssue(path, message) });

    private static bool IsBlank(MemoryStream buffer)
    {
        foreach (var b in buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("JSON property '([^']*)' could not be mapped")]
    private static partial Regex UnmappedRegex();
}
