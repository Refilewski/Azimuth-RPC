using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

internal static class MapResearchScanner
{
    // FH2 Xbox One 1.0.0.64. Confirmed by the public FH2 multiplayer research:
    // MapElementGeneric is 0x58 bytes and uses this vtable. The same map is used
    // by the minimap and the pause/world map.
    private const ulong MapElementVtableRva = 0x02F61E50;
    private const ulong MapSetTagRva = 0x01931D90;
    private const ulong MapGetTagRva = 0x0193AC50;
    private const ulong MapSetTransformRva = 0x01942C70;
    private const ulong MapAddRva = 0x01931A80;
    private const int MapElementSize = 0x58;
    private const int ScanChunkSize = 1024 * 1024;
    private const int MaxElements = 4096;
    private const int CodeSampleSize = 0x180;

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a map research dump");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before map research capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        ulong wantedVtable = checked(game.GameBase + MapElementVtableRva);
        ulong imageEnd = checked(game.GameBase + 0x3A18000UL);
        var elements = new List<object>();
        var seen = new HashSet<ulong>();
        byte[] scanBuffer = new byte[ScanChunkSize];

        foreach (MemoryRegion region in reader.EnumerateRegions())
        {
            token.ThrowIfCancellationRequested();
            if (!region.Readable || !region.ImageOrPrivate || region.Size < 8)
                continue;

            // The vtable itself and code/data references inside the FH2 image are
            // not MapElement objects. Actual elements live in process allocations.
            if (region.BaseAddress >= game.GameBase && region.BaseAddress < imageEnd)
                continue;

            for (ulong regionOffset = 0; regionOffset < region.Size && elements.Count < MaxElements;)
            {
                token.ThrowIfCancellationRequested();
                int length = (int)Math.Min((ulong)scanBuffer.Length, region.Size - regionOffset);
                ulong address = region.BaseAddress + regionOffset;
                Span<byte> chunk = scanBuffer.AsSpan(0, length);
                if (reader.TryRead(address, chunk))
                {
                    for (int i = 0; i + 8 <= length && elements.Count < MaxElements; i += 8)
                    {
                        if (BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(i, 8)) != wantedVtable)
                            continue;

                        ulong candidate = address + (ulong)i;
                        if (!seen.Add(candidate))
                            continue;

                        byte[]? raw = reader.Bytes(candidate, MapElementSize);
                        if (raw is null || BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0, 8)) != wantedVtable)
                            continue;

                        elements.Add(DescribeElement(reader, game, candidate, raw));
                    }
                }
                regionOffset += (ulong)length;
            }

            if (elements.Count >= MaxElements)
                break;
        }

        // Read-only code samples preserve the exact live implementation used for
        // this capture. The current build shows MapSetTransform storing two vec4s
        // in a heap buffer referenced by element+0x40, two scalar values through
        // element+0x48, and the element count/state value 2 at +0x50.
        object[] codeSamples =
        [
            DescribeCode(reader, game.GameBase, "MapSetTag", MapSetTagRva),
            DescribeCode(reader, game.GameBase, "MapGetTag", MapGetTagRva),
            DescribeCode(reader, game.GameBase, "MapSetTransform", MapSetTransformRva),
            DescribeCode(reader, game.GameBase, "MapAdd", MapAddRva)
        ];

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only MapElementGeneric + transform research capture",
            game.Build,
            GameBase = $"0x{game.GameBase:X}",
            MapElementVtable = $"0x{wantedVtable:X}",
            MapElementSize = $"0x{MapElementSize:X}",
            VehiclePosition = game.Vehicle.Position,
            Count = elements.Count,
            Truncated = elements.Count >= MaxElements,
            CodeSamples = codeSamples,
            Elements = elements
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-map-elements-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static object DescribeCode(ExternalProcessMemoryReader reader, ulong gameBase, string name, ulong rva)
    {
        ulong address = checked(gameBase + rva);
        byte[]? code = reader.Bytes(address, CodeSampleSize);
        return new
        {
            Name = name,
            Rva = $"0x{rva:X}",
            Address = $"0x{address:X}",
            Size = code?.Length ?? 0,
            Bytes = code is null ? null : Convert.ToHexString(code)
        };
    }

    private static object DescribeElement(ExternalProcessMemoryReader reader, GameSnapshot game, ulong address, byte[] raw)
    {
        ulong entity = U64(raw, 0x08);
        uint mapCount = U32(raw, 0x20);

        // Live MapSetTransform implementation for this build:
        //   +0x40 -> 0x20-byte buffer containing position vec4 followed by
        //            position+direction vec4
        //   +0x48 -> 0x08-byte buffer containing two copies of the scalar arg
        //   +0x50 -> 2 while this transform representation is initialized
        ulong transformPoints = U64(raw, 0x40);
        ulong transformScalars = U64(raw, 0x48);
        uint transformCount = U32(raw, 0x50);
        object? transform = DescribeTransform(reader, game, transformPoints, transformScalars, transformCount);

        var pointerFields = new List<object>();
        for (int offset = 8; offset + 8 <= raw.Length && pointerFields.Count < 12; offset += 8)
        {
            ulong target = U64(raw, offset);
            if (!MemoryRead.Pointer(target) || target == entity)
                continue;
            byte[]? targetBytes = reader.Bytes(target, 0x200);
            if (targetBytes is null)
                continue;
            string[] strings = Strings(targetBytes, 12).ToArray();
            var small = SmallLocationIds(targetBytes, 20).ToArray();
            if (strings.Length == 0 && small.Length == 0)
                continue;
            pointerFields.Add(new
            {
                Offset = $"0x{offset:X}",
                Target = $"0x{target:X}",
                Strings = strings,
                CandidateLocationIds = small
            });
        }

        object? entityInfo = null;
        if (MemoryRead.Pointer(entity))
        {
            byte[]? entityBytes = reader.Bytes(entity, 0x800);
            if (entityBytes is not null)
            {
                entityInfo = new
                {
                    Address = $"0x{entity:X}",
                    Strings = Strings(entityBytes, 32).ToArray(),
                    CandidateLocationIds = SmallLocationIds(entityBytes, 48).ToArray()
                };
            }
        }

        return new
        {
            Address = $"0x{address:X}",
            Entity = $"0x{entity:X}",
            MapCount = mapCount,
            TransformCount = transformCount,
            TransformPoints = $"0x{transformPoints:X}",
            TransformScalars = $"0x{transformScalars:X}",
            Transform = transform,
            Raw58 = Convert.ToHexString(raw),
            EntityInfo = entityInfo,
            PointerFields = pointerFields
        };
    }

    private static object? DescribeTransform(
        ExternalProcessMemoryReader reader,
        GameSnapshot game,
        ulong pointsAddress,
        ulong scalarsAddress,
        uint count)
    {
        if (count != 2 || !MemoryRead.Pointer(pointsAddress))
            return null;

        byte[]? points = reader.Bytes(pointsAddress, 0x20);
        if (points is null)
            return null;

        Vector4? position = Vector(points, 0x00);
        Vector4? end = Vector(points, 0x10);
        if (position is not Vector4 p || end is not Vector4 e)
            return null;

        var direction = new Vector4(e.X - p.X, e.Y - p.Y, e.Z - p.Z, e.W - p.W);
        float? distanceFromPlayer = null;
        if (game.Vehicle.Position is Vector3 player)
        {
            float distance = Vector3.Distance(player, new Vector3(p.X, p.Y, p.Z));
            if (float.IsFinite(distance))
                distanceFromPlayer = distance;
        }

        float[]? scalars = null;
        if (MemoryRead.Pointer(scalarsAddress))
        {
            byte[]? rawScalars = reader.Bytes(scalarsAddress, 8);
            if (rawScalars is not null)
            {
                float a = F32(rawScalars, 0);
                float b = F32(rawScalars, 4);
                if (float.IsFinite(a) && float.IsFinite(b))
                    scalars = [a, b];
            }
        }

        return new
        {
            Position = p,
            PositionPlusDirection = e,
            Direction = direction,
            Scalars = scalars,
            DistanceFromPlayer = distanceFromPlayer
        };
    }

    private static Vector4? Vector(byte[] raw, int offset)
    {
        if (offset < 0 || offset + 16 > raw.Length)
            return null;
        float x = F32(raw, offset), y = F32(raw, offset + 4), z = F32(raw, offset + 8), w = F32(raw, offset + 12);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(w))
            return null;
        if (MathF.Abs(x) > 1_000_000 || MathF.Abs(y) > 1_000_000 || MathF.Abs(z) > 1_000_000 || MathF.Abs(w) > 1_000_000)
            return null;
        return new Vector4(x, y, z, w);
    }

    private static IEnumerable<string> Strings(byte[] raw, int limit)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in AsciiStrings(raw))
        {
            if (found.Add(value)) yield return value;
            if (found.Count >= limit) yield break;
        }
        foreach (string value in Utf16Strings(raw))
        {
            if (found.Add(value)) yield return value;
            if (found.Count >= limit) yield break;
        }
    }

    private static IEnumerable<string> AsciiStrings(byte[] raw)
    {
        int start = -1;
        for (int i = 0; i <= raw.Length; i++)
        {
            bool printable = i < raw.Length && raw[i] >= 32 && raw[i] <= 126;
            if (printable)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start >= 0 && i - start >= 4)
                yield return $"ascii+0x{start:X}: {Encoding.ASCII.GetString(raw, start, i - start)}";
            start = -1;
        }
    }

    private static IEnumerable<string> Utf16Strings(byte[] raw)
    {
        for (int parity = 0; parity < 2; parity++)
        {
            int start = -1;
            for (int i = parity; i + 1 <= raw.Length; i += 2)
            {
                bool printable = i + 1 < raw.Length && raw[i + 1] == 0 && raw[i] >= 32 && raw[i] <= 126;
                if (printable)
                {
                    if (start < 0) start = i;
                    continue;
                }
                if (start >= 0 && i - start >= 8)
                {
                    int bytes = i - start;
                    yield return $"utf16+0x{start:X}: {Encoding.Unicode.GetString(raw, start, bytes)}";
                }
                start = -1;
            }
        }
    }

    private static IEnumerable<object> SmallLocationIds(byte[] raw, int limit)
    {
        int count = 0;
        for (int offset = 0; offset + 4 <= raw.Length; offset += 4)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset, 4));
            if (value is < 3 or > 9)
                continue;
            yield return new { Offset = $"0x{offset:X}", Value = value };
            if (++count >= limit) yield break;
        }
    }

    private static ulong U64(byte[] raw, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset, 8));
    private static uint U32(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));
    private static float F32(byte[] raw, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset, 4)));
}
