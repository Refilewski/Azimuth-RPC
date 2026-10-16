using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace AzimuthRPC;

internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int HandshakeOpcode = 0;
    private const int FrameOpcode = 1;
    private const int CloseOpcode = 2;
    private const int PingOpcode = 3;
    private const int PongOpcode = 4;

    private readonly string _applicationId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private volatile bool _connected;

    public bool IsConnected => _connected && _pipe is { IsConnected: true };
    public string? Username { get; private set; }

    public DiscordIpcClient(string applicationId) => _applicationId = applicationId.Trim();

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposePipeOnlyAsync();

        for (int i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(350));
                await pipe.ConnectAsync(connectCts.Token);
                _pipe = pipe;

                await SendJsonFrameAsync(HandshakeOpcode, new { v = 1, client_id = _applicationId }, cancellationToken);

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                while (!handshakeCts.IsCancellationRequested)
                {
                    IpcFrame frame = await ReadFrameAsync(pipe, handshakeCts.Token);
                    if (frame.Opcode == PingOpcode)
                    {
                        await SendRawFrameAsync(PongOpcode, frame.Payload, handshakeCts.Token);
                        continue;
                    }
                    if (frame.Opcode == CloseOpcode)
                        throw new IOException("Discord closed the IPC connection during handshake.");
                    if (frame.Opcode != FrameOpcode) continue;

                    using JsonDocument doc = JsonDocument.Parse(frame.Payload);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("evt", out JsonElement evt) &&
                        string.Equals(evt.GetString(), "READY", StringComparison.OrdinalIgnoreCase))
                    {
                        Username = TryGetUsername(root);
                        _connected = true;
                        StartReceiver();
                        return true;
                    }
                    if (root.TryGetProperty("evt", out evt) &&
                        string.Equals(evt.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(TryGetErrorMessage(root) ?? "Discord returned an IPC error.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch
            {
                pipe.Dispose();
                _pipe = null;
                throw;
            }

            pipe.Dispose();
            _pipe = null;
        }
        return false;
    }

    public async Task SetActivityAsync(PresenceOptions config, DateTime sessionStartUtc, string details, string state, CancellationToken cancellationToken)
    {
        EnsureConnected();

        var activity = new Dictionary<string, object?>
        {
            ["type"] = 0,
            ["details"] = string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
            ["state"] = string.IsNullOrWhiteSpace(state) ? null : state.Trim(),
            ["timestamps"] = new Dictionary<string, long>
            {
                ["start"] = new DateTimeOffset(sessionStartUtc).ToUnixTimeSeconds()
            }
        };

        var assets = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config.LargeImageKey))
        {
            assets["large_image"] = config.LargeImageKey.Trim();
            AddIfNotEmpty(assets, "large_text", config.LargeImageText);
        }
        if (!string.IsNullOrWhiteSpace(config.SmallImageKey))
        {
            assets["small_image"] = config.SmallImageKey.Trim();
            AddIfNotEmpty(assets, "small_text", config.SmallImageText);
        }
        if (assets.Count > 0) activity["assets"] = assets;
        if (activity["details"] is null) activity.Remove("details");
        if (activity["state"] is null) activity.Remove("state");

        await SendCommandAsync(activity, cancellationToken);
    }

    public async Task ClearActivityAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) await SendCommandAsync(null, cancellationToken);
    }

    private async Task SendCommandAsync(object? activity, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await SendJsonFrameAsync(FrameOpcode, new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString()
        }, cancellationToken);
    }

    private void StartReceiver()
    {
        var receiveCts = new CancellationTokenSource();
        _receiveCts = receiveCts;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(receiveCts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream? pipe = _pipe;
        if (pipe is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                IpcFrame frame = await ReadFrameAsync(pipe, cancellationToken);
                if (frame.Opcode == PingOpcode)
                    await SendRawFrameAsync(PongOpcode, frame.Payload, cancellationToken);
                else if (frame.Opcode == CloseOpcode)
                {
                    _connected = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
        finally { _connected = false; }
    }

    private async Task SendJsonFrameAsync(int opcode, object payload, CancellationToken cancellationToken)
        => await SendRawFrameAsync(opcode, JsonSerializer.SerializeToUtf8Bytes(payload), cancellationToken);

    private async Task SendRawFrameAsync(int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = _pipe ?? throw new InvalidOperationException("Discord IPC is not connected.");
        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(frame, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally { _sendLock.Release(); }
    }

    private static async Task<IpcFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellationToken);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length < 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException($"Invalid Discord IPC payload length: {length}");
        byte[] payload = new byte[length];
        if (length > 0) await ReadExactlyAsync(stream, payload, cancellationToken);
        return new IpcFrame(opcode, payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException("Discord IPC pipe was closed.");
            offset += read;
        }
    }

    private static string? TryGetUsername(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data) &&
            data.TryGetProperty("user", out JsonElement user) &&
            user.TryGetProperty("username", out JsonElement username))
            return username.GetString();
        return null;
    }

    private static string? TryGetErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data))
        {
            if (data.TryGetProperty("message", out JsonElement message)) return message.GetString();
            if (data.TryGetProperty("code", out JsonElement code)) return $"Discord IPC error code {code}";
        }
        return null;
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Discord IPC is not connected.");
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        try { _receiveCts?.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch { }
        }
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _pipe = null;

    }

    private async Task DisposePipeOnlyAsync()
    {
        _connected = false;
        _receiveCts?.Cancel();
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch (OperationCanceledException) { }
        }
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
    }

    private static void AddIfNotEmpty(Dictionary<string, string> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value.Trim();
    }

    private readonly record struct IpcFrame(int Opcode, byte[] Payload);
}
