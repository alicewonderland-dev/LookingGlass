using System.Globalization;
using System.Text;
using LookingGlass.Core.Crypto;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;

namespace LookingGlass.Server.Services;

/// <summary>How soon an alert goes to Discord.</summary>
public enum AlertKind {
    /// <summary>With whatever else comes within a minute, as one message.</summary>
    Batched,

    /// <summary>At once (with whatever is waiting), without a ping: the restart, the daily summary, a test.</summary>
    AtOnce,

    /// <summary>At once, pinging <c>Alerts:PingRoleId</c> if set: an automatic block, the proxy warning, Funnel, operator keys.</summary>
    Urgent,
}

/// <summary>One alert for the Discord channel: its text in Discord's markdown, everything from outside already escaped.</summary>
/// <param name="Public">
/// Urgent, but something anyone on the internet can set off (an automatic block, a request through Funnel): it pings, and goes at
/// once, only once every <see cref="AlertPoster.PublicPingEvery"/>; between, it waits for the minute's message, without a ping.
/// </param>
public sealed record Alert(string Text, AlertKind Kind, bool Public = false) {
    public bool Urgent => this.Kind == AlertKind.Urgent;
}

/// <summary>What the daily summary says (see <see cref="AlertWords.Summary"/>). Times are Unix seconds.</summary>
/// <param name="PeriodEnd">The end of the 24 hours it covers.</param>
/// <param name="Made">Bans made (not automatic blocks), by the tool that made each (see <see cref="BanTools"/>; null: an older server).</param>
/// <param name="Lifted">Bans lifted, not counting those replaced by a new ban (that is one ban made).</param>
/// <param name="Ended">Bans that ran out.</param>
/// <param name="InForce">Bans in force when it is posted.</param>
/// <param name="Peak">The most players online at once, and when (null when nobody was).</param>
/// <param name="CountedSince">When the server restarted, if within the 24 hours: the peak and the undelivered count are since then.</param>
/// <param name="Registered">Characters registered on this server for the first time.</param>
/// <param name="Undelivered">Alerts that couldn't be posted.</param>
public sealed record DailySummary(long PeriodEnd, int FlaggedCharacters, int FlaggedAddresses, int Blocked, IReadOnlyList<(string? Via, int Count)> Made, int Lifted,
    int Ended, int InForce, int Peak, long? PeakAt, long? CountedSince, int Registered, string Version, TimeSpan Uptime, long DatabaseBytes, int Undelivered) {
    /// <summary>Nothing flagged, blocked, banned or lifted.</summary>
    public bool Quiet => this.FlaggedCharacters + this.FlaggedAddresses + this.Blocked + this.Made.Sum(made => made.Count) + this.Lifted == 0;
}

/// <summary>
/// The words of every Discord alert (see the mockups in docs/operator-tools-plan.md, "Discord alerts"), and what makes them safe
/// to post: names, worlds and reasons are shown visibly (<see cref="VisibleText"/>) and escaped (<see cref="Escape"/>), so they
/// can't format the message, link anywhere or mention anyone; addresses are shortened (<see cref="ClientAddresses.Shorten"/>),
/// except the server's own and its proxy's; times are Discord's time stamps, which each reader sees in their own time zone.
/// The commands given never include <c>--force</c>, and never a full address. The web page's link comes with step 3 of the plan.
/// </summary>
public static class AlertWords {
    /// <summary>The longest message Discord takes, in characters.</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>Where a character's Lodestone page is, before its user ID.</summary>
    public const string LodestoneCharacter = "https://na.finalfantasyxiv.com/lodestone/character/";

    // Room kept at the end of a message for "and N more: ...".
    private const int TailRoom = 100;

    /// <summary>
    /// Text from outside (a name, a world, a reason, a limit's name, the operator character who made a ban) made safe to post:
    /// shown visibly first (control and format characters as escapes, so nothing can start a new line or hide), then every
    /// character Discord's markdown acts on is escaped with a backslash (so it can't bold, strike, spoil, quote code, make a
    /// masked link, a time stamp or a mention), <c>:</c> too (so <c>https://</c> isn't made a link), and every <c>@</c> is
    /// followed by a zero-width space (so <c>@everyone</c> is only text, even before <c>allowed_mentions</c>, which pings nobody).
    /// </summary>
    public static string Escape(string? text) {
        var visible = VisibleText.Of(text, 400);
        var escaped = new StringBuilder(visible.Length + 8);
        foreach (var c in visible) {
            switch (c) {
                case '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '<' or '>' or ':' or '#':
                    escaped.Append('\\').Append(c);
                    break;
                case '@':
                    escaped.Append("@​");
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>A Discord time stamp: <c>t</c> a short time, <c>d</c> a short date, <c>f</c> a date and time.</summary>
    public static string Time(long unixSeconds, char style) => $"<t:{unixSeconds.ToString(CultureInfo.InvariantCulture)}:{style}>";

    /// <summary>"Bob Hatter@Lich (user 31337)", or "user 31337" for a character not registered here.</summary>
    public static string Character(long userId, UserRow? user) =>
        user == null ? $"user {Id(userId)}" : $"{Escape(user.Name)}@{Escape(user.WorldName)} (user {Id(userId)})";

    /// <summary>"address 203.0.113.x (shortened)", or a prefix wider than a /24 (IPv6: a /40) as it is.</summary>
    public static string Address(string addressOrPrefix) {
        var shown = ClientAddresses.Shorten(addressOrPrefix);
        // An address as the server wrote it is digits, dots, colons and a slash: shown as it is (escaped, an IPv6 one would read
        // badly wherever Discord shows the text raw). Anything else, which no ban or flag holds, is escaped.
        var slash = shown.IndexOf('/');
        var plain = System.Net.IPAddress.TryParse(slash < 0 ? shown : shown[..slash], out _) || (shown != addressOrPrefix && shown.EndsWith(".x", StringComparison.Ordinal));
        var text = plain ? shown : Escape(shown);
        return shown == addressOrPrefix ? $"address {text}" : $"address {text} (shortened)";
    }

    /// <summary>A character's Lodestone page (link previews are off, so Discord doesn't fetch it). Not for a debug account (a negative ID), which has none.</summary>
    public static string Lodestone(long userId) => $"{LodestoneCharacter}{Id(userId)}/";

    /// <summary>"on the web page", "in the plugin by Alice Wonderland@Lich", "on the command line", "automatically"; null if not recorded.</summary>
    /// <remarks>Only the plugin's operator character is named: the Unix user and the Tailscale login stay out of Discord.</remarks>
    public static string? Via(string? via, string? by) => via switch {
        null => null,
        BanTools.CommandLine => "on the command line",
        BanTools.WebPage => "on the web page",
        BanTools.Plugin => string.IsNullOrWhiteSpace(by) ? "in the plugin" : $"in the plugin by {Escape(by)}",
        BanTools.Automatic => "automatically",
        _ => $"with {Escape(via)}",
    };

    // ================================================================ flags, blocks and bans

    /// <summary>Someone flagged: an account (with its Lodestone page and the commands) or an address (shortened).</summary>
    public static Alert Flagged(FlagRow flag, UserRow? user, int expiresHours) {
        var ends = $"The flag ends by itself {Count(expiresHours, "hour")} after the last refusal. Nothing was banned.";
        if (flag.UserId is { } id) {
            return new Alert(
                $"**Flagged:** {Character(id, user)}: {Escape(flag.Why)}, by {Escape(flag.Limits)}.\n" +
                (id > 0 ? $"Lodestone: {Lodestone(id)}\n" : "") +
                "Look: `LG --bans`\n" +
                $"Ban: `LG --ban {Id(id)} --days 7 --reason \"...\"`\n" +
                ends, AlertKind.Batched);
        }

        return new Alert(
            $"**Flagged:** {Address(flag.Address ?? "")}: {Escape(flag.Why)} ({Escape(flag.Limits)}).\n" +
            "It may be shared (a household, a mobile network). Full address: `LG --bans`\n" +
            ends, AlertKind.Batched);
    }

    /// <summary>An address blocked automatically (urgent).</summary>
    public static Alert Blocked(BanRow block, AbuseOptions abuse) {
        var minutes = block.ExpiresAt is { } ends ? Duration(ends - block.CreatedAt) : "until lifted";
        var until = block.ExpiresAt is { } at ? $", until {Time(at, 't')}" : "";
        return new Alert(
            $"**Urgent: blocked automatically:** {Address(block.Address ?? "")}, refused {Number(abuse.AutoBlockAfterRefusals)} times within " +
            $"{Count(abuse.WindowMinutes, "minute")}. Blocked for {minutes}{until}.\n" +
            "Look: `LG --bans` (ban it for longer, or lift the block with `LG --unban` and the full address it shows)", AlertKind.Urgent, Public: true);
    }

    /// <summary>A ban made, by any tool; with the ban it replaced, if it replaced one (told here, once, rather than as a lift).</summary>
    public static Alert BanMade(BanRow ban, UserRow? user, BanRow? replaced) {
        var via = Via(ban.MadeVia, ban.MadeBy);
        var whom = ban.UserId is { } id ? Character(id, user) : Address(ban.Address ?? "");
        var replacing = replaced == null ? ""
            : $", replacing the ban made {Time(replaced.CreatedAt, 'd')}{(Via(replaced.MadeVia, replaced.MadeBy) is { } how ? " " + how : "")} ({Span(replaced, withEnd: false)})";
        var reason = string.IsNullOrEmpty(ban.Reason) ? "No reason given." : $"Reason, which the player is shown: {Escape(ban.Reason)}";
        var text = new StringBuilder($"**Ban made**{(via != null ? " " + via : "")}: {whom}, {Span(ban, withEnd: true)}{replacing}. {reason}");
        if (ban.UserId is { } character) {
            if (character > 0) {
                text.Append($"\nLodestone: {Lodestone(character)}");
            }

            text.Append(ban.MadeVia == BanTools.Plugin
                ? $"\nIf you didn't make this, lift it (`LG --unban {Id(character)}`) and check that operator character's computers."
                : $"\nLift: `LG --unban {Id(character)}`");
        } else {
            text.Append("\nLift: `LG --bans` shows the full address, for `LG --unban`");
        }

        return new Alert(text.ToString(), AlertKind.Batched);
    }

    /// <summary>A ban lifted (not one replaced by a new ban: that is told with the new one).</summary>
    public static Alert BanLifted(BanRow ban, UserRow? user) {
        var via = Via(ban.LiftedVia, ban.LiftedBy);
        var whom = ban.UserId is { } id ? Character(id, user) : Address(ban.Address ?? "");
        var made = Via(ban.MadeVia, ban.MadeBy) ?? "by an older version of the server";
        return new Alert($"**Ban lifted**{(via != null ? " " + via : "")}: {whom}. It had been made {made}, {Time(ban.CreatedAt, 'd')}, {Span(ban, withEnd: false)}.",
            AlertKind.Batched);
    }

    // ================================================================ the server

    /// <summary>Every player seems to come from the proxy's (or this machine's) address (urgent). That address is the server's, so it is shown in full.</summary>
    public static Alert ProxyWarning(string address) => new(
        $"**Urgent: every player seems to come from {address}.** The proxy isn't passing the client's address on (X-Forwarded-For), so " +
        "per-address limits apply to everyone together, and no address can be flagged or blocked. Fix the proxy rather than banning anything: " +
        "see \"Behind a reverse proxy\" in docs/server.md.", AlertKind.Urgent);

    /// <summary>The web page was asked for through Tailscale Funnel, from the public internet (urgent; raised by the web page, step 3).</summary>
    /// <param name="more">Further such requests since the last of these alerts (they are posted at most every ten minutes).</param>
    public static Alert FunnelRequest(int more) => new(
        "**Urgent: the web page was asked for through Tailscale Funnel**, from the public internet. It was refused, but Funnel must never lead to " +
        "the page. On the server, `tailscale funnel status` shows which public port does; turn that one off (`sudo tailscale funnel --https=<port> off`), " +
        "and check the public server still answers." + (more > 0 ? $" ({Count(more, "more request")} like it since the last of these alerts.)" : ""),
        AlertKind.Urgent, Public: true);

    /// <summary>The server started, and whether it last stopped cleanly (unknown the first time it keeps track).</summary>
    /// <param name="previousStart">When the run before this one started, if known.</param>
    /// <param name="previousCleanStop">When it stopped cleanly, if it did (a time at or after <paramref name="previousStart"/>).</param>
    public static Alert Started(string version, string environment, long? previousStart, long? previousCleanStop) {
        var start = $"**Server started:** LookingGlass {Escape(version)}, in {Escape(environment)}.";
        if (previousStart == null) {
            return new Alert(start, AlertKind.AtOnce);
        }

        return new Alert(previousCleanStop is { } stopped && stopped >= previousStart
            ? $"{start} It stopped cleanly at {Time(stopped, 't')}."
            : $"{start} It didn't stop cleanly: it had been running since {Time(previousStart.Value, 'f')} (a crash, the machine losing power, or the " +
              "process being killed). See `journalctl -u lookingglass`.", AlertKind.AtOnce);
    }

    /// <summary>The daily summary, or the quiet day's.</summary>
    public static Alert Summary(DailySummary summary) {
        var to = $"the 24 hours to {Time(summary.PeriodEnd, 'f')}";
        var peak = (summary.Peak == 0 ? "nobody online" : $"at most {Number(summary.Peak)} online at once") +
                   (summary.Peak > 0 && summary.PeakAt is { } at ? $" (at {Time(at, 't')})" : "") +
                   (summary.CountedSince is { } since ? $", counted since the restart at {Time(since, 't')}" : "");
        var registered = $"{(summary.Registered == 0 ? "no" : Number(summary.Registered))} {(summary.Registered == 1 ? "character" : "characters")} registered for the first time";
        var undelivered = summary.Undelivered == 0 ? "none" : Number(summary.Undelivered) + (summary.CountedSince != null ? " (since the restart)" : "");
        if (summary.Quiet) {
            var ended = summary.Ended > 0 ? $" {Count(summary.Ended, "ban")} ended." : "";
            var notDelivered = summary.Undelivered > 0 ? $" Alerts not delivered: {undelivered}." : "";
            return new Alert(
                $"**Daily summary**, {to}: a quiet day. Nothing flagged, blocked, banned or lifted.{ended} In force now: {Count(summary.InForce, "ban")}. " +
                $"{char.ToUpperInvariant(peak[0])}{peak[1..]}; {registered}.{notDelivered} Alerts are working.", AlertKind.AtOnce);
        }

        var flagged = summary.FlaggedCharacters + summary.FlaggedAddresses;
        var made = summary.Made.Sum(entry => entry.Count);
        var byTool = made == 0 ? ""
            : " (" + string.Join(", ", summary.Made.Where(entry => entry.Count > 0).Select(entry => $"{Number(entry.Count)} {Via(entry.Via, null) ?? "by an older version of the server"}")) + ")";
        return new Alert(
            $"**Daily summary**, {to}\n" +
            $"Flagged: {Number(flagged)}" +
            (flagged == 0 ? "" : $" ({Count(summary.FlaggedCharacters, "character")}, {Count(summary.FlaggedAddresses, "address", "addresses")})") +
            $". Blocked automatically: {Number(summary.Blocked)}.\n" +
            $"Bans made: {Number(made)}{byTool}. Lifted: {Number(summary.Lifted)}. Ended: {Number(summary.Ended)}. In force now: {Number(summary.InForce)}.\n" +
            $"Players: {peak}; {registered}.\n" +
            $"Server: {Escape(summary.Version)}, up {Uptime(summary.Uptime)}; database {Megabytes(summary.DatabaseBytes)}. Alerts not delivered: {undelivered}.",
            AlertKind.AtOnce);
    }

    /// <summary>A test, at the operator's asking (the web page's <b>Send a test alert</b>, step 3).</summary>
    public static Alert Test(string version) => new($"**Test alert** from LookingGlass {Escape(version)}: alerts reach this channel.", AlertKind.AtOnce);

    // ================================================================ operators

    /// <summary>A listed operator character signed in with keys that aren't its pinned ones (urgent).</summary>
    public static Alert OperatorOtherKeys(UserRow user, OperatorPinRow pin) {
        var when = user.KeysRegisteredAt is { } at ? $" at {Time(at, 't')}" : "";
        return new Alert(
            $"**Urgent: an operator character signed in with other keys:** {Character(user.UserId, user)}, keys {user.Keys.Fingerprint}, not the pinned " +
            $"{IdentityKeys.FingerprintOfHash(pin.KeyHash)}. It registered again through the Lodestone{when}, and this server no longer treats it as an operator.\n" +
            "If it was you (a new computer, \"Reset my identity\"), compare the fingerprint in the plugin's Settings, then restore it: " +
            $"`LG --operator-pin {Id(user.UserId)} --fingerprint \"<the fingerprint you checked>\"`\n" +
            "If not, someone has that character's Lodestone page, so its Square Enix account: secure the account first. Nothing they do gives them " +
            "operator powers.", AlertKind.Urgent);
    }

    /// <summary>An operator character's keys pinned, or pinned again (restored) after they changed (urgent).</summary>
    /// <param name="previous">The key hash pinned before, as last posted; null for a first pin.</param>
    /// <remarks>
    /// Says when it was pinned: one made while alerts were off (or the server was stopped) is posted when the server next looks,
    /// still urgent, as it is what someone with a stolen tailnet device would do.
    /// </remarks>
    public static Alert OperatorPinned(long userId, UserRow? user, OperatorPinRow pin, byte[]? previous) {
        var via = Via(pin.PinnedVia, pin.PinnedVia == BanTools.Plugin ? pin.PinnedBy : null);
        var how = $"{(via != null ? " " + via : "")} at {Time(pin.PinnedAt, 'f')}";
        var keys = IdentityKeys.FingerprintOfHash(pin.KeyHash);
        var what = previous == null
            ? $"**Operator keys pinned**{how}: {Character(userId, user)}, pinned to keys {keys}."
            : $"**Operator status restored**{how}: {Character(userId, user)}, now pinned to keys {keys} " +
              $"(was {IdentityKeys.FingerprintOfHash(previous)}).";
        return new Alert(
            what + "\nIf you didn't do this, unlist the character in the server's settings and restart, then check `LG --audit`.", AlertKind.Urgent);
    }

    // ================================================================ one message

    /// <summary>
    /// Alerts as one Discord message: urgent ones first (so the cut never takes them), each apart from the next by a blank line,
    /// the ping role's mention at the start if <paramref name="ping"/> says to, one is urgent and a role is set; past
    /// <see cref="MaxMessageLength"/>, as many as fit, then "and N more".
    /// </summary>
    /// <param name="ping">Whether the message may ping (the poster's budget for alerts the public can set off allows it).</param>
    /// <param name="pinged">The message mentions the role (so <c>allowed_mentions</c> must list it).</param>
    /// <param name="included">How many alerts it holds in full.</param>
    public static string Message(IReadOnlyList<Alert> alerts, string? roleId, bool ping, out bool pinged, out int included) {
        var ordered = alerts.Where(alert => alert.Urgent).Concat(alerts.Where(alert => !alert.Urgent)).ToList();
        pinged = ping && roleId != null && ordered.Count > 0 && ordered[0].Urgent;
        var message = new StringBuilder(pinged ? $"<@&{roleId}> " : "");
        included = 0;
        foreach (var alert in ordered) {
            var separator = included == 0 ? "" : "\n\n";
            var last = included == ordered.Count - 1;
            var room = MaxMessageLength - message.Length - separator.Length - (last ? 0 : TailRoom);
            if (alert.Text.Length > room) {
                if (included > 0) {
                    break;
                }

                // One alert longer than a message (none is, by far): cut, never between the halves of a surrogate pair.
                var cut = Math.Max(0, room - 1);
                if (cut > 0 && char.IsHighSurrogate(alert.Text[cut - 1])) {
                    cut--;
                }

                message.Append(alert.Text.AsSpan(0, cut)).Append('…');
                included = 1;
                continue;
            }

            message.Append(separator).Append(alert.Text);
            included++;
        }

        if (included < ordered.Count) {
            message.Append($"\n\nand {Number(ordered.Count - included)} more: see `LG --bans`, `LG --audit` and `journalctl -u lookingglass`.");
        }

        return message.ToString();
    }

    // ================================================================ helpers

    /// <summary>"for 7 days, until (time)", "until lifted"; without the end: "for 7 days".</summary>
    private static string Span(BanRow ban, bool withEnd) =>
        ban.ExpiresAt is not { } ends ? "until lifted"
        : withEnd ? $"for {Duration(ends - ban.CreatedAt)}, until {Time(ends, 'f')}"
        : $"for {Duration(ends - ban.CreatedAt)}";

    /// <summary>"7 days", "3 hours", "15 minutes".</summary>
    private static string Duration(long seconds) =>
        seconds >= 86400 && seconds % 86400 == 0 ? Count(seconds / 86400, "day")
        : seconds >= 3600 && seconds % 3600 == 0 ? Count(seconds / 3600, "hour")
        : Count(Math.Max(1, (seconds + 30) / 60), "minute");

    private static string Uptime(TimeSpan uptime) =>
        uptime < TimeSpan.FromHours(1) ? Count(Math.Max(0, (long) uptime.TotalMinutes), "minute")
        : uptime < TimeSpan.FromDays(2) ? Count((long) uptime.TotalHours, "hour")
        : Count((long) uptime.TotalDays, "day");

    private static string Megabytes(long bytes) =>
        bytes < 1024 * 1024 ? $"{Number(Math.Max(1, (bytes + 1023) / 1024))} KB" : $"{Number((bytes + 512 * 1024) / (1024 * 1024))} MB";

    private static string Count(long count, string one, string? many = null) => count == 1 ? $"1 {one}" : $"{Number(count)} {many ?? one + "s"}";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Id(long userId) => userId.ToString(CultureInfo.InvariantCulture);
}
