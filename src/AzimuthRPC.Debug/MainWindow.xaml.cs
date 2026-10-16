using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AzimuthRPC;

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private DebugRuntime? _runtime;
    private Forms.NotifyIcon? _tray;
    private HwndSource? _source;
    private readonly CancellationTokenSource _dumpStop = new();
    private Task? _dumpTask;
    private bool _forced, _closing, _readyToClose;
    private const int CaptureHotkey = 0xA531;
    private const int NativeLocationHotkey = 0xA532;
    private const int LocationXrefHotkey = 0xA533;

    public MainWindow() => InitializeComponent();

    public void InitializeAndRun(string[] args)
    {
        try { _config = ConfigStore.Load(); } catch (Exception e) { MessageText.Text = e.Message; }
        _config.Offline = args.Contains("--offline") || args.Contains("--smoke-test");
        TopmostCheck.IsChecked = _config.AlwaysOnTop; Topmost = _config.AlwaysOnTop;
        ApplicationIdBox.Text = _config.ApplicationId; SpeedCheck.IsChecked = _config.UseLiveSpeedInPresence;
        TrayCheck.IsChecked = _config.CloseToTray; StartupCheck.IsChecked = _config.RunOnStartup; MinimizedCheck.IsChecked = _config.LaunchMinimized;
        _runtime = new(_config); _runtime.Changed += Changed; _runtime.Log += Logged;
        if (!_config.Offline)
        {
            _tray = new() { Text = $"AzimuthRPC {VersionInfo.Version}", Icon = LoadTrayIcon(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open status", null, (_, _) => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(Quit));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => { Show(); Activate(); });
        }
        _ = new WindowInteropHelper(this).EnsureHandle();
        if (_config.LaunchMinimized && !_config.Offline) Hide(); else Show();
        _runtime.Start();
        if (args.Contains("--smoke-test"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); Quit(); }; timer.Start();
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch { }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
        if (!_config.Offline)
        {
            bool f8 = RegisterHotKey(handle, CaptureHotkey, 0x4000, 0x77);
            bool f9 = RegisterHotKey(handle, NativeLocationHotkey, 0x4000, 0x78);
            bool f10 = RegisterHotKey(handle, LocationXrefHotkey, 0x4000, 0x79);
            MessageText.Text = $"F8 full: {(f8 ? "ready" : "unavailable")} · F9 native tracker: {(f9 ? "ready" : "unavailable")} · F10 location XREF: {(f10 ? "ready" : "unavailable")}";
        }
    }

    private IntPtr WindowMessage(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != 0x312)
            return IntPtr.Zero;

        int id = w.ToInt32();
        if (id == CaptureHotkey)
        {
            handled = true;
            BeginDump();
        }
        else if (id == NativeLocationHotkey)
        {
            handled = true;
            BeginNativeLocationDump();
        }
        else if (id == LocationXrefHotkey)
        {
            handled = true;
            BeginLocationXrefDump();
        }
        return IntPtr.Zero;
    }

    private void Changed(DebugStatus status) => Dispatcher.BeginInvoke(() => Render(status));
    private void Logged(string message) => Dispatcher.BeginInvoke(() => MessageText.Text = message);

    private void Render(DebugStatus s)
    {
        var g = s.Game; var v = g.Vehicle; var r = g.Raw;
        StatusText.Text = $"{VersionInfo.Version} · external host · memory {(g.Attached ? "attached" : "unavailable")}";
        GameText.Text = $"{s.Process}\nGameBase: {s.Mapping}\nPlayerManager: 0x{g.Roots.PlayerManager:X}  AudioEngine: 0x{g.Roots.AudioEngine:X}\nCar: {g.Car.DisplayName ?? "?"}  raw: {g.Car.RawId ?? "?"}";
        TelemetryText.Text = $"Speed: {v.SpeedKmh:0.0} km/h / {v.SpeedMph:0.0} mph\nVelocity: {v.Velocity} ({v.VelocityKmh:0.0} km/h)\nXYZ: {v.Position}  mirror={v.PositionMirrorsMatch}\nNormalized rev: {g.Hooks.NormalizedRev:0.000} (not physical RPM)";
        StateText.Text = $"UI: {g.UiMode}  Activity: {g.Activity}  World: {g.WorldPresence}\nLocation: {g.Location ?? "Unknown — Exploring Southern Europe fallback"}";
        HooksText.Text = $"{g.Hooks.Status}\nInstalled={g.Hooks.Installed} Active={g.Hooks.Active} Event hook={g.Hooks.EventHookInstalled}";
        DiscordText.Text = $"{s.Discord}\n{s.Presence.Details}\n{s.Presence.State}";
        RawText.Text = $"ctx={g.Hooks.EventContext?.ToString() ?? "unknown"}  flow={r.FlowPrimary}/{r.FlowSecondary} valid={r.FlowValid}\nfrontend={r.FrontendHubFamily} valid={r.FrontendValid}  load={r.LoadA}/{r.LoadB} valid={r.GameManagerValid}\nshift={g.Hooks.Shift} backfire={g.Hooks.Backfire} gearRpm={g.Hooks.GearRpm}\n{g.Error}";
        if (_tray is not null)
            _tray.Text = g.Attached ? $"AzimuthRPC {VersionInfo.Version} — FH2 connected" : "AzimuthRPC — Waiting for FH2";
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
        var next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new();
        next.AlwaysOnTop = Topmost;
        try { ConfigStore.Save(next); _config = next; _runtime?.UpdateConfig(next); } catch (Exception ex) { MessageText.Text = ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new();
        next.ApplicationId = ApplicationIdBox.Text.Trim(); next.UseLiveSpeedInPresence = SpeedCheck.IsChecked == true;
        next.CloseToTray = TrayCheck.IsChecked == true; next.RunOnStartup = StartupCheck.IsChecked == true; next.LaunchMinimized = MinimizedCheck.IsChecked == true;
        try { ConfigStore.Save(next); if (!_config.Offline) StartupManager.Apply(next.RunOnStartup); _config = next; _runtime?.UpdateConfig(next); MessageText.Text = "Settings saved"; } catch (Exception ex) { MessageText.Text = ex.Message; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is not null)
        {
            System.Windows.Clipboard.SetText(JsonSerializer.Serialize(_runtime.Current, MemoryGraphDumper.Json));
            MessageText.Text = "Debug snapshot copied";
        }
    }

    private void Dump_Click(object sender, RoutedEventArgs e) => BeginDump();
    private void NativeLocationDump_Click(object sender, RoutedEventArgs e) => BeginNativeLocationDump();

    private void BeginDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false }) return;
        _dumpTask = CaptureDumpAsync(_runtime.Current);
    }

    private void BeginNativeLocationDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false }) return;
        _dumpTask = CaptureNativeLocationAsync(_runtime.Current);
    }

    private void BeginLocationXrefDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false }) return;
        _dumpTask = CaptureLocationXrefsAsync(_runtime.Current);
    }

    private async Task CaptureDumpAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Capturing graph on a separate reader…";
            string graphPath = await MemoryGraphDumper.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = "Capturing MapElementGeneric objects…";
            string mapPath = await MapResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = "Probing experimental native location tracker…";
            string nativeLocationPath = await NativeLocationResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = "Scanning location strings and code XREFs…";
            string xrefPath = await LocationStringXrefScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"Graph: {graphPath}\nMap: {mapPath}\nNative location: {nativeLocationPath}\nLocation XREFs: {xrefPath}";
        }
        catch (OperationCanceledException) { MessageText.Text = "Dump cancelled"; }
        catch (Exception e) { MessageText.Text = $"F8 dump: {e.Message}"; }
    }

    private async Task CaptureNativeLocationAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Probing experimental native location tracker…";
            string path = await NativeLocationResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"Native location: {path}";
        }
        catch (OperationCanceledException) { MessageText.Text = "Native-location dump cancelled"; }
        catch (Exception e) { MessageText.Text = $"F9 native location: {e.Message}"; }
    }

    private void Quit() { _forced = true; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Quit();

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_readyToClose) { base.OnClosing(e); return; }
        e.Cancel = true; if (_closing) return;
        if (!_forced && _config.CloseToTray && !_config.Offline) { Hide(); return; }
        _closing = true; _dumpStop.Cancel();
        if (_source is not null)
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, CaptureHotkey);
            UnregisterHotKey(handle, NativeLocationHotkey);
            UnregisterHotKey(handle, LocationXrefHotkey);
            _source.RemoveHook(WindowMessage);
            _source = null;
        }
        if (_runtime is not null)
        {
            _runtime.Changed -= Changed;
            _runtime.Log -= Logged;
            await _runtime.DisposeAsync();
        }
        if (_dumpTask is not null) await _dumpTask;
        var trayIcon = _tray?.Icon; _tray?.Dispose(); trayIcon?.Dispose(); _dumpStop.Dispose(); _readyToClose = true; Close();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
