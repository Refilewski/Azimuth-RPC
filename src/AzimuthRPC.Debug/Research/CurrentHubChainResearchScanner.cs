using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC;

/// <summary>
/// Read-only live probe for the CForza -> CPlayerManager -> X -> obj -> Y -> Z chain
/// discovered while researching GetCurrentHubLocationId (RVA 0x3B5DF0).
/// It never executes game code. Instead it resolves live vtables and recognizes only
/// trivial virtual getters that can be reproduced as ordinary memory reads.
/// </summary>
internal static class CurrentHubChainResearchScanner
{
    private const ulong CPlayerManagerGlobalRva = 0x034B3E18;
    private const ulong CurrentHubGetterRva = 0x003B5DF0;
    private const int ImageSize = 0x03A18000;

    internal static Task<string> CaptureAsync(DebugStatus status, CancellationToken token) =>
        Task.Run(() => Capture(status, token), token);

    private static string Capture(DebugStatus status, CancellationToken token)
    {
        GameSnapshot game = status.Game;
        if (!game.Attached || status.ProcessId is not int pid)
            throw new InvalidOperationException("Attach FH2 before taking a current-hub chain probe");

        using var process = Process.GetProcessById(pid);
        if (process.StartTime.ToUniversalTime() != status.ProcessStartUtc)
            throw new InvalidOperationException("Process changed before current-hub chain capture");

        using var reader = new ExternalProcessMemoryReader(pid);
        token.ThrowIfCancellationRequested();

        ulong cforza = game.Roots.PlayerManager;
        ulong cpmFromCForza = ReadU64(reader, cforza + 0x448);
        ulong cpmFromGlobal = ReadU64(reader, game.GameBase + CPlayerManagerGlobalRva);

        uint cpmState34 = ReadU32(reader, cpmFromCForza + 0x34);
        ulong cpmArrayHolder = ReadU64(reader, cpmFromCForza + 0x38);
        ulong cpmArrayBase = ReadU64(reader, cpmArrayHolder);
        uint cpmCount = ReadU32(reader, cpmFromCForza + 0x40);
        ulong x = ReadU64(reader, cpmArrayBase + 0x10);

        ObjectReport? xReport = DescribeObject(reader, game.GameBase, x, 0xC8, token);
        GetterResolution? xC8 = xReport is null ? null : TryResolvePointerGetter(reader, x, xReport.SelectedVirtualTarget);

        ulong obj = xC8?.ResolvedValue ?? 0;
        ObjectReport? objReport = DescribeObject(reader, game.GameBase, obj, null, token);

        ulong objD8 = ReadU64(reader, obj + 0xD8);
        ulong y = ReadU64(reader, objD8);
        ObjectReport? yReport = DescribeObject(reader, game.GameBase, y, null, token);

        ulong z = ReadU64(reader, y + 0x18);
        ObjectReport? zCurrent = DescribeObject(reader, game.GameBase, z, 0x38, token);
        ulong zDestinationTarget = ReadVirtualTarget(reader, z, 0x40);
        GetterResolution? currentIdGetter = zCurrent is null ? null : TryResolveIntegerGetter(reader, z, zCurrent.SelectedVirtualTarget);
        GetterResolution? destinationIdGetter = zDestinationTarget == 0 ? null : TryResolveIntegerGetter(reader, z, zDestinationTarget);

        byte[] currentHubFunction = ReadBytes(reader, game.GameBase + CurrentHubGetterRva, 0x80);

        var dump = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Version = VersionInfo.Version,
            Purpose = "Read-only CForza/CPlayerManager/current-hub chain resolution",
            game.Build,
            GameBase = Hex(game.GameBase),
            VehiclePosition = game.Vehicle.Position,
            PolygonLocation = game.Location,
            ExpectedRoadTripIdFromStaticMap = StaticExpectedId(game.Location),
            Experimental = true,
            Warning = "Current-hub semantics are not assumed to equal physical player region. Only live boundary tests can establish that.",
            KnownStatic = new
            {
                CurrentHubGetterRva = "0x3B5DF0",
                CPlayerManagerGlobalRva = "0x34B3E18",
                Chain = "CForza+0x448 -> CPlayerManager; [[[CPM+0x38]]+0x10] -> X; X.vfunc+0xC8 -> obj; **(obj+0xD8) -> Y; [Y+0x18] -> Z; Z.vfunc+0x38 -> current id"
            },
            Roots = new
            {
                CForza = Hex(cforza),
                CPlayerManagerFromCForza448 = Hex(cpmFromCForza),
                CPlayerManagerFromGlobal = Hex(cpmFromGlobal),
                SameCPlayerManager = cpmFromCForza != 0 && cpmFromCForza == cpmFromGlobal,
                CPlayerManagerField34 = cpmState34,
                CPlayerManagerArrayHolder = Hex(cpmArrayHolder),
                CPlayerManagerArrayBase = Hex(cpmArrayBase),
                CPlayerManagerCount = cpmCount,
                X = Hex(x)
            },
            X = xReport,
            XC8Resolution = xC8,
            Obj = new
            {
                Address = Hex(obj),
                Descriptor = objReport,
                PlusD8 = Hex(objD8)
            },
            Y = new
            {
                Address = Hex(y),
                Descriptor = yReport,
                Plus18_Z = Hex(z)
            },
            Z = new
            {
                Address = Hex(z),
                CurrentSlot38 = zCurrent,
                CurrentIdResolution = currentIdGetter,
                DestinationSlot40Target = Hex(zDestinationTarget),
                DestinationSlot40Bytes = HexBytes(ReadBytes(reader, zDestinationTarget, 0x40)),
                DestinationIdResolution = destinationIdGetter
            },
            CurrentHubGetter = new
            {
                Address = Hex(game.GameBase + CurrentHubGetterRva),
                Bytes = HexBytes(currentHubFunction)
            }
        };

        string directory = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"fh2-current-hub-chain-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, MemoryGraphDumper.Json));
        return path;
    }

    private static ObjectReport? DescribeObject(ExternalProcessMemoryReader reader, ulong gameBase, ulong address, int? selectedSlot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPointer(address))
            return null;

        ulong vtable = ReadU64(reader, address);
        ulong target = selectedSlot.HasValue ? ReadU64(reader, vtable + (ulong)selectedSlot.Value) : 0;
        byte[] raw = ReadBytes(reader, address, 0x100);
        byte[] targetBytes = ReadBytes(reader, target, 0x80);
        RttiReport? rtti = TryReadMsvcRtti(reader, gameBase, vtable);

        return new ObjectReport(
            Hex(address),
            Hex(vtable),
            rtti,
            selectedSlot.HasValue ? $"0x{selectedSlot.Value:X}" : null,
            Hex(target),
            target,
            HexBytes(targetBytes),
            HexBytes(raw));
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
        string? rawName = ReadAscii(reader, typeDescriptor + 0x10, 192);
        return new RttiReport(Hex(col), signature, Hex(typeDescriptor), rawName);
    }

    private static GetterResolution? TryResolvePointerGetter(ExternalProcessMemoryReader reader, ulong thisPtr, ulong function)
    {
        if (!ValidPointer(thisPtr) || !ValidPointer(function))
            return null;
        byte[] code = ReadBytes(reader, function, 16);
        if (code.Length < 5)
            return null;

        if (code.Length >= 8 && code[0] == 0x48 && code[1] == 0x8B && code[2] == 0x81 && code[7] == 0xC3)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3, 4));
            ulong value = ReadU64(reader, AddSigned(thisPtr, offset));
            return new GetterResolution("mov rax,[rcx+imm32]", offset, Hex(value), value, null);
        }
        if (code.Length >= 8 && code[0] == 0x48 && code[1] == 0x8D && code[2] == 0x81 && code[7] == 0xC3)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3, 4));
            ulong value = AddSigned(thisPtr, offset);
            return new GetterResolution("lea rax,[rcx+imm32]", offset, Hex(value), value, null);
        }
        if (code[0] == 0x48 && code[1] == 0x8B && code[2] == 0x41 && code[4] == 0xC3)
        {
            int offset = unchecked((sbyte)code[3]);
            ulong value = ReadU64(reader, AddSigned(thisPtr, offset));
            return new GetterResolution("mov rax,[rcx+imm8]", offset, Hex(value), value, null);
        }
        if (code[0] == 0x48 && code[1] == 0x8D && code[2] == 0x41 && code[4] == 0xC3)
        {
            int offset = unchecked((sbyte)code[3]);
            ulong value = AddSigned(thisPtr, offset);
            return new GetterResolution("lea rax,[rcx+imm8]", offset, Hex(value), value, null);
        }
        return new GetterResolution("unresolved", null, null, 0, HexBytes(code));
    }

    private static GetterResolution? TryResolveIntegerGetter(ExternalProcessMemoryReader reader, ulong thisPtr, ulong function)
    {
        if (!ValidPointer(thisPtr) || !ValidPointer(function))
            return null;
        byte[] code = ReadBytes(reader, function, 16);
        if (code.Length < 4)
            return null;

        if (code.Length >= 7 && code[0] == 0x8B && code[1] == 0x81 && code[6] == 0xC3)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(2, 4));
            uint value = ReadU32(reader, AddSigned(thisPtr, offset));
            return new GetterResolution("mov eax,[rcx+imm32]", offset, value.ToString(), value, null);
        }
        if (code[0] == 0x8B && code[1] == 0x41 && code[3] == 0xC3)
        {
            int offset = unchecked((sbyte)code[2]);
            uint value = ReadU32(reader, AddSigned(thisPtr, offset));
            return new GetterResolution("mov eax,[rcx+imm8]", offset, value.ToString(), value, null);
        }
        if (code.Length >= 8 && code[0] == 0x0F && code[1] == 0xB6 && code[2] == 0x81 && code[7] == 0xC3)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3, 4));
            byte value = ReadU8(reader, AddSigned(thisPtr, offset));
            return new GetterResolution("movzx eax,byte [rcx+imm32]", offset, value.ToString(), value, null);
        }
        return new GetterResolution("unresolved", null, null, 0, HexBytes(code));
    }

    private static ulong ReadVirtualTarget(ExternalProcessMemoryReader reader, ulong objectAddress, int slotOffset)
    {
        ulong vt = ReadU64(reader, objectAddress);
        return ReadU64(reader, vt + (ulong)slotOffset);
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
        return ValidPointer(address) && reader.TryRead(address, b) ? BinaryPrimitives.ReadUInt64LittleEndian(b) : 0;
    }

    private static uint ReadU32(ExternalProcessMemoryReader reader, ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        return ValidPointer(address) && reader.TryRead(address, b) ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;
    }

    private static byte ReadU8(ExternalProcessMemoryReader reader, ulong address)
    {
        Span<byte> b = stackalloc byte[1];
        return ValidPointer(address) && reader.TryRead(address, b) ? b[0] : (byte)0;
    }

    private static string? ReadAscii(ExternalProcessMemoryReader reader, ulong address, int max)
    {
        byte[] bytes = ReadBytes(reader, address, max);
        if (bytes.Length == 0)
            return null;
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        if (end == 0) return string.Empty;
        return Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static ulong AddSigned(ulong address, int offset) =>
        offset >= 0 ? address + (uint)offset : address - (uint)(-offset);

    private static bool ValidPointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFFFFF;
    private static string Hex(ulong value) => value == 0 ? "0x0" : $"0x{value:X}";
    private static string HexBytes(byte[] bytes) => bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);

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

    private sealed record ObjectReport(
        string Address,
        string Vtable,
        RttiReport? Rtti,
        string? SelectedVirtualSlot,
        string SelectedVirtualTargetHex,
        ulong SelectedVirtualTarget,
        string SelectedVirtualTargetBytes,
        string RawObjectBytes);

    private sealed record RttiReport(string CompleteObjectLocator, uint Signature, string? TypeDescriptor, string? RawTypeName);
    private sealed record GetterResolution(string Kind, int? Offset, string? DisplayValue, ulong ResolvedValue, string? FunctionBytes);
}
