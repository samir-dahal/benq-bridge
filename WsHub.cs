using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BenqBridge.Ddc;

namespace BenqBridge;

/// <summary>Minimal WebSocket fan-out: every connected UI gets the full monitor state on change.</summary>
public sealed class WsHub
{
    private sealed class Client
    {
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly MonitorRegistry _registry;

    public WsHub(MonitorRegistry registry) => _registry = registry;

    public int Count => _clients.Count;

    public async Task RunAsync(WebSocket socket, JsonSerializerOptions json)
    {
        var id = Guid.NewGuid();
        var client = new Client { Socket = socket };
        _clients[id] = client;
        _registry.ActiveClients = true;

        try
        {
            var snapshot = JsonSerializer.Serialize(new { type = "state", monitors = _registry.Snapshot() }, json);
            await SendAsync(client, snapshot);

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // client vanished; drop it below
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _registry.ActiveClients = _clients.Count > 0;
        }
    }

    public async Task BroadcastAsync(string payload)
    {
        foreach (var entry in _clients)
        {
            if (entry.Value.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(entry.Key, out _);
                continue;
            }

            try
            {
                await SendAsync(entry.Value, payload);
            }
            catch
            {
                _clients.TryRemove(entry.Key, out _);
            }
        }

        _registry.ActiveClients = _clients.Count > 0;
    }

    private static async Task SendAsync(Client client, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await client.Gate.WaitAsync();
        try
        {
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            client.Gate.Release();
        }
    }
}
