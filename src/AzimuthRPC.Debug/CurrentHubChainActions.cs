namespace AzimuthRPC;

public partial class MainWindow
{
    private void CurrentHubChainDump_Click(object sender, System.Windows.RoutedEventArgs e) => BeginCurrentHubChainDump();

    private void BeginCurrentHubChainDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false })
            return;
        _dumpTask = CaptureCurrentHubChainAsync(_runtime.Current);
    }

    private async Task CaptureCurrentHubChainAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Resolving live current-hub object chain…";
            string path = await CurrentHubChainResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"Current hub chain: {path}";
        }
        catch (OperationCanceledException)
        {
            MessageText.Text = "Current-hub chain probe cancelled";
        }
        catch (Exception e)
        {
            MessageText.Text = $"F12 current-hub chain: {e.Message}";
        }
    }
}
