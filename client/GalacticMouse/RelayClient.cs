using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GalacticMouse;

internal sealed class RelayClient : IDisposable
{
    private readonly Uri _uri;
    private readonly byte[] _key;
    private readonly string _room;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte> _seenNonces = new();
    private static readonly object LogLock = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalacticMouse", "client.log");
    private Task? _task;
    private volatile bool _peerPresent;

    public event Action<bool>? PresenceChanged;
    public event Action<string>? StatusChanged;
    public event Action<JsonElement>? MessageReceived;
    public bool PeerPresent => _peerPresent;

    public RelayClient(Uri uri, byte[] secret)
    {
        _uri = uri;
        _room = Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant();
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("GalacticMouse v1 key:").Concat(secret).ToArray());
    }

    public void Start() => _task = Task.Run(RunAsync);

    public void Send(object message)
    {
        if (!_peerPresent || _stop.IsCancellationRequested) return;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var frame = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(frame, 0);
        ciphertext.CopyTo(frame, nonce.Length);
        tag.CopyTo(frame, nonce.Length + ciphertext.Length);
        _outgoing.Writer.TryWrite(JsonSerializer.Serialize(new { type = "data", payload = Convert.ToBase64String(frame) }));
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            string? disconnectReason = null;
            try
            {
                Report("Connecting…");
                await ws.ConnectAsync(_uri, _stop.Token);
                _seenNonces.Clear();
                await SendText(ws, JsonSerializer.Serialize(new { type = "join", room = _room }), _stop.Token);
                Report("WebSocket connected; waiting for relay response");
                using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var send = SendLoop(ws, session.Token);
                var receive = ReceiveLoop(ws, session.Token);
                var completed = await Task.WhenAny(send, receive);
                try { await completed; }
                catch (Exception ex) when (!_stop.IsCancellationRequested) { disconnectReason = ex.Message; }
                session.Cancel();
                try { await Task.WhenAll(send, receive); } catch { /* reconnect */ }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { disconnectReason = ex.Message; }
            _peerPresent = false;
            PresenceChanged?.Invoke(false);
            while (_outgoing.Reader.TryRead(out _)) { }
            if (!_stop.IsCancellationRequested)
            {
                Report($"Disconnected: {disconnectReason ?? "connection ended"}. Retrying in 5 s");
                try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(ct)) await SendText(ws, frame, ct);
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Relay closed ({(int?)result.CloseStatus}): {result.CloseStatusDescription}");
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                throw new InvalidOperationException("Unexpected WebSocket frame");
            using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "presence")
            {
                var present = root.GetProperty("peers").GetInt32() == 1;
                _peerPresent = present;
                PresenceChanged?.Invoke(present);
                Report(present ? "Peer connected" : "Waiting for peer");
            }
            else if (type == "data")
            {
                var frame = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
                if (frame.Length < 29) continue;
                var nonce = frame.AsSpan(0, 12);
                if (!_seenNonces.TryAdd(Convert.ToHexString(nonce), 0)) continue;
                if (_seenNonces.Count > 100000) _seenNonces.Clear();
                var plaintext = new byte[frame.Length - 28];
                try
                {
                    using var aes = new AesGcm(_key, 16);
                    aes.Decrypt(nonce, frame.AsSpan(12, plaintext.Length), frame.AsSpan(frame.Length - 16), plaintext);
                    using var inner = JsonDocument.Parse(plaintext);
                    MessageReceived?.Invoke(inner.RootElement.Clone());
                }
                catch (CryptographicException) { Report("Pairing code mismatch or invalid input frame"); }
            }
        }
    }

    private static Task SendText(ClientWebSocket ws, string value, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true, ct);

    private void Report(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never interrupt input */ }
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        _stop.Dispose();
    }
}
