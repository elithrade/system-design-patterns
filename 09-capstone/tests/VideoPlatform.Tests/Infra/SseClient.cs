using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VideoPlatform.Tests;

public sealed record SseEvent(string? Id, string? Event, string Data)
{
    public VideoSnapshot Snapshot => JsonSerializer.Deserialize<VideoSnapshot>(Data, Json.Options)!;
}

// Reads an SSE response as it arrives: events, and comment lines separately.
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<SseEvent> _events = Channel.CreateUnbounded<SseEvent>();
    private readonly Channel<string> _comments = Channel.CreateUnbounded<string>();
    private readonly Task _reader;

    private SseClient(HttpResponseMessage response)
    {
        _response = response;
        _reader = Task.Run(ReadAsync);
    }

    public HttpResponseMessage Response => _response;

    // Fails if the response headers take longer than 3 s.
    public static async Task<SseClient> ConnectAsync(HttpClient client, string url, long? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString());
        }
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (await Task.WhenAny(send, Task.Delay(3000)) != send)
        {
            Assert.Fail("no response headers within 3 s: flush them before waiting for progress");
        }
        return new SseClient(await send);
    }

    public async Task<SseEvent> NextEventAsync(int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _events.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"no event within {timeoutMs} ms");
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("the stream ended");
        }
    }

    public async Task<List<SseEvent>> NextEventsAsync(int count, int timeoutMs = 5000)
    {
        var events = new List<SseEvent>();
        for (var i = 0; i < count; i++)
        {
            events.Add(await NextEventAsync(timeoutMs));
        }
        return events;
    }

    // True if no event arrives within the time.
    public async Task<bool> NoEventWithinAsync(int ms)
    {
        await Task.Delay(ms);
        return !_events.Reader.TryPeek(out _);
    }

    public async Task<string> NextCommentAsync(int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _comments.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"no comment line within {timeoutMs} ms");
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await using var body = await _response.Content.ReadAsStreamAsync(_stop.Token);
            using var reader = new StreamReader(body, Encoding.UTF8);
            string? id = null, type = null;
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        _events.Writer.TryWrite(new SseEvent(id, type, data.ToString()));
                    }
                    id = type = null;
                    data.Clear();
                }
                else if (line.StartsWith(':'))
                {
                    _comments.Writer.TryWrite(line);
                }
                else
                {
                    var colon = line.IndexOf(':');
                    var field = colon < 0 ? line : line[..colon];
                    var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
                    switch (field)
                    {
                        case "id": id = value; break;
                        case "event": type = value; break;
                        case "data": data.Append(data.Length > 0 ? "\n" : "").Append(value); break;
                    }
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            _events.Writer.TryComplete();
            _comments.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _response.Dispose();
        try
        {
            await _reader;
        }
        catch (Exception)
        {
        }
    }
}
