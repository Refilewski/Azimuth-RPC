using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Read-only probe for the experimental Career::LocationTrackerScripted lead.
/// Everything in this file is research-only until live captures prove semantics.
/// No game functions are called and no process memory is modified.
/// </summary>
internal static class NativeLocationResearchScanner
{
    private const ulong LocationTrackerScriptedVtableRva = 0x02C6D5C0;
    private const int CurrentLocationGetterSlotOffset = 0x38;

    // Both live helper bodies at 0x3A91A0 / 0x3A9290 construct the same 11-byte
    // string from this image address before querying the object reached through
    // this+0x20/+0x08 -> +0xD0. Capture it so the next dump tells us the key.
    private const ulong ObservedQueryKeyRva = 0x01E316D8;

    private const int ObjectSampleSize = 0x200;
    private const int VtableSampleSize = 0x80;
    private const int FunctionCodeSampleSize = 0xC0;
    private const int ScanChunkSize = 1024 * 1024;
    private const int MaxCandidates = 64;
    private const int MaxGuidedNodes = 96;

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a native-location research dump");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before native-location capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        ulong wantedVtable = checked(game.GameBase + LocationTrackerScriptedVtableRva);
        ulong imageEnd = checked(game.GameBase + 0x3A18000UL);
        var candidates = new List<object>();
        var seen = new HashSet<ulong>();
        byte[] scanBuffer = new byte[ScanChunkSize];

        foreach (MemoryRegion region in reader.EnumerateRegions())
        {
            token.ThrowIfCancellationRequested();
            if (!region.Readable || !region.ImageOrPrivate || region.Size < 8)
                continue;

            if (region.BaseAddress >= game.GameBase && region.BaseAddress < imageEnd)
                continue;

            for (ulong regionOffset = 0; regionOffset < region.Size && candidates.Count < MaxCandidates;)
            {
                token.ThrowIfCancellationRequested();
                int length = (int)Math.Min((ulong)scanBuffer.Length, region.Size - regionOffset);
                ulong address = region.BaseAddress + regionOffset;
                Span<byte> chunk = scanBuffer.AsSpan(0, length);

                if (reader.TryRead(address, chunk))
                {
                    for (int i = 0; i + 8 <= length && candidates.Count < MaxCandidates; i += 8)
                    {
                        if (BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(i, 8)) != wantedVtable)
                            continue;

                        ulong candidate = address + (ulong)i;
                        if (!seen.Add(candidate))
                            continue;

                        byte[]? raw = reader.Bytes(candidate, ObjectSampleSize);
                        if (raw is null || BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0, 8)) != wantedVtable)
                            continue;

                        candidates.Add(DescribeCandidate(reader, game, candidate, raw));
                    }
                }

                regionOffset += (ulong)length;
            }

            if (candidates.Count >= MaxCandidates)
                break;
        }

        byte[]? vtableBytes = reader.Bytes(wantedVtable, VtableSampleSize);
        object[] vtableEntries = DescribeVtable(game.GameBase, vtableBytes).ToArray();
        object[] vtableFunctions = DescribeVtableFunctions(reader, game.GameBase, vtableBytes).ToArray();

        ulong getter = vtableBytes is not null && CurrentLocationGetterSlotOffset + 8 <= vtableBytes.Length
            ? BinaryPrimitives.ReadUInt64LittleEndian(vtableBytes.AsSpan(CurrentLocationGetterSlotOffset, 8))
            : 0;
        byte[]? getterCode = ReadCode(reader, getter, FunctionCodeSampleSize);
        object[] getterTargets = DescribeRelativeTargets(reader, game.GameBase, getter, getterCode, 0x30).ToArray();

        ulong queryKeyAddress = checked(game.GameBase + ObservedQueryKeyRva);
        byte[]? queryKeyBytes = ReadCode(reader, queryKeyAddress, 0x40);

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Experimental read-only Career::LocationTrackerScripted probe",
            game.Build,
            GameBase = $"0x{game.GameBase:X}",
            VehiclePosition = game.Vehicle.Position,
            PolygonLocation = game.Location,
            Experimental = true,
            LocationTrackerScriptedVtableRva = $"0x{LocationTrackerScriptedVtableRva:X}",
            LocationTrackerScriptedVtable = $"0x{wantedVtable:X}",
            CandidateCurrentLocationGetterSlot = $"+0x{CurrentLocationGetterSlotOffset:X}",
            CandidateCurrentLocationGetter = getter == 0 ? null : $"0x{getter:X}",
            CandidateCurrentLocationGetterRva = Rva(game.GameBase, getter),
            GetterCode = getterCode is null ? null : Convert.ToHexString(getterCode),
            GetterRelativeTargets = getterTargets,
            GetterLikelyRcXFieldLoads = LikelyRcXFieldLoads(getterCode).ToArray(),
            ObservedQueryKeyRva = $"0x{ObservedQueryKeyRva:X}",
            ObservedQueryKeyAddress = $"0x{queryKeyAddress:X}",
            ObservedQueryKeyBytes = queryKeyBytes is null ? null : Convert.ToHexString(queryKeyBytes),
            ObservedQueryKeyStrings = queryKeyBytes is null ? Array.Empty<string>() : Strings(queryKeyBytes, 8).ToArray(),
            VtableEntries = vtableEntries,
            VtableFunctions = vtableFunctions,
            Count = candidates.Count,
            Truncated = candidates.Count >= MaxCandidates,
            Candidates = candidates
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-native-location-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static object DescribeCandidate(ExternalProcessMemoryReader reader, GameSnapshot game, ulong address, byte[] raw)
    {
        var pointerFields = new List<object>();
        for (int offset = 8; offset + 8 <= raw.Length && pointerFields.Count < 24; offset += 8)
        {
            ulong target = U64(raw, offset);
            if (!MemoryRead.Pointer(target))
                continue;

            byte[]? targetBytes = reader.Bytes(target, 0x400);
            if (targetBytes is null)
                continue;

            string[] strings = Strings(targetBytes, 24).ToArray();
            object[] ids = SmallInts(targetBytes, 0, 16, 32).ToArray();
            if (strings.Length == 0 && ids.Length == 0)
                continue;

            pointerFields.Add(new
            {
                Offset = $"0x{offset:X}",
                Target = $"0x{target:X}",
                Strings = strings,
                CandidateSmallInts = ids
            });
        }

        return new
        {
            Address = $"0x{address:X}",
            Raw = Convert.ToHexString(raw),
            CandidateLocationIds3To9 = SmallInts(raw, 3, 9, 64).ToArray(),
            CandidateSmallInts0To16 = SmallInts(raw, 0, 16, 96).ToArray(),
            Strings = Strings(raw, 32).ToArray(),
            PointerFields = pointerFields,
            GuidedQueryChains = DescribeGuidedQueryChains(reader, raw).ToArray(),
            DistanceHint = game.Vehicle.Position
        };
    }

    private static IEnumerable<object> DescribeGuidedQueryChains(ExternalProcessMemoryReader reader, byte[] candidateRaw)
    {
        // Live helpers:
        // 0x3A91A0: [this+0x20] -> [+0xD0] -> query
        // 0x3A9290: [this+0x08] -> [+0xD0] -> query
        foreach (int sourceOffset in new[] { 0x20, 0x08 })
        {
            ulong source = sourceOffset + 8 <= candidateRaw.Length ? U64(candidateRaw, sourceOffset) : 0;
            byte[]? sourceBytes = MemoryRead.Pointer(source) ? reader.Bytes(source, 0x100) : null;
            ulong queryContainer = sourceBytes is not null && sourceBytes.Length >= 0xD8 ? U64(sourceBytes, 0xD0) : 0;
            byte[]? containerBytes = MemoryRead.Pointer(queryContainer) ? reader.Bytes(queryContainer, 0x200) : null;

            yield return new
            {
                SourceOffset = $"0x{sourceOffset:X}",
                Source = source == 0 ? null : $"0x{source:X}",
                SourceRaw100 = sourceBytes is null ? null : Convert.ToHexString(sourceBytes),
                QueryContainerOffset = "+0xD0",
                QueryContainer = queryContainer == 0 ? null : $"0x{queryContainer:X}",
                QueryContainerRaw200 = containerBytes is null ? null : Convert.ToHexString(containerBytes),
                QueryContainerStrings = containerBytes is null ? Array.Empty<string>() : Strings(containerBytes, 24).ToArray(),
                QueryContainerSmallInts = containerBytes is null ? Array.Empty<object>() : SmallInts(containerBytes, 0, 16, 48).ToArray(),
                PointerGraph = MemoryRead.Pointer(queryContainer) ? DescribeGuidedPointerGraph(reader, queryContainer).ToArray() : Array.Empty<object>()
            };
        }
    }

    private static IEnumerable<object> DescribeGuidedPointerGraph(ExternalProcessMemoryReader reader, ulong root)
    {
        var queue = new Queue<(ulong Address, int Depth, string Via)>();
        var seen = new HashSet<ulong>();
        queue.Enqueue((root, 0, "query-container"));
        int emitted = 0;

        while (queue.Count > 0 && emitted < MaxGuidedNodes)
        {
            var item = queue.Dequeue();
            if (!seen.Add(item.Address))
                continue;

            byte[]? bytes = reader.Bytes(item.Address, 0x100);
            if (bytes is null)
                continue;

            uint field14 = U32(bytes, 0x14);
            byte field11 = bytes[0x11];
            byte field24 = bytes[0x24];
            byte field3B = bytes[0x3B];
            string[] strings = Strings(bytes, 12).ToArray();

            var pointers = new List<object>();
            for (int offset = 0; offset + 8 <= bytes.Length; offset += 8)
            {
                ulong target = U64(bytes, offset);
                if (!MemoryRead.Pointer(target))
                    continue;
                if (reader.Bytes(target, 8) is null)
                    continue;

                pointers.Add(new { Offset = $"0x{offset:X}", Target = $"0x{target:X}" });
                if (item.Depth < 2 && !seen.Contains(target) && queue.Count + emitted < MaxGuidedNodes * 2)
                    queue.Enqueue((target, item.Depth + 1, $"0x{item.Address:X}+0x{offset:X}"));
            }

            yield return new
            {
                Address = $"0x{item.Address:X}",
                item.Depth,
                item.Via,
                Field14 = field14,
                Field14LooksLikeRoadTripLocationId = field14 is >= 3 and <= 9,
                Byte11 = field11,
                Byte24 = field24,
                Byte3B = field3B,
                Strings = strings,
                CandidateSmallInts = SmallInts(bytes, 0, 16, 32).ToArray(),
                Pointers = pointers.Take(24).ToArray(),
                Raw100 = Convert.ToHexString(bytes)
            };
            emitted++;
        }
    }

    private static IEnumerable<object> DescribeVtable(ulong gameBase, byte[]? bytes)
    {
        if (bytes is null)
            yield break;

        for (int offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            ulong target = U64(bytes, offset);
            yield return new
            {
                Slot = $"+0x{offset:X}",
                Target = target == 0 ? null : $"0x{target:X}",
                Rva = Rva(gameBase, target)
            };
        }
    }

    private static IEnumerable<object> DescribeVtableFunctions(ExternalProcessMemoryReader reader, ulong gameBase, byte[]? bytes)
    {
        if (bytes is null)
            yield break;

        for (int offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            ulong target = U64(bytes, offset);
            if (target == 0)
                continue;

            byte[]? code = ReadCode(reader, target, FunctionCodeSampleSize);
            yield return new
            {
                Slot = $"+0x{offset:X}",
                Target = $"0x{target:X}",
                Rva = Rva(gameBase, target),
                Code = code is null ? null : Convert.ToHexString(code),
                RelativeTargets = DescribeRelativeTargets(reader, gameBase, target, code, 0x40).ToArray(),
                LikelyRcXFieldLoads = LikelyRcXFieldLoads(code).ToArray()
            };
        }
    }

    private static IEnumerable<object> DescribeRelativeTargets(
        ExternalProcessMemoryReader reader,
        ulong gameBase,
        ulong function,
        byte[]? code,
        int searchBytes)
    {
        if (code is null)
            yield break;

        int end = Math.Min(code.Length - 5, searchBytes);
        var seen = new HashSet<ulong>();
        for (int i = 0; i <= end; i++)
        {
            byte opcode = code[i];
            if (opcode is not (0xE8 or 0xE9))
                continue;

            int relative = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 1, 4));
            ulong next = function + (ulong)i + 5;
            long signedTarget = checked((long)next + relative);
            if (signedTarget <= 0)
                continue;

            ulong target = (ulong)signedTarget;
            if (!seen.Add(target))
                continue;

            byte[]? targetCode = ReadCode(reader, target, FunctionCodeSampleSize);
            yield return new
            {
                Offset = $"+0x{i:X}",
                Kind = opcode == 0xE8 ? "call rel32" : "jmp rel32",
                Target = $"0x{target:X}",
                Rva = Rva(gameBase, target),
                Code = targetCode is null ? null : Convert.ToHexString(targetCode),
                LikelyRcXFieldLoads = LikelyRcXFieldLoads(targetCode).ToArray()
            };
        }
    }

    private static IEnumerable<object> LikelyRcXFieldLoads(byte[]? code)
    {
        if (code is null)
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < code.Length; i++)
        {
            string? kind = null;
            int field = 0;
            int length = 0;

            if (i + 3 <= code.Length && code[i] == 0x8B && code[i + 1] == 0x41)
            {
                kind = "mov eax,[rcx+imm8]"; field = unchecked((sbyte)code[i + 2]); length = 3;
            }
            else if (i + 6 <= code.Length && code[i] == 0x8B && code[i + 1] == 0x81)
            {
                kind = "mov eax,[rcx+imm32]"; field = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 2, 4)); length = 6;
            }
            else if (i + 4 <= code.Length && code[i] == 0x48 && code[i + 1] == 0x8B && code[i + 2] == 0x41)
            {
                kind = "mov rax,[rcx+imm8]"; field = unchecked((sbyte)code[i + 3]); length = 4;
            }
            else if (i + 7 <= code.Length && code[i] == 0x48 && code[i + 1] == 0x8B && code[i + 2] == 0x81)
            {
                kind = "mov rax,[rcx+imm32]"; field = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(i + 3, 4)); length = 7;
            }
            else if (i + 4 <= code.Length && code[i] == 0x0F && code[i + 1] is 0xB6 or 0xB7 && code[i + 2] == 0x41)
            {
                kind = code[i + 1] == 0xB6 ? "movzx eax,byte[rcx+imm8]" : "movzx eax,word[rcx+imm8]";
                field = unchecked((sbyte)code[i + 3]); length = 4;
            }

            if (kind is null)
                continue;

            string key = $"{i:X}:{kind}:{field:X}";
            if (seen.Add(key))
                yield return new { CodeOffset = $"+0x{i:X}", Kind = kind, FieldOffset = $"0x{field:X}", InstructionBytes = Convert.ToHexString(code.AsSpan(i, length)) };
        }
    }

    private static byte[]? ReadCode(IMemoryReader reader, ulong address, int size)
    {
        if (address < 0x10000 || address > 0x00007FFFFFFFFFFF || size <= 0)
            return null;
        var bytes = new byte[size];
        return reader.TryRead(address, bytes) ? bytes : null;
    }

    private static string? Rva(ulong gameBase, ulong address)
    {
        ulong imageEnd = checked(gameBase + 0x3A18000UL);
        return address >= gameBase && address < imageEnd ? $"0x{address - gameBase:X}" : null;
    }

    private static IEnumerable<object> SmallInts(byte[] raw, int min, int max, int limit)
    {
        int count = 0;
        for (int offset = 0; offset + 4 <= raw.Length; offset += 4)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset, 4));
            if (value < min || value > max)
                continue;

            yield return new { Offset = $"0x{offset:X}", Value = value };
            if (++count >= limit)
                yield break;
        }
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

    private static ulong U64(byte[] raw, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset, 8));
    private static uint U32(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));
}
