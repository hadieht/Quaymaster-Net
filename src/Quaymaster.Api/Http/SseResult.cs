using System.Text;
using Quaymaster.Application.Streaming;

namespace Quaymaster.Api.Http;

/// <summary>
/// Writes an <see cref="ISseConnection"/> to the response as <c>text/event-stream</c>: <c>: connected</c>, then every
/// queued frame (events and heartbeat comments) until the connection closes (final <c>end</c> event) or the client leaves.
/// </summary>
public sealed class SseResult(ISseConnection connection) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var registration = httpContext.RequestAborted.Register(() => connection.Close("client disconnected"));
        try
        {
            await WriteAsync(response, ": connected\n\n", httpContext.RequestAborted);
            await foreach (var frame in connection.Frames.ReadAllAsync(httpContext.RequestAborted))
            {
                await WriteAsync(response, frame.ToWire(), httpContext.RequestAborted);
                connection.Written(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // client went away
        }
        catch (IOException)
        {
            // client went away
        }
        finally
        {
            connection.Close("closed");
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }
}
