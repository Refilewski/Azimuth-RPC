using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzimuthRPC;

internal sealed class Fh2TelemetryHook : ITelemetrySource
{
    // Confirmed live on FH2 1.0.0.64. This thunk receives a CarIn* in R8 every
    // frame while the game is producing controller feedback. CarIn+0x00 is
    // normalized engine revs (not physical RPM), +0x04 is gearRpm.
    private const ulong RumbleThunkRva = 0x010768B0;

    // ExtraOptions independently identified this vtable slot as the event-context
    // Enter callback. On the stock FH2 1.0.0.64 image the slot points to
    // GameBase+0x912860. The callback's RCX is the context object and +0x60 is
    // the event_context integer; event_context == 2 is Free Roam.
    private const ulong EventContextVtableSlotRva = 0x02D1EA50;
    private const ulong StockEventContextEnterRva = 0x00912860;
    private const int EventTrampolineOffset = 0x200;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;

    private static readonly byte[] ExpectedThunkPrefix =
    {
        0x0F, 0xB6, 0x41, 0x06,             // movzx eax, byte ptr [rcx+6]
        0x2C, 0x02,                         // sub al, 2
        0x3C, 0x01,                         // cmp al, 1
        0x41, 0x0F, 0x96, 0xC1,             // setbe r9b
        0x48, 0x81, 0xC1, 0xE0, 0x02, 0x00, 0x00, // add rcx, 0x2E0
        0xE9                                // jmp rel32
    };

    private readonly Process _process;
    private readonly ulong _gameBase;
    private IntPtr _handle;
    private IntPtr _codeCave;
    private IntPtr _telemetryBuffer;
    private ulong _hookAddress;
    private ulong _eventSlotAddress;
    private ulong _eventTrampolineAddress;
    private ulong _originalEventHandler;
    private readonly byte[] _original12 = new byte[12];
    private uint _lastSequence;
    private DateTime _lastActivityUtc = DateTime.MinValue;
    private bool _eventHookInstalled;
    private bool _disposed;
    private bool _published;
    private readonly byte[] _firstSample = new byte[24];
    private readonly byte[] _secondSample = new byte[24];

    public bool Installed { get; private set; }
    public bool EventHookInstalled => _eventHookInstalled;
    public string Status { get; private set; } = "Not installed";
    public bool RequiresAdmin { get; private set; }

    public Fh2TelemetryHook(Process process, ulong gameBase)
    {
        _process = process;
        _gameBase = gameBase;
    }

    public TelemetryHookInstallResult TryInstall()
    {
        if (_disposed)
            return TelemetryHookInstallResult.Fail("Telemetry hook object is disposed.");
        if (Installed)
            return TelemetryHookInstallResult.Ok(Status);
        if (!OperatingSystem.IsWindows())
            return TelemetryHookInstallResult.Fail("Telemetry hook only works on Windows.");

        _handle = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessSuspendResume, false, _process.Id);
        if (_handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            RequiresAdmin = error == 5;
            Status = $"Telemetry hook: OpenProcess failed (Win32 {error})";
            return TelemetryHookInstallResult.Fail(Status, RequiresAdmin);
        }

        _hookAddress = _gameBase + RumbleThunkRva;
        byte[] current = new byte[24];
        if (!ReadProcessMemory(_handle, ToIntPtr(_hookAddress), current, (nuint)current.Length, out nuint read) || read != (nuint)current.Length)
        {
            int error = Marshal.GetLastWin32Error();
            Status = $"RPM hook: could not read feedback thunk (Win32 {error})";
            return TelemetryHookInstallResult.Fail(Status);
        }

        bool adopted = false;
        if (!current.AsSpan(0, ExpectedThunkPrefix.Length).SequenceEqual(ExpectedThunkPrefix))
        {
            // If a previous AzimuthRPC instance exited unexpectedly, its remote
            // trampoline remains valid inside Azimuth. Recognize our own stub and
            // reattach instead of forcing a game restart.
            adopted = TryAdoptExistingHook(current);
            if (!adopted)
            {
                bool looksDetoured = current.Length >= 12 && current[0] == 0x48 && current[1] == 0xB8 && current[10] == 0xFF && current[11] == 0xE0;
                Status = looksDetoured
                    ? "RPM hook unavailable: feedback thunk is already detoured by another mod/debugger"
                    : "RPM hook unavailable: FH2 feedback thunk bytes do not match this build";
                return TelemetryHookInstallResult.Fail(Status);
            }
        }

        if (!adopted)
        {
            Array.Copy(current, 0, _original12, 0, _original12.Length);

            _telemetryBuffer = VirtualAllocEx(_handle, IntPtr.Zero, 0x1000, MemCommit | MemReserve, PageReadWrite);
            if (_telemetryBuffer == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Status = $"RPM hook: telemetry allocation failed (Win32 {error})";
                return TelemetryHookInstallResult.Fail(Status);
            }

            _codeCave = VirtualAllocEx(_handle, IntPtr.Zero, 0x1000, MemCommit | MemReserve, PageExecuteReadWrite);
            if (_codeCave == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Status = $"RPM hook: code allocation failed (Win32 {error})";
                return TelemetryHookInstallResult.Fail(Status);
            }

            byte[] trampoline = X64TelemetryStubs.BuildRpmTrampoline((ulong)_telemetryBuffer.ToInt64(), _hookAddress + 12);
            if (!WriteProcessMemory(_handle, _codeCave, trampoline, (nuint)trampoline.Length, out nuint written) || written != (nuint)trampoline.Length)
            {
                int error = Marshal.GetLastWin32Error();
                Status = $"RPM hook: trampoline write failed (Win32 {error})";
                return TelemetryHookInstallResult.Fail(Status);
            }

            byte[] patch = X64TelemetryStubs.BuildAbsoluteJump((ulong)_codeCave.ToInt64());
            _ = FlushInstructionCache(_handle, _codeCave, (nuint)trampoline.Length);
            int suspendStatus = NtSuspendProcess(_handle);
            if (suspendStatus < 0)
            {
                Status = $"RPM hook: could not briefly suspend Azimuth (NTSTATUS 0x{suspendStatus:X8})";
                return TelemetryHookInstallResult.Fail(Status);
            }

            bool patchOk = false;
            int patchError = 0;
            try
            {
                byte[] beforePatch = new byte[_original12.Length];
                if (!ReadExact(ToIntPtr(_hookAddress), beforePatch) || !beforePatch.AsSpan().SequenceEqual(_original12))
                    return TelemetryHookInstallResult.Fail("RPM hook: entry changed before patch publication");
                if (!VirtualProtectEx(_handle, ToIntPtr(_hookAddress), (nuint)patch.Length, PageExecuteReadWrite, out uint oldProtect))
                {
                    patchError = Marshal.GetLastWin32Error();
                }
                else
                {
                    // A partial published write also requires retaining the cave:
                    // rollback success cannot be assumed during process teardown.
                    _published = true;
                    patchOk = WriteProcessMemory(_handle, ToIntPtr(_hookAddress), patch, (nuint)patch.Length, out written) && written == (nuint)patch.Length;
                    if (!patchOk)
                    {
                        patchError = Marshal.GetLastWin32Error();
                        // A partial code write is not safe. Restore the known original bytes
                        // before resuming the game.
                        _ = WriteProcessMemory(_handle, ToIntPtr(_hookAddress), _original12, (nuint)_original12.Length, out _);
                    }
                    _ = VirtualProtectEx(_handle, ToIntPtr(_hookAddress), (nuint)patch.Length, oldProtect, out _);
                    _ = FlushInstructionCache(_handle, ToIntPtr(_hookAddress), (nuint)patch.Length);
                }
            }
            finally
            {
                _ = NtResumeProcess(_handle);
            }

            if (!patchOk)
            {
                Status = $"RPM hook: patch write failed (Win32 {patchError})";
                return TelemetryHookInstallResult.Fail(Status);
            }
        }

        // RPM is enough to keep the telemetry hook useful. Event-context capture is
        // installed independently and fails closed if that slot is unavailable.
        string eventStatus = TryInstallEventContextHook();
        _published = true;
        Installed = true;
        Status = _eventHookInstalled
            ? (adopted ? "Telemetry hook reattached · RPM + event context" : "Telemetry hook installed · RPM + event context · waiting for driving")
            : (adopted ? $"RPM hook reattached · {eventStatus}" : $"RPM hook installed · {eventStatus}");
        return TelemetryHookInstallResult.Ok(Status);
    }

    private bool TryAdoptExistingHook(byte[] thunkBytes)
    {
        if (thunkBytes.Length < 12 || thunkBytes[0] != 0x48 || thunkBytes[1] != 0xB8 || thunkBytes[10] != 0xFF || thunkBytes[11] != 0xE0)
            return false;

        ulong caveAddress = BinaryPrimitives.ReadUInt64LittleEndian(thunkBytes.AsSpan(2, 8));
        byte[] cave = new byte[24];
        if (!ReadProcessMemory(_handle, ToIntPtr(caveAddress), cave, (nuint)cave.Length, out nuint read) || read != (nuint)cave.Length)
            return false;

        // Our RPM trampoline starts: mov r10,imm64 ; inc dword ptr [r10+4] ; movss xmm0,[r8]
        if (!cave.AsSpan(0, 2).SequenceEqual(new byte[] { 0x49, 0xBA }) ||
            !cave.AsSpan(10, 4).SequenceEqual(new byte[] { 0x41, 0xFF, 0x42, 0x04 }) ||
            !cave.AsSpan(14, 5).SequenceEqual(new byte[] { 0xF3, 0x41, 0x0F, 0x10, 0x00 }))
            return false;

        ulong telemetryAddress = BinaryPrimitives.ReadUInt64LittleEndian(cave.AsSpan(2, 8));
        if (caveAddress == 0 || telemetryAddress == 0)
            return false;

        byte[] expected = X64TelemetryStubs.BuildRpmTrampoline(telemetryAddress, _hookAddress + 12);
        byte[] complete = new byte[expected.Length];
        if (!ReadExact(ToIntPtr(caveAddress), complete) || !complete.AsSpan().SequenceEqual(expected))
            return false;

        _published = true;
        _codeCave = ToIntPtr(caveAddress);
        _telemetryBuffer = ToIntPtr(telemetryAddress);
        Array.Copy(ExpectedThunkPrefix, 0, _original12, 0, _original12.Length);
        return true;
    }

    private string TryInstallEventContextHook()
    {
        if (_handle == IntPtr.Zero || _codeCave == IntPtr.Zero || _telemetryBuffer == IntPtr.Zero)
            return "event hook unavailable";

        _eventSlotAddress = _gameBase + EventContextVtableSlotRva;
        byte[] rawPointer = new byte[8];
        if (!ReadProcessMemory(_handle, ToIntPtr(_eventSlotAddress), rawPointer, 8, out nuint read) || read != 8)
            return "event hook unavailable: vtable slot read failed";

        ulong currentHandler = BinaryPrimitives.ReadUInt64LittleEndian(rawPointer);
        if (currentHandler == 0)
            return "event hook unavailable: empty vtable slot";

        // Reattach to a owned event trampoline that survived an unclean app exit.
        if (TryAdoptExistingEventHook(currentHandler))
            return "event context hook reattached";

        // Stock is GameBase+0x912860. If another mod replaced the vtable slot first,
        // chain to its current pointer instead of clobbering its behavior.
        _originalEventHandler = currentHandler;
        _eventTrampolineAddress = (ulong)_codeCave.ToInt64() + EventTrampolineOffset;

        // -1 means "not observed yet"; sequence zero means no context callback captured.
        byte[] initial = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(initial.AsSpan(0, 4), -1);
        if (!WriteProcessMemory(_handle, IntPtr.Add(_telemetryBuffer, 0x10), initial, (nuint)initial.Length, out nuint initialized) || initialized != (nuint)initial.Length)
            return "event hook unavailable: telemetry init failed";

        byte[] trampoline = X64TelemetryStubs.BuildEventContextTrampoline((ulong)_telemetryBuffer.ToInt64(), _originalEventHandler);
        IntPtr eventCave = ToIntPtr(_eventTrampolineAddress);
        if (!WriteProcessMemory(_handle, eventCave, trampoline, (nuint)trampoline.Length, out nuint written) || written != (nuint)trampoline.Length)
            return "event hook unavailable: trampoline write failed";

        byte[] targetPointer = new byte[8];
        _ = FlushInstructionCache(_handle, eventCave, (nuint)trampoline.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(targetPointer, _eventTrampolineAddress);

        int suspendStatus = NtSuspendProcess(_handle);
        if (suspendStatus < 0)
            return $"event hook unavailable: suspend failed (0x{suspendStatus:X8})";

        bool ok = false;
        int error = 0;
        try
        {
            byte[] beforePatch = new byte[8];
            if (!ReadExact(ToIntPtr(_eventSlotAddress), beforePatch) || !beforePatch.AsSpan().SequenceEqual(rawPointer))
                return "event hook unavailable: slot changed before publication";
            if (!VirtualProtectEx(_handle, ToIntPtr(_eventSlotAddress), 8, PageExecuteReadWrite, out uint oldProtect))
            {
                error = Marshal.GetLastWin32Error();
            }
            else
            {
                ok = WriteProcessMemory(_handle, ToIntPtr(_eventSlotAddress), targetPointer, 8, out written) && written == 8;
                if (!ok)
                {
                    error = Marshal.GetLastWin32Error();
                    _ = WriteProcessMemory(_handle, ToIntPtr(_eventSlotAddress), rawPointer, 8, out _);
                }
                _ = VirtualProtectEx(_handle, ToIntPtr(_eventSlotAddress), 8, oldProtect, out _);
                _ = FlushInstructionCache(_handle, ToIntPtr(_eventSlotAddress), 8);
            }
        }
        finally
        {
            _ = NtResumeProcess(_handle);
        }

        if (!ok)
            return $"event hook unavailable: vtable patch failed (Win32 {error})";

        _eventHookInstalled = true;
        return currentHandler == _gameBase + StockEventContextEnterRva
            ? "event context hook installed"
            : $"event context hook installed (chained existing handler 0x{currentHandler:X})";
    }

    private bool TryAdoptExistingEventHook(ulong currentHandler)
    {
        byte[] code = new byte[72];
        if (!ReadProcessMemory(_handle, ToIntPtr(currentHandler), code, (nuint)code.Length, out nuint read) || read < 67)
            return false;

        // Owned wrapper: save RCX, call previous handler, save RAX, then sample
        // context+0x60 into the shared buffer and return the original RAX.
        if (!code.AsSpan(0, 9).SequenceEqual(new byte[] { 0x48, 0x83, 0xEC, 0x38, 0x48, 0x89, 0x4C, 0x24, 0x20 }) ||
            !code.AsSpan(9, 2).SequenceEqual(new byte[] { 0x49, 0xBB }) ||
            !code.AsSpan(19, 3).SequenceEqual(new byte[] { 0x41, 0xFF, 0xD3 }) ||
            !code.AsSpan(22, 5).SequenceEqual(new byte[] { 0x48, 0x89, 0x44, 0x24, 0x28 }) ||
            !code.AsSpan(27, 2).SequenceEqual(new byte[] { 0x49, 0xBA }) ||
            !code.AsSpan(37, 4).SequenceEqual(new byte[] { 0x41, 0xFF, 0x42, 0x14 }) ||
            !code.AsSpan(41, 5).SequenceEqual(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x20 }) ||
            !code.AsSpan(46, 3).SequenceEqual(new byte[] { 0x8B, 0x40, 0x60 }) ||
            !code.AsSpan(49, 4).SequenceEqual(new byte[] { 0x41, 0x89, 0x42, 0x10 }) ||
            !code.AsSpan(53, 4).SequenceEqual(new byte[] { 0x41, 0xFF, 0x42, 0x14 }) ||
            !code.AsSpan(57, 10).SequenceEqual(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x28, 0x48, 0x83, 0xC4, 0x38, 0xC3 }))
            return false;

        ulong telemetry = BinaryPrimitives.ReadUInt64LittleEndian(code.AsSpan(29, 8));
        if (telemetry != (ulong)_telemetryBuffer.ToInt64())
            return false;

        _eventTrampolineAddress = currentHandler;
        _originalEventHandler = BinaryPrimitives.ReadUInt64LittleEndian(code.AsSpan(11, 8));
        _eventHookInstalled = _originalEventHandler != 0;
        return _eventHookInstalled;
    }

    public Fh2TelemetrySample Read()
    {
        if (!Installed || _handle == IntPtr.Zero || _telemetryBuffer == IntPtr.Zero)
            return Fh2TelemetrySample.Unavailable(Status);

        // Shared layout written by the two trampolines:
        // +0x00 float rpmNorm
        // +0x04 uint  rpm sequence (odd while writing, even when stable)
        // +0x08 float gearRpm
        // +0x0C byte  shift
        // +0x0D byte  backfire
        // +0x10 int   eventContext (-1 until observed)
        // +0x14 uint  event sequence (zero until observed)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] first = _firstSample;
            byte[] second = _secondSample;
            if (!ReadExact(_telemetryBuffer, first) || !ReadExact(_telemetryBuffer, second))
                return Fh2TelemetrySample.Unavailable("Telemetry hook read failed");

            uint seq1 = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4, 4));
            uint seq2 = BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(4, 4));
            if (seq1 != seq2 || (seq2 & 1u) != 0)
                continue;

            float rpm = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(0, 4)));
            float gearRpm = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(8, 4)));
            bool shift = second[12] != 0;
            bool backfire = second[13] != 0;

            uint eventSeq1 = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x14, 4));
            uint eventSeq2 = BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(0x14, 4));
            int eventRaw = BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(0x10, 4));
            int? eventContext = _eventHookInstalled && eventSeq1 == eventSeq2 && eventSeq2 != 0 && (eventSeq2 & 1u) == 0
                ? eventRaw
                : null;

            if (seq2 != _lastSequence)
            {
                _lastSequence = seq2;
                _lastActivityUtc = DateTime.UtcNow;
            }

            bool active = _lastActivityUtc != DateTime.MinValue && (DateTime.UtcNow - _lastActivityUtc) <= TimeSpan.FromMilliseconds(450);
            float? validRpm = float.IsFinite(rpm) && rpm >= -0.05f && rpm <= 1.20f ? rpm : null;
            float? validGearRpm = float.IsFinite(gearRpm) && gearRpm >= -2f && gearRpm <= 2f ? gearRpm : null;

            string eventPart = eventContext.HasValue ? $" · context {eventContext.Value}" : _eventHookInstalled ? " · context waiting" : "";
            Status = active ? $"Telemetry live{eventPart}" : $"Telemetry installed · waiting for driving{eventPart}";
            return new Fh2TelemetrySample(true, active, validRpm, validGearRpm, shift, backfire, eventContext, _eventHookInstalled, Status);
        }

        return Fh2TelemetrySample.Unavailable("Telemetry sample was changing during read");
    }

    private bool ReadExact(IntPtr address, byte[] buffer) =>
        ReadProcessMemory(_handle, address, buffer, (nuint)buffer.Length, out nuint read) && read == (nuint)buffer.Length;

    private void RestoreOriginal()
    {
        if (!Installed || _handle == IntPtr.Zero || _hookAddress == 0)
            return;

        try
        {
            int suspended = NtSuspendProcess(_handle);
            if (suspended < 0)
            {
                Status = "Hook restoration deferred: process could not be suspended";
                return;
            }
            try
            {
                // Restore event-context vtable slot only if nobody changed it after us.
                if (_eventHookInstalled && _eventSlotAddress != 0 && _originalEventHandler != 0 && _eventTrampolineAddress != 0)
                {
                    byte[] current = new byte[8];
                    if (ReadProcessMemory(_handle, ToIntPtr(_eventSlotAddress), current, 8, out nuint eventRead) && eventRead == 8 &&
                        BinaryPrimitives.ReadUInt64LittleEndian(current) == _eventTrampolineAddress &&
                        VirtualProtectEx(_handle, ToIntPtr(_eventSlotAddress), 8, PageExecuteReadWrite, out uint eventOldProtect))
                    {
                        byte[] original = new byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(original, _originalEventHandler);
                        _ = WriteProcessMemory(_handle, ToIntPtr(_eventSlotAddress), original, 8, out _);
                        _ = VirtualProtectEx(_handle, ToIntPtr(_eventSlotAddress), 8, eventOldProtect, out _);
                        _ = FlushInstructionCache(_handle, ToIntPtr(_eventSlotAddress), 8);
                    }
                }

                byte[] ownedPatch = new byte[12];
                bool stillOwned = ReadExact(ToIntPtr(_hookAddress), ownedPatch) &&
                    ownedPatch.AsSpan().SequenceEqual(X64TelemetryStubs.BuildAbsoluteJump((ulong)_codeCave.ToInt64()));
                if (stillOwned && VirtualProtectEx(_handle, ToIntPtr(_hookAddress), (nuint)_original12.Length, PageExecuteReadWrite, out uint oldProtect))
                {
                    _ = WriteProcessMemory(_handle, ToIntPtr(_hookAddress), _original12, (nuint)_original12.Length, out _);
                    _ = VirtualProtectEx(_handle, ToIntPtr(_hookAddress), (nuint)_original12.Length, oldProtect, out _);
                    _ = FlushInstructionCache(_handle, ToIntPtr(_hookAddress), (nuint)_original12.Length);
                }
            }
            finally
            {
                if (suspended >= 0) _ = NtResumeProcess(_handle);
            }
        }
        catch { }
        _eventHookInstalled = false;
        Installed = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!HasExited(_process))
            RestoreOriginal();

        if (!_published && _codeCave != IntPtr.Zero && _handle != IntPtr.Zero)
        {
            _ = VirtualFreeEx(_handle, _codeCave, 0, MemRelease);
            _codeCave = IntPtr.Zero;
        }
        if (!_published && _telemetryBuffer != IntPtr.Zero && _handle != IntPtr.Zero)
        {
            _ = VirtualFreeEx(_handle, _telemetryBuffer, 0, MemRelease);
            _telemetryBuffer = IntPtr.Zero;
        }
        if (_handle != IntPtr.Zero)
        {
            _ = CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static IntPtr ToIntPtr(ulong value) => unchecked((IntPtr)(long)value);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, [Out] byte[] buffer, nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, nuint size);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
