using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Read-only follow-up for FH2 RoadTripLocations lookup code.
///
/// 0xC09F80 is treated as a generic name-lookup helper that reads dword
/// [object+0x84], not as a vtable method or proof of the player's current
/// physical region. 0xE879A0 is kept as a second lookup lead associated with
/// destination-results code.
///
/// Live-object probing is intentionally restricted to two vtables identified
/// during binary analysis as RoadTripDestinationResults and
/// PreRaceRoadTripDestinationResults. Their +0x84 values are research data
/// only and must not be promoted to runtime location semantics without live
/// validation.
/// </summary>
internal static class RoadTripLookupResearchScanner
{
    private const int ImageSize = 0x3A18000;
    private const int HeaderReadSize = 0x4000;
    private const int ReadChunkSize = 1024 * 1024;
    private const int ObjectReadSize = 0x4C0;
    private const int MaxLiveMatchesPerTable = 48;

    // Supported FH2 Xbox One 1.0.0.64 RVAs.
    private static readonly uint[] CandidateFunctionRvas = [0xC09F80, 0xE879A0];

    // Binary-analysis leads. These are function starts/callers worth preserving
    // in the dump so they can be disassembled externally without rescanning.
    private static readonly uint[] KnownCodeLeadRvas = [0xBEFD10, 0xE8BC80];

    // Preferred-base addresses reported by binary analysis:
    //   0x142E4B430 RoadTripDestinationResults
    //   0x142D7C9C8 PreRaceRoadTripDestinationResults
    // Store them as RVAs so relocation stays correct.
    private static readonly uint[] KnownDestinationResultVtableRvas = [0x2E4B430, 0x2D7C9C8];

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a RoadTrip lookup probe");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before RoadTrip lookup capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        byte[]? header = reader.Bytes(game.GameBase, HeaderReadSize);
        if (header is null)
            throw new InvalidOperationException("Could not read FH2 PE headers");

        PeSection[] sections = ParseSections(header).ToArray();
        if (sections.Length == 0)
            throw new InvalidOperationException("Could not parse FH2 PE sections");

        byte[] image = ReadImage(reader, game.GameBase, token);
        ulong imageEnd = game.GameBase + (ulong)ImageSize;

        var functions = CandidateFunctionRvas.Select(rva =>
        {
            token.ThrowIfCancellationRequested();
            ulong target = game.GameBase + rva;
            DirectCallSite[] callers = FindDirectCallers(image, game.GameBase, sections, target, token)
                .Take(96)
                .ToArray();
            ImagePointerHit[] pointerHits = FindAbsoluteImagePointers(image, game.GameBase, sections, target)
                .Take(96)
                .ToArray();

            return new
            {
                Function = $"0x{target:X}",
                FunctionRva = $"0x{rva:X}",
                FunctionBytes = HexRange(image, rva, 0x300),
                DirectCallers = callers.Select(call => new
                {
                    Address = $"0x{call.Address:X}",
                    Rva = $"0x{call.Address - game.GameBase:X}",
                    call.Section,
                    Window = HexWindow(image, game.GameBase, call.Address, 0x100),
                }).ToArray(),
                AbsoluteImagePointers = pointerHits.Select(hit => new
                {
                    Address = $"0x{hit.Address:X}",
                    Rva = $"0x{hit.Address - game.GameBase:X}",
                    hit.Section,
                }).ToArray(),
            };
        }).ToArray();

        var codeLeads = KnownCodeLeadRvas.Select(rva => new
        {
            Address = $"0x{game.GameBase + rva:X}",
            Rva = $"0x{rva:X}",
            Bytes = HexRange(image, rva, 0x300),
        }).ToArray();

        var knownVtables = KnownDestinationResultVtableRvas.Select(rva =>
        {
            token.ThrowIfCancellationRequested();
            ulong tableAddress = game.GameBase + rva;
            PointerEntry[] entries = ReadExecutablePointerTable(image, game.GameBase, sections, rva, 96).ToArray();
            LiveObjectHit[] live = FindLiveObjects(reader, game.GameBase, imageEnd, tableAddress, token)
                .Take(MaxLiveMatchesPerTable)
                .ToArray();

            return new
            {
                Address = $"0x{tableAddress:X}",
                Rva = $"0x{rva:X}",
                ExecutableEntryCount = entries.Length,
                Entries = entries.Select((entry, index) => new
                {
                    Slot = index,
                    Address = $"0x{entry.Address:X}",
                    Rva = entry.Address >= game.GameBase && entry.Address < imageEnd
                        ? $"0x{entry.Address - game.GameBase:X}"
                        : null,
                }).ToArray(),
                LiveObjects = live.Select(obj => new
                {
                    Address = $"0x{obj.Address:X}",
                    Vtable = $"0x{obj.Vtable:X}",
                    obj.Field80,
                    obj.Field84,
                    obj.Field88,
                    Field84LooksLikeRoadTripLocationId = obj.Field84 is >= 3 and <= 9,
                    obj.Raw,
                }).ToArray(),
            };
        }).ToArray();

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only RoadTripLocations lookup / caller / known destination-result vtable research",
            game.Build,
            GameBase = $"0x{game.GameBase:X}",
            VehiclePosition = game.Vehicle.Position,
            PolygonLocation = game.Location,
            ExpectedRoadTripIdFromStaticMap = StaticExpectedId(game.Location),
            Experimental = true,
            Warning = "0xC09F80 is a generic RoadTripLocations name lookup helper. +0x84 and destination-results objects are not evidence of the player's physical region until repeated live tests prove matching semantics.",
            Functions = functions,
            CodeLeads = codeLeads,
            KnownDestinationResultVtables = knownVtables,
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-roadtrip-lookup-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static int? StaticExpectedId(string? location) => location switch
    {
        "Sisteron" => 3,
        "Nice" or "Nice Massena" => 4,
        "Saint-Martin" or "SaintMartin" => 5,
        "Castelletto" => 6,
        "Montellino" => 7,
        "San Gimignano" or "SanGimignano" => 8,
        _ => null,
    };

    private static IEnumerable<DirectCallSite> FindDirectCallers(
        byte[] image,
        ulong gameBase,
        PeSection[] sections,
        ulong target,
        CancellationToken token)
    {
        foreach (PeSection section in sections.Where(section => section.Executable))
        {
            int start = checked((int)section.Rva);
            int end = Math.Min(image.Length, checked(start + (int)section.Size));
            for (int offset = start; offset + 5 <= end; offset++)
            {
                if ((offset & 0xFFFFF) == 0)
                    token.ThrowIfCancellationRequested();
                if (image[offset] != 0xE8)
                    continue;

                int rel = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset + 1, 4));
                long destination = checked((long)(gameBase + (ulong)offset + 5) + rel);
                if (destination > 0 && (ulong)destination == target)
                    yield return new DirectCallSite(gameBase + (ulong)offset, section.Name);
            }
        }
    }

    private static IEnumerable<ImagePointerHit> FindAbsoluteImagePointers(
        byte[] image,
        ulong gameBase,
        PeSection[] sections,
        ulong target)
    {
        foreach (PeSection section in sections.Where(section => !section.Executable))
        {
            int start = checked((int)section.Rva);
            int end = Math.Min(image.Length, checked(start + (int)section.Size));
            int alignedStart = (start + 7) & ~7;
            for (int offset = alignedStart; offset + 8 <= end; offset += 8)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset, 8)) == target)
                    yield return new ImagePointerHit(gameBase + (ulong)offset, section.Name);
            }
        }
    }

    private static IEnumerable<PointerEntry> ReadExecutablePointerTable(
        byte[] image,
        ulong gameBase,
        PeSection[] sections,
        uint rva,
        int maxEntries)
    {
        int offset = checked((int)rva);
        for (int i = 0; i < maxEntries && offset + 8 <= image.Length; i++, offset += 8)
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset, 8));
            if (!IsExecutableAddress(value, gameBase, sections))
                yield break;
            yield return new PointerEntry(value);
        }
    }

    private static IEnumerable<LiveObjectHit> FindLiveObjects(
        ExternalProcessMemoryReader reader,
        ulong gameBase,
        ulong imageEnd,
        ulong tableAddress,
        CancellationToken token)
    {
        byte[] buffer = new byte[ReadChunkSize];
        HashSet<ulong> seen = [];
        int emitted = 0;

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

            for (ulong address = region.BaseAddress; address < regionEnd && emitted < MaxLiveMatchesPerTable;)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min((ulong)buffer.Length, regionEnd - address);
                if (count < 8)
                    break;

                if (reader.TryRead(address, buffer.AsSpan(0, count)))
                {
                    int firstAligned = (int)((8 - (address & 7)) & 7);
                    for (int offset = firstAligned; offset + 8 <= count; offset += 8)
                    {
                        if (BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8)) != tableAddress)
                            continue;

                        ulong objectAddress = address + (ulong)offset;
                        if (!seen.Add(objectAddress))
                            continue;

                        byte[]? raw = reader.Bytes(objectAddress, ObjectReadSize);
                        if (raw is null || raw.Length < 0x8C)
                            continue;

                        uint f80 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x80, 4));
                        uint f84 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x84, 4));
                        uint f88 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x88, 4));
                        yield return new LiveObjectHit(objectAddress, tableAddress, f80, f84, f88, Convert.ToHexString(raw));
                        emitted++;
                        if (emitted >= MaxLiveMatchesPerTable)
                            yield break;
                    }
                }

                // Seven-byte overlap prevents an eight-byte pointer from being
                // missed when it straddles two read chunks.
                ulong advance = (ulong)(count > 7 ? count - 7 : count);
                if (advance == 0)
                    break;
                address += advance;
            }
        }
    }

    private static string HexWindow(byte[] image, ulong gameBase, ulong address, int radius)
    {
        if (address < gameBase)
            return string.Empty;
        int center = checked((int)(address - gameBase));
        int start = Math.Max(0, center - radius);
        int end = Math.Min(image.Length, center + radius);
        return end > start ? Convert.ToHexString(image.AsSpan(start, end - start)) : string.Empty;
    }

    private static string HexRange(byte[] image, uint rva, int length)
    {
        int offset = checked((int)rva);
        if (offset < 0 || offset >= image.Length)
            return string.Empty;
        int count = Math.Min(length, image.Length - offset);
        return count > 0 ? Convert.ToHexString(image.AsSpan(offset, count)) : string.Empty;
    }

    private static bool IsExecutableAddress(ulong address, ulong gameBase, PeSection[] sections)
    {
        if (address < gameBase || address >= gameBase + (ulong)ImageSize)
            return false;
        ulong rva = address - gameBase;
        return sections.Any(section => section.Executable && rva >= section.Rva && rva < section.Rva + section.Size);
    }

    private static byte[] ReadImage(ExternalProcessMemoryReader reader, ulong gameBase, CancellationToken token)
    {
        byte[] image = new byte[ImageSize];
        ulong imageEnd = gameBase + (ulong)ImageSize;
        foreach (MemoryRegion region in reader.EnumerateRegions())
        {
            token.ThrowIfCancellationRequested();
            if (!region.Readable)
                continue;

            ulong regionEnd = region.BaseAddress > ulong.MaxValue - region.Size
                ? ulong.MaxValue
                : region.BaseAddress + region.Size;
            ulong start = Math.Max(region.BaseAddress, gameBase);
            ulong end = Math.Min(regionEnd, imageEnd);
            if (start >= end)
                continue;

            for (ulong address = start; address < end;)
            {
                int count = (int)Math.Min((ulong)ReadChunkSize, end - address);
                _ = reader.TryRead(address, image.AsSpan(checked((int)(address - gameBase)), count));
                address += (ulong)count;
            }
        }
        return image;
    }

    private static IEnumerable<PeSection> ParseSections(byte[] header)
    {
        if (header.Length < 0x100 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2)) != 0x5A4D)
            yield break;

        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C, 4));
        if (lfanew < 0x40 || lfanew + 24 > header.Length)
            yield break;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(lfanew, 4)) != 0x00004550)
            yield break;

        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(lfanew + 6, 2));
        ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(lfanew + 20, 2));
        int sectionTable = lfanew + 24 + optionalSize;

        for (int index = 0; index < sectionCount; index++)
        {
            int offset = sectionTable + index * 40;
            if (offset < 0 || offset + 40 > header.Length)
                yield break;

            ReadOnlySpan<byte> row = header.AsSpan(offset, 40);
            int nameLength = row[..8].IndexOf((byte)0);
            if (nameLength < 0)
                nameLength = 8;
            string name = Encoding.ASCII.GetString(row[..nameLength]);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(12, 4));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(36, 4));
            uint size = Math.Max(virtualSize, rawSize);
            if (size == 0 || virtualAddress >= ImageSize)
                continue;
            size = Math.Min(size, (uint)ImageSize - virtualAddress);
            yield return new PeSection(name, virtualAddress, size, (characteristics & 0x20000000) != 0);
        }
    }

    private readonly record struct PeSection(string Name, uint Rva, uint Size, bool Executable);
    private readonly record struct DirectCallSite(ulong Address, string Section);
    private readonly record struct ImagePointerHit(ulong Address, string Section);
    private readonly record struct PointerEntry(ulong Address);
    private readonly record struct LiveObjectHit(ulong Address, ulong Vtable, uint Field80, uint Field84, uint Field88, string Raw);
}
