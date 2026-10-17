using System.Numerics;

namespace AzimuthRPC;

public enum WorldPresence { Unknown, InWorld }
public enum GameActivity { Unknown, FreeRoam, Event, Loading, RaceLoading }
public sealed record RootPointers(ulong PlayerManager = 0, ulong AudioEngine = 0, ulong Registry = 0);
public sealed record CarState(string? RawId = null, string? DisplayName = null);
public sealed record VehicleState(float? SpeedMph = null, Vector3? Position = null, Vector3? Velocity = null, bool PositionMirrorsMatch = false)
{
    public float? SpeedKmh => SpeedMph * 1.609344f;
    public float? VelocityKmh => Velocity is { } v && v.Length() * 3.6f is float kmh && float.IsFinite(kmh) && kmh <= 1000 ? kmh : null;
}
public sealed record HookState(bool Installed = false, bool Active = false, float? NormalizedRev = null, float? GearRpm = null, bool Shift = false, bool Backfire = false, int? EventContext = null, bool EventHookInstalled = false, string Status = "Not installed");
public sealed record RawState(bool FrontendValid = false, bool FrontendHubFamily = false, bool FlowValid = false, int FlowPrimary = 0, int FlowSecondary = 0, bool GameManagerValid = false, bool GameManagerLoading = false, byte LoadA = 255, byte LoadB = 255);
public sealed record GameSnapshot
{
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Attached { get; init; }
    public ulong GameBase { get; init; }
    public string Build { get; init; } = "FH2 Xbox One 1.0.0.64";
    public RootPointers Roots { get; init; } = new();
    public CarState Car { get; init; } = new();
    // Diagnostic only: classification of the car ID field, no raw heap bytes.
    public string CarProbeStatus { get; init; } = "Not sampled";
    public VehicleState Vehicle { get; init; } = new();
    public UiMode UiMode { get; init; }
    public GameActivity Activity { get; init; }
    public WorldPresence WorldPresence { get; init; }
    public HookState Hooks { get; init; } = new();
    public RawState Raw { get; init; } = new();
    public string? Location { get; init; }
    public string? Error { get; init; }
    public static GameSnapshot Unavailable(string reason) => new() { Error = reason };
}

public interface ILocationResolver { string? Resolve(Vector3 position); }
public sealed class UnknownLocationResolver : ILocationResolver { public string? Resolve(Vector3 position) => null; }
