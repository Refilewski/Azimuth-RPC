namespace AzimuthRPC;

public class PresenceOptions
{
    public bool UseLiveCarInPresence { get; set; } = true;
    public bool UseLiveSpeedInPresence { get; set; } = true;
    public string ApplicationId { get; set; } = "1559866038085226536";
    public string Details { get; set; } = "Exploring Southern Europe";
    public string State { get; set; } = "Forza Horizon 2 via Azimuth";
    public string LargeImageKey { get; set; } = "fh2";
    public string LargeImageText { get; set; } = "Forza Horizon 2";
    public string SmallImageKey { get; set; } = "azimuth";
    public string SmallImageText { get; set; } = "Azimuth Launcher";
}
