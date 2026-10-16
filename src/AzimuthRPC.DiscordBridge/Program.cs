using System.Net;
using System.Net.Sockets;

const int DefaultPort = 64630;
int port = ParsePort(args);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("AzimuthRPC.DiscordBridge is intended for Linux/macOS hosts running Discord natively.");
}

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start(8);
Console.WriteLine($"AzimuthRPC Discord bridge listening on 127.0.0.1:{port}");
Console.WriteLine("Waiting for AzimuthRPC/Wine clients. Press Ctrl+C to stop.");

try
{
    while (!stop.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            break;
        }

        _ = Task.Run(() => HandleClientAsync(client, stop.Token));
    }
}
finally
{
    listener.Stop();
}

static int ParsePort(string[] args)
{
    string? value = null;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
        {
            value = args[i + 1];
            break;
        }
    }

    value ??= Environment.GetEnvironmentVariable("AZIMUTHRPC_BRIDGE_PORT");
    if (string.IsNullOrWhiteSpace(value)) return DefaultPort;
    if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
        throw new ArgumentOutOfRangeException(nameof(args), "Bridge port must be between 1 and 65535.");
    return port;
}

static async Task HandleClientAsync(TcpClient client, CancellationToken shutdownToken)
{
    using (client)
    {
        client.NoDelay = true;
        Socket? discord = await ConnectDiscordAsync(shutdownToken);
        if (discord is null)
        {
            Console.Error.WriteLine("Discord IPC socket not found. Is the native Discord desktop client running?");
            return;
        }

        using var discordStream = new NetworkStream(discord, ownsSocket: true);
        using NetworkStream clientStream = client.GetStream();
        using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);

        Console.WriteLine("Client connected; relaying Discord IPC.");
        Task toDiscord = RelayAsync(clientStream, discordStream, relayStop.Token);
        Task fromDiscord = RelayAsync(discordStream, clientStream, relayStop.Token);

        try
        {
            await Task.WhenAny(toDiscord, fromDiscord);
        }
        finally
        {
            relayStop.Cancel();
            try { await Task.WhenAll(toDiscord, fromDiscord); } catch { }
            Console.WriteLine("Client disconnected.");
        }
    }
}

static async Task<Socket?> ConnectDiscordAsync(CancellationToken cancellationToken)
{
    foreach (string path in DiscordSocketPaths())
    {
        cancellationToken.ThrowIfCancellationRequested();
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
            Console.WriteLine($"Connected to Discord IPC: {path}");
            return socket;
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

static IEnumerable<string> DiscordSocketPaths()
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

static async Task RelayAsync(Stream input, Stream output, CancellationToken cancellationToken)
{
    byte[] buffer = new byte[64 * 1024];
    while (!cancellationToken.IsCancellationRequested)
    {
        int read = await input.ReadAsync(buffer, cancellationToken);
        if (read == 0) return;
        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
