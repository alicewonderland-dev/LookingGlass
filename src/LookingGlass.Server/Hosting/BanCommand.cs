using System.Globalization;
using LookingGlass.Core.Util;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// The operator's bans, from the command line, without starting a server (as <see cref="BackupCommand"/>), safe while the
/// server runs: it uses the same database the same way (SQLite in WAL mode, every change one short transaction), and the
/// running server picks up what changed within <see cref="AbuseOptions.BanCheckSeconds"/>.
/// <list type="bullet">
/// <item><c>--ban &lt;name@world | user ID | address or prefix&gt; [--days N] [--reason "..."]</c> bans a registered character
/// by name, any character by its user ID (its Lodestone ID; one not registered yet then can't register), or an address
/// or prefix (see <see cref="ClientAddresses.BanPrefix"/>), until lifted or for N days. Not the server's own or its proxy's address,
/// which would shut out every player, unless <c>--force</c> says it is meant.</item>
/// <item><c>--unban &lt;the same&gt;</c> lifts it.</item>
/// <item><c>--bans</c> lists the bans in force, the accounts and addresses flagged (see <see cref="Services.AbuseMonitor"/>),
/// and the bans lifted or ended lately.</item>
/// </list>
/// </summary>
public static class BanCommand {
    public enum BanAction {
        Ban,
        Unban,
        List,
    }

    /// <param name="Target">Whom to ban or unban, as given.</param>
    /// <param name="Days">How long the ban lasts; null until lifted.</param>
    /// <param name="Force">Ban even the server's own or its proxy's address (see <see cref="ClientAddresses.ProxyProblem"/>).</param>
    public sealed record Request(BanAction Action, string? Target, int? Days, string? Reason, bool Force = false);

    /// <summary>The longest reason, in characters: the player is shown it.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>The longest ban in days, short of one until lifted (about a hundred years).</summary>
    public const int MaxDays = 36_500;

    private const string Targets = "name@world (in quotes if the name has a space), a user ID (the character's Lodestone ID), or an address or prefix " +
                                   "(203.0.113.5, 203.0.113.0/24, 2001:db8:1:2::/64)";

    /// <returns>The request, if the arguments ask for one of these; throws if they do but are wrong.</returns>
    /// <exception cref="ArgumentException">What's wrong with them, in words for the operator.</exception>
    public static Request? Parse(string[] args) {
        var actions = new[] { ("--ban", BanAction.Ban), ("--unban", BanAction.Unban), ("--bans", BanAction.List) }
            .Where(option => Array.IndexOf(args, option.Item1) >= 0).ToList();
        var days = Array.IndexOf(args, "--days");
        var force = Array.IndexOf(args, "--force") >= 0;
        var reason = Array.IndexOf(args, "--reason");
        if (actions.Count == 0) {
            return null;
        }

        if (actions.Count > 1) {
            throw new ArgumentException("Use one of --ban, --unban and --bans at a time.");
        }

        var (option, action) = actions[0];
        if (action != BanAction.Ban && (days >= 0 || reason >= 0 || force)) {
            throw new ArgumentException("--days, --reason and --force go with --ban.");
        }

        if (action == BanAction.List) {
            return new Request(action, null, null, null);
        }

        var at = Array.IndexOf(args, option);
        if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[at + 1])) {
            throw new ArgumentException($"{option} needs whom to {(action == BanAction.Ban ? "ban" : "unban")}: {Targets}.");
        }

        int? dayCount = null;
        if (days >= 0) {
            if (days + 1 >= args.Length || !int.TryParse(args[days + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > MaxDays) {
                throw new ArgumentException($"--days needs a whole number of days, 1 to {MaxDays}; without it, the ban lasts until --unban lifts it.");
            }

            dayCount = count;
        }

        string? because = null;
        if (reason >= 0) {
            if (reason + 1 >= args.Length || args[reason + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[reason + 1])) {
                throw new ArgumentException("--reason needs the reason, in quotes, as in --reason \"Spamming invites\". The player is shown it.");
            }

            because = args[reason + 1].Trim();
            if (because.Length > MaxReasonLength || !TextSanitizer.IsPlain(because)) {
                throw new ArgumentException($"The reason must be one line of plain text, at most {MaxReasonLength} characters. The player is shown it.");
            }
        }

        return new Request(action, args[at + 1].Trim(), dayCount, because, force);
    }

    /// <param name="now">The time it is (for tests); by default, now.</param>
    /// <returns>The process's exit code: 0 if it was done, 1 if it couldn't be.</returns>
    /// <param name="trustedProxies">The server's <c>LookingGlass:TrustedProxies</c>, whose addresses are banned only with <see cref="Request.Force"/>.</param>
    public static int Run(Request request, string databasePath, AbuseOptions settings, TextWriter output, TextWriter errors, DateTimeOffset? now = null,
        IReadOnlyList<string>? trustedProxies = null) {
        var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (!File.Exists(databasePath)) {
            // Never made here: a wrong data folder would otherwise get an empty database of its own.
            errors.WriteLine($"There is no database at {Path.GetFullPath(databasePath)}. Run this as the server's user, with the server's settings " +
                             "(LookingGlass__DataDirectory, say), so it finds the server's database.");
            return 1;
        }

        try {
            var db = new Database(databasePath);
            return request.Action switch {
                BanAction.Ban => Ban(db, request, settings, at, output, errors, trustedProxies),
                BanAction.Unban => Unban(db, request, settings, at, output, errors),
                _ => List(db, settings, at, output),
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                                         or UnsupportedDatabaseException) {
            errors.WriteLine($"That didn't work: {ex.Message}");
            return 1;
        }
    }

    private static int Ban(Database db, Request request, AbuseOptions settings, long now, TextWriter output, TextWriter errors, IReadOnlyList<string>? trustedProxies) {
        if (Resolve(db, request.Target!, errors) is not { } subject) {
            return 1;
        }

        // Where every player's connections come from when a proxy's forwarded address goes missing: almost surely a mistake.
        if (subject.Address != null && !request.Force && ClientAddresses.ProxyProblem(subject.Address, trustedProxies) is { } problem) {
            errors.WriteLine($"{problem} Nothing was banned. A flag on it means the proxy isn't passing the client's address on (see \"Behind a reverse proxy\" " +
                             "in docs/server.md). If you really mean to ban it, add --force.");
            return 1;
        }

        var expires = request.Days is { } days ? now + days * 86400L : (long?) null;
        var ban = db.AddBan(subject.UserId, subject.Address, request.Reason ?? "", expires, automatic: false, now, out var replaced);
        output.WriteLine($"Banned {subject.Name} {Until(ban)}{(replaced > 0 ? ", replacing the ban it had" : "")}." +
                         (ban.Reason.Length > 0 ? $" Reason (the player is shown it): {ban.Reason}" : ""));
        output.WriteLine(subject.UserId != null
            ? $"A running server applies it within {settings.BanCheckSeconds} seconds: it drops their connection, and refuses their logins and any " +
              "registration of the character. Their places in channels stay."
            : $"A running server applies it within {settings.BanCheckSeconds} seconds: it drops connections from there, and refuses new ones.");
        return 0;
    }

    private static int Unban(Database db, Request request, AbuseOptions settings, long now, TextWriter output, TextWriter errors) {
        if (Resolve(db, request.Target!, errors) is not { } subject) {
            return 1;
        }

        if (db.LiftBans(subject.UserId, subject.Address, now) == 0) {
            errors.WriteLine($"There is no ban on {subject.Name} in force{(subject.Address != null ? " (exactly that address or prefix: see --bans)" : "")}.");
            return 1;
        }

        output.WriteLine($"Lifted the ban on {subject.Name}. A running server lets them back within {settings.BanCheckSeconds} seconds; " +
                         "the plugin tries again by itself within 5 minutes (or at once with \"Try again now\").");
        return 0;
    }

    private static int List(Database db, AbuseOptions settings, long now, TextWriter output) {
        var bans = db.GetActiveBans(now);
        var names = Names(db, bans.Select(ban => ban.UserId));
        output.WriteLine(bans.Count == 0 ? "No bans in force." : $"Bans in force ({bans.Count}):");
        foreach (var ban in bans) {
            output.WriteLine($"  {Named(ban.Subject, ban.UserId, names)}: since {Time(ban.CreatedAt)}, {Until(ban)}" +
                             (ban.Automatic ? " (made automatically)" : "") + (ban.Reason.Length > 0 ? $". Reason: {ban.Reason}" : ""));
        }

        var hours = Math.Clamp(settings.FlagExpiresAfterHours, 1, 8760);
        var flags = db.GetFlags(now - hours * 3600L);
        names = Names(db, flags.Select(flag => flag.UserId));
        output.WriteLine();
        output.WriteLine(flags.Count == 0 ? $"Nothing flagged in the last {hours} hours." : $"Flagged in the last {hours} hours ({flags.Count}):");
        foreach (var flag in flags) {
            var banned = bans.Any(ban => ban.Subject == flag.Subject) ? " [banned]" : "";
            output.WriteLine($"  {Named(flag.Subject, flag.UserId, names)}{banned}: {flag.Why}; flagged {Time(flag.FirstFlaggedAt)}, last refused " +
                             $"{Time(flag.LastRefusedAt)}; limits: {flag.Limits}");
        }

        var days = Math.Clamp(settings.BanHistoryDays, 1, 3650);
        var history = db.GetBanHistory(now).Where(ban => (ban.LiftedAt ?? ban.ExpiresAt) >= now - days * 86400L).ToList();
        names = Names(db, history.Select(ban => ban.UserId));
        output.WriteLine();
        output.WriteLine(history.Count == 0 ? $"No bans lifted or ended in the last {days} days." : $"Lifted or ended in the last {days} days ({history.Count}):");
        foreach (var ban in history) {
            var ended = ban.LiftedAt is { } lifted ? $"lifted {Time(lifted)}" : $"ended {Time(ban.ExpiresAt!.Value)}";
            output.WriteLine($"  {Named(ban.Subject, ban.UserId, names)}: from {Time(ban.CreatedAt)}, {ended}" + (ban.Automatic ? " (made automatically)" : "") +
                             (ban.Reason.Length > 0 ? $". Reason: {ban.Reason}" : ""));
        }

        return 0;
    }

    private sealed record Subject(long? UserId, string? Address, string Name);

    /// <summary>The character or address a target names, or null (and why, in <paramref name="errors"/>) if it names none.</summary>
    private static Subject? Resolve(Database db, string target, TextWriter errors) {
        if (long.TryParse(target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId)) {
            var user = db.GetUser(userId);
            return new Subject(userId, null, user == null ? $"user {userId}" : $"user {userId} ({user.Name}@{user.WorldName})");
        }

        var at = target.LastIndexOf('@');
        if (at > 0) {
            var found = db.FindUser(target[..at], target[(at + 1)..]);
            if (found == null) {
                errors.WriteLine($"No character {target} is registered on this server. To ban one that isn't (so it can't register), use its user ID: " +
                                 "the number in its Lodestone address (https://na.finalfantasyxiv.com/lodestone/character/<ID>/).");
                return null;
            }

            return new Subject(found.UserId, null, $"user {found.UserId} ({found.Name}@{found.WorldName})");
        }

        var prefix = ClientAddresses.BanPrefix(target, out var problem);
        if (prefix == null) {
            errors.WriteLine($"{problem} Whom to ban is {Targets}.");
            return null;
        }

        return new Subject(null, prefix, $"address {prefix}");
    }

    private static Dictionary<long, string> Names(Database db, IEnumerable<long?> userIds) =>
        db.GetUsers(userIds.OfType<long>()).ToDictionary(user => user.UserId, user => $"{user.Name}@{user.WorldName}");

    private static string Named(string subject, long? userId, Dictionary<long, string> names) =>
        userId is { } id && names.TryGetValue(id, out var name) ? $"{subject} ({name})" : subject;

    private static string Until(BanRow ban) {
        if (ban.ExpiresAt is not { } expires) {
            return "until lifted";
        }

        var days = Math.Round((expires - ban.CreatedAt) / 86400.0, 1);
        return $"until {Time(expires)} ({days.ToString(CultureInfo.InvariantCulture)} {(days == 1 ? "day" : "days")})";
    }

    private static string Time(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
