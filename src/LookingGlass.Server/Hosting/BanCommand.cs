using System.Globalization;
using LookingGlass.Core.Util;
using LookingGlass.Server.Data;
using LookingGlass.Server.Services;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// The operator's commands, from the command line, without starting a server (as <see cref="BackupCommand"/>), safe while the
/// server runs: it uses the same database the same way (SQLite in WAL mode, every change one short transaction), and the
/// running server picks up what changed within <see cref="AbuseOptions.BanCheckSeconds"/>. Each goes through
/// <see cref="OperatorActions"/>, as the web page and the plugin will, so the same rules apply and every change is in the
/// audit trail, recorded as made on the command line by the Unix user who ran it (<c>SUDO_USER</c>).
/// <list type="bullet">
/// <item><c>--ban &lt;name@world | user ID | address or prefix&gt; [--days N] [--reason "..."]</c> bans a registered character
/// by name, any character by its user ID (its Lodestone ID; one not registered yet then can't register), or an address
/// or prefix (see <see cref="ClientAddresses.BanPrefix"/>), until lifted or for N days. Not the server's own or its proxy's address,
/// which would shut out every player, nor a character listed as an operator, unless <c>--force</c> says it is meant.</item>
/// <item><c>--unban &lt;the same&gt;</c> lifts it.</item>
/// <item><c>--bans</c> lists the bans in force (with who made each), the accounts and addresses flagged (see
/// <see cref="Services.AbuseMonitor"/>), and the bans lifted or ended lately.</item>
/// <item><c>--audit [--days N]</c> lists the audit trail: the last 50 entries, or all of the last N days.</item>
/// <item><c>--operators</c> lists the characters listed as operators, with their pinned and current keys.</item>
/// <item><c>--operator-pin &lt;name@world | user ID&gt; --fingerprint "..."</c> pins a listed character's keys, only if the
/// fingerprint typed is its current keys'.</item>
/// <item><c>--allow-key-login &lt;name@world | user ID&gt;</c> lets a character's identity key sign in again after its
/// "Sign out everywhere else" (see <see cref="Database.AllowKeyLogin"/>), as registering again through the Lodestone would:
/// for a player who can't. A running server goes by it at the character's next key login.</item>
/// </list>
/// </summary>
public static class BanCommand {
    public enum BanAction {
        Ban,
        Unban,
        List,
        AllowKeyLogin,
        Audit,
        Operators,
        OperatorPin,
    }

    /// <param name="Target">Whom to ban, unban, pin or let sign in again, as given.</param>
    /// <param name="Days">How long the ban lasts (null until lifted); for <c>--audit</c>, how many days back to list.</param>
    /// <param name="Force">Ban even the server's own or its proxy's address (see <see cref="ClientAddresses.ProxyProblem"/>), or an operator character.</param>
    /// <param name="Fingerprint">For <c>--operator-pin</c>, the fingerprint typed.</param>
    public sealed record Request(BanAction Action, string? Target, int? Days, string? Reason, bool Force = false, string? Fingerprint = null);

    /// <summary>The longest reason, in characters: the player is shown it.</summary>
    public const int MaxReasonLength = OperatorActions.MaxReasonLength;

    /// <summary>The longest ban in days, short of one until lifted (about a hundred years).</summary>
    public const int MaxDays = OperatorActions.MaxDays;

    /// <summary>How many audit entries <c>--audit</c> lists without <c>--days</c>.</summary>
    public const int AuditEntries = 50;

    private const string Targets = "name@world (in quotes if the name has a space), a user ID (the character's Lodestone ID), or an address or prefix " +
                                   "(203.0.113.5, 203.0.113.0/24, 2001:db8:1:2::/64)";

    private const string Character = "name@world (in quotes if the name has a space), or its user ID (its Lodestone ID)";

    private const string WhereTheFingerprintIs = "the 25 digits under \"Your identity\" in the plugin's Settings, with Advanced mode on";

    /// <returns>The request, if the arguments ask for one of these; throws if they do but are wrong.</returns>
    /// <exception cref="ArgumentException">What's wrong with them, in words for the operator.</exception>
    public static Request? Parse(string[] args) {
        var actions = new[] {
                ("--ban", BanAction.Ban), ("--unban", BanAction.Unban), ("--bans", BanAction.List), ("--audit", BanAction.Audit),
                ("--operators", BanAction.Operators), ("--operator-pin", BanAction.OperatorPin), ("--allow-key-login", BanAction.AllowKeyLogin),
            }
            .Where(option => Array.IndexOf(args, option.Item1) >= 0).ToList();
        var days = Array.IndexOf(args, "--days");
        var force = Array.IndexOf(args, "--force") >= 0;
        var reason = Array.IndexOf(args, "--reason");
        var fingerprint = Array.IndexOf(args, "--fingerprint");
        if (actions.Count == 0) {
            return null;
        }

        if (actions.Count > 1) {
            throw new ArgumentException("Use one of --ban, --unban, --bans, --audit, --operators, --operator-pin and --allow-key-login at a time.");
        }

        var (option, action) = actions[0];
        if (action == BanAction.Audit && (reason >= 0 || force)) {
            throw new ArgumentException("--reason and --force go with --ban.");
        }

        if (action is not (BanAction.Ban or BanAction.Audit) && (days >= 0 || reason >= 0 || force)) {
            throw new ArgumentException("--days, --reason and --force go with --ban (and --days with --audit).");
        }

        if (action != BanAction.OperatorPin && fingerprint >= 0) {
            throw new ArgumentException("--fingerprint goes with --operator-pin.");
        }

        if (action is BanAction.List or BanAction.Operators) {
            return new Request(action, null, null, null);
        }

        if (action == BanAction.Audit) {
            if (days < 0) {
                return new Request(action, null, null, null);
            }

            if (days + 1 >= args.Length || !int.TryParse(args[days + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var back) ||
                back is < 1 or > OperatorActions.AuditKeptDays) {
                throw new ArgumentException($"--days needs a whole number of days, 1 to {OperatorActions.AuditKeptDays} (the audit trail is kept that long); " +
                                            $"without it, --audit lists the last {AuditEntries} entries.");
            }

            return new Request(action, null, back, null);
        }

        var at = Array.IndexOf(args, option);
        if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[at + 1])) {
            throw new ArgumentException(action switch {
                BanAction.AllowKeyLogin => $"--allow-key-login needs which character: {Character}.",
                BanAction.OperatorPin => $"--operator-pin needs which character: {Character}; and --fingerprint \"...\" with {WhereTheFingerprintIs}.",
                _ => $"{option} needs whom to {(action == BanAction.Ban ? "ban" : "unban")}: {Targets}.",
            });
        }

        if (action == BanAction.OperatorPin) {
            if (fingerprint < 0 || fingerprint + 1 >= args.Length || OperatorActions.NormalFingerprint(args[fingerprint + 1]) == null) {
                throw new ArgumentException($"--operator-pin needs --fingerprint \"...\" with the character's fingerprint, {WhereTheFingerprintIs}, " +
                                            "in quotes, as in --fingerprint \"48213 90551 17342 66018 30927\". Compare it there first: it is pinned only if it " +
                                            "is the character's current keys'.");
            }

            return new Request(action, args[at + 1].Trim(), null, null, Fingerprint: args[fingerprint + 1].Trim());
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
    /// <param name="operators">The server's <c>LookingGlass:Operators:Characters</c>, by user ID: banned only with <see cref="Request.Force"/>, and the only ones pinned.</param>
    /// <param name="who">Who is running it, for the audit trail; by default <see cref="OperatorActions.CommandLineUser"/>.</param>
    public static int Run(Request request, string databasePath, AbuseOptions settings, TextWriter output, TextWriter errors, DateTimeOffset? now = null,
        IReadOnlyList<string>? trustedProxies = null, IReadOnlyList<long>? operators = null, string? who = null) {
        if (!File.Exists(databasePath)) {
            // Never made here: a wrong data folder would otherwise get an empty database of its own.
            errors.WriteLine($"There is no database at {Path.GetFullPath(databasePath)}. Run this as the server's user, with the server's settings " +
                             "(LookingGlass__DataDirectory, say), so it finds the server's database.");
            return 1;
        }

        try {
            var db = new Database(databasePath);
            var actions = new OperatorActions(db, new OperatorSettings(settings, trustedProxies ?? [], operators ?? []), now is { } fixedTime ? new FixedTime(fixedTime) : null);
            var actor = new OperatorActor(OperatorTool.CommandLine, who ?? OperatorActions.CommandLineUser());
            var at = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            return request.Action switch {
                BanAction.Ban => Ban(actions, actor, request, settings, output, errors),
                BanAction.Unban => Unban(actions, actor, request, settings, output, errors),
                BanAction.AllowKeyLogin => AllowKeyLogin(actions, actor, request, output, errors),
                BanAction.Audit => Audit(actions, request, at, output),
                BanAction.Operators => ListOperators(actions, operators ?? [], output),
                BanAction.OperatorPin => Pin(actions, actor, request, output, errors),
                _ => List(actions, output),
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                                         or UnsupportedDatabaseException) {
            errors.WriteLine($"That didn't work: {ex.Message}");
            return 1;
        }
    }

    private static int Ban(OperatorActions actions, OperatorActor actor, Request request, AbuseOptions settings, TextWriter output, TextWriter errors) {
        var result = actions.Ban(actor, request.Target!, request.Days, request.Reason, request.Force);
        if (!result.Done) {
            errors.WriteLine(result.Refusal switch {
                // Where every player's connections come from when a proxy's forwarded address goes missing: almost surely a mistake.
                OperatorRefusal.ProxyAddress => $"{result.Problem} Nothing was banned. A flag on it means the proxy isn't passing the client's address on " +
                                                "(see \"Behind a reverse proxy\" in docs/server.md). If you really mean to ban it, add --force.",
                OperatorRefusal.OperatorCharacter => $"{result.Whom} is listed as an operator (LookingGlass:Operators:Characters): banning it would lock an " +
                                                     "operator out. Nothing was banned. If you really mean to ban it, add --force.",
                _ => Unresolved(result),
            });
            return 1;
        }

        var ban = result.Ban!;
        output.WriteLine($"Banned {result.Whom} {Until(ban)}{(result.Replaced > 0 ? ", replacing the ban it had" : "")}." +
                         (ban.Reason.Length > 0 ? $" Reason (the player is shown it): {ban.Reason}" : ""));
        output.WriteLine(result.UserId != null
            ? $"A running server applies it within {settings.BanCheckSeconds} seconds: it drops their connection, and refuses their logins and any " +
              "registration of the character. Their places in channels stay."
            : $"A running server applies it within {settings.BanCheckSeconds} seconds: it drops connections from there, and refuses new ones.");
        return 0;
    }

    private static int Unban(OperatorActions actions, OperatorActor actor, Request request, AbuseOptions settings, TextWriter output, TextWriter errors) {
        var result = actions.Lift(actor, request.Target!);
        if (!result.Done) {
            errors.WriteLine(result.Refusal == OperatorRefusal.NothingToLift
                ? $"There is no ban on {result.Whom} in force{(result.Address != null ? " (exactly that address or prefix: see --bans)" : "")}."
                : Unresolved(result));
            return 1;
        }

        output.WriteLine($"Lifted the ban on {result.Whom}. A running server lets them back within {settings.BanCheckSeconds} seconds; " +
                         "the plugin tries again by itself within 5 minutes (or at once with \"Try again now\").");
        return 0;
    }

    private static int AllowKeyLogin(OperatorActions actions, OperatorActor actor, Request request, TextWriter output, TextWriter errors) {
        var result = actions.AllowKeyLogin(actor, request.Target!);
        if (!result.Done) {
            errors.WriteLine(result.Refusal switch {
                OperatorRefusal.NotRegistered => $"{result.Whom} isn't a character registered on this server: --allow-key-login needs name@world, or a registered " +
                                                 "character's user ID.",
                OperatorRefusal.KeyLoginNotOff => $"Signing in with the identity key isn't turned off for {result.Whom}: nothing to do.",
                _ => Unresolved(result),
            });
            return 1;
        }

        output.WriteLine($"{result.Whom} can sign in with their identity key again (their \"Sign out everywhere else\" had turned it off). Their " +
                         "computers that were signed out sign in by themselves at their next connection, and so would anyone holding a copy of " +
                         "that key: if the player thinks someone has one, they should reset their identity instead.");
        return 0;
    }

    /// <summary>A target that names nobody (or another refusal), in the command line's words.</summary>
    private static string Unresolved(OperatorResult result) => result.Refusal switch {
        OperatorRefusal.NoSuchCharacter => $"No character {result.Whom} is registered on this server. To ban one that isn't (so it can't register), use its user " +
                                           "ID: the number in its Lodestone address (https://na.finalfantasyxiv.com/lodestone/character/<ID>/).",
        OperatorRefusal.NotATarget => $"{result.Problem} Whom to ban is {Targets}.",
        _ => result.Why,
    };

    private static int List(OperatorActions actions, TextWriter output) {
        var view = actions.See(OperatorTool.CommandLine);
        output.WriteLine(view.InForce.Count == 0 ? "No bans in force." : $"Bans in force ({view.InForce.Count}):");
        foreach (var (ban, whom) in view.InForce) {
            output.WriteLine($"  {whom}: since {Time(ban.CreatedAt)}, {Until(ban)}{Made(ban)}" + (ban.Reason.Length > 0 ? $". Reason: {ban.Reason}" : ""));
        }

        output.WriteLine();
        output.WriteLine(view.Flags.Count == 0 ? $"Nothing flagged in the last {view.FlagHours} hours." : $"Flagged in the last {view.FlagHours} hours ({view.Flags.Count}):");
        foreach (var (flag, whom, banned) in view.Flags) {
            output.WriteLine($"  {whom}{(banned ? " [banned]" : "")}: {flag.Why}; flagged {Time(flag.FirstFlaggedAt)}, last refused " +
                             $"{Time(flag.LastRefusedAt)}; limits: {flag.Limits}");
        }

        output.WriteLine();
        output.WriteLine(view.History.Count == 0
            ? $"No bans lifted or ended in the last {view.HistoryDays} days."
            : $"Lifted or ended in the last {view.HistoryDays} days ({view.History.Count}):");
        foreach (var (ban, whom) in view.History) {
            var ended = ban.LiftedAt is { } lifted
                ? $"lifted {Time(lifted)}{(OperatorActions.LiftedWords(ban) is { } by ? $" {by}" : "")}"
                : $"ended {Time(ban.ExpiresAt!.Value)}";
            output.WriteLine($"  {whom}: from {Time(ban.CreatedAt)}, {ended}{Made(ban)}" + (ban.Reason.Length > 0 ? $". Reason: {ban.Reason}" : ""));
        }

        return 0;
    }

    /// <summary>Who made a ban, as <c>--bans</c> adds it: " (made automatically)", ", made on the web page by alice@github", or nothing if not recorded.</summary>
    private static string Made(BanRow ban) => OperatorActions.MadeWords(ban) switch {
        null => "",
        "automatically" => " (made automatically)",
        { } words => $", made {words}",
    };

    private static int Audit(OperatorActions actions, Request request, long now, TextWriter output) {
        var entries = request.Days is { } days
            ? actions.Audit(now - days * 86400L, int.MaxValue)
            : actions.Audit(now - OperatorActions.AuditKeptDays * 86400L, AuditEntries);
        var within = request.Days is { } back ? $" in the last {back} {(back == 1 ? "day" : "days")}" : "";
        if (entries.Count == 0) {
            output.WriteLine($"Nothing in the audit trail{within}. It keeps every ban, lift, operator pin and key login allowed again, tried with any tool, " +
                             $"done or refused, for {OperatorActions.AuditKeptDays} days.");
            return 0;
        }

        output.WriteLine(request.Days != null
            ? $"What operators did{within}, newest first ({entries.Count}):"
            : $"What operators did, newest first (the last {entries.Count}{(entries.Count == AuditEntries ? "; --audit --days N lists all of the last N days" : "")}):");
        foreach (var entry in entries) {
            var who = entry.Who + (entry.From.Length > 0 ? $" from {entry.From}" : "");
            output.WriteLine($"  {Time(entry.At)}  {entry.Tool,-12}  {who}: {AuditWords(entry)}");
        }

        return 0;
    }

    /// <summary>What an audit entry says was done, or refused and why.</summary>
    internal static string AuditWords(OperatorLogRow entry) {
        var what = entry.Action switch {
            OperatorActions.Actions.Ban => ("banned", "refused to ban", entry.Target),
            OperatorActions.Actions.Lift => ("lifted the ban on", "refused to lift the ban on", entry.Target),
            OperatorActions.Actions.Pin => ("pinned the operator keys of", "refused to pin the operator keys of", entry.Target),
            OperatorActions.Actions.AllowKeyLogin => ("let the identity key sign in again for", "refused to let the identity key sign in again for", entry.Target),
            _ => (entry.Action, $"refused: {entry.Action}", entry.Target),
        };
        return entry.Done
            ? $"{what.Item1} {what.Item3}{(entry.Detail.Length > 0 ? $", {entry.Detail}" : "")}"
            : $"{what.Item2} {what.Item3}: {entry.Why}";
    }

    private static int ListOperators(OperatorActions actions, IReadOnlyList<long> operators, TextWriter output) {
        var listed = actions.Operators();
        if (listed.Count == 0) {
            output.WriteLine("No characters are listed as operators: LookingGlass:Operators:Characters is empty (run this with the service's settings, " +
                             "as the LG helper in docs/server.md does).");
            return 0;
        }

        output.WriteLine($"Characters listed as operators ({listed.Count}):");
        foreach (var one in listed) {
            var pinned = one.Pin is { } pin ? $"pinned {Time(pin.PinnedAt)} {OperatorActions.ToolWords(pin.PinnedVia, pin.PinnedBy)}" : "";
            var compare = $"Compare the fingerprint under \"Your identity\" in that character's plugin Settings (Advanced mode on) with \"Now\", then " +
                          $"--operator-pin {one.UserId.ToString(CultureInfo.InvariantCulture)} --fingerprint \"<the fingerprint>\".";
            switch (one.Status) {
                case OperatorStatus.Operator:
                    output.WriteLine($"  {one.Name}: an operator. Keys {one.CurrentFingerprint}, {pinned}.");
                    break;
                case OperatorStatus.KeysChanged:
                    var changed = one.User!.KeysRegisteredAt is { } at && at >= one.Pin!.PinnedAt ? $" at {Time(at)}" : "";
                    output.WriteLine($"  {one.Name}: NOT an operator now: its keys changed{changed} (it was registered again), after they were {pinned}.");
                    output.WriteLine($"      Pinned: {one.PinnedFingerprint}");
                    output.WriteLine($"      Now:    {one.CurrentFingerprint}");
                    output.WriteLine($"      If its player did that (a new computer, \"Reset my identity\"): {compare} If not, someone else registered it, which " +
                                     "only needs its Lodestone page: secure that Square Enix account first, and don't pin these keys.");
                    break;
                case OperatorStatus.NotPinned:
                    output.WriteLine($"  {one.Name}: not an operator yet: its keys aren't pinned.");
                    output.WriteLine($"      Now:    {one.CurrentFingerprint}");
                    output.WriteLine($"      {compare}");
                    break;
                default:
                    output.WriteLine($"  {one.Name}: not registered on this server, so it has no keys to pin yet.");
                    break;
            }
        }

        return 0;
    }

    private static int Pin(OperatorActions actions, OperatorActor actor, Request request, TextWriter output, TextWriter errors) {
        var result = actions.PinByFingerprint(actor, request.Target!, request.Fingerprint!);
        if (!result.Done) {
            errors.WriteLine(result.Refusal switch {
                OperatorRefusal.KeysDiffer => $"The fingerprint you typed isn't that of the keys {result.Whom} has now ({result.Fingerprint}). Nothing was pinned. " +
                                              "Compare again with the plugin's Settings (Advanced mode, \"Your identity\"): if the plugin shows another, someone " +
                                              "else registered the character, which only needs its Lodestone page.",
                OperatorRefusal.NotRegistered => $"{result.Whom} isn't a character registered on this server, so it has no keys to pin. It registers through " +
                                                 "the Lodestone first.",
                OperatorRefusal.NoSuchCharacter => $"No character {result.Whom} is registered on this server.",
                OperatorRefusal.NotATarget => $"--operator-pin needs a character: {Character}.",
                _ => result.Why,
            });
            return 1;
        }

        output.WriteLine(result.Unchanged
            ? $"The keys of {result.Whom} ({result.Fingerprint}) were pinned already: nothing changed."
            : result.PreviousPin is { } previous
                ? $"Restored operator status for {result.Whom}: its keys {result.Fingerprint} are pinned now (they were " +
                  $"{Core.Crypto.IdentityKeys.FingerprintOfHash(previous.KeyHash)})."
                : $"Pinned the keys of {result.Whom} as an operator's: {result.Fingerprint}.");
        if (!result.Unchanged) {
            output.WriteLine("It is an operator while its account's keys stay these: registering it again (a new computer, \"Reset my identity\") ends that " +
                             "until they are pinned again.");
        }

        return 0;
    }

    private static string Until(BanRow ban) {
        if (ban.ExpiresAt is not { } expires) {
            return "until lifted";
        }

        var days = Math.Round((expires - ban.CreatedAt) / 86400.0, 1);
        return $"until {Time(expires)} ({days.ToString(CultureInfo.InvariantCulture)} {(days == 1 ? "day" : "days")})";
    }

    private static string Time(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>A clock stopped at the time given, for <see cref="Run"/>'s <c>now</c>.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
