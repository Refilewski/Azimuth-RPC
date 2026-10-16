namespace AzimuthRPC;

public delegate bool GuardedMemoryCopy(ulong address, Span<byte> destination);

// Adapter contract only. The native host must supply an SEH-protected copy.
// An invalid raw Marshal.Copy/unsafe pointer could terminate the host process.
public sealed class InProcessMemoryReader(GuardedMemoryCopy guardedCopy) : IMemoryReader
{
    public bool TryRead(ulong address, Span<byte> destination) => address >= 0x10000 && !destination.IsEmpty && guardedCopy(address, destination);
    public void Dispose() { }
}

public static class InProcessIntegration
{
    public const bool NativeBootstrapImplemented = false;
    public static GameSnapshotReader CreateReader(IMemoryReader memory) => new(memory);
}
