using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

internal static class MemoryGraphDumper
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) => Task.Run(() => Capture(status, token), token);
    private static string Capture(DebugStatus status, CancellationToken token)
    {
        var game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid) throw new InvalidOperationException("Attach FH2 before taking a dump");
        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc) throw new InvalidOperationException("Process changed before dump capture");
        using var reader = new ExternalProcessMemoryReader(pid);
        bool RootsMatch() => reader.PointerAt(game.GameBase + UiModeReader.AudioEngineRva) == game.Roots.AudioEngine &&
            reader.PointerAt(game.GameBase + UiModeReader.PlayerManagerRva) == game.Roots.PlayerManager &&
            reader.PointerAt(game.Roots.AudioEngine + UiModeReader.RegistryPointerOffset) == game.Roots.Registry;
        if (!RootsMatch()) throw new InvalidOperationException("Roots changed; retry dump");
        var queue = new Queue<(ulong Address, string Source, int Depth)>();
        queue.Enqueue((game.Roots.PlayerManager, "PlayerManager", 0)); queue.Enqueue((game.Roots.AudioEngine, "AudioEngine", 0));
        // Capture known state-chain nodes before the bounded generic graph can
        // fill with unrelated pointers. This does not infer any new offset.
        if (MemoryRead.Pointer(game.Roots.Registry)) queue.Enqueue((game.Roots.Registry, "AudioEngine+0x630", 1));
        foreach (ulong offset in new ulong[] { UiModeReader.GameManagerPointerOffset, UiModeReader.SimulationManagerPointerOffset })
        {
            ulong target = reader.PointerAt(game.Roots.PlayerManager + offset);
            if (MemoryRead.Pointer(target)) queue.Enqueue((target, $"PlayerManager+0x{offset:X}", 1));
        }
        var seen = new HashSet<ulong>(); var blocks = new List<object>();
        while (queue.Count > 0 && blocks.Count < 128)
        {
            token.ThrowIfCancellationRequested(); var item = queue.Dequeue(); if (!seen.Add(item.Address)) continue;
            var bytes = reader.Bytes(item.Address, 4096); if (bytes is null) continue;
            var pointers = new List<object>();
            if (item.Depth < 2)
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    ulong target = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i, 8));
                    if (!MemoryRead.Pointer(target) || target >= game.GameBase && target < game.GameBase + 0x3A18000 || target >= item.Address && target < item.Address + (ulong)bytes.Length) continue;
                    if (reader.Bytes(target, 16) is null) continue;
                    pointers.Add(new { Offset = $"0x{i:X}", Target = $"0x{target:X}" });
                    if (!seen.Contains(target) && queue.Count + blocks.Count < 256) queue.Enqueue((target, $"{item.Source}+0x{i:X}", item.Depth + 1));
                }
            blocks.Add(new { Address = $"0x{item.Address:X}", item.Source, item.Depth, Size = bytes.Length, Hex = Convert.ToHexString(bytes), AsciiStrings = Strings(bytes), Pointers = pointers });
        }
        bool stable = RootsMatch();
        var dump = new { CapturedUtc = DateTimeOffset.UtcNow, Version = VersionInfo.Version, game.Build, GameBase = $"0x{game.GameBase:X}", GameImageSize = "0x3A18000", RawCarId = game.Car.RawId, CarDisplayName = game.Car.DisplayName, game.Vehicle.SpeedMph, game.Vehicle.SpeedKmh, game.Vehicle.Position, game.Vehicle.PositionMirrorsMatch, game.Vehicle.Velocity, game.Hooks.NormalizedRev, UiMode = game.UiMode.ToString(), Activity = game.Activity.ToString(), WorldPresence = game.WorldPresence.ToString(), SnapshotCapturedUtc = game.CapturedUtc, Raw = game.Raw, game.Hooks, RootsStableDuringDump = stable, Roots = new[] { new { Name = "PlayerManager", Address = $"0x{game.Roots.PlayerManager:X}" }, new { Name = "AudioEngine", Address = $"0x{game.Roots.AudioEngine:X}" } }, Blocks = blocks };
        string directory = Path.Combine(AppContext.BaseDirectory, "dumps"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-live-dump-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, Json)); return path;
    }
    private static IEnumerable<string> Strings(byte[] raw)
    {
        int start = -1, count = 0;
        for (int i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i] >= 32 && raw[i] <= 126) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 5) { yield return $"0x{start:X}: {Encoding.ASCII.GetString(raw, start, i - start)}"; if (++count == 32) yield break; }
            start = -1;
        }
    }
}
