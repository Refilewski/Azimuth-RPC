namespace AzimuthRPC;

public partial class MainWindow
{
    private void TriggerZoneDump_Click(object sender, System.Windows.RoutedEventArgs e) => BeginTriggerZoneDump();

    private void BeginTriggerZoneDump()
    {
        if (_runtime is null || _dumpTask is { IsCompleted: false })
            return;
        _dumpTask = CaptureTriggerZonesAsync(_runtime.Current);
    }

    private async Task CaptureTriggerZonesAsync(DebugStatus status)
    {
        try
        {
            MessageText.Text = "Decoding live TriggerZone geometry / triggerable RTTI…";
            string path = await TriggerZoneGeometryResearchScanner.CaptureAsync(status, _dumpStop.Token);
            MessageText.Text = $"Trigger geometry: {path}";
        }
        catch (OperationCanceledException)
        {
            MessageText.Text = "Trigger-zone geometry probe cancelled";
        }
        catch (Exception e)
        {
            MessageText.Text = $"F13 trigger geometry: {e.Message}";
        }
    }
}
