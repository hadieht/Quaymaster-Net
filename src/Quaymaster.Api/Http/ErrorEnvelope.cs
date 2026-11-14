using System.Text.Json;
using Quaymaster.Application.Errors;

namespace Quaymaster.Api.Http;

/// <summary>The uniform error body <c>{"error":{"code","message","details?"}}</c> - the only error shape clients ever see.</summary>
public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, object? Details = null);

public static class ErrorEnvelopeWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Task WriteAsync(HttpContext context, AppException error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);
        return WriteAsync(context, error.Status, error.Code, error.Message, error.Details, error.Headers);
    }

    public static Task WriteAsync(
        HttpContext context,
        int status,
        ErrorCode code,
        string message,
        object? details = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                response.Headers[name] = value;
            }
        }

        return response.WriteAsync(
            JsonSerializer.Serialize(new ErrorEnvelope(new ErrorBody(code.ToWire(), message, details)), Options),
            context.RequestAborted);
    }
}
