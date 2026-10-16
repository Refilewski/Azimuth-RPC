using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AzimuthRPC;

internal sealed class ExternalProcessMemoryReader : IMemoryReader, IMemoryRegions
{
    private IntPtr _handle;
    internal ExternalProcessMemoryReader(int processId)
    {
        _handle = OpenProcess(0x410, false, processId);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess read/query failed");
    }
    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (_handle == IntPtr.Zero || address < 0x10000 || address > 0x00007FFFFFFFFFFF || destination.IsEmpty) return false;
        unsafe
        {
            fixed (byte* buffer = destination)
                return ReadProcessMemory(_handle, (IntPtr)(long)address, (IntPtr)buffer, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
        }
    }
    public IEnumerable<MemoryRegion> EnumerateRegions()
    {
        ulong address = 0x10000;
        while (address < 0x00007FFFFFFEFFFF && _handle != IntPtr.Zero)
        {
            // Query failure also means a dead process/invalid handle. Do not
            // walk the entire 47-bit address space page by page after detach.
            if (VirtualQueryEx(_handle, (IntPtr)(long)address, out var m, 48) == 0) yield break;
            ulong start = (ulong)m.BaseAddress.ToInt64();
            ulong next = start + Math.Max(m.RegionSize, 0x1000);
            if (next <= address) yield break;
            uint p = m.Protect & 0xff;
            yield return new(start, (ulong)m.AllocationBase.ToInt64(), m.RegionSize, m.State == 0x1000 && (m.Protect & 0x100) == 0 && p is 2 or 4 or 8 or 0x20 or 0x40 or 0x80, m.Type is 0x20000 or 0x1000000);
            address = next;
        }
    }
    public void Dispose() { var h = Interlocked.Exchange(ref _handle, IntPtr.Zero); if (h != IntPtr.Zero) CloseHandle(h); }
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct Mbi
    {
        [FieldOffset(0)] public IntPtr BaseAddress;
        [FieldOffset(8)] public IntPtr AllocationBase;
        [FieldOffset(16)] public uint AllocationProtect;
        [FieldOffset(24)] public ulong RegionSize;
        [FieldOffset(32)] public uint State;
        [FieldOffset(36)] public uint Protect;
        [FieldOffset(40)] public uint Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, IntPtr buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(IntPtr handle, IntPtr address, out Mbi info, nuint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
