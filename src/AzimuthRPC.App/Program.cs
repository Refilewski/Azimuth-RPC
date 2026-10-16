using System.Drawing;
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

        bool smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var config = new AppConfig { Offline = smokeTest };
        using var context = new TrayApplicationContext(config, smokeTest);
        Application.Run(context);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer? _smokeTimer;
    private readonly DebugRuntime _runtime;
    private int _exiting;

    internal TrayApplicationContext(AppConfig config, bool smokeTest)
    {
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
            _statusTimer.Dispose();
            _smokeTimer?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
