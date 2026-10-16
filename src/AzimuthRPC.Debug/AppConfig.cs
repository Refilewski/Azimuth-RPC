using System.Text.Json;
using Microsoft.Win32;
using System.IO;
namespace AzimuthRPC;

internal sealed class AppConfig : PresenceOptions
{
    public string GameExePath { get; set; } = "";
    public string ProcessName { get; set; } = "azimuth";
    public bool LaunchMinimized { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = false;
    public bool Offline { get; set; }
}
internal static class ConfigStore
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "rpc-config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        string json = File.ReadAllText(ConfigPath);
        var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? new AppConfig();

        return config;
    }

    public static void Save(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AzimuthRPC";

    public static void Apply(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AzimuthRPC.exe");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
