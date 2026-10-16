using System.Reflection;

namespace AzimuthRPC;

public static class VersionInfo
{
    public static string Version { get; } = typeof(VersionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
