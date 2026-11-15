using System.Text;
using System.Text.Json;

namespace Quaymaster.IntegrationTests.Support;

public sealed record SseMessage(string Event, string Data)
{
    public JsonElement Json => JsonElement.Parse(Data);
}

/// <summary>Opens a Server-Sent Events response on the in-memory host and reads frames as they arrive.</summary>
public sealed class SseSession : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

    private SseSession(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public int Status => (int)_response.StatusCode;

    public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

    public static async Task<SseSession> OpenAsync(QmFactory factory, ApiClient client, string path)
    {
        var http = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + path);
        if (client.Cookie is not null)
        {
            request.Headers.Add("Cookie", $"{client.CookieName}={client.Cookie}");
        }

        request.Headers.Add("Accept", "text/event-stream");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        return new SseSession(response, new StreamReader(stream, Encoding.UTF8));
    }

    public async Task<string> ReadBodyAsync() => await _response.Content.ReadAsStringAsync();

    /// <summary>Next non-comment frame, or null when the stream ended.</summary>
    public async Task<SseMessage?> NextAsync()
    {
        string? name = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await _reader.ReadLineAsync(_cts.Token);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (name is not null || data.Length > 0)
                {
                    return new SseMessage(name ?? "message", data.ToString());
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }
    }

    /// <summary>Waits for a comment line (<c>: text</c>) such as the <c>ping</c> heartbeat; null when the stream ended first.</summary>
    public async Task<string?> NextCommentAsync(string text)
    {
        while (await _reader.ReadLineAsync(_cts.Token) is { } line)
        {
            if (line.StartsWith(':') && line[1..].Trim() == text)
            {
                return line;
            }
        }

        return null;
    }

    public async Task<SseMessage> NextOfAsync(string eventName)
    {
        while (await NextAsync() is { } frame)
        {
            if (frame.Event == eventName)
            {
                return frame;
            }
        }

        throw new InvalidOperationException("stream ended before event " + eventName);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _reader.Dispose();
        _response.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
