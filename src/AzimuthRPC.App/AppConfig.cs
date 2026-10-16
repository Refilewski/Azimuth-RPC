namespace AzimuthRPC;

internal sealed class AppConfig : PresenceOptions
{
    public string ProcessName { get; set; } = "azimuth";
    public bool Offline { get; set; }
}
