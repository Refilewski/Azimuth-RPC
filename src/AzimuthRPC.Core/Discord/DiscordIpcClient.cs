using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AzimuthRPC;

internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int HandshakeOpcode = 0;
    private const int FrameOpcode = 1;
    private const int CloseOpcode = 2;
    private const int PingOpcode = 3;
    private const int PongOpcode = 4;
    private const string BridgeEnvironmentVariable = "AZIMUTHRPC_DISCORD_TCP";

    private readonly string _applicationId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Stream? _stream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private volatile bool _connected;

    public bool IsConnected => _connected && _stream is not null;
    public string? Username { get; private set; }

    public DiscordIpcClient(string applicationId) => _applicationId = applicationId.Trim();

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeTransportOnlyAsync();

        try
        {
            _stream = await OpenTransportAsync(cancellationToken);
            if (_stream is null) return false;

            await SendJsonFrameAsync(HandshakeOpcode, new { v = 1, client_id = _applicationId }, cancellationToken);

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
            while (!handshakeCts.IsCancellationRequested)
            {
                IpcFrame frame = await ReadFrameAsync(_stream, handshakeCts.Token);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeTransportOnlyAsync();
            return false;
        }
        catch (IOException)
        {
            await DisposeTransportOnlyAsync();
            return false;
        }
        catch
        {
            await DisposeTransportOnlyAsync();
            throw;
        }

        await DisposeTransportOnlyAsync();
        return false;
    }

    private static async Task<Stream?> OpenTransportAsync(CancellationToken cancellationToken)
    {
        string? bridge = Environment.GetEnvironmentVariable(BridgeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(bridge))
            return await OpenTcpBridgeAsync(bridge.Trim(), cancellationToken);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return await OpenUnixSocketAsync(cancellationToken);

        return await OpenWindowsPipeAsync(cancellationToken);
    }

    private static async Task<Stream?> OpenTcpBridgeAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!TryParseLoopbackEndpoint(endpoint, out IPAddress? address, out int port) || address is null)
            throw new InvalidOperationException($"{BridgeEnvironmentVariable} must be a loopback endpoint such as 127.0.0.1:64630.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool TryParseLoopbackEndpoint(string value, out IPAddress? address, out int port)
    {
        address = null;
        port = 0;
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535)
            return false;

        string host = value[..separator].Trim('[', ']', ' ');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            address = IPAddress.Loopback;
        else if (!IPAddress.TryParse(host, out address) || !IPAddress.IsLoopback(address))
            return false;

        return true;
    }

    private static async Task<Stream?> OpenWindowsPipeAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
                await pipe.ConnectAsync(timeout.Token);
                return pipe;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
                pipe.Dispose();
            }
        }
        return null;
    }

    private static async Task<Stream?> OpenUnixSocketAsync(CancellationToken cancellationToken)
    {
        foreach (string path in DiscordUnixSocketPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }
        return null;
    }

    private static IEnumerable<string> DiscordUnixSocketPaths()
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (string variable in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
        }
        roots.Add("/tmp");

        foreach (string root in roots)
            for (int i = 0; i < 10; i++)
                yield return Path.Combine(root, $"discord-ipc-{i}");
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
        Stream? stream = _stream;
        if (stream is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IpcFrame frame = await ReadFrameAsync(stream, cancellationToken);
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
        Stream stream = _stream ?? throw new InvalidOperationException("Discord IPC is not connected.");
        byte[] frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
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
            if (read == 0) throw new EndOfStreamException("Discord IPC transport was closed.");
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
        await DisposeTransportOnlyAsync();
        _sendLock.Dispose();
    }

    private async Task DisposeTransportOnlyAsync()
    {
        _connected = false;
        try { _receiveCts?.Cancel(); } catch { }
        if (_stream is not null)
        {
            try { await _stream.DisposeAsync(); } catch { }
            _stream = null;
        }
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch (OperationCanceledException) { } catch { }
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
