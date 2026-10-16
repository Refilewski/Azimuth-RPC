using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AzimuthRPC;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, @"Local\AzimuthRPC", out bool firstInstance);
        if (!firstInstance)
            return;

        using var log = FileLog.Open();
        bool smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        log.Write($"AzimuthRPC {VersionInfo.Version} standalone started{(smokeTest ? " (smoke test)" : string.Empty)}");
        log.Write($"Log file: {log.FilePath}");

        try
        {
            var config = new AppConfig { Offline = smokeTest };
            using var context = new TrayApplicationContext(config, smokeTest, log);
            Application.Run(context);
        }
        catch (Exception e)
        {
            log.Write($"Fatal error: {e}");
            throw;
        }
        finally
        {
            log.Write("AzimuthRPC standalone stopped");
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer? _smokeTimer;
    private readonly DebugRuntime _runtime;
    private readonly FileLog _log;
    private string? _lastLoggedStatus;
    private int _exiting;

    internal TrayApplicationContext(AppConfig config, bool smokeTest, FileLog log)
    {
        _log = log;
        Icon appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;
        _statusItem = new ToolStripMenuItem("Waiting for Azimuth") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"AzimuthRPC {VersionInfo.Version}") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _tray = new NotifyIcon
        {
            Text = $"AzimuthRPC {VersionInfo.Version}",
            Icon = appIcon,
            Visible = !smokeTest,
            ContextMenuStrip = menu
        };

        _runtime = new DebugRuntime(config);
        _runtime.Log += OnRuntimeLog;
        _runtime.Changed += OnRuntimeChanged;
        _runtime.Start();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        if (smokeTest)
        {
            _smokeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _smokeTimer.Tick += async (_, _) =>
            {
                _smokeTimer.Stop();
                await ExitAsync();
            };
            _smokeTimer.Start();
        }
    }

    private void OnRuntimeLog(string message) => _log.Write(message);

    private void OnRuntimeChanged(DebugStatus status)
    {
        string state = string.IsNullOrWhiteSpace(status.Presence.State) ? "<omitted>" : status.Presence.State;
        string next = $"Process={status.Process}; Mapping={status.Mapping}; Discord={status.Discord}; Details=\"{status.Presence.Details}\"; State=\"{state}\"";
        if (string.Equals(next, _lastLoggedStatus, StringComparison.Ordinal))
            return;

        _lastLoggedStatus = next;
        _log.Write(next);
    }

    private void RefreshStatus()
    {
        DebugStatus status = _runtime.Current;
        string text = status.Game.Attached
            ? $"FH2 connected — {status.Presence.Details}"
            : status.Process;

        _statusItem.Text = text;
        _tray.Text = Tooltip(text);
    }

    private static string Tooltip(string value)
    {
        string prefix = "AzimuthRPC — ";
        string text = prefix + value;
        return text.Length <= 63 ? text : text[..63];
    }

    private async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
            return;

        _statusTimer.Stop();
        _smokeTimer?.Stop();
        _tray.Visible = false;
        await _runtime.DisposeAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtime.Log -= OnRuntimeLog;
            _runtime.Changed -= OnRuntimeChanged;
            _statusTimer.Dispose();
            _smokeTimer?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class FileLog : IDisposable
{
    private const long MaxBytes = 2L * 1024L * 1024L;
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    private FileLog(string path, StreamWriter writer)
    {
        FilePath = path;
        _writer = writer;
    }

    internal string FilePath { get; }

    internal static FileLog Open()
    {
        string executable = Environment.ProcessPath ?? string.Empty;
        string directory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
        string preferred = Path.Combine(directory, "AzimuthRPC.log");

        try
        {
            return Create(preferred);
        }
        catch (Exception)
        {
            string fallbackDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AzimuthRPC");
            Directory.CreateDirectory(fallbackDirectory);
            return Create(Path.Combine(fallbackDirectory, "AzimuthRPC.log"));
        }
    }

    private static FileLog Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);

        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            File.WriteAllText(path, string.Empty);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        return new FileLog(path, writer);
    }

    internal void Write(string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
