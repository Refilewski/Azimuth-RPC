using System.Buffers.Binary;
using System.IO;
namespace AzimuthRPC;

internal sealed class PeIdentity
{
    public ushort Machine { get; init; }
    public uint TimeDateStamp { get; init; }
    public ushort Characteristics { get; init; }
    public ushort OptionalMagic { get; init; }
    public uint AddressOfEntryPoint { get; init; }
    public ulong PreferredImageBase { get; init; }
    public uint SizeOfImage { get; init; }
    public bool IsDll => (Characteristics & 0x2000) != 0;

    public static PeIdentity Fh2XboxOne10064 { get; } = new()
    {
        Machine = 0x8664,
        TimeDateStamp = 0x565501ED,
        Characteristics = 0,
        OptionalMagic = 0x20B,
        AddressOfEntryPoint = 0xF18058,
        PreferredImageBase = 0x140000000,
        SizeOfImage = 0x3A18000,
    };

    public static PeIdentity ReadFromFile(string path)
    {
        using var fs = File.OpenRead(path);
        byte[] header = new byte[Math.Min((int)Math.Min(fs.Length, 8192), 8192)];
        int got = fs.Read(header, 0, header.Length);
        if (got != header.Length)
            Array.Resize(ref header, got);

        if (!TryParse(header, out PeIdentity? identity) || identity is null)
            throw new InvalidDataException("Not a valid PE image.");
        return identity;
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out PeIdentity? identity)
    {
        identity = null;
        if (bytes.Length < 0x100 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]) != 0x5A4D)
            return false;

        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(0x3C, 4));
        if (lfanew < 0x40 || lfanew > 0x1000 || lfanew + 0x80 > bytes.Length)
            return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(lfanew, 4)) != 0x00004550)
            return false;

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(lfanew + 4, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(lfanew + 8, 4));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(lfanew + 22, 2));
        int optional = lfanew + 24;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(optional, 2));
        if (magic is not (0x10B or 0x20B))
            return false;

        uint entryPoint = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(optional + 16, 4));
        ulong preferredImageBase = magic == 0x20B
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(optional + 24, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(optional + 28, 4));
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(optional + 56, 4));

        if (imageSize == 0 || preferredImageBase == 0)
            return false;

        identity = new PeIdentity
        {
            Machine = machine,
            TimeDateStamp = timestamp,
            Characteristics = characteristics,
            OptionalMagic = magic,
            AddressOfEntryPoint = entryPoint,
            PreferredImageBase = preferredImageBase,
            SizeOfImage = imageSize,
        };
        return true;
    }
}
