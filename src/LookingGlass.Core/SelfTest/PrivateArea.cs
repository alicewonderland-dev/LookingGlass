namespace LookingGlass.Core.SelfTest;

/// <summary>What the game's housing manager says the player is in (FFXIVClientStructs' <c>HousingTerritoryType</c>).</summary>
public enum HousingKind {
    None,
    Outdoor,
    Indoor,
    Workshop,
}

/// <summary>
/// What the plugin reads about where the player is, for <see cref="PrivateArea"/>: each fact null if it couldn't be read.
/// Read on the framework thread by the plugin's <c>PrivateAreaReader</c>; nothing here is ever logged or reported.
/// </summary>
/// <param name="Territory">The territory (a <c>TerritoryType</c> row); 0 during a loading screen.</param>
/// <param name="IntendedUse">The territory's intended use, as the game holds it (<c>GameMain.CurrentTerritoryIntendedUseId</c>).</param>
/// <param name="SheetUse">The same, read from the <c>TerritoryType</c> sheet: the two must agree.</param>
/// <param name="Housing">The housing manager's kind of place.</param>
/// <param name="HouseId">The indoor house's ID (<c>GetCurrentIndoorHouseId</c>); invalid as 0 or <see cref="ulong.MaxValue"/>.</param>
/// <param name="Plot">The plot (<c>GetCurrentPlot</c>): -128 or -127 for apartments.</param>
/// <param name="Room">The room (<c>GetCurrentRoom</c>): 0 in an apartment lobby and on a house's own floors.</param>
/// <param name="InWorkshop">The housing manager's <c>IsInWorkshop</c>.</param>
/// <param name="Name">The place's name, for the window only ("Limsa Lominsa Lower Decks").</param>
public sealed record PlaceFacts(
    uint Territory,
    uint? IntendedUse,
    uint? SheetUse,
    HousingKind? Housing,
    ulong? HouseId,
    int? Plot,
    int? Room,
    bool? InWorkshop,
    string? Name = null) {
    /// <summary>Nothing could be read (not logged in, or the reader failed): never private.</summary>
    public static readonly PlaceFacts Unreadable = new(0, null, null, null, null, null, null, null);
}

/// <summary>The kind of place <see cref="PrivateArea.Judge"/> found.</summary>
public enum PlaceKind {
    /// <summary>A loading screen (no territory yet).</summary>
    Loading,

    /// <summary>Something couldn't be read, or the game's facts disagree: never private (fail closed).</summary>
    Unknown,

    InnRoom,

    /// <summary>Inside a house, any of its own floors.</summary>
    House,

    /// <summary>Private chambers, or a free company's chambers.</summary>
    Chambers,

    /// <summary>The company workshop.</summary>
    Workshop,

    /// <summary>Inside an apartment (not the lobby).</summary>
    Apartment,

    /// <summary>An apartment building's lobby: everyone in the building passes through.</summary>
    ApartmentLobby,

    /// <summary>A ward or a yard.</summary>
    Outdoor,

    /// <summary>Anywhere else.</summary>
    Elsewhere,
}

/// <summary>What <see cref="PrivateArea.Judge"/> decided.</summary>
public readonly record struct PlaceVerdict(PlaceKind Kind) {
    /// <summary>The self-test may run here.</summary>
    public bool Private => PrivateArea.IsPrivate(this.Kind);

    /// <summary>
    /// A step that types a line also waits while anyone else is near (see <see cref="PrivateArea.NearbyLimit"/>): only an
    /// inn room is certainly empty.
    /// </summary>
    public bool NeedsNobodyNear => this.Kind is PlaceKind.House or PlaceKind.Chambers or PlaceKind.Workshop or PlaceKind.Apartment;
}

/// <summary>
/// Where the self-test may run (owner, 2026-10-13): an inn room; inside a house, any floor, its private chambers, a free
/// company's chambers or the company workshop; inside an apartment. Never an apartment lobby, a yard or a ward. Pure: the
/// plugin reads the facts (<see cref="PlaceFacts"/>), this decides. It fails closed: anything it can't read, or doesn't
/// recognise, or where the game's facts disagree, is "not private". See docs/self-test-plan.md, "The private-area check".
/// </summary>
public static class PrivateArea {
    /// <summary>Territory intended uses (FFXIVClientStructs' <c>TerritoryIntendedUse</c>).</summary>
    public const uint InnUse = 2;

    public const uint HousingOutdoorUse = 13;

    public const uint HousingIndoorUse = 14;

    /// <summary>The eight inn rooms (territory use 2 on the 7.56 sheets), for reference and tests.</summary>
    public static readonly IReadOnlySet<uint> InnRooms = new HashSet<uint> { 177, 178, 179, 429, 629, 843, 990, 1205 };

    /// <summary>The apartment lobbies, which have use 14 too: a deny list.</summary>
    public static readonly IReadOnlySet<uint> ApartmentLobbies = new HashSet<uint> { 573, 574, 575, 654, 985 };

    /// <summary>Apartments' plot numbers.</summary>
    public static readonly IReadOnlySet<int> ApartmentPlots = new HashSet<int> { -128, -127 };

    /// <summary>How near, in yalms, someone else keeps a typing step waiting in a house or apartment (Say's reach is less).</summary>
    public const double NearbyLimit = 30;

    /// <summary>How long the answer must stay the same before a place counts as private (the housing facts lag a zone change).</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    public static bool IsPrivate(PlaceKind kind) =>
        kind is PlaceKind.InnRoom or PlaceKind.House or PlaceKind.Chambers or PlaceKind.Workshop or PlaceKind.Apartment;

    /// <summary>Decides what kind of place the facts describe. Never throws.</summary>
    public static PlaceVerdict Judge(PlaceFacts? facts) {
        if (facts == null) {
            return new PlaceVerdict(PlaceKind.Unknown);
        }

        if (facts.Territory == 0) {
            return new PlaceVerdict(PlaceKind.Loading);
        }

        // Both readings of the territory's use must be there and agree: a patch that moves one of them fails closed.
        if (facts.IntendedUse is not { } use || facts.SheetUse is not { } sheetUse || use != sheetUse || facts.Housing is not { } housing) {
            return new PlaceVerdict(PlaceKind.Unknown);
        }

        if (use == InnUse) {
            return new PlaceVerdict(housing == HousingKind.None ? PlaceKind.InnRoom : PlaceKind.Unknown);
        }

        if (use == HousingOutdoorUse || housing == HousingKind.Outdoor) {
            return new PlaceVerdict(PlaceKind.Outdoor);
        }

        if (use != HousingIndoorUse) {
            return new PlaceVerdict(housing == HousingKind.None ? PlaceKind.Elsewhere : PlaceKind.Unknown);
        }

        if (ApartmentLobbies.Contains(facts.Territory)) {
            return new PlaceVerdict(PlaceKind.ApartmentLobby);
        }

        if (housing == HousingKind.Workshop) {
            return new PlaceVerdict(PlaceKind.Workshop);
        }

        if (housing != HousingKind.Indoor) {
            // Indoors by the sheet, not by the housing manager: they disagree.
            return new PlaceVerdict(PlaceKind.Unknown);
        }

        if (facts.InWorkshop == true) {
            return new PlaceVerdict(PlaceKind.Workshop);
        }

        if (facts.HouseId is not { } house || house == 0 || house == ulong.MaxValue || facts.Plot is not { } plot || facts.Room is not { } room || room < 0) {
            return new PlaceVerdict(PlaceKind.Unknown);
        }

        if (ApartmentPlots.Contains(plot)) {
            return new PlaceVerdict(room >= 1 ? PlaceKind.Apartment : PlaceKind.ApartmentLobby);
        }

        if (plot < 0) {
            return new PlaceVerdict(PlaceKind.Unknown);
        }

        return new PlaceVerdict(room == 0 ? PlaceKind.House : PlaceKind.Chambers);
    }
}

/// <summary>
/// The private-area check over time: a place counts as private only once it has given the same answer for
/// <see cref="PrivateArea.SettleTime"/>; a change of territory, or of the answer, starts that again. Not thread-safe: the
/// runner keeps one.
/// </summary>
public sealed class PrivateAreaWatch {
    private uint? _territory;
    private PlaceKind? _kind;
    private DateTimeOffset _since;

    /// <summary>The last answer, settled or not.</summary>
    public PlaceVerdict Last { get; private set; } = new(PlaceKind.Unknown);

    /// <summary>Takes in what was read now.</summary>
    /// <returns>Whether the place counts as private now: private, and settled.</returns>
    public bool Observe(PlaceFacts? facts, DateTimeOffset now) {
        var verdict = PrivateArea.Judge(facts);
        var territory = facts?.Territory;
        if (territory != this._territory || verdict.Kind != this._kind) {
            this._territory = territory;
            this._kind = verdict.Kind;
            this._since = now;
        }

        this.Last = verdict;
        return verdict.Private && now - this._since >= PrivateArea.SettleTime;
    }

    /// <summary>
    /// The last answer, whatever it is, has stayed the same for <see cref="PrivateArea.SettleTime"/>: "outside" only once it is
    /// steady, not for a moment while the housing facts catch up with a zone change.
    /// </summary>
    public bool Steady(DateTimeOffset now) => this._kind != null && now - this._since >= PrivateArea.SettleTime;

    /// <summary>How long until the answer has settled, if it is private but not settled yet.</summary>
    public TimeSpan UntilSettled(DateTimeOffset now) {
        var left = PrivateArea.SettleTime - (now - this._since);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
