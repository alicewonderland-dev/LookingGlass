namespace LookingGlass.Core.Client;

/// <summary>
/// Zones where LookingGlass sends nothing, so the game gets everything typed: talking in a channel (or in local chat)
/// can't start there and ends on arrival (<see cref="StickyEnd.QuietPlace"/>), /lgc, /lgl and a channel window's input
/// send nothing, and no channel window opens by itself. Messages still arrive. One line says so on each arrival (see
/// <see cref="QuietPlaceVisit"/>), rather than "Not sent" for every line. Each is a <c>TerritoryType</c> row, as the game
/// reports where the player is (0 during a loading screen, which is no such zone).
/// </summary>
public static class QuietPlaces {
    /// <summary>
    /// Keybound Brawler (Gold Saucer, <c>ContentFinderCondition</c> 1095), played by typing, which talking in a channel
    /// kept from the game. Its lobby, Keybound Square (1360), is not one.
    /// </summary>
    private static readonly HashSet<uint> Territories = [1359];

    /// <summary>LookingGlass sends nothing in <paramref name="territory"/>.</summary>
    public static bool IsQuiet(uint territory) => Territories.Contains(territory);

    /// <summary>Said once on arriving in one, and when asked to talk in a channel there.</summary>
    public const string Disabled = "Outgoing LookingGlass messages are disabled in this zone.";
}

/// <summary>
/// When to say <see cref="QuietPlaces.Disabled"/>: once on arriving in a quiet zone, and again only after leaving it (a
/// loading screen counts as leaving). Talking in a channel ending there says it itself, in its "Stopped talking in" line,
/// and takes this visit's turn (see <see cref="StickyMessages.Ended"/>).
/// </summary>
public sealed class QuietPlaceVisit {
    private bool _said;

    /// <summary>Whether to say it now, in <paramref name="territory"/>; if so, it counts as said for this visit.</summary>
    public bool ShouldSay(uint territory) {
        if (!QuietPlaces.IsQuiet(territory)) {
            this._said = false;
            return false;
        }

        if (this._said) {
            return false;
        }

        this._said = true;
        return true;
    }
}
