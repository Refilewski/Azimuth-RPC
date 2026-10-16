using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AzimuthRPC;

public partial class MainWindow
{
    private const int RoadTripLookupHotkey = 0xA534;
    private const int CurrentHubChainHotkey = 0xA535;
    private const int TriggerZoneHotkey = 0xA536;
    private HwndSource? _roadTripHotkeySource;
    private IntPtr _roadTripHotkeyHandle;

    private void RoadTripLookupDump_Click(object sender, System.Windows.RoutedEventArgs e) => BeginRoadTripLookupDump();

    private void RoadTripHotkey_SourceInitialized(object? sender, EventArgs e)
    {
        if (_config.Offline)
            return;

        _roadTripHotkeyHandle = new WindowInteropHelper(this).Handle;
        _roadTripHotkeySource = HwndSource.FromHwnd(_roadTripHotkeyHandle);
        _roadTripHotkeySource?.AddHook(RoadTripHotkeyWindowMessage);
        bool f11 = RegisterHotKeyRoadTrip(_roadTripHotkeyHandle, RoadTripLookupHotkey, 0x4000, 0x7A);
        bool f12 = RegisterHotKeyRoadTrip(_roadTripHotkeyHandle, CurrentHubChainHotkey, 0x4000, 0x7B);
        bool f13 = RegisterHotKeyRoadTrip(_roadTripHotkeyHandle, TriggerZoneHotkey, 0x4000, 0x7C);
        Dispatcher.BeginInvoke(() => MessageText.Text += $" · F11 RoadTrip lookup: {(f11 ? "ready" : "unavailable")} · F12 current hub chain: {(f12 ? "ready" : "unavailable")} · F13 trigger zones: {(f13 ? "ready" : "unavailable")}");
    }

    private IntPtr RoadTripHotkeyWindowMessage(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != 0x312)
            return IntPtr.Zero;

        int id = w.ToInt32();
        if (id == RoadTripLookupHotkey)
        {
            handled = true;
            BeginRoadTripLookupDump();
        }
        else if (id == CurrentHubChainHotkey)
        {
            handled = true;
            BeginCurrentHubChainDump();
        }
        else if (id == TriggerZoneHotkey)
        {
            handled = true;
            BeginTriggerZoneDump();
        }
        return IntPtr.Zero;
    }

    private void RoadTripHotkey_Closed(object? sender, EventArgs e)
    {
        if (_roadTripHotkeyHandle != IntPtr.Zero)
        {
            UnregisterHotKeyRoadTrip(_roadTripHotkeyHandle, RoadTripLookupHotkey);
            UnregisterHotKeyRoadTrip(_roadTripHotkeyHandle, CurrentHubChainHotkey);
            UnregisterHotKeyRoadTrip(_roadTripHotkeyHandle, TriggerZoneHotkey);
        }
        _roadTripHotkeySource?.RemoveHook(RoadTripHotkeyWindowMessage);
        _roadTripHotkeySource = null;
        _roadTripHotkeyHandle = IntPtr.Zero;
    }

    private void BeginRoadTripLookupDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false })
            return;
        _dumpTask = CaptureRoadTripLookupAsync(_runtime.Current);
    }

    private async Task CaptureRoadTripLookupAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Probing RoadTripLocations runtime lookup / live objects…";
            string path = await RoadTripLookupResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"RoadTrip lookup: {path}";
        }
        catch (OperationCanceledException)
        {
            MessageText.Text = "RoadTrip lookup probe cancelled";
        }
        catch (Exception e)
        {
            MessageText.Text = $"F11 RoadTrip lookup: {e.Message}";
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")]
    private static extern bool RegisterHotKeyRoadTrip(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    private static extern bool UnregisterHotKeyRoadTrip(IntPtr hwnd, int id);
}
