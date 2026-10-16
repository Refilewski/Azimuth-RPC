using System.IO;
namespace AzimuthRPC;

internal static class GamePathResolver
{
    internal static string? ResolveGameExePath(System.Diagnostics.Process process, string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string configured = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
            if (File.Exists(configured))
                return configured;
        }

        string? azimuthPath = null;
        try { azimuthPath = process.MainModule?.FileName; } catch { }
        if (string.IsNullOrWhiteSpace(azimuthPath))
            return null;

        string azimuthDir = Path.GetDirectoryName(azimuthPath)!;
        string iniPath = Path.Combine(azimuthDir, "data", "azimuth.ini");
        if (!File.Exists(iniPath))
            return null;

        string? gameFolder = ReadIniValue(iniPath, "launcher", "game_folder");
        if (string.IsNullOrWhiteSpace(gameFolder))
            return null;

        gameFolder = Environment.ExpandEnvironmentVariables(gameFolder.Trim().Trim('"'));
        if (!Path.IsPathRooted(gameFolder))
            gameFolder = Path.GetFullPath(Path.Combine(azimuthDir, gameFolder));

        string[] candidates =
        {
            Path.Combine(gameFolder, "Mount", "forza_durango_release_final.exe"),
            Path.Combine(gameFolder, "forza_durango_release_final.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ReadIniValue(string path, string wantedSection, string wantedKey)
    {
        string section = string.Empty;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (!section.Equals(wantedSection, StringComparison.OrdinalIgnoreCase))
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq].Trim();
            if (key.Equals(wantedKey, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }

        return null;
    }

}
