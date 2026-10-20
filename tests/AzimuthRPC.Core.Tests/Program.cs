using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text;
using AzimuthRPC;
using AzimuthRPC.Tests;

int passed = 0, failed = 0;
void Check(bool ok, string reason = "Assertion failed") { if (!ok) throw new Exception(reason); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); } }
byte[] Pointer(ulong value) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b; }
string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline-regression.json")));
var captures = SyntheticFixture.Load(fixtures);
Test("22 synthetic scenarios, five without semantic ground truth", () => { Check(captures.Length == 22); Check(captures.Count(c => c.Label.StartsWith("UNLABELED_")) == 5); });
var games = new Dictionary<string, GameSnapshot>();
foreach (var capture in captures)
{
    Test("Synthetic memory scenario: " + capture.Label, () =>
    {
        ulong gameBase = SyntheticFixture.GameBase;
        ulong audio = SyntheticFixture.Audio, player = SyntheticFixture.Player;
        var memory = new FixtureMemory();
        foreach (var block in capture.Memory()) memory.Blocks.Add(block.Key, block.Value);
        var reader = new GameSnapshotReader(memory); var game = reader.Capture(gameBase, true);
        Check(game.Attached, game.Error ?? "Not attached"); games[capture.Label] = game;
        Check(game.UiMode.ToString() == baseline.RootElement.GetProperty("Modes").GetProperty(capture.Label).GetString(), "UI behavior differs from frozen current-source baseline");
        Check(game.Roots.AudioEngine == audio && game.Roots.PlayerManager == player);
        Check(game.Vehicle.Position is not null && game.Vehicle.Velocity is not null, "Synthetic finite vectors lost");
        if (!capture.Label.StartsWith("UNLABELED_"))
        {
            var expected = capture.Label switch
            {
                "horizon-hub" => UiMode.HorizonHub,
                "garage" => UiMode.Garage,
                "race-loading" => UiMode.Loading,
                var s when s.StartsWith("pause_") => UiMode.PauseMenu,
                var s when s.StartsWith("photomode_") => UiMode.PhotoMode,
                var s when s.StartsWith("worldmap_") => UiMode.WorldMap,
                _ => UiMode.Unknown
            };
            Check(game.UiMode == expected, $"Expected {expected}, got {game.UiMode}");
        }
        Check(game.Car.RawId == capture.CarId);
        Check(game.CarProbeStatus == "ok", "Synthetic car field should parse successfully");
        Check(game.Car.DisplayName == (capture.CarId == "LAM_Huracan_15" ? "Lamborghini Huracan LP 610-4" : "Toyota Supra RZ"));
        Check(game.Vehicle.Position == new Vector3(100, 20, 300));
        Check(game.Vehicle.Velocity == new Vector3(capture.SpeedMph * .44704f, 0, 0));
        Check(game.Vehicle.SpeedMph == capture.SpeedMph);
        Check(game.WorldPresence == (game.UiMode == UiMode.Unknown ? WorldPresence.InWorld : WorldPresence.Unknown));
        if (capture.EventContext is int context)
        {
            game = GameStateInterpreter.Apply(game, new(true, capture.TelemetryActive, null, null, false, false, context, true, "Synthetic metadata"), true);
            var expected = capture.Label switch { "free-roam-stopped" => GameActivity.FreeRoam, "race-loading" => GameActivity.RaceLoading, _ => GameActivity.Unknown };
            Check(game.Activity == expected, $"Synthetic activity {game.Activity} != {expected}");
        }
        // A subsequent read failure cannot publish previous car, vectors or roots.
        memory.Fail = true; var unavailable = reader.Capture(gameBase, true);
        Check(!unavailable.Attached && unavailable.Car.RawId is null && unavailable.Vehicle.Position is null && unavailable.Roots.AudioEngine == 0);
        Check(unavailable.CarProbeStatus == "Not sampled", "Memory failures must not report a successful car read");
        memory.Fail = false;
        int rootReads = 0; memory.BeforeRead = (a, n) => { if (a == gameBase + UiModeReader.AudioEngineRva && ++rootReads == 2) memory.Blocks[a] = Pointer(audio + 0x1000); };
        Check(!reader.Capture(gameBase, true).Attached, "Changing root published a mixed snapshot");
    });
}
Test("Car IDs survive heap string / inline changes and never reuse stale IDs", () =>
{
    var memory = new FixtureMemory();
    var fixture = captures.First(c => c.Label == "free-roam-stopped");
    foreach (var block in fixture.Memory()) memory.Blocks.Add(block.Key, block.Value);
    var audio = memory.Blocks[SyntheticFixture.Audio];
    var reader = new GameSnapshotReader(memory);
    const ulong heapA = 0x7500000000, heapB = 0x7600000000;

    void PutHeap(string id, ulong address)
    {
        Check(id.Length >= 16 && id.Length <= 127);
        audio.AsSpan(0x278, 64).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(audio.AsSpan(0x278), address);
        BinaryPrimitives.WriteUInt64LittleEndian(audio.AsSpan(0x288), (ulong)id.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(audio.AsSpan(0x290), (ulong)Math.Max(31, id.Length));
        memory.Blocks[address] = Encoding.ASCII.GetBytes(id + "\0");
    }
    void PutShort(string id)
    {
        Check(id.Length >= 5 && id.Length <= 15);
        audio.AsSpan(0x278, 64).Clear();
        Encoding.ASCII.GetBytes(id + "\0").CopyTo(audio, 0x278);
        BinaryPrimitives.WriteUInt64LittleEndian(audio.AsSpan(0x288), (ulong)id.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(audio.AsSpan(0x290), 15);
    }

    PutHeap("HON_CivicTypeR_2015", heapA);
    var first = reader.Capture(SyntheticFixture.GameBase, true);
    Check(first.Car.RawId == "HON_CivicTypeR_2015" && first.CarProbeStatus == "ok-heap",
        "Long MSVC string should resolve via guarded heap pointer");

    PutHeap("BMW_M3Coupe_2010", heapB);
    var switched = reader.Capture(SyntheticFixture.GameBase, true);
    Check(switched.Car.RawId == "BMW_M3Coupe_2010" && switched.CarProbeStatus == "ok-heap",
        "Second long car should replace first");

    PutShort("TOY_SupraRZ_98");
    var shortCar = reader.Capture(SyntheticFixture.GameBase, true);
    Check(shortCar.Car.RawId == "TOY_SupraRZ_98" && shortCar.CarProbeStatus == "ok",
        "Short car should switch back to inline encoding");

    PutHeap("HON_CivicTypeR_2015", heapA);
    memory.Blocks.Remove(heapA);
    var unreadable = reader.Capture(SyntheticFixture.GameBase, true);
    Check(unreadable.Attached && unreadable.Car.RawId is null && unreadable.CarProbeStatus == "heap-unreadable",
        "Unreadable heap must not reuse a previous valid car");

    PutHeap("HON_CivicTypeR_2015", heapA);
    memory.Blocks[heapA][0] = (byte)'!';
    var malformed = reader.Capture(SyntheticFixture.GameBase, true);
    Check(malformed.Car.RawId is null && malformed.CarProbeStatus == "heap-invalid-id",
        "Malformed pointer payload must be rejected");

    PutHeap("HON_CivicTypeR_2015", heapA);
    int heapReads = 0;
    memory.BeforeRead = (address, size) =>
    {
        if (address == heapA && ++heapReads == 2)
            memory.Blocks[heapA][0] = (byte)'A';
    };
    var racing = reader.Capture(SyntheticFixture.GameBase, true);
    Check(racing.Car.RawId is null && racing.CarProbeStatus == "heap-changed",
        "Heap changing during a snapshot must not publish a mixed ID");
});

Test("Unsupported and overflowing bases fail without reads", () =>
{
    using var memory = new FixtureMemory(); var reader = new GameSnapshotReader(memory);
    Check(!reader.Capture(ulong.MaxValue, true).Attached); Check(!reader.Capture(0x140000000, false).Attached); Check(memory.Reads == 0);
});
Test("Observed live context 2 FreeRoam / 1 Event; normalized rev is not physical RPM", () =>
{
    foreach (var (context, expected) in new[] { (2, GameActivity.FreeRoam), (1, GameActivity.Event) })
    {
        var game = GameStateInterpreter.Apply(new() { Attached = true }, new(true, true, .13f, null, false, false, context, true, "User x64dbg validation"), true);
        Check(game.Activity == expected); Check(game.Hooks.NormalizedRev == .13f); Check(game.WorldPresence == WorldPresence.InWorld);
    }
});
Test("Unavailable memory clears live telemetry and activity", () =>
{
    var game = GameStateInterpreter.Apply(GameSnapshot.Unavailable("Read failed"), new(true, true, .5f, .2f, true, true, 1, true, "Old"), true);
    Check(game.Activity == GameActivity.Unknown && !game.Hooks.Active && game.Hooks.EventContext is null && game.Hooks.NormalizedRev is null);
});
Test("Captured loading overrides FreeRoam context; zero vectors never prove driving", () =>
{
    Check(games["race-loading"].UiMode == UiMode.Loading);
    var game = GameStateInterpreter.Apply(games["race-loading"], new(true, false, null, null, false, false, 2, true, "Recorded"), true);
    Check(game.Activity == GameActivity.RaceLoading && game.WorldPresence == WorldPresence.Unknown);
});
Test("Presence uses recorded modes, shared snapshot and Southern Europe fallback", () =>
{
    var options = new PresenceOptions(); var now = DateTime.UnixEpoch;
    foreach (var (label, details) in new[] { ("pause_1", "Paused"), ("photomode_1", "Photo Mode"), ("worldmap_1", "Browsing World Map"), ("garage", "Garage"), ("horizon-hub", "Horizon Hub"), ("race-loading", "Loading Race") })
        Check(DiscordPresenceBuilder.Build(games[label], options, now).Details == details, label);
    var roam = new GameSnapshot { Attached = true, WorldPresence = WorldPresence.InWorld, Activity = GameActivity.FreeRoam };
    Check(DiscordPresenceBuilder.Build(roam, options, now).Details == "Exploring Southern Europe");
    Check(DiscordPresenceBuilder.Build(roam with { Activity = GameActivity.Event }, options, now).Details == "Racing");
    Check(DiscordPresenceBuilder.Build(new(), options, now).Details == "Forza Horizon 2");
    // On race loading the second line must be entirely omitted. DiscordIpcClient
    // maps the empty state to no "state" property in the Discord payload.
    var loading = DiscordPresenceBuilder.Build(games["race-loading"], options, now);
    Check(loading.Details == "Loading Race" && loading.State == "", "Loading Race must not show generic fallback");
    var staleCar = games["race-loading"] with { Car = new("TOY_SupraRZ_98", "Toyota Supra RZ"), Activity = GameActivity.RaceLoading };
    Check(DiscordPresenceBuilder.Build(staleCar, options, now).State == "", "Loading Race must not show stale car");
    var activityLoading = new GameSnapshot { Attached = true, Activity = GameActivity.RaceLoading };
    Check(DiscordPresenceBuilder.Build(activityLoading, options, now).State == "", "Activity-based RaceLoading must omit state");
    var fingerprint = DiscordPresenceBuilder.Build(roam, options, now).Fingerprint; options.LargeImageKey = "changed";
    Check(fingerprint != DiscordPresenceBuilder.Build(roam, options, now).Fingerprint, "Asset update was suppressed");
});
Test("Location adapter honestly remains unknown", () => Check(new UnknownLocationResolver().Resolve(new Vector3(100, 20, 400)) is null));
Test("Extracted native stubs match original source byte for byte (no execution)", () =>
{
    Check(Convert.ToHexString(X64TelemetryStubs.BuildAbsoluteJump(0x18000000C)) == baseline.RootElement.GetProperty("Jump").GetString());
    Check(Convert.ToHexString(X64TelemetryStubs.BuildRpmTrampoline(0x123456789000, 0x18000000C)) == baseline.RootElement.GetProperty("Rpm").GetString());
    Check(Convert.ToHexString(X64TelemetryStubs.BuildEventContextTrampoline(0x123456789000, 0x18000000C)) == baseline.RootElement.GetProperty("Event").GetString());
});
Test("InProcess requires supplied guarded copier; native bootstrap is absent", () =>
{
    int calls = 0; using var reader = new InProcessMemoryReader((ulong a, Span<byte> b) => { calls++; b.Fill(42); return a == 0x10000; });
    var bytes = new byte[4]; Check(reader.TryRead(0x10000, bytes) && bytes.All(b => b == 42)); Check(!reader.TryRead(0, bytes) && calls == 1);
    Check(!InProcessIntegration.NativeBootstrapImplemented);
});
Test("Synthetic relocated PE matches full identity, wrong timestamp rejected", () =>
{
    var bytes = new byte[4096]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5A4D); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), 0x4550); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x88), 0x565501ED); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20b);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xA8), 0xF18058); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0xB0), 0x140000000);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xD0), 0x3A18000);
    Check(PeIdentity.TryParse(bytes, out var pe) && pe is not null); Check(MappedImageLocator.Supported(pe!));
    using var memory = new FixtureMemory(); memory.Blocks[0x180000000] = bytes;
    Check(MappedImageLocator.Matches(memory, 0x180000000, pe!)); bytes[0x88] ^= 1; Check(!MappedImageLocator.Matches(memory, 0x180000000, pe!));
});
Test("Discord framing handles truncation and refuses oversized payloads locally", () =>
{
    var read = typeof(DiscordIpcClient).GetMethod("ReadFrameAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    void Frame(byte[] bytes) { using var stream = new MemoryStream(bytes); ((Task)read.Invoke(null, new object[] { stream, CancellationToken.None })!).GetAwaiter().GetResult(); }
    Frame(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 123, 125 });
    bool truncated = false, oversized = false;
    try { Frame(new byte[7]); } catch (EndOfStreamException) { truncated = true; }
    try { Frame(new byte[] { 1, 0, 0, 0, 255, 255, 255, 127 }); } catch (InvalidDataException) { oversized = true; }
    Check(truncated && oversized);
});
Console.WriteLine($"{passed} passed; {failed} failed. Synthetic controls are robustness checks, not additional live observations.");
return failed == 0 ? 0 : 1;

sealed class FixtureMemory : IMemoryReader
{
    internal readonly Dictionary<ulong, byte[]> Blocks = [];
    internal bool Fail; internal int Reads; internal Action<ulong, int>? BeforeRead;
    public bool TryRead(ulong address, Span<byte> destination)
    {
        Reads++; BeforeRead?.Invoke(address, destination.Length); if (Fail) return false;
        foreach (var (start, data) in Blocks)
        {
            if (address < start || address - start > (ulong)data.Length) continue; ulong offset = address - start;
            if ((ulong)destination.Length > (ulong)data.Length - offset) continue; data.AsSpan((int)offset, destination.Length).CopyTo(destination); return true;
        }
        return false;
    }
    public void Dispose() { }
}
