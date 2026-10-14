namespace LookingGlass.Core.Client;

/// <summary>
/// What the game holds about a party finder listing, as read when a "&lt;pfinder&gt;" placeholder is resolved (the
/// plugin's <c>GameLinks</c>), to tell whether the listing is limited to its leader's home world.
/// </summary>
/// <param name="ListingId">The listing the chat log agent holds as linked (<c>AgentChatLog.LinkedPartyFinderId</c>).</param>
/// <param name="OwnListingId">The player's own recruitment (<c>AgentLookingForGroup.OwnListingId</c>), 0 for none.</param>
/// <param name="LimitRecruitingToWorld">
/// The player's own recruitment criteria as last set (<c>StoredRecruitmentInfo.LimitRecruitingToWorld</c>): 0 when
/// limited to the home world, 1 when open to the data centre.
/// </param>
/// <param name="LastViewedId">The listing the party finder showed last (<c>LastViewedListing.ListingId</c>).</param>
/// <param name="LastViewedJoinConditions">That listing's search area flags (<c>LastViewedListing.JoinConditionFlags</c>).</param>
public readonly record struct ListingFacts(ulong ListingId, uint OwnListingId, byte LimitRecruitingToWorld, ulong LastViewedId, byte LastViewedJoinConditions) {
    /// <summary>The listing is the player's own recruitment.</summary>
    public bool IsOwn => this.OwnListingId != 0 && this.OwnListingId == this.ListingId;

    /// <summary>The listing is the one the party finder showed last.</summary>
    public bool IsLastViewed => this.LastViewedId != 0 && this.LastViewedId == this.ListingId;
}

/// <summary>Where <see cref="ListingWorld.Decide"/> took its answer from.</summary>
public enum ListingWorldSource {
    /// <summary>The player's own recruitment criteria, as last set.</summary>
    OwnRecruitment,

    /// <summary>The listing the party finder showed last (someone else's).</summary>
    LastViewed,

    /// <summary>Neither: taken as open to other worlds.</summary>
    Unknown,
}

/// <summary>
/// Whether a linked party finder listing is limited to its leader's home world (see docs/design.md, the "&lt;pfinder&gt;"
/// placeholder). That only changes whether the link shows the cross-world mark.
/// </summary>
public static class ListingWorld {
    /// <summary>The "world" search area in a listing's join conditions (FFXIVClientStructs' <c>JoinCondition.World</c>).</summary>
    public const byte WorldCondition = 1 << 3;

    /// <summary>
    /// The player's own recruitment goes by its criteria as last set, which the game changes when the recruitment is
    /// edited; the party finder's last shown listing can still hold how it was before the edit. Anyone else's listing
    /// goes by the last shown listing (where its chat button is). Otherwise it isn't known, and taken as open to other
    /// worlds, as most listings are.
    /// </summary>
    public static (bool HomeWorldOnly, ListingWorldSource Source) Decide(ListingFacts facts) =>
        facts.IsOwn ? (facts.LimitRecruitingToWorld == 0, ListingWorldSource.OwnRecruitment)
        : facts.IsLastViewed ? ((facts.LastViewedJoinConditions & WorldCondition) != 0, ListingWorldSource.LastViewed)
        : (false, ListingWorldSource.Unknown);
}
