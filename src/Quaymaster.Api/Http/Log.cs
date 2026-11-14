namespace Quaymaster.Api.Http;

internal static partial class Log
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Unhandled error on {Method} {Path}")]
    public static partial void Unhandled(ILogger logger, Exception exception, string method, string path);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Unhandled error after the response had started on {Method} {Path}; aborting the connection")]
    public static partial void UnhandledAfterStart(ILogger logger, Exception exception, string method, string path);
}
