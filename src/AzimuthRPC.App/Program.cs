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

        bool smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        bool keepLogs = args.Contains("--keep-logs", StringComparer.OrdinalIgnoreCase);
        if (smokeTest) SupportLogSender.AssertSanitizationForSmokeTest();
        using var mutex = new Mutex(true, @"Local\AzimuthRPC", out bool firstInstance);
        if (!firstInstance)
        {
            if (!smokeTest)
                MessageBox.Show("AzimuthRPC is already running. Check the Windows system tray for its icon and log-folder menu.",
                    "AzimuthRPC already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        FileLog log;
        try
        {
            log = FileLog.Open();
        }
        catch (Exception e)
        {
            if (!smokeTest)
                MessageBox.Show($"AzimuthRPC could not create a startup log in AppData or TEMP, so it cannot start.\n\n{e}",
                    "AzimuthRPC startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            throw;
        }

        using (log)
        {
            log.Write($"AzimuthRPC {VersionInfo.Version} standalone started{(smokeTest ? " (smoke test)" : string.Empty)}");
            log.Write($"Log file: {log.FilePath}");

            // Regression: open a read handle while the logger still owns its
            // write handle. This must work without closing or pausing logging.
            if (smokeTest && !log.ReadSupportLines().Any(line =>
                    line.Contains("standalone started (smoke test)", StringComparison.Ordinal)))
                throw new IOException("Live log snapshot read failed during smoke test.");

            if (log.FallbackReason is string fallbackReason)
            {
                log.Write($"Primary log unavailable: {fallbackReason}");
                if (!smokeTest)
                    MessageBox.Show($"The standard log folder was unavailable. AzimuthRPC is logging to:\n{log.FilePath}\n\nUse the tray menu 'Open log folder' to find it.",
                        "AzimuthRPC log fallback", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            bool exitedNormally = false;
            try
            {
                var config = new AppConfig { Offline = smokeTest };
                using var context = new TrayApplicationContext(config, smokeTest, log);
                Application.Run(context);
                exitedNormally = true;
            }
            catch (Exception e)
            {
                log.Write($"Fatal error: {e}");
                if (!smokeTest)
                    MessageBox.Show($"AzimuthRPC encountered a startup error:\n\n{e.Message}\n\nLog: {log.FilePath}",
                        "AzimuthRPC error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
            finally
            {
                log.Write("AzimuthRPC standalone stopped");
                // Leave diagnostics behind if startup fails or the process crashes.
                // Only clean files after the normal tray/smoke-test shutdown.
                log.DeleteOnDispose = exitedNormally && !keepLogs;
            }
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
    private readonly ToolStripMenuItem _sendLogItem;
    private string? _lastLoggedStatus;
    private string? _lastDiagnosticContext;
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
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        _sendLogItem = new ToolStripMenuItem("Send logs to Discord...", null, async (_, _) => await SendLogsAsync());
        menu.Items.Add(_sendLogItem);
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
        // Status logs show only what Discord displays. A missing car could mean
        // no valid game ID, a hidden presence option, or stale race context.
        // Log the actual classification on change without dumping process memory.
        string diagnostics = status.Game.Attached
            ? $"PID={status.ProcessId}; UiMode={status.Game.UiMode}; Activity={status.Game.Activity}; " +
              $"HooksInstalled={status.Game.Hooks.Installed}; TelemetryActive={status.Game.Hooks.Active}; " +
              $"EventContext={status.Game.Hooks.EventContext?.ToString() ?? "none"}; " +
              $"CarProbe={status.Game.CarProbeStatus}; CarId={status.Game.Car.RawId ?? "none"}"
            : $"PID={status.ProcessId}; Game not attached; Reason={status.Game.Error ?? "unknown"}";

        if (diagnostics != _lastDiagnosticContext)
        {
            _lastDiagnosticContext = diagnostics;
            _log.Write($"Snapshot diagnostic: {diagnostics}");
        }

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

    private void OpenLogFolder()
    {
        try
        {
            string folder = Path.GetDirectoryName(_log.FilePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception e)
        {
            _log.Write($"Could not open log folder: {e.Message}");
            MessageBox.Show($"Log file: {_log.FilePath}\n\n{e.Message}",
                "AzimuthRPC log location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task SendLogsAsync()
    {
        if (Volatile.Read(ref _exiting) != 0 || !_sendLogItem.Enabled)
            return;

        _sendLogItem.Enabled = false;
        try
        {
            string report = SupportLogSender.Prepare(_log.ReadSupportLines());
            if (string.IsNullOrWhiteSpace(report))
            {
                MessageBox.Show("No diagnostic lines are available to send.", "AzimuthRPC",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!SupportLogSender.ConfirmAndPreview(report))
                return;

            await SupportLogSender.SendAsync(report);
            MessageBox.Show("Diagnostic log sent to Discord successfully.", "AzimuthRPC",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException
                                  or TaskCanceledException or InvalidOperationException)
        {
            MessageBox.Show("Could not send the log. Please try again later.\n\n" + e.Message,
                "AzimuthRPC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (Volatile.Read(ref _exiting) == 0)
                _sendLogItem.Enabled = true;
        }
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
    internal string? FallbackReason { get; private set; }
    internal bool DeleteOnDispose { get; set; }

    internal static FileLog Open()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new IOException("Windows did not return a LocalAppData path.");

            return Create(Path.Combine(localAppData, "AzimuthRPC", "logs", "AzimuthRPC.log"));
        }
        catch (Exception primaryError) when (primaryError is IOException or UnauthorizedAccessException or ArgumentException)
        {
            string fallbackPath = Path.Combine(Path.GetTempPath(), "AzimuthRPC", "AzimuthRPC.log");
            var log = Create(fallbackPath);
            log.FallbackReason = primaryError.GetType().Name + ": " + primaryError.Message;
            return log;
        }
    }

    private static FileLog Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);

        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
        {
            string rotated = path + ".1";
            if (File.Exists(rotated))
                File.Delete(rotated);
            File.Move(path, rotated);
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        return new FileLog(path, writer);
    }

    internal string[] ReadSupportLines()
    {
        lock (_gate)
        {
            _writer.Flush();

            // File.ReadAllLines uses FileShare.Read, which conflicts with our
            // still-open FileAccess.Write handle. Explicitly allow other readers,
            // writers and delete/rotation while taking this snapshot.
            using var stream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
                lines.Add(line);
            return lines.ToArray();
        }
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
            // Close the writer first; Windows otherwise retains the file handle.
            _writer.Dispose();
            if (!DeleteOnDispose)
                return;

            // Delete only our known log files, never user files or the folder.
            // Cleanup is best-effort if antivirus/another process has a file open.
            foreach (string path in new[] { FilePath, FilePath + ".1" })
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
