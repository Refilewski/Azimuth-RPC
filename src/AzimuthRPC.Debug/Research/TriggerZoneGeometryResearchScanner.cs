using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Second-generation read-only TriggerZone probe. Unlike the original pointer
/// heuristic, this decodes the transform observed directly in live TriggerZone
/// objects: translation at +0xA0, an orthonormal 3x3 basis at +0xB0/+0xC0/+0xD0,
/// and shape extents at +0xE0. It also describes the first triggerable/handle
/// objects with RTTI. Research only; no game code is executed or patched.
/// </summary>
internal static class TriggerZoneGeometryResearchScanner
{
    private const int ReadChunkSize = 1024 * 1024;
    private const int ZoneReadSize = 0x200;
    private const int MaxZones = 512;
    private const int MaxVectorEntries = 32;
    private const int MaxDescribedEntries = 16;
    private const int ImageSize = 0x03A18000;

    private const ulong TriggerZoneVtableRva = 0x02F62E48;
    private const ulong MobileTriggerZoneVtableRva = 0x02F62E98;
    private const int WorldManagerOffset = 0x460;

    private static readonly int[] WorldManagerChildOffsets = [0x08, 0x10, 0x18, 0x20, 0x40, 0x48, 0x58];

    private static readonly Dictionary<ulong, string> KnownVtables = new()
    {
        [0x02CD1F20] = "EntityModel::WorldManager",
        [0x02CCB2E0] = "TriggerModule",
        [0x02CAA698] = "NavigationModule",
        [0x02CC31E8] = "SatNavModule",
        [0x02CAA4A8] = "LocatorsModule",
        [0x02C816C8] = "CareerNavigationModule",
        [0x02C88AA0] = "CarTriggerComponent",
        [0x02CBB648] = "RoadDiscoveryComponent",
        [0x02CC3278] = "SatNavRouteTrackingComponent",
        [0x02C987E8] = "FestivalSpatialComponent",
        [0x02CAA938] = "NavigationWorldComponent",
        [TriggerZoneVtableRva] = "Trigger::TriggerZone",
        [MobileTriggerZoneVtableRva] = "Trigger::MobileTriggerZone",
        [0x02F62E80] = "Trigger::TriggerZoneHandle",
    };

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a TriggerZone geometry probe");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before TriggerZone geometry capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        token.ThrowIfCancellationRequested();

        Vector3? playerPosition = game.Vehicle.Position;
        ulong cforza = game.Roots.PlayerManager;
        ulong worldManager = ReadU64(reader, cforza + WorldManagerOffset);

        var worldChildren = WorldManagerChildOffsets.Select(offset =>
        {
            token.ThrowIfCancellationRequested();
            ulong pointer = ReadU64(reader, worldManager + (ulong)offset);
            return new
            {
                Offset = $"0x{offset:X}",
                Pointer = Hex(pointer),
                Object = DescribeObject(reader, game.GameBase, pointer),
            };
        }).ToArray();

        ulong triggerZoneVtable = game.GameBase + TriggerZoneVtableRva;
        ulong mobileTriggerZoneVtable = game.GameBase + MobileTriggerZoneVtableRva;
        ZoneHit[] zones = FindZones(reader, game.GameBase, triggerZoneVtable, mobileTriggerZoneVtable, playerPosition, token).ToArray();

        var nearestStatic = zones
            .Where(z => z.Kind == "Trigger::TriggerZone" && z.Geometry is not null && z.Geometry.DistanceToPlayer.HasValue)
            .OrderBy(z => z.Geometry!.DistanceToPlayer!.Value)
            .Take(24)
            .Select(z => new
            {
                z.Address,
                z.Geometry,
                TriggerableCount = z.Triggerables.Count,
                HandleCount = z.Handles.Count,
                TriggerableTypes = z.TriggerableObjects
                    .Select(o => o.Rtti?.RawTypeName ?? o.KnownType)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .ToArray(),
            })
            .ToArray();

        var kindCounts = zones
            .GroupBy(z => z.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only direct TriggerZone geometry / triggerable RTTI research",
            game.Build,
            GameBase = Hex(game.GameBase),
            VehiclePosition = playerPosition,
            PolygonLocation = game.Location,
            Experimental = true,
            Warning = "The direct transform layout is strongly evidenced by live objects, but using TriggerZones as city boundaries is not established. MobileTriggerZone translation may be parent-relative.",
            GeometryLayout = new
            {
                Translation = "+0xA0 Vector3",
                BasisRow0 = "+0xB0 Vector3",
                BasisRow1 = "+0xC0 Vector3",
                BasisRow2 = "+0xD0 Vector3",
                Extents = "+0xE0 Vector3",
                ExtentW = "+0xEC float",
            },
            KnownStatic = new
            {
                TriggerZoneVtable = Hex(triggerZoneVtable),
                MobileTriggerZoneVtable = Hex(mobileTriggerZoneVtable),
                TriggerZoneGeometrySlot4Rva = "0x194CB80",
                TriggerZoneSpatialCallerLeadRva = "0x194E4E0",
                WorldManagerFromCForzaOffset = "0x460",
            },
            WorldManager = new
            {
                Address = Hex(worldManager),
                Descriptor = DescribeObject(reader, game.GameBase, worldManager),
                Children = worldChildren,
            },
            ZoneCap = MaxZones,
            ZoneCount = zones.Length,
            ZoneCapHit = zones.Length >= MaxZones,
            KindCounts = kindCounts,
            NearestStaticZones = nearestStatic,
            Zones = zones,
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-trigger-geometry-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static IEnumerable<ZoneHit> FindZones(
        ExternalProcessMemoryReader reader,
        ulong gameBase,
        ulong triggerZoneVtable,
        ulong mobileTriggerZoneVtable,
        Vector3? playerPosition,
        CancellationToken token)
    {
        byte[] buffer = new byte[ReadChunkSize + 7];
        var seen = new HashSet<ulong>();
        ulong imageEnd = gameBase + (ulong)ImageSize;

        foreach (MemoryRegion region in reader.EnumerateRegions())
        {
            token.ThrowIfCancellationRequested();
            if (!region.Readable)
                continue;

            ulong regionEnd = region.BaseAddress > ulong.MaxValue - region.Size
                ? ulong.MaxValue
                : region.BaseAddress + region.Size;
            if (region.BaseAddress < imageEnd && regionEnd > gameBase)
                continue;

            ulong address = region.BaseAddress;
            int carry = 0;
            while (address < regionEnd && seen.Count < MaxZones)
            {
                token.ThrowIfCancellationRequested();
                int fresh = (int)Math.Min((ulong)ReadChunkSize, regionEnd - address);
                if (fresh <= 0)
                    break;

                if (!reader.TryRead(address, buffer.AsSpan(carry, fresh)))
                {
                    carry = 0;
                    address += (ulong)fresh;
                    continue;
                }

                int count = carry + fresh;
                ulong chunkBase = address - (ulong)carry;
                int firstAligned = (int)((8 - (chunkBase & 7)) & 7);
                for (int offset = firstAligned; offset + 8 <= count && seen.Count < MaxZones; offset += 8)
                {
                    ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
                    if (value != triggerZoneVtable && value != mobileTriggerZoneVtable)
                        continue;

                    ulong objectAddress = chunkBase + (ulong)offset;
                    if (!seen.Add(objectAddress))
                        continue;

                    ZoneHit? zone = DescribeZone(reader, gameBase, objectAddress, value, playerPosition);
                    if (zone is not null)
                        yield return zone;
                }

                carry = Math.Min(7, count);
                if (carry > 0)
                    Buffer.BlockCopy(buffer, count - carry, buffer, 0, carry);
                address += (ulong)fresh;
            }

            if (seen.Count >= MaxZones)
                yield break;
        }
    }

    private static ZoneHit? DescribeZone(ExternalProcessMemoryReader reader, ulong gameBase, ulong address, ulong vtable, Vector3? playerPosition)
    {
        byte[] raw = ReadBytes(reader, address, ZoneReadSize);
        if (raw.Length != ZoneReadSize)
            return null;

        string kind = KnownVtableName(gameBase, vtable) ?? "TriggerZone";
        VectorReport triggerables = ReadVector(reader, address + 0x10);
        VectorReport handles = ReadVector(reader, address + 0x28);

        ObjectReport[] triggerableObjects = DescribeEntries(reader, gameBase, triggerables.Entries);
        ObjectReport[] handleObjects = DescribeEntries(reader, gameBase, handles.Entries);
        DirectGeometryReport? geometry = DecodeDirectGeometry(raw, kind, playerPosition);

        return new ZoneHit(
            Hex(address),
            kind,
            Hex(vtable),
            TryReadMsvcRtti(reader, gameBase, vtable),
            triggerables,
            handles,
            triggerableObjects,
            handleObjects,
            geometry,
            ReadMsvcString(reader, address + 0x188),
            ReadMsvcString(reader, address + 0x1B8),
            Convert.ToHexString(raw));
    }

    private static DirectGeometryReport? DecodeDirectGeometry(byte[] raw, string kind, Vector3? playerPosition)
    {
        if (raw.Length < 0xF0)
            return null;

        Vector3 center = ReadVector3(raw, 0xA0);
        Vector3 row0 = ReadVector3(raw, 0xB0);
        Vector3 row1 = ReadVector3(raw, 0xC0);
        Vector3 row2 = ReadVector3(raw, 0xD0);
        Vector3 extents = ReadVector3(raw, 0xE0);
        float extentW = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(0xEC, 4));

        if (!FiniteVector(center) || !FiniteVector(row0) || !FiniteVector(row1) || !FiniteVector(row2) || !FiniteVector(extents) || !float.IsFinite(extentW))
            return null;

        float row0Len = row0.Length();
        float row1Len = row1.Length();
        float row2Len = row2.Length();
        bool orthonormal =
            MathF.Abs(row0Len - 1f) <= 0.01f &&
            MathF.Abs(row1Len - 1f) <= 0.01f &&
            MathF.Abs(row2Len - 1f) <= 0.01f &&
            MathF.Abs(Vector3.Dot(row0, row1)) <= 0.01f &&
            MathF.Abs(Vector3.Dot(row0, row2)) <= 0.01f &&
            MathF.Abs(Vector3.Dot(row1, row2)) <= 0.01f;

        float? distance = null;
        Vector3? localRows = null;
        Vector3? localColumns = null;
        bool? containsRows = null;
        bool? containsColumns = null;
        float? outsideRows = null;
        float? outsideColumns = null;

        if (playerPosition is Vector3 player && FiniteVector(player))
        {
            Vector3 delta = player - center;
            distance = delta.Length();

            localRows = new Vector3(
                Vector3.Dot(delta, row0),
                Vector3.Dot(delta, row1),
                Vector3.Dot(delta, row2));

            localColumns = new Vector3(
                delta.X * row0.X + delta.Y * row1.X + delta.Z * row2.X,
                delta.X * row0.Y + delta.Y * row1.Y + delta.Z * row2.Y,
                delta.X * row0.Z + delta.Y * row1.Z + delta.Z * row2.Z);

            Vector3 absExtents = new(MathF.Abs(extents.X), MathF.Abs(extents.Y), MathF.Abs(extents.Z));
            containsRows = InsideExtents(localRows.Value, absExtents);
            containsColumns = InsideExtents(localColumns.Value, absExtents);
            outsideRows = DistanceOutside(localRows.Value, absExtents);
            outsideColumns = DistanceOutside(localColumns.Value, absExtents);
        }

        return new DirectGeometryReport(
            center,
            row0,
            row1,
            row2,
            extents,
            extentW,
            orthonormal,
            kind == "Trigger::TriggerZone",
            distance,
            localRows,
            localColumns,
            containsRows,
            containsColumns,
            outsideRows,
            outsideColumns);
    }

    private static bool InsideExtents(Vector3 local, Vector3 extents) =>
        MathF.Abs(local.X) <= extents.X && MathF.Abs(local.Y) <= extents.Y && MathF.Abs(local.Z) <= extents.Z;

    private static float DistanceOutside(Vector3 local, Vector3 extents)
    {
        float x = MathF.Max(MathF.Abs(local.X) - extents.X, 0f);
        float y = MathF.Max(MathF.Abs(local.Y) - extents.Y, 0f);
        float z = MathF.Max(MathF.Abs(local.Z) - extents.Z, 0f);
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    private static Vector3 ReadVector3(byte[] data, int offset) => new(
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4, 4)),
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8, 4)));

    private static ObjectReport[] DescribeEntries(ExternalProcessMemoryReader reader, ulong gameBase, string[] entries)
    {
        var result = new List<ObjectReport>();
        foreach (string entry in entries.Take(MaxDescribedEntries))
        {
            if (!TryParseHex(entry, out ulong address))
                continue;
            ObjectReport? report = DescribeObject(reader, gameBase, address);
            if (report is not null)
                result.Add(report);
        }
        return result.ToArray();
    }

    private static VectorReport ReadVector(ExternalProcessMemoryReader reader, ulong vectorAddress)
    {
        ulong begin = ReadU64(reader, vectorAddress);
        ulong end = ReadU64(reader, vectorAddress + 8);
        ulong capacity = ReadU64(reader, vectorAddress + 16);

        long count = -1;
        string[] entries = [];
        if (ValidPointer(begin) && end >= begin && capacity >= end && ((end - begin) % 8) == 0)
        {
            ulong rawCount = (end - begin) / 8;
            if (rawCount <= 4096)
            {
                count = (long)rawCount;
                int take = (int)Math.Min(rawCount, (ulong)MaxVectorEntries);
                var list = new string[take];
                for (int i = 0; i < take; i++)
                    list[i] = Hex(ReadU64(reader, begin + (ulong)(i * 8)));
                entries = list;
            }
        }

        return new VectorReport(Hex(begin), Hex(end), Hex(capacity), count, entries);
    }

    private static ObjectReport? DescribeObject(ExternalProcessMemoryReader reader, ulong gameBase, ulong address)
    {
        if (!ValidPointer(address))
            return null;
        ulong vtable = ReadU64(reader, address);
        if (!ValidPointer(vtable))
            return new ObjectReport(Hex(address), Hex(vtable), null, null, string.Empty);

        return new ObjectReport(
            Hex(address),
            Hex(vtable),
            KnownVtableName(gameBase, vtable),
            TryReadMsvcRtti(reader, gameBase, vtable),
            Convert.ToHexString(ReadBytes(reader, address, 0x100)));
    }

    private static string? KnownVtableName(ulong gameBase, ulong vtable)
    {
        if (vtable < gameBase)
            return null;
        ulong rva = vtable - gameBase;
        return KnownVtables.TryGetValue(rva, out string? name) ? name : null;
    }

    private static RttiReport? TryReadMsvcRtti(ExternalProcessMemoryReader reader, ulong gameBase, ulong vtable)
    {
        if (!ValidPointer(vtable) || vtable < 8)
            return null;
        ulong col = ReadU64(reader, vtable - 8);
        byte[] raw = ReadBytes(reader, col, 24);
        if (raw.Length < 24)
            return null;

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
        int typeDescriptorRva = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12, 4));
        if (signature != 1 || typeDescriptorRva <= 0 || typeDescriptorRva >= ImageSize)
            return new RttiReport(Hex(col), signature, null, null);

        ulong typeDescriptor = gameBase + (uint)typeDescriptorRva;
        return new RttiReport(Hex(col), signature, Hex(typeDescriptor), ReadAscii(reader, typeDescriptor + 0x10, 192));
    }

    private static string? ReadMsvcString(ExternalProcessMemoryReader reader, ulong address)
    {
        byte[] raw = ReadBytes(reader, address, 0x20);
        if (raw.Length != 0x20)
            return null;

        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x10, 8));
        ulong capacity = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x18, 8));
        if (length == 0 || length > 160 || capacity < length)
            return null;

        byte[] bytes;
        if (capacity < 16)
        {
            bytes = raw.AsSpan(0, (int)length).ToArray();
        }
        else
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0, 8));
            bytes = ReadBytes(reader, pointer, (int)length);
            if (bytes.Length != (int)length)
                return null;
        }

        if (bytes.Any(b => b < 0x20 || b > 0x7E))
            return null;
        return Encoding.ASCII.GetString(bytes);
    }

    private static string? ReadAscii(ExternalProcessMemoryReader reader, ulong address, int max)
    {
        byte[] bytes = ReadBytes(reader, address, max);
        if (bytes.Length == 0)
            return null;
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return end == 0 ? string.Empty : Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static byte[] ReadBytes(ExternalProcessMemoryReader reader, ulong address, int count)
    {
        if (!ValidPointer(address) || count <= 0)
            return [];
        byte[] buffer = new byte[count];
        return reader.TryRead(address, buffer) ? buffer : [];
    }

    private static ulong ReadU64(ExternalProcessMemoryReader reader, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        return ValidPointer(address) && reader.TryRead(address, b)
            ? BinaryPrimitives.ReadUInt64LittleEndian(b)
            : 0;
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;
        return ulong.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static bool FiniteVector(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        MathF.Abs(value.X) <= 100000f && MathF.Abs(value.Y) <= 100000f && MathF.Abs(value.Z) <= 100000f;

    private static bool ValidPointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;
    private static string Hex(ulong value) => value == 0 ? "0x0" : $"0x{value:X}";

    private sealed record ZoneHit(
        string Address,
        string Kind,
        string Vtable,
        RttiReport? Rtti,
        VectorReport Triggerables,
        VectorReport Handles,
        ObjectReport[] TriggerableObjects,
        ObjectReport[] HandleObjects,
        DirectGeometryReport? Geometry,
        string? NameAt188,
        string? NameAt1B8,
        string RawObjectBytes);

    private sealed record DirectGeometryReport(
        Vector3 Center,
        Vector3 BasisRow0,
        Vector3 BasisRow1,
        Vector3 BasisRow2,
        Vector3 Extents,
        float ExtentW,
        bool BasisLooksOrthonormal,
        bool WorldSpaceLikely,
        float? DistanceToPlayer,
        Vector3? PlayerLocalRows,
        Vector3? PlayerLocalColumns,
        bool? ContainsPlayerRows,
        bool? ContainsPlayerColumns,
        float? DistanceOutsideRows,
        float? DistanceOutsideColumns);

    private sealed record VectorReport(string Begin, string End, string Capacity, long Count, string[] Entries);
    private sealed record ObjectReport(string Address, string Vtable, string? KnownType, RttiReport? Rtti, string RawObjectBytes);
    private sealed record RttiReport(string CompleteObjectLocator, uint Signature, string? TypeDescriptor, string? RawTypeName);
}
