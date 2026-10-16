using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Read-only FH2 image scanner for location-related strings and x64
/// RIP-relative code references. This is research tooling only: a string/xref
/// is a lead, not proof that a function reports the player's physical region.
/// </summary>
internal static class LocationStringXrefScanner
{
    private const int ImageSize = 0x3A18000;
    private const int HeaderReadSize = 0x4000;
    private const int ReadChunkSize = 1024 * 1024;
    private const int CodeWindowRadius = 0x50;
    private const int NearbyBranchRadius = 0x60;
    private const uint ImageScnMemExecute = 0x20000000;

    private static readonly string[] Targets =
    [
        "CurrentRoadTripLocation",
        "MapLocationDataSource",
        "MapDestinationDataSource",
        "MapDestinationOnlineRoadTripDataSource",
        "DestinationName",
        "RoadTripLocations",
        "SELECT Name FROM RoadTripLocations WHERE Id=",
        "NiceMassena",
        "Nice",
        "Sisteron",
        "SaintMartin",
        "Castelletto",
        "Montellino",
        "SanGimignano",
        "San Giovanni",
        "destination_hub",
    ];

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a location-xref research dump");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before location-xref capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        byte[]? header = reader.Bytes(game.GameBase, HeaderReadSize);
        if (header is null)
            throw new InvalidOperationException("Could not read FH2 PE headers");

        PeSection[] sections = ParseSections(header).ToArray();
        if (sections.Length == 0)
            throw new InvalidOperationException("Could not parse FH2 PE sections");

        byte[] image = ReadImage(reader, game.GameBase, token);
        token.ThrowIfCancellationRequested();

        List<StringHit> strings = FindStrings(image, game.GameBase).ToList();
        List<XrefHit> xrefs = FindXrefs(image, game.GameBase, sections, strings, token).ToList();

        var grouped = Targets.Select(target => new
        {
            String = target,
            Occurrences = strings
                .Where(hit => hit.Name == target)
                .Select(hit => new
                {
                    hit.Encoding,
                    Address = $"0x{hit.Address:X}",
                    Rva = $"0x{hit.Address - game.GameBase:X}",
                    hit.ByteLength,
                    Xrefs = xrefs
                        .Where(xref => xref.StringAddress == hit.Address)
                        .Select(xref => new
                        {
                            Address = $"0x{xref.Address:X}",
                            Rva = $"0x{xref.Address - game.GameBase:X}",
                            xref.Kind,
                            xref.Register,
                            Target = $"0x{xref.Target:X}",
                            TargetRva = InImage(game.GameBase, xref.Target) ? $"0x{xref.Target - game.GameBase:X}" : null,
                            xref.TargetDelta,
                            xref.InstructionBytes,
                            xref.Section,
                            HeuristicFunctionStart = xref.FunctionStart == 0 ? null : $"0x{xref.FunctionStart:X}",
                            HeuristicFunctionStartRva = xref.FunctionStart == 0 ? null : $"0x{xref.FunctionStart - game.GameBase:X}",
                            xref.CodeWindow,
                            xref.NearbyBranches,
                        })
                        .ToArray(),
                })
                .ToArray(),
        }).ToArray();

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only FH2 location-related string/XREF research",
            game.Build,
            GameBase = $"0x{game.GameBase:X}",
            VehiclePosition = game.Vehicle.Position,
            PolygonLocation = game.Location,
            Experimental = true,
            Warning = "String references are research leads only; do not promote them to runtime physical-location semantics without live validation.",
            Sections = sections.Select(section => new
            {
                section.Name,
                Rva = $"0x{section.Rva:X}",
                Size = $"0x{section.Size:X}",
                Characteristics = $"0x{section.Characteristics:X8}",
                section.Executable,
            }).ToArray(),
            TargetCount = Targets.Length,
            StringOccurrenceCount = strings.Count,
            XrefCount = xrefs.Count,
            Targets = grouped,
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-location-xrefs-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static byte[] ReadImage(ExternalProcessMemoryReader reader, ulong gameBase, CancellationToken token)
    {
        byte[] image = new byte[ImageSize];
        ulong imageEnd = checked(gameBase + (ulong)ImageSize);

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
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min((ulong)ReadChunkSize, end - address);
                int destinationOffset = checked((int)(address - gameBase));
                Span<byte> destination = image.AsSpan(destinationOffset, count);
                _ = reader.TryRead(address, destination);
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
            if (nameLength < 0) nameLength = 8;
            string name = Encoding.ASCII.GetString(row[..nameLength]);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(12, 4));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(36, 4));
            uint size = Math.Max(virtualSize, rawSize);
            if (size == 0 || virtualAddress >= ImageSize)
                continue;
            size = Math.Min(size, (uint)ImageSize - virtualAddress);

            yield return new PeSection(name, virtualAddress, size, characteristics, (characteristics & ImageScnMemExecute) != 0);
        }
    }

    private static IEnumerable<StringHit> FindStrings(byte[] image, ulong gameBase)
    {
        foreach (string target in Targets)
        {
            foreach ((string encoding, byte[] needle) in Encodings(target))
            {
                int start = 0;
                while (start <= image.Length - needle.Length)
                {
                    int index = image.AsSpan(start).IndexOf(needle);
                    if (index < 0)
                        break;
                    index += start;
                    yield return new StringHit(target, encoding, gameBase + (ulong)index, needle.Length);
                    start = index + 1;
                }
            }
        }
    }

    private static IEnumerable<(string Encoding, byte[] Bytes)> Encodings(string value)
    {
        yield return ("ASCII", Encoding.ASCII.GetBytes(value + "\0"));
        yield return ("UTF16", Encoding.Unicode.GetBytes(value + "\0"));
    }

    private static IEnumerable<XrefHit> FindXrefs(
        byte[] image,
        ulong gameBase,
        PeSection[] sections,
        List<StringHit> strings,
        CancellationToken token)
    {
        foreach (PeSection section in sections.Where(section => section.Executable))
        {
            token.ThrowIfCancellationRequested();
            int start = checked((int)section.Rva);
            int end = checked(start + (int)section.Size);
            end = Math.Min(end, image.Length);

            for (int offset = start; offset + 6 <= end; offset++)
            {
                if ((offset & 0xFFFFF) == 0)
                    token.ThrowIfCancellationRequested();

                if (!TryDecodeRipRelative(image, offset, end, out DecodedRipRelative decoded))
                    continue;

                ulong instructionAddress = gameBase + (ulong)offset;
                ulong next = instructionAddress + (ulong)decoded.Length;
                long signedTarget = checked((long)next + decoded.Displacement);
                if (signedTarget <= 0)
                    continue;
                ulong target = (ulong)signedTarget;

                foreach (StringHit text in strings)
                {
                    ulong textEnd = text.Address + (ulong)text.ByteLength;
                    if (target < text.Address || target >= textEnd)
                        continue;

                    int windowStart = Math.Max(start, offset - CodeWindowRadius);
                    int windowEnd = Math.Min(end, offset + decoded.Length + CodeWindowRadius);
                    string codeWindow = Convert.ToHexString(image.AsSpan(windowStart, windowEnd - windowStart));
                    ulong functionStart = FindHeuristicFunctionStart(image, gameBase, start, offset);
                    object[] nearby = FindNearbyBranches(image, gameBase, start, end, offset, decoded.Length).ToArray();

                    yield return new XrefHit(
                        text.Address,
                        instructionAddress,
                        target,
                        checked((int)(target - text.Address)),
                        decoded.Kind,
                        decoded.Register,
                        Convert.ToHexString(image.AsSpan(offset, decoded.Length)),
                        section.Name,
                        functionStart,
                        codeWindow,
                        nearby);
                }
            }
        }
    }

    private static bool TryDecodeRipRelative(byte[] image, int offset, int end, out DecodedRipRelative decoded)
    {
        decoded = default;
        if (offset >= end)
            return false;

        int cursor = offset;
        byte rex = 0;
        if (image[cursor] is >= 0x40 and <= 0x4F)
        {
            rex = image[cursor++];
            if (cursor >= end)
                return false;
        }

        byte opcode = image[cursor++];
        if (opcode is not (0x8D or 0x8B or 0x89) || cursor + 5 > end)
            return false;

        byte modrm = image[cursor++];
        if ((modrm & 0xC7) != 0x05)
            return false; // mod=00, r/m=101 => RIP+disp32

        int displacement = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(cursor, 4));
        cursor += 4;
        int registerIndex = ((modrm >> 3) & 7) | ((rex & 0x04) != 0 ? 8 : 0);
        bool wide = (rex & 0x08) != 0;
        string register = RegisterName(registerIndex, wide);
        string kind = opcode switch
        {
            0x8D => "lea-rip",
            0x8B => "mov-load-rip",
            0x89 => "mov-store-rip",
            _ => "rip-relative",
        };

        decoded = new DecodedRipRelative(cursor - offset, displacement, kind, register);
        return true;
    }

    private static string RegisterName(int index, bool wide)
    {
        string[] r64 = ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"];
        string[] r32 = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi", "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"];
        if ((uint)index >= 16)
            return $"reg{index}";
        return wide ? r64[index] : r32[index];
    }

    private static ulong FindHeuristicFunctionStart(byte[] image, ulong gameBase, int sectionStart, int instructionOffset)
    {
        int minimum = Math.Max(sectionStart, instructionOffset - 0x180);
        for (int i = instructionOffset - 1; i >= minimum + 3; i--)
        {
            if (image[i] != 0xCC || image[i - 1] != 0xCC || image[i - 2] != 0xCC || image[i - 3] != 0xCC)
                continue;

            int candidate = i + 1;
            while (candidate < instructionOffset && image[candidate] == 0xCC)
                candidate++;
            return candidate < instructionOffset ? gameBase + (ulong)candidate : 0;
        }
        return 0;
    }

    private static IEnumerable<object> FindNearbyBranches(
        byte[] image,
        ulong gameBase,
        int sectionStart,
        int sectionEnd,
        int instructionOffset,
        int instructionLength)
    {
        int from = Math.Max(sectionStart, instructionOffset - NearbyBranchRadius);
        int to = Math.Min(sectionEnd - 5, instructionOffset + instructionLength + NearbyBranchRadius);
        int emitted = 0;

        for (int i = from; i <= to && emitted < 16; i++)
        {
            byte opcode = image[i];
            if (opcode is not (0xE8 or 0xE9))
                continue;

            int relative = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(i + 1, 4));
            ulong next = gameBase + (ulong)i + 5;
            long signedTarget = checked((long)next + relative);
            if (signedTarget <= 0)
                continue;

            ulong target = (ulong)signedTarget;
            yield return new
            {
                OffsetFromXref = i - instructionOffset,
                Kind = opcode == 0xE8 ? "call rel32" : "jmp rel32",
                Address = $"0x{gameBase + (ulong)i:X}",
                Rva = $"0x{i:X}",
                Target = $"0x{target:X}",
                TargetRva = InImage(gameBase, target) ? $"0x{target - gameBase:X}" : null,
            };
            emitted++;
        }
    }

    private static bool InImage(ulong gameBase, ulong address) =>
        address >= gameBase && address < gameBase + ImageSize;

    private readonly record struct PeSection(string Name, uint Rva, uint Size, uint Characteristics, bool Executable);
    private readonly record struct StringHit(string Name, string Encoding, ulong Address, int ByteLength);
    private readonly record struct DecodedRipRelative(int Length, int Displacement, string Kind, string Register);
    private readonly record struct XrefHit(
        ulong StringAddress,
        ulong Address,
        ulong Target,
        int TargetDelta,
        string Kind,
        string Register,
        string InstructionBytes,
        string Section,
        ulong FunctionStart,
        string CodeWindow,
        object[] NearbyBranches);
}
