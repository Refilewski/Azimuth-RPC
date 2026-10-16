namespace AzimuthRPC;

internal static class GameStateInterpreter
{
    // Exact current-source priority: loading is independent of event context;
    // no recent-menu timer and no fixed Garage secondary enum are introduced.
    internal static GameSnapshot Apply(GameSnapshot game, Fh2TelemetrySample telemetry, bool installed)
    {
        if (!game.Attached) return game with { Hooks = new(Status: "Memory unavailable"), Activity = GameActivity.Unknown, WorldPresence = WorldPresence.Unknown };
        var hooks = new HookState(installed, telemetry.Active, telemetry.Active ? telemetry.RpmNormalized : null, telemetry.Active ? telemetry.GearRpm : null,
            telemetry.Active && telemetry.Shift, telemetry.Active && telemetry.Backfire, installed ? telemetry.EventContext : null, installed && telemetry.EventHookInstalled, telemetry.Status);
        if (!installed) return game with { Hooks = hooks, Activity = GameActivity.Unknown };
        var activity = game.UiMode switch
        {
            UiMode.Loading => GameActivity.RaceLoading,
            UiMode.HorizonHub or UiMode.Garage => GameActivity.Unknown,
            _ => telemetry.EventContext switch
            {
                2 => GameActivity.FreeRoam,
                1 when telemetry.Active => GameActivity.Event,
                1 when game.UiMode is UiMode.PauseMenu or UiMode.PhotoMode or UiMode.WorldMap => GameActivity.Event,
                1 => GameActivity.RaceLoading,
                _ => GameActivity.Unknown
            }
        };
        return game with { Hooks = hooks, Activity = activity, WorldPresence = game.UiMode == UiMode.Unknown && telemetry.Active ? WorldPresence.InWorld : WorldPresence.Unknown };
    }
}
