using System.Collections.Immutable;
using System.Globalization;
using LookingGlass.Core.Util;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// How this character stands as one of the server's operators, as the server said in the answer to its own login (capability
/// "operator.v1"; see "The plugin's operator panel" in docs/operator-tools-plan.md). <see cref="None"/> with a server from
/// before it, while not logged in, and for every character the server doesn't list.
/// </summary>
public enum OperatorStanding {
    /// <summary>Not one of the server's operators (or the server doesn't say): nothing is shown.</summary>
    None,

    /// <summary>An operator: Settings has the Operator section, and the right-click menus "Ban from server...".</summary>
    Operator,

    /// <summary>Listed, but its keys aren't the ones pinned for it any more (it was set up again): told once, and in place of the section.</summary>
    KeysChanged,

    /// <summary>Listed, but no keys pinned for it yet: told once, and in place of the section.</summary>
    NotPinned,
}

/// <summary>A ban as the operator panel shows it (addresses shortened by the server; never in full).</summary>
/// <param name="UserId">The character, or 0 for an address.</param>
/// <param name="User">Its name, if it is registered.</param>
/// <param name="Address">For an address: as the server shortened it ("203.0.113.x").</param>
/// <param name="Until">When it ends; null until lifted.</param>
/// <param name="MadeVia">"command line", "web page", "plugin", "automatic", or empty.</param>
/// <param name="MadeBy">For one made in the plugin: the operator character, as the server recorded it; empty otherwise.</param>
/// <param name="Lifted">When it was lifted, if it was.</param>
/// <param name="Replaced">It was lifted by banning the same character again.</param>
public sealed record PanelBan(long UserId, User? User, string? Address, string Reason, DateTimeOffset Created, DateTimeOffset? Until, bool Automatic,
    string MadeVia, string MadeBy, DateTimeOffset? Lifted, string LiftedVia, string LiftedBy, bool Replaced) {
    /// <summary>"Bob Hatter@Lich", "address 203.0.113.x", or "user 4242" for a character not registered here.</summary>
    public string Who => OperatorWords.Who(this.UserId, this.User, this.Address);

    /// <summary>A character's ban (which the plugin may lift), not an address's.</summary>
    public bool OnCharacter => this.UserId != 0;
}

/// <summary>Someone flagged, as the operator panel shows it.</summary>
/// <param name="Why">"refused by limits in 30 of the last 60 minutes".</param>
/// <param name="Limits">"InviteBurstPerPair, LookupBurst".</param>
/// <param name="Banned">A ban in force is on the same subject.</param>
public sealed record PanelFlag(long UserId, User? User, string? Address, string Why, string Limits, DateTimeOffset First, DateTimeOffset Last, bool Banned) {
    public string Who => OperatorWords.Who(this.UserId, this.User, this.Address);

    public bool OnCharacter => this.UserId != 0;
}

/// <summary>How one character stands, for the "Ban from server" dialog.</summary>
/// <param name="User">Null if it isn't registered on this server.</param>
/// <param name="ListedOperator">Listed as one of the server's operators: the plugin can't ban it.</param>
/// <param name="Ban">The ban in force on it, if any.</param>
/// <param name="Flag">Its flag, if any.</param>
public sealed record PanelSubject(long UserId, User? User, bool ListedOperator, PanelBan? Ban, PanelFlag? Flag);

/// <summary>The operator's lists, as last fetched from the server: at most <see cref="OperatorPanel.ListLines"/> of each, newest first.</summary>
/// <param name="PageUrl">The operator web page's address (an https address), if the server has one.</param>
/// <param name="Operators">The characters listed as operators that are registered, which the menus grey out.</param>
/// <param name="About">How the character the lists were asked about stands, if they were.</param>
public sealed record OperatorLists(
    ImmutableArray<PanelFlag> Flags,
    ImmutableArray<PanelBan> InForce,
    ImmutableArray<PanelBan> History,
    int FlagsTotal,
    int InForceTotal,
    int HistoryTotal,
    int BlockedAutomatically,
    string? PageUrl,
    int BansLeftThisHour,
    ImmutableArray<User> Operators,
    DateTimeOffset FetchedAt,
    PanelSubject? About = null) {
    /// <summary>Flags not shown here (the web page has them).</summary>
    public int MoreFlags => Math.Max(0, this.FlagsTotal - this.Flags.Length);

    public int MoreInForce => Math.Max(0, this.InForceTotal - this.InForce.Length);

    public int MoreHistory => Math.Max(0, this.HistoryTotal - this.History.Length);

    /// <summary>Whether a character is listed as one of the server's operators, by its name and home world (ignoring case).</summary>
    public bool IsOperatorCharacter(string name, string worldName) => this.Operators.Any(user =>
        string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(user.WorldName, worldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The lists as the server sent them, kept to what the panel shows: at most <see cref="OperatorPanel.ListLines"/> lines of
    /// each, names and texts cut to sensible lengths, the page's address only if it is an https address. What a server sends
    /// is shown as text (the plugin cleans it again as it draws).
    /// </summary>
    public static OperatorLists From(OperatorView view, DateTimeOffset now) {
        var operators = view.Operators.Take(OperatorPanel.MaxOperatorsListed).Select(Named).OfType<User>().ToImmutableArray();
        return new OperatorLists(
            [.. view.Flags.Take(OperatorPanel.ListLines).Select(FlagOf)],
            [.. view.InForce.Take(OperatorPanel.ListLines).Select(BanOf)],
            [.. view.History.Take(OperatorPanel.ListLines).Select(BanOf)],
            (int) Math.Min(view.FlagsTotal, int.MaxValue),
            (int) Math.Min(view.InForceTotal, int.MaxValue),
            (int) Math.Min(view.HistoryTotal, int.MaxValue),
            (int) Math.Min(view.BlockedAutomatically, int.MaxValue),
            PageAddress(view.PageUrl),
            (int) Math.Min(view.BansLeftThisHour, OperatorPanel.BansPerHour),
            operators,
            now,
            view.About is { UserId: not 0 } about
                ? new PanelSubject(about.UserId, Named(about.User), about.ListedOperator, about.Ban == null ? null : BanOf(about.Ban), about.Flag == null ? null : FlagOf(about.Flag))
                : null);
    }

    private static User? Named(User? user) => user == null || string.IsNullOrWhiteSpace(user.Name)
        ? null
        : new User { UserId = user.UserId, Name = TextSanitizer.Name(user.Name), WorldName = TextSanitizer.Name(user.WorldName), WorldId = user.WorldId };

    private static PanelBan BanOf(OperatorBanLine ban) => new(ban.UserId, Named(ban.User), Shortened(ban.Address),
        TextSanitizer.Clean(ban.Reason, OperatorPanel.MaxReasonLength), TimeOf(ban.CreatedUnix) ?? DateTimeOffset.UnixEpoch, TimeOf(ban.UntilUnix), ban.Automatic,
        TextSanitizer.Clean(ban.MadeVia, 32), TextSanitizer.Name(ban.MadeBy), TimeOf(ban.LiftedUnix), TextSanitizer.Clean(ban.LiftedVia, 32),
        TextSanitizer.Name(ban.LiftedBy), ban.Replaced);

    private static PanelFlag FlagOf(OperatorFlag flag) => new(flag.UserId, Named(flag.User), Shortened(flag.Address), TextSanitizer.Clean(flag.Why, 200),
        TextSanitizer.Clean(flag.Limits, 200), TimeOf(flag.FirstUnix) ?? DateTimeOffset.UnixEpoch, TimeOf(flag.LastUnix) ?? DateTimeOffset.UnixEpoch, flag.Banned);

    private static string? Shortened(string address) => string.IsNullOrWhiteSpace(address) ? null : TextSanitizer.Clean(address, 64);

    private static string? PageAddress(string url) =>
        url.Length is > 0 and <= 200 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    private static DateTimeOffset? TimeOf(long unixSeconds) =>
        unixSeconds > 0 && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) : null;
}

/// <summary>How long a ban the "Ban from server" dialog makes lasts.</summary>
public enum OperatorDuration {
    OneDay,
    SevenDays,
    ThirtyDays,
    UntilLifted,

    /// <summary>A number of days the operator types.</summary>
    Custom,
}

/// <summary>Whom "Ban someone" names: a registered character by name@world, or any character by its Lodestone ID.</summary>
/// <param name="Name">With <paramref name="WorldName"/>: a character's name, as typed.</param>
/// <param name="UserId">A Lodestone ID; 0 for a name.</param>
public sealed record OperatorTarget(string? Name, string? WorldName, long UserId) {
    /// <summary>"Bob Hatter@Lich", or "user 31337".</summary>
    public string Who => this.UserId != 0 ? $"user {this.UserId.ToString(CultureInfo.InvariantCulture)}" : $"{this.Name}@{this.WorldName}";
}

/// <summary>
/// The operator panel's rules that don't need the game (see "The plugin's operator panel" in docs/operator-tools-plan.md):
/// what the menus offer, what "Ban someone" accepts, and what the dialog lets through. The server checks everything again.
/// </summary>
public static class OperatorPanel {
    /// <summary>The most lines of each list shown; the rest are "and N more (see the web page)".</summary>
    public const int ListLines = 10;

    /// <summary>The longest reason, in characters (the server refuses longer): the player is shown it.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>The longest ban in days, short of one until lifted (the server's limit).</summary>
    public const int MaxDays = 36_500;

    /// <summary>The bans an hour one operator character may make from the plugin (the server's limit).</summary>
    public const int BansPerHour = 10;

    /// <summary>The most operator characters' names kept from a server's answer.</summary>
    internal const int MaxOperatorsListed = 50;

    /// <summary>
    /// What the right-click menus offer for <paramref name="target"/>: nothing (null) unless this character is an operator now
    /// and the target is another player with a home world (the invite item's rules); greyed out, saying why, for a character
    /// listed as an operator (as far as the lists last fetched say: the server refuses it anyway).
    /// </summary>
    /// <returns>The item's label and whether it can be picked, or null for none.</returns>
    public static (string Label, bool Enabled)? MenuItem(SessionSnapshot snapshot, InviteTarget target) {
        if (snapshot.Operator != OperatorStanding.Operator || snapshot.Me is not { } me) {
            return null;
        }

        if (!ContextInvites.IsPlayerName(target.Name) || string.IsNullOrWhiteSpace(target.WorldName) || target.Is(me)) {
            return null;
        }

        return snapshot.OperatorLists?.IsOperatorCharacter(target.Name, target.WorldName) == true
            ? ($"{OperatorWords.MenuLabel} ({OperatorWords.MenuOperatorReason})", false)
            : (OperatorWords.MenuLabel, true);
    }

    /// <summary>Whom "Ban someone" names: "First Last@World" or a Lodestone ID (a positive number); null if neither.</summary>
    public static OperatorTarget? ParseTarget(string? typed) {
        var text = (typed ?? "").Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) {
            return id > 0 ? new OperatorTarget(null, null, id) : null;
        }

        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1) {
            return null;
        }

        var name = string.Join(' ', text[..at].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var world = text[(at + 1)..].Trim();
        return ContextInvites.IsPlayerName(name) && world.Length <= 32 && world.All(char.IsAsciiLetter) && world.Length > 0
            ? new OperatorTarget(name, world, 0)
            : null;
    }

    /// <summary>The days a choice stands for (0: until lifted), or null if a custom number isn't 1 to <see cref="MaxDays"/>.</summary>
    public static int? DaysOf(OperatorDuration duration, string? customDays) => duration switch {
        OperatorDuration.OneDay => 1,
        OperatorDuration.SevenDays => 7,
        OperatorDuration.ThirtyDays => 30,
        OperatorDuration.UntilLifted => 0,
        _ => int.TryParse((customDays ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= MaxDays ? days : null,
    };

    /// <summary>Whether a reason can be sent: one line of plain text, at most <see cref="MaxReasonLength"/> characters (empty is fine).</summary>
    public static bool ReasonFits(string? reason) {
        var text = (reason ?? "").Trim();
        return text.Length <= MaxReasonLength && (text.Length == 0 || TextSanitizer.IsPlain(text));
    }
}
