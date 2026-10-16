using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Read-only probe for FH2 trigger-zone / world spatial state. Research only.
/// It scans live memory for known TriggerZone vtables, inspects their vectors,
/// probes likely descriptor pointers for sphere-like geometry, and describes
/// WorldManager child pointers. It never executes or patches game code.
/// </summary>
internal static class TriggerZoneResearchScanner
{
    private const int ReadChunkSize = 1024 * 1024;
    private const int ZoneReadSize = 0x200;
    private const int MaxZones = 192;
    private const int MaxVectorEntries = 32;
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
            throw new InvalidOperationException("Attach FH2 before taking a trigger-zone probe");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before trigger-zone capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        token.ThrowIfCancellationRequested();

        Vector3? playerPosition = game.Vehicle.Position;
        ulong cforza = game.Roots.PlayerManager;
        ulong worldManager = ReadU64(reader, cforza + WorldManagerOffset);
        ObjectReport? worldManagerReport = DescribeObject(reader, game.GameBase, worldManager);

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

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only TriggerZone / WorldManager physical-location research",
            game.Build,
            GameBase = Hex(game.GameBase),
            VehiclePosition = playerPosition,
            PolygonLocation = game.Location,
            Experimental = true,
            Warning = "Sphere geometry is heuristic. A candidate is not a physical city-region until repeated live captures prove that it changes with vehicle position.",
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
                Descriptor = worldManagerReport,
                Children = worldChildren,
            },
            ZoneCount = zones.Length,
            Zones = zones,
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-trigger-zones-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
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

        VectorReport triggerables = ReadVector(reader, address + 0x10);
        VectorReport handles = ReadVector(reader, address + 0x28);
        string? name188 = ReadMsvcString(reader, address + 0x188);
        string? name1B8 = ReadMsvcString(reader, address + 0x1B8);
        SphereCandidate[] sphereCandidates = FindSphereCandidates(reader, raw, playerPosition).Take(24).ToArray();

        string kind = KnownVtableName(gameBase, vtable) ?? "TriggerZone";
        return new ZoneHit(
            Hex(address),
            kind,
            Hex(vtable),
            TryReadMsvcRtti(reader, gameBase, vtable),
            triggerables,
            handles,
            name188,
            name1B8,
            sphereCandidates,
            Convert.ToHexString(raw));
    }

    private static IEnumerable<SphereCandidate> FindSphereCandidates(ExternalProcessMemoryReader reader, byte[] zoneRaw, Vector3? playerPosition)
    {
        var seenPointers = new HashSet<ulong>();
        for (int pointerOffset = 0x08; pointerOffset + 8 <= Math.Min(zoneRaw.Length, 0x180); pointerOffset += 8)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(zoneRaw.AsSpan(pointerOffset, 8));
            if (!ValidPointer(pointer) || !seenPointers.Add(pointer))
                continue;

            byte[] target = ReadBytes(reader, pointer, 0x80);
            if (target.Length < 0x34)
                continue;

            for (int offset = 0; offset + 16 <= target.Length; offset += 4)
            {
                float x = BinaryPrimitives.ReadSingleLittleEndian(target.AsSpan(offset, 4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(target.AsSpan(offset + 4, 4));
                float z = BinaryPrimitives.ReadSingleLittleEndian(target.AsSpan(offset + 8, 4));
                float radius = BinaryPrimitives.ReadSingleLittleEndian(target.AsSpan(offset + 12, 4));
                if (!FiniteCoordinate(x) || !FiniteCoordinate(y) || !FiniteCoordinate(z) || !float.IsFinite(radius) || radius < 0.5f || radius > 20000f)
                    continue;

                float? distance = playerPosition is Vector3 player
                    ? Vector3.Distance(player, new Vector3(x, y, z))
                    : null;
                bool? contains = distance.HasValue ? distance.Value <= radius : null;

                // Keep containing spheres and reasonably nearby candidates. This is
                // intentionally permissive because desc+0x20/+0x30 is not yet linked
                // to a concrete live descriptor pointer.
                if (distance.HasValue && !contains!.Value && distance.Value > MathF.Max(radius * 3f, 1000f))
                    continue;

                yield return new SphereCandidate(
                    $"0x{pointerOffset:X}",
                    Hex(pointer),
                    $"0x{offset:X}",
                    x, y, z, radius,
                    distance,
                    contains);
            }
        }
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

    private static bool FiniteCoordinate(float value) => float.IsFinite(value) && MathF.Abs(value) <= 100000f;
    private static bool ValidPointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;
    private static string Hex(ulong value) => value == 0 ? "0x0" : $"0x{value:X}";

    private sealed record ZoneHit(
        string Address,
        string Kind,
        string Vtable,
        RttiReport? Rtti,
        VectorReport Triggerables,
        VectorReport Handles,
        string? NameAt188,
        string? NameAt1B8,
        SphereCandidate[] SphereCandidates,
        string RawObjectBytes);

    private sealed record VectorReport(string Begin, string End, string Capacity, long Count, string[] Entries);
    private sealed record SphereCandidate(string ZonePointerOffset, string DescriptorPointer, string GeometryOffset, float X, float Y, float Z, float Radius, float? DistanceToPlayer, bool? ContainsPlayer);
    private sealed record ObjectReport(string Address, string Vtable, string? KnownType, RttiReport? Rtti, string RawObjectBytes);
    private sealed record RttiReport(string CompleteObjectLocator, uint Signature, string? TypeDescriptor, string? RawTypeName);
}
