using System.Buffers.Binary;
using System.Numerics;
using System.Text;
namespace AzimuthRPC;

public sealed class GameSnapshotReader
{
    private readonly IMemoryReader memory;
    private readonly ILocationResolver _locationResolver;
    private string? _lastCarId;
    private string? _displayName;

    public GameSnapshotReader(IMemoryReader memory, ILocationResolver? locationResolver = null)
    {
        this.memory = memory;
        _locationResolver = locationResolver ?? new UnknownLocationResolver();
    }

    public GameSnapshot Capture(ulong gameBase, bool supportedBuild)
    {
        GameSnapshot initial = CaptureOnce(gameBase, supportedBuild);
        if (!initial.Attached || initial.Car.RawId is not null || !ShouldRetryCarProbe(initial.CarProbeStatus))
            return initial;

        // Re-evaluate roots, AudioEngine and the car field from scratch once.
        // Never reuse a stale car when a garage/race transition interrupts a read.
        GameSnapshot retried = CaptureOnce(gameBase, supportedBuild);
        if (!retried.Attached)
            return retried;

        return retried with
        {
            CarRetryStatus = retried.Car.RawId is null
                ? $"failed:{initial.CarProbeStatus}->{retried.CarProbeStatus}"
                : $"recovered:{initial.CarProbeStatus}->{retried.CarProbeStatus}"
        };
    }

    private static bool ShouldRetryCarProbe(string probeStatus) => probeStatus is
        "invalid-character" or "unterminated" or "too-short" or "missing-underscore"
        or "heap-invalid-pointer" or "heap-unreadable" or "heap-changed"
        or "heap-unterminated" or "heap-invalid-id" or "inline-changed";

    private GameSnapshot CaptureOnce(ulong gameBase, bool supportedBuild)
    {
        if (!supportedBuild) return GameSnapshot.Unavailable("Unsupported FH2 PE identity");
        if (!MemoryRead.Pointer(gameBase)) return GameSnapshot.Unavailable("Invalid game base");
        var frame = new PointerSnapshot(memory);
        var audioBytes = frame.Read(checked(gameBase + UiModeReader.AudioEngineRva), 8);
        var playerBytes = frame.Read(checked(gameBase + UiModeReader.PlayerManagerRva), 8);
        ulong audio = audioBytes is null ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(audioBytes);
        ulong player = playerBytes is null ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(playerBytes);
        if (!MemoryRead.Pointer(audio) || !MemoryRead.Pointer(player)) return GameSnapshot.Unavailable("Root pointers unavailable");
        var raw = frame.Read(audio, 0x2C0);
        if (raw is null) return GameSnapshot.Unavailable("Audio/vehicle snapshot read failed");
        var ui = UiModeReader.ReadSnapshot(gameBase, true, frame.Read);
        string? car = ReadCar(raw.AsSpan(0x278, 64), checked(audio + 0x278), frame, out string carProbeStatus);
        if (car != _lastCarId) { _lastCarId = car; _displayName = FriendlyCarName(car); }
        float mph = F32(raw, 0x90);
        float? speed = float.IsFinite(mph) && mph >= 0 && mph <= 400 ? mph : null;
        var position = Vector(raw, 0xA0, 100000);
        var mirror = Vector(raw, 0x190, 100000);
        bool mirrors = position.HasValue && mirror.HasValue && MathF.Abs(position.Value.X - mirror.Value.X) <= 1.5f && MathF.Abs(position.Value.Y - mirror.Value.Y) <= 1.5f && MathF.Abs(position.Value.Z - mirror.Value.Z) <= 1.5f;
        var vehicle = new VehicleState(speed, position, Vector(raw, 0x170, 500), mirrors);
        var registryBytes = frame.Read(audio + UiModeReader.RegistryPointerOffset, 8);
        ulong registry = registryBytes is null ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(registryBytes);
        if (!frame.Validate()) return GameSnapshot.Unavailable("Pointer chain changed while reading");
        string? location = position is Vector3 p ? _locationResolver.Resolve(p) : null;
        return new GameSnapshot
        {
            Attached = true,
            GameBase = gameBase,
            Roots = new(player, audio, registry),
            Car = new(car, _displayName),
            CarProbeStatus = carProbeStatus,
            Vehicle = vehicle,
            UiMode = ui.Mode,
            Raw = new(ui.Frontend.Valid, ui.Frontend.HubFamily, ui.Flow.Valid, ui.Flow.Primary, ui.Flow.Secondary, ui.GameManager.Valid, ui.GameManager.Loading, ui.GameManager.LoadA, ui.GameManager.LoadB),
            WorldPresence = VehiclePresence(ui.Mode, car, vehicle),
            Location = location
        };
    }
    private static WorldPresence VehiclePresence(UiMode mode, string? car, VehicleState v)
    {
        // Preserved current-source fallback, not a confirmed gameplay detector.
        if (mode != UiMode.Unknown || string.IsNullOrWhiteSpace(car) || v.Position is null || !v.PositionMirrorsMatch || v.SpeedKmh is not float speed || v.VelocityKmh is not float velocity) return WorldPresence.Unknown;
        return speed <= 3 && velocity <= 5 || MathF.Abs(speed - velocity) <= MathF.Max(10, speed * .15f) ? WorldPresence.InWorld : WorldPresence.Unknown;
    }
    private static float F32(byte[] data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
    private static Vector3? Vector(byte[] data, int offset, float bound)
    {
        float x = F32(data, offset), y = F32(data, offset + 4), z = F32(data, offset + 8);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && MathF.Abs(x) <= bound && MathF.Abs(y) <= bound && MathF.Abs(z) <= bound ? new Vector3(x, y, z) : null;
    }
    private static string? ReadCar(ReadOnlySpan<byte> bytes, ulong fieldAddress, PointerSnapshot frame, out string status)
    {
        // Candidate MSVC std::string (32-byte object): 16-byte buffer/pointer,
        // followed by 64-bit size and capacity. Short IDs remain inline.
        // This is a guarded fallback, not yet a live-validated FH2 layout.
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        ulong capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        if (length is >= 16 and <= 127 && capacity >= length && capacity <= 4096)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (!MemoryRead.Pointer(pointer))
            {
                status = "heap-invalid-pointer";
                return null;
            }

            byte[]? first = frame.Read(pointer, (int)length + 1);
            byte[]? second = frame.Read(pointer, (int)length + 1);
            byte[]? currentField = frame.Read(fieldAddress, 32);
            if (first is null || second is null)
            {
                status = "heap-unreadable";
                return null;
            }
            if (!first.AsSpan().SequenceEqual(second) || currentField is null ||
                !bytes[..32].SequenceEqual(currentField))
            {
                status = "heap-changed";
                return null;
            }
            if (first[(int)length] != 0)
            {
                status = "heap-unterminated";
                return null;
            }
            if (!ValidCarId(first.AsSpan(0, (int)length)))
            {
                status = "heap-invalid-id";
                return null;
            }

            status = "ok-heap";
            return Encoding.ASCII.GetString(first, 0, (int)length);
        }

        int end = bytes.IndexOf((byte)0);
        if (end < 0 || end > 63)
        {
            status = "unterminated";
            return null;
        }
        if (end == 0)
        {
            status = "empty";
            return null;
        }
        if (end < 5)
        {
            status = "too-short";
            return null;
        }
        if (!ValidCarId(bytes[..end]))
        {
            status = "invalid-character";
            return null;
        }

        // The inline identifier can change after the bulk AudioEngine read.
        // Reject torn snapshots instead of attributing an old ID to a new car.
        byte[]? currentInline = frame.Read(fieldAddress, 32);
        if (currentInline is null || !bytes[..32].SequenceEqual(currentInline))
        {
            status = "inline-changed";
            return null;
        }
        status = "ok";
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static bool ValidCarId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || !bytes.Contains((byte)'_')) return false;
        foreach (byte b in bytes)
            if (!((b >= 'A' && b <= 'Z') ||
                  (b >= 'a' && b <= 'z') ||
                  (b >= '0' && b <= '9') || b == '_'))
                return false;
        return true;
    }
    private static string? FriendlyCarName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Confirmed from live FH2 dumps.
        if (raw.Equals("LAM_Huracan_15", StringComparison.OrdinalIgnoreCase))
            return "Lamborghini Huracan LP 610-4";
        if (raw.Equals("TOY_SupraRZ_98", StringComparison.OrdinalIgnoreCase))
            return "Toyota Supra RZ";

        string[] parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return raw;

        var brands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LAM"] = "Lamborghini",
            ["FER"] = "Ferrari",
            ["FOR"] = "Ford",
            ["BMW"] = "BMW",
            ["AUD"] = "Audi",
            ["CHE"] = "Chevrolet",
            ["DOD"] = "Dodge",
            ["NIS"] = "Nissan",
            ["HON"] = "Honda",
            ["MAZ"] = "Mazda",
            ["MCL"] = "McLaren",
            ["MER"] = "Mercedes-Benz",
            ["POR"] = "Porsche",
            ["SUB"] = "Subaru",
            ["TOY"] = "Toyota",
            ["VOL"] = "Volvo",
            ["VW"] = "Volkswagen",
            ["AST"] = "Aston Martin",
            ["ALF"] = "Alfa Romeo",
            ["BUG"] = "Bugatti",
            ["JAG"] = "Jaguar",
            ["LOT"] = "Lotus",
            ["MAS"] = "Maserati",
            ["RUF"] = "RUF",
            ["TES"] = "Tesla",
            ["LEX"] = "Lexus",
            ["MITS"] = "Mitsubishi",
            ["PEU"] = "Peugeot",
            ["REN"] = "Renault",
            ["MIN"] = "MINI",
        };

        string brand = brands.TryGetValue(parts[0], out string? b) ? b : parts[0];
        string model = string.Join(' ', parts.Skip(1).Where(x => !(x.Length == 2 && int.TryParse(x, out _))));
        model = model.Replace('-', ' ');
        return string.IsNullOrWhiteSpace(model) ? brand : $"{brand} {model}";
    }

}
