using System.Buffers.Binary;

namespace AzimuthRPC;

// Positive states require repeated/structural evidence. Inactive records do not
// establish gameplay and MUST stay Unknown.
public enum UiMode { Unknown, PauseMenu, PhotoMode, WorldMap, HorizonHub, Garage, Loading }

internal static class UiModeReader
{
    internal const ulong AudioEngineRva = 0x0365D3D8;
    internal const ulong RegistryPointerOffset = 0x630;

    // BASE_Loading remains as a secondary loading signal. Race loading in
    // the live 0.9.7 build does not necessarily raise this mixer snapshot.
    internal const ulong LoadingRecordOffset = 0x7A0;
    internal const int LoadingRecordBytes = 29; // 16-byte header + "BASE_Loading\0"

    internal const ulong RecordOffset = 0xC00;
    internal const int RecordBytes = 28;
    internal const ulong MapRecordOffset = 0xE80;
    internal const int MapRecordBytes = 32;

    internal const ulong PlayerManagerRva = 0x034ABA60;

    // PlayerManager+0x48 -> CGameManager. The vtable is stable in FH2 1.0.0.64.
    // In the user's labelled race-loading capture bytes +0x2D/+0x2E both drop to
    // zero. They are 1/1 in 18 older non-loading controls plus current Hub,
    // Garage and stopped Free Roam captures. We keep strict neighbouring guards.
    internal const ulong GameManagerPointerOffset = 0x48;
    internal const ulong GameManagerVtableRva = 0x02C5F8C8;
    internal const int GameManagerBytes = 0x40;

    // PlayerManager+0x50 -> ForzaSimulationManager. These are sub-flow diagnostics.
    // Live observations: Horizon Hub is 2/0. Garage has produced several 3/x
    // substates (3/7, 3/8, 3/10, 3/11, 3/13). The secondary value is a transient
    // garage sub-flow, so do not hardcode every observed number.
    internal const ulong SimulationManagerPointerOffset = 0x50;
    internal const ulong SimulationManagerVtableRva = 0x02C5FD70;
    internal const int SimulationManagerBytes = 0x28;

    // AudioEngine root has a compact front-end marker at +0x38. In the current
    // Hub and Garage captures it is 0.0f while the three neighbouring values stay
    // -80.0f. It is -80.0f in stopped Free Roam, race loading and all 18 older
    // gameplay/event/pause/photo/map controls. The extra guards avoid trusting one
    // transient float by itself.
    internal const int AudioFrontendBytes = 0x48;
    private const int Minus80Bits = unchecked((int)0xC2A00000);

    internal readonly record struct UiStateSample(UiMode Mode, FrontendState Frontend, HorizonFlowState Flow, GameManagerState GameManager);
    internal static UiMode Read(ulong gameBase, bool supportedBuild, Func<ulong, int, byte[]?> readMemory) => ReadSnapshot(gameBase, supportedBuild, readMemory).Mode;
    internal static UiStateSample ReadSnapshot(ulong gameBase, bool supportedBuild, Func<ulong, int, byte[]?> readMemory)
    {
        if (!supportedBuild || !Pointer(gameBase)) return default;
        var frontend = ReadFrontendState(gameBase, readMemory);
        var flow = ReadHorizonFlow(gameBase, readMemory);
        var manager = ReadGameManagerState(gameBase, readMemory);
        return new(ReadMode(gameBase, supportedBuild, readMemory, frontend, flow, manager), frontend, flow, manager);
    }

    private static UiMode ReadMode(ulong gameBase, bool supportedBuild, Func<ulong, int, byte[]?> readMemory, FrontendState frontend, HorizonFlowState flow, GameManagerState gameManager)
    {
        if (!supportedBuild || !Pointer(gameBase)) return UiMode.Unknown;


        // Known front-end modes first. In live captures the Garage secondary
        // value changes while moving through the garage (7/8/10/11/13 so far).
        // Primary==3 is the stable discriminator; the front-end marker + stable
        // ForzaSimulationManager vtable/guards keep this from matching gameplay.
        if (frontend.Valid && frontend.HubFamily && flow.Valid)
        {
            if (flow.Primary == 3 && flow.Secondary >= 0 && flow.Secondary <= 0x40)
                return UiMode.Garage;
            if (flow.Primary == 2 && flow.Secondary == 0)
                return UiMode.HorizonHub;
        }

        // This is independent of telemetry/event-context and catches the race
        // transition that previously looked exactly like stopped Free Roam.
        if (gameManager.Valid && gameManager.Loading)
            return UiMode.Loading;

        try
        {
            ulong slot = checked(gameBase + AudioEngineRva);
            ulong audio = ReadPointer(slot, readMemory);
            if (Pointer(audio))
            {
                ulong pointerSlot = checked(audio + RegistryPointerOffset);
                ulong registry = ReadPointer(pointerSlot, readMemory);
                if (Pointer(registry))
                {
                    ulong loadingAddress = checked(registry + LoadingRecordOffset);
                    ulong recordAddress = checked(registry + RecordOffset);
                    ulong mapAddress = checked(registry + MapRecordOffset);

                    byte[]? firstLoading = Exact(loadingAddress, LoadingRecordBytes, readMemory);
                    byte[]? first = Exact(recordAddress, RecordBytes, readMemory);
                    byte[]? firstMap = Exact(mapAddress, MapRecordBytes, readMemory);
                    byte[]? secondLoading = Exact(loadingAddress, LoadingRecordBytes, readMemory);
                    byte[]? second = Exact(recordAddress, RecordBytes, readMemory);
                    byte[]? secondMap = Exact(mapAddress, MapRecordBytes, readMemory);

                    if (firstLoading is not null && secondLoading is not null &&
                        first is not null && second is not null &&
                        firstMap is not null && secondMap is not null &&
                        firstLoading.AsSpan().SequenceEqual(secondLoading) &&
                        first.AsSpan().SequenceEqual(second) &&
                        firstMap.AsSpan().SequenceEqual(secondMap) &&
                        ReadPointer(pointerSlot, readMemory) == registry &&
                        ReadPointer(slot, readMemory) == audio)
                    {
                        // Some non-race loading flows do use BASE_Loading, so keep it.
                        if (DecodeLoadingRecord(firstLoading) == UiMode.Loading)
                            return UiMode.Loading;

                        UiMode mixerMode = DecodeSnapshot(first, firstMap);
                        if (mixerMode != UiMode.Unknown)
                            return mixerMode;
                    }
                }
            }
        }
        catch (OverflowException)
        {
            // Fail closed.
        }

        return UiMode.Unknown;
    }

    internal static UiMode DecodeLoadingRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length != LoadingRecordBytes) return UiMode.Unknown;
        if (BinaryPrimitives.ReadInt32LittleEndian(record) != 0x3F800000 ||
            BinaryPrimitives.ReadInt32LittleEndian(record[12..]) != 1 ||
            !record[16..].SequenceEqual("BASE_Loading\0"u8))
            return UiMode.Unknown;

        int activeBits = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        return activeBits == 0x3F800000 ? UiMode.Loading : UiMode.Unknown;
    }

    internal static UiMode DecodeRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length != RecordBytes) return UiMode.Unknown;
        if (BinaryPrimitives.ReadInt32LittleEndian(record) != 0x3F800000 ||
            BinaryPrimitives.ReadInt32LittleEndian(record[12..]) != 2 ||
            !record[16..].SequenceEqual("BASE_Paused\0"u8)) return UiMode.Unknown;
        int activeBits = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
        int submode = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        if (activeBits != 0x3F800000) return UiMode.Unknown;
        return submode switch { 1 => UiMode.PauseMenu, 2 => UiMode.PhotoMode, _ => UiMode.Unknown };
    }

    internal static UiMode DecodeSnapshot(ReadOnlySpan<byte> pause, ReadOnlySpan<byte> map)
    {
        if (pause.Length != RecordBytes || map.Length != MapRecordBytes) return UiMode.Unknown;
        if (BinaryPrimitives.ReadInt32LittleEndian(pause) != 0x3F800000 ||
            BinaryPrimitives.ReadInt32LittleEndian(pause[12..]) != 2 ||
            !pause[16..].SequenceEqual("BASE_Paused\0"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(map) != 0x3F800000 ||
            BinaryPrimitives.ReadInt32LittleEndian(map[12..]) != 2 ||
            !map[16..].SequenceEqual("BASE_Map_Paused\0"u8)) return UiMode.Unknown;

        int pauseActive = BinaryPrimitives.ReadInt32LittleEndian(pause[4..]);
        int pauseSubmode = BinaryPrimitives.ReadInt32LittleEndian(pause[8..]);
        int mapActive = BinaryPrimitives.ReadInt32LittleEndian(map[4..]);
        int mapSubmode = BinaryPrimitives.ReadInt32LittleEndian(map[8..]);
        bool pauseInactive = pauseActive == 0 && pauseSubmode == 0;
        bool mapInactive = mapActive == 0 && mapSubmode == 0;
        bool mapPositive = mapActive == 0x3F800000 && mapSubmode == 1;
        if (pauseInactive && mapPositive) return UiMode.WorldMap;
        if (mapInactive) return DecodeRecord(pause);
        return UiMode.Unknown;
    }

    internal static FrontendState ReadFrontendState(ulong gameBase, Func<ulong, int, byte[]?> readMemory)
    {
        try
        {
            ulong audioSlot = checked(gameBase + AudioEngineRva);
            ulong audio = ReadPointer(audioSlot, readMemory);
            if (!Pointer(audio)) return FrontendState.Invalid;

            byte[]? first = Exact(audio, AudioFrontendBytes, readMemory);
            byte[]? second = Exact(audio, AudioFrontendBytes, readMemory);
            if (first is null || second is null || !first.AsSpan().SequenceEqual(second) ||
                ReadPointer(audioSlot, readMemory) != audio)
                return FrontendState.Invalid;

            // Structural neighbourhood seen in every current labelled capture.
            bool guard =
                I32(first, 0x30) == 0 &&
                I32(first, 0x34) == 0 &&
                I32(first, 0x3C) == Minus80Bits &&
                I32(first, 0x40) == Minus80Bits &&
                I32(first, 0x44) == Minus80Bits;
            if (!guard) return FrontendState.Invalid;

            int marker = I32(first, 0x38);
            if (marker != 0 && marker != Minus80Bits)
                return FrontendState.Invalid;

            return new FrontendState(true, marker == 0);
        }
        catch (OverflowException)
        {
            return FrontendState.Invalid;
        }
    }

    internal static GameManagerState ReadGameManagerState(ulong gameBase, Func<ulong, int, byte[]?> readMemory)
    {
        try
        {
            ulong playerManagerSlot = checked(gameBase + PlayerManagerRva);
            ulong playerManager = ReadPointer(playerManagerSlot, readMemory);
            if (!Pointer(playerManager)) return GameManagerState.Invalid;

            ulong managerSlot = checked(playerManager + GameManagerPointerOffset);
            ulong manager = ReadPointer(managerSlot, readMemory);
            if (!Pointer(manager)) return GameManagerState.Invalid;

            byte[]? first = Exact(manager, GameManagerBytes, readMemory);
            byte[]? second = Exact(manager, GameManagerBytes, readMemory);
            if (first is null || second is null || !first.AsSpan().SequenceEqual(second) ||
                ReadPointer(managerSlot, readMemory) != manager ||
                ReadPointer(playerManagerSlot, readMemory) != playerManager)
                return GameManagerState.Invalid;

            ulong vtable = BinaryPrimitives.ReadUInt64LittleEndian(first);
            if (vtable != gameBase + GameManagerVtableRva)
                return GameManagerState.Invalid;

            // +0x28..+0x2F is a compact byte-state group. These six guards are
            // unchanged across all labelled controls; only +0x2D/+0x2E change.
            bool guards =
                first[0x28] == 0 &&
                first[0x29] == 1 &&
                first[0x2A] == 1 &&
                first[0x2B] == 1 &&
                first[0x2C] == 1 &&
                first[0x2F] == 0;
            if (!guards) return GameManagerState.Invalid;

            byte a = first[0x2D];
            byte b = first[0x2E];
            if (!((a == 0 && b == 0) || (a == 1 && b == 1)))
                return GameManagerState.Invalid;

            return new GameManagerState(true, a == 0 && b == 0, a, b);
        }
        catch (OverflowException)
        {
            return GameManagerState.Invalid;
        }
    }

    internal static HorizonFlowState ReadHorizonFlow(ulong gameBase, Func<ulong, int, byte[]?> readMemory)
    {
        try
        {
            ulong playerManagerSlot = checked(gameBase + PlayerManagerRva);
            ulong playerManager = ReadPointer(playerManagerSlot, readMemory);
            if (!Pointer(playerManager)) return HorizonFlowState.Invalid;

            ulong managerSlot = checked(playerManager + SimulationManagerPointerOffset);
            ulong manager = ReadPointer(managerSlot, readMemory);
            if (!Pointer(manager)) return HorizonFlowState.Invalid;

            byte[]? first = Exact(manager, SimulationManagerBytes, readMemory);
            byte[]? second = Exact(manager, SimulationManagerBytes, readMemory);
            if (first is null || second is null || !first.AsSpan().SequenceEqual(second) ||
                ReadPointer(managerSlot, readMemory) != manager ||
                ReadPointer(playerManagerSlot, readMemory) != playerManager)
                return HorizonFlowState.Invalid;

            ulong vtable = BinaryPrimitives.ReadUInt64LittleEndian(first);
            int guard1 = I32(first, 0x18);
            int guard2 = I32(first, 0x24);
            int primary = I32(first, 0x10);
            int secondary = I32(first, 0x20);

            bool valid = vtable == gameBase + SimulationManagerVtableRva && guard1 == 1 && guard2 == 5;
            return new HorizonFlowState(valid, primary, secondary);
        }
        catch (OverflowException)
        {
            return HorizonFlowState.Invalid;
        }
    }

    internal readonly record struct FrontendState(bool Valid, bool HubFamily)
    {
        internal static FrontendState Invalid => new(false, false);
    }

    internal readonly record struct GameManagerState(bool Valid, bool Loading, byte LoadA, byte LoadB)
    {
        internal static GameManagerState Invalid => new(false, false, 0xFF, 0xFF);
    }

    internal readonly record struct HorizonFlowState(bool Valid, int Primary, int Secondary)
    {
        internal static HorizonFlowState Invalid => new(false, 0, 0);
    }

    private static int I32(ReadOnlySpan<byte> raw, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(raw[offset..]);

    private static bool Pointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFE000 && value % 8 == 0;
    private static ulong ReadPointer(ulong address, Func<ulong, int, byte[]?> readMemory) =>
        Exact(address, 8, readMemory) is { } raw ? BinaryPrimitives.ReadUInt64LittleEndian(raw) : 0;
    private static byte[]? Exact(ulong address, int size, Func<ulong, int, byte[]?> readMemory)
    {
        var raw = readMemory(address, size);
        return raw is not null && raw.Length == size ? raw : null;
    }
}
