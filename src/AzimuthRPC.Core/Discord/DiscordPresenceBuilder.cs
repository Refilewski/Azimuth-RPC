namespace AzimuthRPC;

internal sealed record DiscordPresence(string Details, string State, DateTime SessionStartUtc, PresenceOptions Options)
{
    internal string Fingerprint => $"{Details}\n{State}\n{Options.LargeImageKey}\n{Options.LargeImageText}\n{Options.SmallImageKey}\n{Options.SmallImageText}\n{SessionStartUtc.Ticks}";
}
internal static class DiscordPresenceBuilder
{
    internal static DiscordPresence Build(GameSnapshot game, PresenceOptions options, DateTime sessionStart)
    {
        string fallback = string.IsNullOrWhiteSpace(options.Details) ? "Exploring Southern Europe" : options.Details;
        bool memoryDrivingSignal = game.Vehicle.Position is not null && game.Vehicle.PositionMirrorsMatch && game.Vehicle.SpeedKmh is float;
        bool liveDrivingContext = game.Attached && game.UiMode == UiMode.Unknown &&
            (game.WorldPresence == WorldPresence.InWorld || game.Activity is GameActivity.FreeRoam or GameActivity.Event || memoryDrivingSignal);
        string exploring = string.IsNullOrWhiteSpace(game.Location) ? fallback : $"Exploring {game.Location}";
        string details = game.UiMode switch
        {
            UiMode.PauseMenu => "Paused",
            UiMode.PhotoMode => "Photo Mode",
            UiMode.WorldMap => "Browsing World Map",
            UiMode.Garage => "Garage",
            _ when game.Activity == GameActivity.RaceLoading => "Loading Race",
            UiMode.Loading => "Loading Race",
            UiMode.HorizonHub => "Horizon Hub",
            _ when liveDrivingContext && game.Activity == GameActivity.Event => "Racing",
            _ when liveDrivingContext => exploring,
            _ => "Forza Horizon 2"
        };
        bool carContext = liveDrivingContext || game.UiMode is UiMode.PauseMenu or UiMode.PhotoMode or UiMode.WorldMap or UiMode.HorizonHub or UiMode.Garage or UiMode.Loading || game.Activity == GameActivity.RaceLoading;
        string? car = game.Car.DisplayName ?? game.Car.RawId;
        string state = string.IsNullOrWhiteSpace(options.State) ? "Forza Horizon 2 via Azimuth" : options.State;
        if (carContext)
        {
            var parts = new List<string>(2);
            if (options.UseLiveCarInPresence && !string.IsNullOrWhiteSpace(car)) parts.Add(car);
            if (liveDrivingContext && options.UseLiveSpeedInPresence && game.Vehicle.SpeedKmh is float speed)
                parts.Add($"{Math.Max(0, (int)(Math.Round(speed / 5f, MidpointRounding.AwayFromZero) * 5))} km/h");
            if (parts.Count > 0) state = string.Join(" | ", parts);
        }
        // Discord should show only "Loading Race", without a stale car,
        // speed or the generic "Forza Horizon 2 via Azimuth" second line.
        if (details == "Loading Race") state = string.Empty;
        return new(details, state, sessionStart, options);
    }
}
