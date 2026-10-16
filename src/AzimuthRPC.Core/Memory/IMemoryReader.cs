using System.Buffers.Binary;

namespace AzimuthRPC;

public interface IMemoryReader : IDisposable
{
    bool TryRead(ulong address, Span<byte> destination);
}

public readonly record struct MemoryRegion(ulong BaseAddress, ulong AllocationBase, ulong Size, bool Readable, bool ImageOrPrivate);
public interface IMemoryRegions { IEnumerable<MemoryRegion> EnumerateRegions(); }

internal static class MemoryRead
{
    internal static byte[]? Bytes(this IMemoryReader reader, ulong address, int size)
    {
        if (!Pointer(address) || size <= 0 || size > 16 * 1024 * 1024) return null;
        var data = new byte[size];
        return reader.TryRead(address, data) ? data : null;
    }
    internal static ulong PointerAt(this IMemoryReader reader, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        return Pointer(address) && reader.TryRead(address, bytes) ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
    }
    internal static bool Pointer(ulong address) => address >= 0x10000 && address <= 0x00007FFFFFFFE000 && address % 8 == 0;
}

// Lifetime is one polling attempt only. Reuses root/pointer reads, not object
// contents. Every cached pointer is reread from the actual reader before publish.
internal sealed class PointerSnapshot(IMemoryReader memory)
{
    private readonly Dictionary<ulong, byte[]> _pointers = [];
    internal byte[]? Read(ulong address, int size)
    {
        if (size == 8 && _pointers.TryGetValue(address, out var cached)) return cached;
        var bytes = memory.Bytes(address, size);
        if (size == 8 && bytes is not null) _pointers[address] = bytes;
        return bytes;
    }
    internal bool Validate()
    {
        Span<byte> current = stackalloc byte[8];
        foreach (var (address, old) in _pointers)
            if (!memory.TryRead(address, current) || !current.SequenceEqual(old)) return false;
        return true;
    }
}
