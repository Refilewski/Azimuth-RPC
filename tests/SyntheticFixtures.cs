using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AzimuthRPC.Tests;

// Purpose-built memory, not a captured heap. Addresses are deliberately invented.
internal sealed class SyntheticFixture
{
    public string Label { get; set; } = "";
    public bool Frontend { get; set; }
    public int PauseActiveBits { get; set; }
    public int PauseSubtype { get; set; }
    public int MapActiveBits { get; set; }
    public int MapSubtype { get; set; }
    public int LoadingActiveBits { get; set; }
    public int FlowPrimary { get; set; }
    public int FlowSecondary { get; set; }
    public bool ManagerLoading { get; set; }
    public string CarId { get; set; } = "";
    public float SpeedMph { get; set; }
    public int? EventContext { get; set; }
    public bool TelemetryActive { get; set; }
    internal const ulong GameBase = 0x180000000, Audio = 0x7000000000, Player = 0x7100000000,
        Registry = 0x7200000000, Manager = 0x7300000000, Simulation = 0x7400000000;

    internal static SyntheticFixture[] Load(string directory) =>
        JsonSerializer.Deserialize<SyntheticFixture[]>(File.ReadAllText(Path.Combine(directory, "scenarios.json")))
        ?? throw new InvalidDataException("Missing synthetic scenarios");

    internal Dictionary<ulong, byte[]> Memory()
    {
        var audio = new byte[0x638];
        var registry = new byte[0xEA0];
        var player = new byte[0x58];
        var manager = new byte[0x40];
        var simulation = new byte[0x28];
        static void I32(byte[] b, int at, int n) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), n);
        static void F32(byte[] b, int at, float n) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), n);
        static void U64(byte[] b, int at, ulong n) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), n);
        void Record(int at, string name, int active, int subtype, int guard)
        {
            I32(registry, at, 0x3F800000); I32(registry, at + 4, active);
            I32(registry, at + 8, subtype); I32(registry, at + 12, guard);
            Encoding.ASCII.GetBytes(name + "\0").CopyTo(registry, at + 16);
        }
        F32(audio, 0x38, Frontend ? 0 : -80);
        foreach (int at in new[] { 0x3C, 0x40, 0x44 }) F32(audio, at, -80);
        F32(audio, 0x90, SpeedMph);
        foreach (int at in new[] { 0xA0, 0x190 })
        {
            F32(audio, at, 100); F32(audio, at + 4, 20); F32(audio, at + 8, 300);
        }
        F32(audio, 0x170, SpeedMph * 0.44704f);
        Encoding.ASCII.GetBytes(CarId + "\0").CopyTo(audio, 0x278);
        U64(audio, 0x630, Registry);
        Record(0xC00, "BASE_Paused", PauseActiveBits, PauseSubtype, 2);
        Record(0xE80, "BASE_Map_Paused", MapActiveBits, MapSubtype, 2);
        Record(0x7A0, "BASE_Loading", LoadingActiveBits, 0, 1);
        U64(player, 0x48, Manager); U64(player, 0x50, Simulation);
        U64(manager, 0, GameBase + 0x2C5F8C8);
        for (int at = 0x29; at <= 0x2C; at++) manager[at] = 1;
        manager[0x2D] = manager[0x2E] = ManagerLoading ? (byte)0 : (byte)1;
        U64(simulation, 0, GameBase + 0x2C5FD70);
        I32(simulation, 0x10, FlowPrimary); I32(simulation, 0x20, FlowSecondary);
        I32(simulation, 0x18, 1); I32(simulation, 0x24, 5);
        byte[] Ptr(ulong n) { var b = new byte[8]; U64(b, 0, n); return b; }
        return new()
        {
            [GameBase + 0x365D3D8] = Ptr(Audio), [GameBase + 0x34ABA60] = Ptr(Player),
            [Audio] = audio, [Registry] = registry, [Player] = player,
            [Manager] = manager, [Simulation] = simulation
        };
    }
}
