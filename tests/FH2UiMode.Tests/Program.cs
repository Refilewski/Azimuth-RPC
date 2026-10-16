using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AzimuthRPC;
using AzimuthRPC.Tests;

int passed = 0, failed = 0;
void Check(bool condition, string reason = "Assertion failed") { if (!condition) throw new Exception(reason); }
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
}
byte[] Pointer(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
string fixtures = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "Fixtures") : Path.GetFullPath(args[0]);
var captured = SyntheticFixture.Load(fixtures).Take(18).Select(c =>
{
    var memory = c.Memory();
    return (c.Label, GameBase: SyntheticFixture.GameBase, Audio: SyntheticFixture.Audio,
        Registry: SyntheticFixture.Registry, AudioBytes: memory[SyntheticFixture.Audio],
        RegistryBytes: memory[SyntheticFixture.Registry]);
}).ToList();
byte[]? Read(Dictionary<ulong, byte[]> memory, ulong address, int size)
{
    foreach (var (start, bytes) in memory)
    {
        if (address < start || address - start >= (ulong)bytes.Length) continue;
        int offset = checked((int)(address - start));
        return bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)).ToArray();
    }
    return null;
}
Dictionary<ulong, byte[]> Memory(int i) => new()
{
    // Every byte and address in this memory model is synthesized for regression tests.
    [captured[i].GameBase + UiModeReader.AudioEngineRva] = Pointer(captured[i].Audio),
    [captured[i].Audio] = captured[i].AudioBytes.ToArray(),
    [captured[i].Registry] = captured[i].RegistryBytes.ToArray()
};
int photoIndex = captured.FindIndex(c => c.Label == "photomode_3");
byte[] GoodRecord() => captured[photoIndex].RegistryBytes.AsSpan(checked((int)UiModeReader.RecordOffset), UiModeReader.RecordBytes).ToArray();
byte[] InactiveMapRecord() => captured[photoIndex].RegistryBytes.AsSpan(checked((int)UiModeReader.MapRecordOffset), UiModeReader.MapRecordBytes).ToArray();
int mapIndex = captured.FindIndex(c => c.Label == "worldmap_2");
byte[] PositiveMapRecord() => captured[mapIndex].RegistryBytes.AsSpan(checked((int)UiModeReader.MapRecordOffset), UiModeReader.MapRecordBytes).ToArray();
byte[] InactivePauseRecord() => captured[mapIndex].RegistryBytes.AsSpan(checked((int)UiModeReader.RecordOffset), UiModeReader.RecordBytes).ToArray();

Test("Eighteen synthetic controls preserve thirteen historically labeled scenarios", () =>
{
    Check(captured.Count == 18);
    Check(captured.Count(c => c.Label.StartsWith("UNLABELED_", StringComparison.Ordinal)) == 5);
});
for (int i = 0; i < captured.Count; i++)
{
    int index = i;
    var cap = captured[index];
    Test((cap.Label.StartsWith("UNLABELED_", StringComparison.Ordinal) ? "Unlabeled parser consistency (no semantic assertion): " : "Exact production reader: ") + cap.Label, () =>
    {
        var memory = Memory(index);
        var expected = cap.Label.StartsWith("pause_", StringComparison.Ordinal) ? UiMode.PauseMenu
            : cap.Label.StartsWith("photomode_", StringComparison.Ordinal) ? UiMode.PhotoMode
            : cap.Label.StartsWith("worldmap_", StringComparison.Ordinal) ? UiMode.WorldMap : UiMode.Unknown;
        var observed = UiModeReader.Read(cap.GameBase, true, (a, n) => Read(memory, a, n));
        if (cap.Label.StartsWith("UNLABELED_", StringComparison.Ordinal))
        {
            // Parser consistency only: these historical cases never supplied semantic ground truth.
            Check(Enum.IsDefined(observed));
            Check(observed == UiModeReader.Read(cap.GameBase, true, (a, n) => Read(memory, a, n)));
        }
        else Check(observed == expected);
    });
}
Test("Old pause pair aliases Map/driving; new Map record supplies numeric evidence", () =>
{
    int map = captured.FindIndex(c => c.Label == "worldmap_1");
    var raw = captured[map].RegistryBytes;
    Check(raw.AsSpan(0xC10, 12).SequenceEqual("BASE_Paused\0"u8));
    Check(UiModeReader.DecodeRecord(raw.AsSpan(0xC00, 28)) == UiMode.Unknown);
    Check(UiModeReader.DecodeSnapshot(raw.AsSpan(0xC00, 28), raw.AsSpan(0xE80, 32)) == UiMode.WorldMap);
});
Test("Unsupported PE layout returns Unknown without reading memory", () =>
{
    int reads = 0;
    Check(UiModeReader.Read(0x140000000, false, (_, _) => { reads++; return null; }) == UiMode.Unknown);
    Check(reads == 0);
});
Test("New +0x630 offset follows synthetic pointer bytes", () =>
    Check(captured.All(c => BinaryPrimitives.ReadUInt64LittleEndian(c.AudioBytes.AsSpan(0x630, 8)) == c.Registry)));
Test("Record +0xC00 guard is required", () =>
{
    var b = GoodRecord(); BinaryPrimitives.WriteInt32LittleEndian(b, 0);
    Check(UiModeReader.DecodeRecord(b) == UiMode.Unknown);
});
Test("Active +0xC04 must be exactly float32 1.0", () =>
{
    foreach (int bits in new[] { 0, 0x3F000000, 0x7FC00000, 0x7F800000, -1 })
    {
        var b = GoodRecord(); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), bits);
        Check(UiModeReader.DecodeRecord(b) == UiMode.Unknown);
    }
});
Test("Subtype +0xC08 decodes only observed positive modes", () =>
{
    foreach (var (value, expected) in new[] { (1, UiMode.PauseMenu), (2, UiMode.PhotoMode), (0, UiMode.Unknown), (3, UiMode.Unknown), (-1, UiMode.Unknown) })
    {
        var b = GoodRecord(); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), value);
        Check(UiModeReader.DecodeRecord(b) == expected);
    }
});
Test("Record +0xC0C guard is required", () =>
{
    var b = GoodRecord(); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 3);
    Check(UiModeReader.DecodeRecord(b) == UiMode.Unknown);
});
Test("Record +0xC10 name and NUL are layout guards only", () =>
{
    foreach (int at in new[] { 16, 27 })
    {
        var b = GoodRecord(); b[at] = 1;
        Check(UiModeReader.DecodeRecord(b) == UiMode.Unknown);
    }
});
Test("Every synthetic record matches the three structural guards", () =>
{
    foreach (var cap in captured)
    {
        Check(BinaryPrimitives.ReadInt32LittleEndian(cap.RegistryBytes.AsSpan(0xC00)) == 0x3F800000);
        Check(BinaryPrimitives.ReadInt32LittleEndian(cap.RegistryBytes.AsSpan(0xC0C)) == 2);
        Check(cap.RegistryBytes.AsSpan(0xC10, 12).SequenceEqual("BASE_Paused\0"u8));
    }
});
Test("Short and oversized record buffers fail closed", () =>
{
    Check(UiModeReader.DecodeRecord(new byte[27]) == UiMode.Unknown);
    Check(UiModeReader.DecodeRecord(new byte[29]) == UiMode.Unknown);
});
Test("Partial root, registry pointer and record reads return Unknown", () =>
{
    foreach (int cut in new[] { 0, 1, 2, 3 })
    {
        var cap = captured[photoIndex]; var mem = Memory(photoIndex);
        ulong bad = cut == 0 ? cap.GameBase + UiModeReader.AudioEngineRva : cut == 1 ? cap.Audio + 0x630 : cut == 2 ? cap.Registry + 0xC00 : cap.Registry + 0xE80;
        Check(UiModeReader.Read(cap.GameBase, true, (a, n) => a == bad ? new byte[n - 1] : Read(mem, a, n)) == UiMode.Unknown);
    }
});
Test("Null reads and invalid/noncanonical/unaligned pointers return Unknown", () =>
{
    var cap = captured[photoIndex];
    Check(UiModeReader.Read(cap.GameBase, true, (_, _) => null) == UiMode.Unknown);
    foreach (ulong pointer in new ulong[] { 0, 1, 0x602517D8C01, ulong.MaxValue })
    {
        var mem = Memory(photoIndex); mem[cap.GameBase + UiModeReader.AudioEngineRva] = Pointer(pointer);
        Check(UiModeReader.Read(cap.GameBase, true, (a, n) => Read(mem, a, n)) == UiMode.Unknown);
    }
});
Test("Two differing record reads return Unknown", () =>
{
    var cap = captured[photoIndex]; var mem = Memory(photoIndex); int records = 0;
    Check(UiModeReader.Read(cap.GameBase, true, (a, n) =>
    {
        var b = Read(mem, a, n);
        if (a == cap.Registry + 0xC00 && ++records == 2) b![8] = 1;
        return b;
    }) == UiMode.Unknown);
});
Test("Changed registry pointer after record sampling returns Unknown", () =>
{
    var cap = captured[photoIndex]; var mem = Memory(photoIndex); int pointers = 0;
    Check(UiModeReader.Read(cap.GameBase, true, (a, n) => a == cap.Audio + 0x630 && ++pointers == 2 ? Pointer(cap.Registry + 0x1000) : Read(mem, a, n)) == UiMode.Unknown);
});
Test("Changed AudioEngine root after record sampling returns Unknown", () =>
{
    var cap = captured[photoIndex]; var mem = Memory(photoIndex); int roots = 0;
    Check(UiModeReader.Read(cap.GameBase, true, (a, n) => a == cap.GameBase + UiModeReader.AudioEngineRva && ++roots >= 2 ? Pointer(cap.Audio + 0x1000) : Read(mem, a, n)) == UiMode.Unknown);
});
Test("Synthetic relocated image and heap traverse correctly without absolute-address assumptions", () =>
{
    const ulong gameBase = 0x180000000, audio = 0x7000000000, registry = 0x7100000000;
    var mem = new Dictionary<ulong, byte[]>
    {
        [gameBase + UiModeReader.AudioEngineRva] = Pointer(audio),
        [audio + UiModeReader.RegistryPointerOffset] = Pointer(registry),
        [registry + UiModeReader.RecordOffset] = GoodRecord(),
        [registry + UiModeReader.MapRecordOffset] = InactiveMapRecord(),
        // Synthetic inactive loading record: this relocation test verifies traversal only.
        [registry + UiModeReader.LoadingRecordOffset] = new byte[UiModeReader.LoadingRecordBytes]
    };
    Check(UiModeReader.Read(gameBase, true, (a, n) => Read(mem, a, n)) == UiMode.PhotoMode);
    mem[registry + UiModeReader.RecordOffset] = InactivePauseRecord();
    mem[registry + UiModeReader.MapRecordOffset] = PositiveMapRecord();
    Check(UiModeReader.Read(gameBase, true, (a, n) => Read(mem, a, n)) == UiMode.WorldMap);
});
Test("Map record +E80/+E8C/+E90 layout guards match all eighteen synthetic fixtures", () =>
{
    foreach (var cap in captured)
    {
        Check(BinaryPrimitives.ReadInt32LittleEndian(cap.RegistryBytes.AsSpan(0xE80)) == 0x3F800000);
        Check(BinaryPrimitives.ReadInt32LittleEndian(cap.RegistryBytes.AsSpan(0xE8C)) == 2);
        Check(cap.RegistryBytes.AsSpan(0xE90, 16).SequenceEqual("BASE_Map_Paused\0"u8));
    }
});
Test("Corrupt Map layout guards fail closed", () =>
{
    foreach (int at in new[] { 0, 12, 16, 31 })
    {
        var map = PositiveMapRecord(); map[at] ^= 1;
        Check(UiModeReader.DecodeSnapshot(InactivePauseRecord(), map) == UiMode.Unknown);
    }
    Check(UiModeReader.DecodeSnapshot(InactivePauseRecord(), new byte[31]) == UiMode.Unknown);
});
Test("Map +E84 must be exactly float 1.0 and +E88 exactly int32 1", () =>
{
    foreach (int value in new[] { 0, 0x3F000000, 0x7FC00000, 0x7F800000, -1 })
    {
        var map = PositiveMapRecord(); BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(4), value);
        Check(UiModeReader.DecodeSnapshot(InactivePauseRecord(), map) == UiMode.Unknown);
    }
    foreach (int value in new[] { 0, 2, -1 })
    {
        var map = PositiveMapRecord(); BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(8), value);
        Check(UiModeReader.DecodeSnapshot(InactivePauseRecord(), map) == UiMode.Unknown);
    }
});
Test("Both inactive signals stay Unknown; names alone never imply activity", () =>
    Check(UiModeReader.DecodeSnapshot(InactivePauseRecord(), InactiveMapRecord()) == UiMode.Unknown));
Test("Simultaneous Photo/Pause and Map positives are ambiguous", () =>
{
    Check(UiModeReader.DecodeSnapshot(GoodRecord(), PositiveMapRecord()) == UiMode.Unknown);
    var pause = GoodRecord(); BinaryPrimitives.WriteInt32LittleEndian(pause.AsSpan(8), 1);
    Check(UiModeReader.DecodeSnapshot(pause, PositiveMapRecord()) == UiMode.Unknown);
});
Test("A changed Map record on its second read returns Unknown", () =>
{
    var cap = captured[mapIndex]; var memory = Memory(mapIndex); int reads = 0;
    Check(UiModeReader.Read(cap.GameBase, true, (a, n) =>
    {
        var raw = Read(memory, a, n);
        if (a == cap.Registry + UiModeReader.MapRecordOffset && ++reads == 2) raw![8] = 2;
        return raw;
    }) == UiMode.Unknown);
});
Console.WriteLine($"{passed} passed; {failed} failed. All memory is synthetic regression data, not captured evidence.");
return failed == 0 ? 0 : 1;
