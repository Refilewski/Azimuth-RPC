namespace AzimuthRPC;

internal sealed record TelemetryHookInstallResult(bool Success, string Status, bool RequiresAdmin)
{
    public static TelemetryHookInstallResult Ok(string status) => new(true, status, false);
    public static TelemetryHookInstallResult Fail(string status, bool requiresAdmin = false) => new(false, status, requiresAdmin);
}

internal sealed record Fh2TelemetrySample(
    bool Available,
    bool Active,
    float? RpmNormalized,
    float? GearRpm,
    bool Shift,
    bool Backfire,
    int? EventContext,
    bool EventHookInstalled,
    string Status)
{
    public static Fh2TelemetrySample Unavailable(string status) => new(false, false, null, null, false, false, null, false, status);
}

internal interface ITelemetrySource : IDisposable
{
    bool Installed { get; }
    TelemetryHookInstallResult TryInstall();
    Fh2TelemetrySample Read();
}
