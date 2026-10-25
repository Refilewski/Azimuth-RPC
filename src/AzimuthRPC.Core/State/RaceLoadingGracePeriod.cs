namespace AzimuthRPC;

/// <summary>
/// Avoid reporting "Loading Race" during a brief loss of the telemetry-active bit
/// while an event context is still confirmed. A real loading UI always wins.
/// This is a bounded display-only grace period, not a new game-state detector.
/// </summary>
internal sealed class RaceLoadingGracePeriod
{
    private static readonly TimeSpan MaxTelemetryGap = TimeSpan.FromSeconds(2);
    private DateTime? _lastConfirmedRaceUtc;

    internal GameSnapshot Apply(GameSnapshot snapshot, DateTime nowUtc)
    {
        // Do not carry a previous race into a different activity, game process,
        // missing hook, or an explicitly detected loading screen.
        if (!snapshot.Attached || !snapshot.Hooks.Installed ||
            snapshot.Hooks.EventContext != 1 || snapshot.UiMode == UiMode.Loading)
        {
            Reset();
            return snapshot;
        }

        if (snapshot.Activity == GameActivity.Event && snapshot.Hooks.Active)
        {
            _lastConfirmedRaceUtc = nowUtc;
            return snapshot;
        }

        // UiMode.Unknown is not proof of a loading screen. A single inactive
        // hook sample should not replace a recently observed racing presence.
        if (snapshot.UiMode == UiMode.Unknown &&
            snapshot.Activity == GameActivity.RaceLoading &&
            !snapshot.Hooks.Active &&
            _lastConfirmedRaceUtc is DateTime last &&
            nowUtc >= last && nowUtc - last <= MaxTelemetryGap)
        {
            return snapshot with { Activity = GameActivity.Event };
        }

        return snapshot;
    }

    internal void Reset() => _lastConfirmedRaceUtc = null;
}
