using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.TestHost;

namespace Realtime.Tests;

// A WebSocket client on the test server: sends JSON, collects every message the server sends.
public sealed class SocketClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    private readonly Task _reader;
    private readonly SemaphoreSlim _send = new(1, 1);

    private SocketClient(WebSocket socket)
    {
        _socket = socket;
        _reader = Task.Run(ReadAsync);
    }

    public WebSocket Socket => _socket;
    public List<string> Invalid { get; } = [];

    public static async Task<SocketClient> ConnectAsync(App app, long auctionId)
    {
        if (app.OnKestrel)
        {
            var real = new ClientWebSocket();
            await real.ConnectAsync(new UriBuilder(app.KestrelAddress) { Scheme = "ws", Path = $"/auctions/{auctionId}/ws" }.Uri, CancellationToken.None);
            return new SocketClient(real);
        }
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = $"/auctions/{auctionId}/ws" }.Uri;
        var socket = await app.Server.CreateWebSocketClient().ConnectAsync(uri, CancellationToken.None);
        return new SocketClient(socket);
    }

    public Task SendJsonAsync(object message) => SendTextAsync(JsonSerializer.Serialize(message, Json.Options));

    public async Task SendTextAsync(string text)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    public Task BidAsync(string reference, long amount, long bidderId = 1) =>
        SendJsonAsync(new PlaceBidMessage("bid", reference, bidderId, amount));

    public async Task<JsonElement> NextAsync(int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _messages.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"no message within {timeoutMs} ms");
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("the socket closed");
        }
    }

    // The next message of that type, skipping others.
    public async Task<JsonElement> NextOfTypeAsync(string type, int timeoutMs = 3000)
    {
        while (true)
        {
            var message = await NextAsync(timeoutMs);
            if (message.GetProperty("type").GetString() == type)
            {
                return message;
            }
        }
    }

    public async Task<bool> NothingWithinAsync(int ms)
    {
        await Task.Delay(ms);
        return !_messages.Reader.TryPeek(out _);
    }

    public async Task CloseAsync()
    {
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await _reader.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                var text = Encoding.UTF8.GetString(message.ToArray());
                try
                {
                    _messages.Writer.TryWrite(JsonDocument.Parse(text).RootElement.Clone());
                }
                catch (JsonException)
                {
                    lock (Invalid)
                    {
                        Invalid.Add(text);
                    }
                }
            }
        }
        catch (Exception) when (_socket.State != WebSocketState.Open)
        {
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await CloseAsync();
            }
            catch (Exception)
            {
            }
        }
        _socket.Dispose();
    }
}
