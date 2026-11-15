using System.Text;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Api.Http;

/// <summary>
/// Last line of defence for JSON API responses (red-team F-01): every learned secret value (env / label / command secrets
/// of every container and project that was loaded) is masked in the serialized body, wherever it appears (Cmd, Args,
/// Healthcheck, compose command ...). The original did this in an <c>onSend</c> hook. Skipped for <c>/auth/*</c> and
/// <c>/users*</c> (they carry the user's own password-ish data and no learned secrets) and for non-JSON responses
/// (SSE streams pass through unbuffered; operation output is scrubbed at the source).
/// </summary>
public sealed class ResponseScrubMiddleware(RequestDelegate next, Scrubber scrubber)
{
    private const string Prefix = "/api/v1/";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = CanonicalPath.Of(context.Request);
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || IsExcluded(path))
        {
            await next(context);
            return;
        }

        var original = context.Response.Body;
        var decider = new DecidingStream(context.Response, original);
        context.Response.Body = decider;
        try
        {
            await next(context);
            await decider.CompleteAsync(scrubber, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    private static bool IsExcluded(string path)
    {
        var rest = path[Prefix.Length..];
        return rest.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)
            || rest.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("users", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Buffers while the response is JSON (decided at the first write), passes everything else straight through.</summary>
    private sealed class DecidingStream(HttpResponse response, Stream inner) : Stream
    {
        private MemoryStream? _buffer;
        private bool _decided;

        private bool Decide()
        {
            if (!_decided)
            {
                _decided = true;
                var ct = response.ContentType;
                if (ct is not null && ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    _buffer = new MemoryStream();
                }
            }

            return _buffer is not null;
        }

        public async Task CompleteAsync(Scrubber scrubber, CancellationToken ct)
        {
            if (_buffer is null)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);
            var bytes = Encoding.UTF8.GetBytes(scrubber.Scrub(text));
            _buffer = null;
            response.ContentLength = bytes.Length;
            await inner.WriteAsync(bytes, ct);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Decide())
            {
                _buffer!.Write(buffer);
            }
            else
            {
                inner.Write(buffer);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Decide())
            {
                _buffer!.Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _decided && _buffer is null ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override void Flush()
        {
            if (_decided && _buffer is null)
            {
                inner.Flush();
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
