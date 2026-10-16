namespace AzimuthRPC;

public partial class MainWindow
{
    private void LocationXrefDump_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false })
            return;
        _dumpTask = CaptureLocationXrefsAsync(_runtime.Current);
    }

    private async Task CaptureLocationXrefsAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Scanning FH2 location strings and code XREFs…";
            string path = await LocationStringXrefScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"Location XREFs: {path}";
        }
        catch (OperationCanceledException)
        {
            MessageText.Text = "Location-XREF scan cancelled";
        }
        catch (Exception e)
        {
            MessageText.Text = $"Location XREF scan: {e.Message}";
        }
    }
}
