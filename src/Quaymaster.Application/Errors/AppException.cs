namespace Quaymaster.Application.Errors;

/// <summary>
/// An error with a stable machine-readable code and HTTP status. The API layer renders it as the uniform
/// envelope <c>{"error":{"code","message","details?"}}</c>; nothing else may leak to clients.
/// </summary>
public sealed class AppException : Exception
{
    public AppException(
        int status,
        ErrorCode code,
        string message,
        object? details = null,
        IReadOnlyDictionary<string, string>? headers = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
        Headers = headers;
    }

    public int Status { get; }
    public ErrorCode Code { get; }
    public object? Details { get; }

    /// <summary>Extra response headers (e.g. <c>Retry-After</c>).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    public static AppException BadRequest(string message, object? details = null) =>
        new(400, ErrorCode.Validation, message, details);

    public static AppException Unauthorized(string message = "Authentication required") =>
        new(401, ErrorCode.Unauthorized, message);

    public static AppException Forbidden(string message = "Not permitted") =>
        new(403, ErrorCode.Forbidden, message);

    public static AppException NotFound(string what) => new(404, ErrorCode.NotFound, $"{what} not found");

    public static AppException Conflict(string message, ErrorCode code = ErrorCode.Conflict, object? details = null) =>
        new(409, code, message, details);

    public static AppException RateLimited(string message, TimeSpan retryAfter) =>
        new(
            429,
            ErrorCode.RateLimited,
            message,
            null,
            new Dictionary<string, string>
            {
                ["Retry-After"] = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}

/// <summary>One entry of the <c>details</c> array of a validation error (same shape as the original zod issues).</summary>
public sealed record ValidationIssue(string Path, string Message);
