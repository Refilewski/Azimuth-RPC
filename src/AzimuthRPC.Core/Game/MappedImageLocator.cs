namespace AzimuthRPC;

internal static class MappedImageLocator
{
    internal static bool Matches(IMemoryReader memory, ulong address, PeIdentity wanted)
    {
        var raw = memory.Bytes(address, 4096);
        return raw is not null && PeIdentity.TryParse(raw, out var pe) && pe is not null &&
            pe.Machine == wanted.Machine && pe.TimeDateStamp == wanted.TimeDateStamp && pe.SizeOfImage == wanted.SizeOfImage &&
            pe.OptionalMagic == wanted.OptionalMagic && pe.AddressOfEntryPoint == wanted.AddressOfEntryPoint && pe.PreferredImageBase == wanted.PreferredImageBase && !pe.IsDll;
    }
    internal static ulong Find(IMemoryReader memory, IMemoryRegions regions, PeIdentity wanted)
    {
        if (MemoryRead.Pointer(wanted.PreferredImageBase) && Matches(memory, wanted.PreferredImageBase, wanted)) return wanted.PreferredImageBase;
        foreach (var region in regions.EnumerateRegions())
            if (region.BaseAddress == region.AllocationBase && region.Readable && region.ImageOrPrivate && region.Size >= 64 && Matches(memory, region.BaseAddress, wanted)) return region.BaseAddress;
        return 0;
    }
    internal static bool Supported(PeIdentity pe) => pe.Machine == 0x8664 && pe.OptionalMagic == 0x20b && pe.TimeDateStamp == 0x565501ED && pe.SizeOfImage == 0x3A18000 && pe.AddressOfEntryPoint == 0xF18058 && !pe.IsDll;
}
