using LookingGlass.Core.Crypto;
using LookingGlass.Core.Util;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>A player right-clicked in a menu (the game's, or ChatTwo's): their name and home world, as the game names it.</summary>
public sealed record InviteTarget(string Name, string WorldName) {
    /// <summary>"Name@World", as LookingGlass names players everywhere.</summary>
    public string Who => $"{this.Name}@{this.WorldName}";

    /// <summary>The same character as <paramref name="user"/>: the same name and world, ignoring case.</summary>
    public bool Is(User user) =>
        string.Equals(user.Name, this.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(user.WorldName, this.WorldName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A channel offered in "Invite to LookingGlass": its tag (in its colour), its name, and why it can't be picked, if it can't.</summary>
/// <param name="Colour">The channel's colour (a UIColor row or a custom colour), or null for the default (the tag in LookingGlass blue).</param>
/// <param name="Unavailable">Why the player can't be invited to it (already in it, say), shown after the name; null if they can.</param>
public sealed record InviteOffer(string ChannelId, string Tag, ChannelColour? Colour, string Name, string? Unavailable) {
    public bool Available => this.Unavailable == null;

    /// <summary>What follows the tag in the menu: the name, and why it can't be picked.</summary>
    public string Rest => this.Unavailable == null ? $" {this.Name}" : $" {this.Name} ({this.Unavailable})";

    /// <summary>The whole menu entry, as "[sky] Tea party" or "[sky] Tea party (already a member)".</summary>
    public string Label => this.Tag + this.Rest;
}

/// <summary>
/// Right-click invites (see "Context menu invites" in docs/design.md): which channels "Invite to LookingGlass" offers for
/// a player, and what is said when an invite is sent or fails. The plugin only reads the menus' targets and shows these;
/// the invite itself is <see cref="ClientSession.InviteAsync"/>, as from the channel's Invite button.
/// </summary>
public static class ContextInvites {
    /// <summary>The menu item, in the game's menus and in ChatTwo's.</summary>
    public const string MenuLabel = "Invite to LookingGlass";

    /// <summary>
    /// The most channels offered: the game's menus hold 32 lines at most, and a few must stay for its own. A player with
    /// more channels they can invite to than this gets the first ones, by number (see <see cref="Offers"/>).
    /// </summary>
    public const int MaxOffers = 24;

    /// <summary>The longest channel name shown in the menu, before "…".</summary>
    public const int MaxNameLength = 32;

    /// <summary>
    /// The game's menus that open on a player, by the window they open from (null: a character right-clicked in the world).
    /// Others are left alone: a menu about something else can still hold the last player's name and world.
    /// </summary>
    private static readonly HashSet<string> PlayerMenus = new(StringComparer.Ordinal) {
        "ChatLog",
        "_PartyList",
        "PartyMemberList",
        "FriendList",
        "SocialList",
        "ContactList",
        "FreeCompany",
        "LinkShell",
        "CrossWorldLinkshell",
        "ContentMemberList",
        "BeginnerChatList",
        "LookingForGroup",
        "_TargetInfo",
        "_TargetInfoMainTarget",
        "_FocusTargetInfo",
    };

    /// <summary>Whether the game's menu opened from <paramref name="addonName"/> (null: the world) can be about a player.</summary>
    public static bool IsPlayerMenu(string? addonName) => addonName == null || PlayerMenus.Contains(addonName);

    /// <summary>
    /// Whether <paramref name="name"/> is a player character's name: a forename and a surname, each of 2 to 15 letters
    /// (with ' and -), 20 letters at most in all. NPCs, minions and retainers (one word) aren't.
    /// </summary>
    public static bool IsPlayerName(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return false;
        }

        var parts = name.Split(' ');
        if (parts.Length != 2 || name.Length > 21) {
            return false;
        }

        foreach (var part in parts) {
            if (part.Length is < 2 or > 15 || !char.IsAsciiLetter(part[0])) {
                return false;
            }

            foreach (var c in part) {
                if (!char.IsAsciiLetter(c) && c != '\'' && c != '-') {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// What the menu offers for <paramref name="target"/>: each channel the user can invite to, in number order. Empty, so
    /// the menu has no "Invite to LookingGlass", when the user isn't connected and registered, the target isn't a
    /// player's name with a home world, is the user themselves, or the user can invite to no channel.
    /// <para>
    /// A channel can be invited to where the user is a moderator or the admin under their current keys (an old key's place
    /// has no rank; a forgotten one isn't listed). One the target is already in, or invited to, is offered greyed out, with
    /// why, as is one whose name isn't known yet (an invite carries it), and every channel if the user blocked them.
    /// </para>
    /// </summary>
    /// <param name="slots">The channels' command numbers (see <see cref="CommandSlots"/>).</param>
    /// <param name="nicknames">The channels' nicknames (see <see cref="ChannelNicknames"/>).</param>
    /// <param name="colours">The channels' colours (see <see cref="ChannelColours"/>).</param>
    /// <param name="nicknameTags">Whether chat tags show nicknames (the setting).</param>
    /// <param name="advanced">Advanced mode: the words for a channel whose name isn't known yet.</param>
    public static IReadOnlyList<InviteOffer> Offers(SessionSnapshot snapshot, InviteTarget target, IReadOnlyDictionary<string, int> slots,
        IReadOnlyDictionary<string, string> nicknames, IReadOnlyDictionary<string, ChannelColour> colours, bool nicknameTags, bool advanced) {
        if (snapshot.State != ConnectionState.Ready || snapshot.Me is not { } me) {
            return [];
        }

        if (!IsPlayerName(target.Name) || string.IsNullOrWhiteSpace(target.WorldName) || target.Is(me)) {
            return [];
        }

        var blocked = snapshot.BlockedUsers.Any(target.Is);
        var offers = new List<(int Order, string Name, InviteOffer Offer)>();
        foreach (var channel in snapshot.Channels) {
            if (channel.MyRank < Rank.Moderator || channel.OldKeyMembership) {
                continue;
            }

            int? slot = slots.TryGetValue(channel.Id, out var number) ? number : null;
            var tag = ChannelTag.For(slot, nicknames.GetValueOrDefault(channel.Id), nicknameTags);
            var name = Shorten(TextSanitizer.Clean(channel.DisplayNameFor(advanced)));
            var member = channel.Members.FirstOrDefault(m => target.Is(m.User));
            var unavailable =
                member is { Rank: Rank.Invited } ? "already invited"
                : member != null ? "already a member"
                : blocked ? "you blocked them"
                : channel.Name == null ? "not ready yet"
                : null;
            offers.Add((slot ?? int.MaxValue, name, new InviteOffer(channel.Id, tag, ChannelColours.Of(colours, channel.Id), name, unavailable)));
        }

        return offers
            .OrderBy(offer => offer.Order)
            .ThenBy(offer => offer.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(offer => offer.Offer)
            .Take(MaxOffers)
            .ToList();
    }

    /// <summary>A channel name shortened for the menu.</summary>
    private static string Shorten(string name) => name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)].TrimEnd() + "…";

    /// <summary>
    /// How the invite's channel is named in what is said after: its tag, as in "[sky]", or its name in quotes for a channel
    /// with neither a number nor a nickname (whose tag, [LGC], would say nothing).
    /// </summary>
    private static string Place(InviteOffer offer) => offer.Tag == ChannelTag.Fallback ? $"\"{offer.Name}\"" : offer.Tag;

    /// <summary>Said in LookingGlass blue once the invite is sent, as "Invited Bob Hatter@Lich to [sky]." (the same in both modes).</summary>
    public static string Invited(InviteTarget target, InviteOffer offer) => $"Invited {target.Who} to {Place(offer)}.";

    /// <summary>
    /// Said in LookingGlass blue when the invite fails: "Couldn't invite Bob Hatter@Lich to [sky]: " and why, in the mode's
    /// words, as the channel's Invite button says it (<see cref="PlainMessages.MessageOf"/>). What a server said comes without
    /// its error code in simple mode, and without any registration code (see <see cref="LodestoneCode.Redact"/>).
    /// </summary>
    public static string NotInvited(InviteTarget target, InviteOffer offer, Exception ex, bool advanced) {
        return LodestoneCode.Redact(PlainMessages.Of($"Couldn't invite {target.Who} to {Place(offer)}: {PlainMessages.MessageOf(ex, advanced)}", advanced));
    }

    /// <summary>The channel's tag as it appears in <see cref="Invited"/> and <see cref="NotInvited"/>, to colour; null if it doesn't.</summary>
    public static string? TagIn(InviteOffer offer) => offer.Tag == ChannelTag.Fallback ? null : offer.Tag;
}
