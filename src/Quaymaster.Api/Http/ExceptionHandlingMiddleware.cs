using Quaymaster.Application.Errors;

namespace Quaymaster.Api.Http;

/// <summary>
/// Outermost middleware. Converts EVERY exception into the standard error envelope with the right code and status,
/// logs unexpected ones server-side and never leaks a message or stack trace of a 5xx to the client.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // client went away; nothing to answer
        }
#pragma warning disable CA1031 // this IS the global handler
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (context.Response.HasStarted)
            {
                Log.UnhandledAfterStart(logger, exception, context.Request.Method, context.Request.Path);
                context.Abort();
                return;
            }

            context.Response.Clear();
            RestoreSecurityHeaders(context);
            await ErrorEnvelopeWriter.WriteAsync(context, Map(exception, context, logger));
        }
    }

    /// <summary>Maps any exception to the API error (unknown exceptions become a generic 500 and are logged).</summary>
    internal static AppException Map(Exception exception, HttpContext context, ILogger logger)
    {
        switch (exception)
        {
            case AppException app:
                return app;
            case BadHttpRequestException { StatusCode: 413 }:
                return new AppException(413, ErrorCode.Validation, "Request body too large");
            case BadHttpRequestException { StatusCode: 415 }:
                return new AppException(415, ErrorCode.Validation, "Content-Type must be application/json");
            case BadHttpRequestException { StatusCode: 404 }:
                return new AppException(404, ErrorCode.NotFound, "Bad request");
            case BadHttpRequestException { StatusCode: >= 400 and < 500 } bad:
                return new AppException(bad.StatusCode, ErrorCode.Validation, "Bad request");
            default:
                Log.Unhandled(logger, exception, context.Request.Method, context.Request.Path);
                return new AppException(500, ErrorCode.Internal, "Internal server error");
        }
    }

    // Response.Clear() drops the headers the SecurityHeaders middleware already set; put them back.
    private static void RestoreSecurityHeaders(HttpContext context) =>
        SecurityHeadersMiddleware.Apply(context, context.RequestServices.GetRequiredService<Quaymaster.Application.Configuration.QmConfig>().CookieSecure);
}
