using System.Globalization;
using System.Security.Cryptography;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Util;
using LookingGlass.Server.Data;
using LookingGlass.Server.Hosting;

namespace LookingGlass.Server.Services;

/// <summary>Who asks <see cref="OperatorActions"/> for something, with which tool.</summary>
/// <param name="Who">As the tool knows them: the Unix user (<c>SUDO_USER</c>), the Tailscale login, or the operator character's name@world.</param>
/// <param name="From">Where from, if the tool knows (the web page: the device's tailnet address); empty otherwise.</param>
/// <param name="UserId">The operator character's user ID, for the plugin (its bans an hour are counted by it).</param>
/// <param name="KeyHash">
/// For the plugin: the full hash of the keys whose signature the request was checked with. The character must still be an operator,
/// with these keys, when the change is made (checked again under the lock that makes it), so keys registered while the request was
/// on its way end it. Null: not checked here (the caller checked it; the plugin's requests always give it).
/// </param>
public sealed record OperatorActor(OperatorTool Tool, string Who, string From = "", long? UserId = null, byte[]? KeyHash = null);

/// <summary>What <see cref="OperatorActions"/> goes by: the server's settings that bear on bans and operators.</summary>
/// <param name="TrustedProxies"><c>LookingGlass:TrustedProxies</c>, whose addresses (like this machine's) are banned only when forced.</param>
/// <param name="Operators"><c>LookingGlass:Operators:Characters</c>: the characters that may be operators, by user ID.</param>
/// <param name="DebugAccounts"><c>LookingGlass:Dev:AllowDebugAccounts</c>: debug accounts, whose user IDs are negative, may register here.</param>
public sealed record OperatorSettings(AbuseOptions Abuse, IReadOnlyList<string> TrustedProxies, IReadOnlyList<long> Operators, bool DebugAccounts = false) {
    public static OperatorSettings From(ServerOptions options) =>
        new(options.Abuse, options.TrustedProxies, options.Operators.UserIds, options.Dev.AllowDebugAccounts);
}

/// <summary>What <see cref="OperatorActions"/> did, or why not.</summary>
/// <param name="Whom">Whom it was about, as shown ("user 31337 (Bob Hatter@Lich)", "address 203.0.113.0/24"), or as given if it named nobody.</param>
/// <param name="Why">Why it wasn't done, in words for any tool (each tool may say it its own way, from <see cref="Refusal"/>); empty if it was.</param>
public sealed record OperatorResult(OperatorRefusal Refusal, string Whom, string Why) {
    public bool Done => this.Refusal == OperatorRefusal.None;

    /// <summary>The character it was about, if any.</summary>
    public long? UserId { get; init; }

    /// <summary>The address or prefix it was about, if any, as a ban writes it.</summary>
    public string? Address { get; init; }

    /// <summary>The ban made.</summary>
    public BanRow? Ban { get; init; }

    /// <summary>How many bans in force a ban replaced (0 or 1).</summary>
    public int Replaced { get; init; }

    /// <summary>For <see cref="OperatorRefusal.NotATarget"/> and <see cref="OperatorRefusal.ProxyAddress"/>: the problem with the address, in words.</summary>
    public string? Problem { get; init; }

    /// <summary>For a pin: the fingerprint of the keys pinned, or (refused) of the character's current keys.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>For a pin: the pin the character had before, if any.</summary>
    public OperatorPinRow? PreviousPin { get; init; }

    /// <summary>For a pin: those keys were pinned already, so nothing changed.</summary>
    public bool Unchanged { get; init; }
}

/// <summary>Whether a character is an operator now (see "Key-pinned operators" in docs/operator-tools-plan.md).</summary>
public enum OperatorStatus {
    /// <summary>Not listed in <c>LookingGlass:Operators:Characters</c>.</summary>
    NotListed,

    /// <summary>Listed, and its account's current keys are the ones pinned.</summary>
    Operator,

    /// <summary>Listed and pinned, but its keys changed since (registered again): not an operator until pinned again.</summary>
    KeysChanged,

    /// <summary>Listed, but its keys were never pinned: not an operator until they are.</summary>
    NotPinned,

    /// <summary>Listed, but not registered on this server: no keys to pin.</summary>
    NotRegistered,
}

/// <summary>A character listed as one that may be an operator, and how it stands.</summary>
public sealed record ListedOperator(long UserId, UserRow? User, OperatorPinRow? Pin, OperatorStatus Status) {
    /// <summary>"user 31337 (Alice Wonderland@Lich)", or "user 31337" if it isn't registered.</summary>
    public string Name => this.User == null ? $"user {this.UserId}" : $"user {this.UserId} ({this.User.Name}@{this.User.WorldName})";

    /// <summary>The fingerprint of its account's current keys, if registered.</summary>
    public string? CurrentFingerprint => this.User?.Keys.Fingerprint;

    /// <summary>The fingerprint of the keys pinned for it, if any.</summary>
    public string? PinnedFingerprint => this.Pin == null ? null : IdentityKeys.FingerprintOfHash(this.Pin.KeyHash);
}

/// <summary>A ban as one tool is shown it.</summary>
/// <param name="Ban">The ban as the database has it, its address in full: never sent to a tool that doesn't see full addresses (use <see cref="ShownAddress"/>).</param>
/// <param name="Whom">"user 31337 (Bob Hatter@Lich)", or "address 203.0.113.0/24" (shortened for a tool that doesn't see full addresses).</param>
public sealed record ShownBan(BanRow Ban, string Whom) {
    /// <summary>For an address or prefix, as this tool may see it (shortened for the plugin); null for a character.</summary>
    public string? ShownAddress { get; init; }
}

/// <summary>A flag as one tool is shown it.</summary>
/// <param name="Flag">The flag as the database has it, its address in full: never sent to a tool that doesn't see full addresses (use <see cref="ShownAddress"/>).</param>
/// <param name="Banned">A ban in force is on the same subject.</param>
public sealed record ShownFlag(FlagRow Flag, string Whom, bool Banned) {
    /// <summary>For an address or prefix, as this tool may see it (shortened for the plugin); null for a character.</summary>
    public string? ShownAddress { get; init; }
}

/// <summary>The flags and bans as one tool is shown them.</summary>
/// <param name="FlagHours">How far back flags are listed (a flag expires this long after its last refusal).</param>
/// <param name="HistoryDays">How far back lifted and ended bans are listed.</param>
public sealed record OperatorView(IReadOnlyList<ShownBan> InForce, IReadOnlyList<ShownFlag> Flags, IReadOnlyList<ShownBan> History, int FlagHours, int HistoryDays);

/// <summary>
/// The operator's actions, written once for every tool (see "Operator actions, one layer for every tool" in docs/design.md):
/// list the flags and bans, ban, lift a ban, list the operator characters and pin their keys, and let a key sign in again.
/// Each tool (the command line, the web page and the plugin) asks this, and <see cref="OperatorPolicy"/> says what
/// it may do. Every attempt to change something, refused ones too, goes into the audit trail (<c>operator_log</c>), kept
/// <see cref="AuditKeptDays"/> days, a change's entry in the same transaction as the change; every ban and lift records the
/// tool and who.
///
/// Made inside the running server (the web page, the plugin), a ban or lift applies at once (<paramref name="applyBans"/>
/// rereads the bans and closes the connections they cover); from the command line, another process, the server picks it up
/// within <see cref="AbuseOptions.BanCheckSeconds"/>.
/// </summary>
/// <param name="applyBans">Called after every ban and lift, when this runs inside the server; null on the command line.</param>
public sealed class OperatorActions(Database db, OperatorSettings settings, TimeProvider? time = null, Action? applyBans = null, ILogger? logger = null) {
    /// <summary>The longest reason, in characters: the player is shown it.</summary>
    public const int MaxReasonLength = 300;

    /// <summary>The longest ban in days, short of one until lifted (about a hundred years).</summary>
    public const int MaxDays = 36_500;

    /// <summary>Days the audit trail is kept.</summary>
    public const int AuditKeptDays = 365;

    /// <summary>The longest "who" or "from" recorded, in characters.</summary>
    private const int MaxWhoLength = 100;

    /// <summary>The longest "whom" kept as typed (a name@world is under 40; an IPv6 prefix under 50), in characters.</summary>
    private const int MaxWhomLength = 200;

    /// <summary>The longest fingerprint kept as typed (25 digits and their spaces are 29), in characters.</summary>
    private const int MaxFingerprintLength = 64;

    /// <summary>The action names the audit trail records.</summary>
    public static class Actions {
        public const string Ban = "ban";
        public const string Lift = "lift";
        public const string Pin = "pin";
        public const string AllowKeyLogin = "allow key login";
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HashSet<long> _operators = [.. settings.Operators];

    // One change at a time in this process, so a tool's bans an hour can't be overrun by requests at once.
    private readonly Lock _changing = new();

    private long Now => this._time.GetUtcNow().ToUnixTimeSeconds();

    // ================================================================ seeing

    /// <summary>The bans in force, the flags that haven't expired and the bans lifted or ended lately, as <paramref name="tool"/> may see them.</summary>
    public OperatorView See(OperatorTool tool) {
        var full = OperatorPolicy.For(tool).FullAddresses;
        var now = this.Now;
        var bans = db.GetActiveBans(now);
        var hours = Math.Clamp(settings.Abuse.FlagExpiresAfterHours, 1, 8760);
        var flags = db.GetFlags(now - hours * 3600L);
        var days = Math.Clamp(settings.Abuse.BanHistoryDays, 1, 3650);
        var history = db.GetBanHistory(now).Where(ban => (ban.LiftedAt ?? ban.ExpiresAt) >= now - days * 86400L).ToList();
        var names = db.GetUsers(bans.Concat(history).Select(ban => ban.UserId).Concat(flags.Select(flag => flag.UserId)).OfType<long>())
            .ToDictionary(user => user.UserId, user => $"{user.Name}@{user.WorldName}");

        string? Shown(long? userId, string? address) => userId == null && address != null ? (full ? address : ClientAddresses.Shorten(address)) : null;

        string Whom(long? userId, string? address, string subject) =>
            userId is { } id ? (names.TryGetValue(id, out var name) ? $"user {id} ({name})" : $"user {id}")
            : Shown(userId, address) is { } shown ? $"address {shown}"
            : subject;

        ShownBan ShownBanOf(BanRow ban) => new(ban, Whom(ban.UserId, ban.Address, ban.Subject)) { ShownAddress = Shown(ban.UserId, ban.Address) };

        return new OperatorView(
            bans.Select(ShownBanOf).ToList(),
            flags.Select(flag => new ShownFlag(flag, Whom(flag.UserId, flag.Address, flag.Subject), bans.Any(ban => ban.Subject == flag.Subject)) {
                ShownAddress = Shown(flag.UserId, flag.Address),
            }).ToList(),
            history.Select(ShownBanOf).ToList(),
            hours, days);
    }

    /// <summary>
    /// The bans <paramref name="actor"/>'s tool may still make in this hour (for the plugin, this operator character's); null if
    /// it has no limit.
    /// </summary>
    public int? BansLeftThisHour(OperatorActor actor) {
        var rules = OperatorPolicy.For(actor.Tool);
        if (rules.BansPerHour is not { } limit) {
            return null;
        }

        var made = db.CountOperatorBans(OperatorPolicy.Code(actor.Tool), this.Now - 3600, rules.BansPerHourPerOperator ? actor.UserId ?? -1 : null);
        return Math.Max(0, limit - made);
    }

    /// <summary>Whether <c>LookingGlass:Operators:Characters</c> lists this character (whether or not it is an operator now).</summary>
    public bool IsListed(long userId) => this._operators.Contains(userId);

    /// <summary>The audit trail's entries at or after <paramref name="since"/> (Unix seconds), newest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<OperatorLogRow> Audit(long since, int limit) => db.GetOperatorLog(since, limit);

    /// <summary>"on the web page by alice@github", "in the plugin by Alice Wonderland@Lich", "automatically"; null if no tool was recorded.</summary>
    public static string? MadeWords(BanRow ban) => ToolWords(ban.Automatic && ban.MadeVia == null ? BanTools.Automatic : ban.MadeVia, ban.MadeBy);

    /// <summary>As <see cref="MadeWords"/>, for who lifted it; null if it wasn't lifted, or that wasn't recorded.</summary>
    public static string? LiftedWords(BanRow ban) => ban.LiftedAt == null ? null : ToolWords(ban.LiftedVia, ban.LiftedBy);

    /// <summary>"on the command line by alice", and so on, for a tool as the database records it.</summary>
    public static string? ToolWords(string? via, string? by) {
        var who = string.IsNullOrWhiteSpace(by) ? "" : $" by {by}";
        return via switch {
            null => null,
            BanTools.Automatic => "automatically",
            BanTools.CommandLine => "on the command line" + who,
            BanTools.WebPage => "on the web page" + who,
            BanTools.Plugin => "in the plugin" + who,
            _ => $"with {via}{who}",
        };
    }

    // ================================================================ banning

    /// <summary>
    /// Bans a registered character by name@world, any character by its user ID (its Lodestone ID; one not registered yet then
    /// can't register), or an address or prefix (see <see cref="ClientAddresses.BanPrefix"/>), for <paramref name="days"/> or until
    /// lifted, if <paramref name="actor"/>'s tool may (see <see cref="OperatorPolicy"/>). A ban the subject has already is
    /// replaced. Recorded in the audit trail either way.
    /// </summary>
    /// <param name="force">The server's own or its proxy's address is meant (and, on the command line, an operator character).</param>
    public OperatorResult Ban(OperatorActor actor, string whom, int? days, string? reason, bool force = false) => this.BanOrCheck(actor, whom, days, reason, force, change: true);

    /// <summary>
    /// What <see cref="Ban"/> would do, without doing it or recording anything: whom it names and whether the rules allow it,
    /// now (the web page's confirmation page shows this; the ban itself is checked again when confirmed).
    /// </summary>
    public OperatorResult CheckBan(OperatorActor actor, string whom, int? days, string? reason, bool force = false) => this.BanOrCheck(actor, whom, days, reason, force, change: false);

    private OperatorResult BanOrCheck(OperatorActor actor, string whom, int? days, string? reason, bool force, bool change) {
        actor = Clean(actor);
        whom = CleanWhom(whom);
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        // As kept in the audit trail: a reason refused for what it holds is kept visible, and cut.
        var detail = (days is { } d ? $"for {Days(d)}" : "until lifted") + (reason != null ? $". Reason: {VisibleText.Of(reason, MaxReasonLength)}" : "");
        lock (this._changing) {
            var now = this.Now;
            OperatorResult Refused(OperatorResult result) => change ? this.Record(actor, Actions.Ban, result, detail, now) : result;

            if (this.NoLongerOperator(actor, whom) is { } gone) {
                return Refused(gone);
            }

            if (days is < 1 or > MaxDays || reason is { Length: > MaxReasonLength } || (reason != null && !TextSanitizer.IsPlain(reason))) {
                return Refused(new OperatorResult(OperatorRefusal.NotValid, whom,
                    $"A ban lasts 1 to {MaxDays} days, or until lifted, and its reason is one line of plain text, at most {MaxReasonLength} characters."));
            }

            var subject = this.Resolve(whom);
            if (!subject.Done) {
                return Refused(subject);
            }

            var proxy = subject.Address != null ? ClientAddresses.ProxyProblem(subject.Address, settings.TrustedProxies) : null;
            var target = new PolicyTarget(subject.Address != null, proxy != null, subject.UserId is { } id && this._operators.Contains(id),
                subject.UserId is { } character && db.GetUser(character) == null);
            var rules = OperatorPolicy.For(actor.Tool);
            var bansThisHour = rules.BansPerHour == null ? 0
                : db.CountOperatorBans(OperatorPolicy.Code(actor.Tool), now - 3600, rules.BansPerHourPerOperator ? actor.UserId ?? -1 : null);
            var refusal = OperatorPolicy.Check(actor.Tool, OperatorAction.Ban, target, force, bansThisHour);
            if (refusal != OperatorRefusal.None) {
                return Refused(subject with { Refusal = refusal, Why = this.WhyNot(refusal, actor.Tool, subject.Whom, proxy, action: Actions.Ban), Problem = proxy });
            }

            if (!change) {
                return subject with { Problem = proxy };
            }

            var expires = days is { } count ? now + count * 86400L : (long?) null;
            var ban = db.AddBan(subject.UserId, subject.Address, reason ?? "", expires, automatic: false, now, out var replaced, OperatorPolicy.Code(actor.Tool),
                actor.Who, replacing => Row(actor, Actions.Ban, subject, detail + (replacing > 0 ? " (replacing the ban it had)" : ""), now));
            this.Apply();
            return subject with { Ban = ban, Replaced = replaced };
        }
    }

    /// <summary>Lifts the ban in force on a character or an address (exactly as written), if <paramref name="actor"/>'s tool may.</summary>
    /// <param name="banId">
    /// The ban a confirmation page showed: lifted only if it is still the one in force on the subject (not one that replaced it
    /// since, unseen); null for whatever ban is in force.
    /// </param>
    public OperatorResult Lift(OperatorActor actor, string whom, long? banId = null) {
        actor = Clean(actor);
        whom = CleanWhom(whom);
        lock (this._changing) {
            var now = this.Now;
            if (this.NoLongerOperator(actor, whom) is { } gone) {
                return this.Record(actor, Actions.Lift, gone, "", now);
            }

            var subject = this.Resolve(whom);
            if (!subject.Done) {
                return this.Record(actor, Actions.Lift, subject, "", now);
            }

            var refusal = OperatorPolicy.Check(actor.Tool, OperatorAction.Lift,
                new PolicyTarget(subject.Address != null, IsOperatorCharacter: subject.UserId is { } id && this._operators.Contains(id)));
            if (refusal == OperatorRefusal.None && banId is { } shown
                && !db.GetActiveBans(now).Any(ban => ban.BanId == shown && ban.UserId == subject.UserId && ban.Address == subject.Address)) {
                return this.Record(actor, Actions.Lift, subject with {
                    Refusal = OperatorRefusal.NothingToLift,
                    Why = $"The ban on {subject.Whom} that was shown isn't in force any more (it ended, or was lifted or replaced since). Nothing was lifted.",
                }, "", now);
            }

            if (refusal == OperatorRefusal.None &&
                db.LiftBans(subject.UserId, subject.Address, now, OperatorPolicy.Code(actor.Tool), actor.Who, Row(actor, Actions.Lift, subject, "", now)) == 0) {
                refusal = OperatorRefusal.NothingToLift;
            }

            if (refusal != OperatorRefusal.None) {
                return this.Record(actor, Actions.Lift, subject with { Refusal = refusal, Why = this.WhyNot(refusal, actor.Tool, subject.Whom, null, action: Actions.Lift) }, "", now);
            }

            this.Apply();
            return subject;
        }
    }

    // ================================================================ key login

    /// <summary>
    /// Lets a registered character's identity key sign in again after its "Sign out everywhere else" (see
    /// <see cref="Database.AllowKeyLogin"/>), if <paramref name="actor"/>'s tool may. A running server goes by it at the next key login.
    /// </summary>
    public OperatorResult AllowKeyLogin(OperatorActor actor, string whom) {
        actor = Clean(actor);
        whom = CleanWhom(whom);
        lock (this._changing) {
            var now = this.Now;
            var subject = this.Resolve(whom);
            if (!subject.Done) {
                return this.Record(actor, Actions.AllowKeyLogin, subject, "", now);
            }

            var refusal = OperatorPolicy.Check(actor.Tool, OperatorAction.AllowKeyLogin);
            if (refusal == OperatorRefusal.None && (subject.UserId is not { } userId || db.GetUser(userId) == null)) {
                refusal = OperatorRefusal.NotRegistered;
            } else if (refusal == OperatorRefusal.None && !db.AllowKeyLogin(subject.UserId!.Value, Row(actor, Actions.AllowKeyLogin, subject, "", now))) {
                refusal = OperatorRefusal.KeyLoginNotOff;
            }

            return refusal == OperatorRefusal.None
                ? subject
                : this.Record(actor, Actions.AllowKeyLogin, subject with { Refusal = refusal, Why = this.WhyNot(refusal, actor.Tool, subject.Whom, null) }, "", now);
        }
    }

    // ================================================================ operators

    /// <summary>
    /// No character is listed here, yet the database holds operator pins: the server's own settings list some (pins of
    /// unlisted characters go when it starts), so these settings aren't the server's, and an operator character can't be told
    /// from another (the command line run without the setting, say).
    /// </summary>
    public bool OperatorListMissing => settings.Operators.Count == 0 && db.GetOperatorPins().Count > 0;

    /// <summary>Whether a character is an operator now: listed, and its account's current keys hash to its pin (the full hash, never the 25 digits).</summary>
    public OperatorStatus StatusOf(long userId) {
        if (!this._operators.Contains(userId)) {
            return OperatorStatus.NotListed;
        }

        return Status(db.GetUser(userId), db.GetOperatorPin(userId));
    }

    /// <summary>Each character listed in <c>LookingGlass:Operators:Characters</c>, in that order, with its pin and how it stands.</summary>
    public IReadOnlyList<ListedOperator> Operators() {
        var users = db.GetUsers(settings.Operators).ToDictionary(user => user.UserId);
        var pins = db.GetOperatorPins().ToDictionary(pin => pin.UserId);
        return settings.Operators.Distinct().Select(id => {
            var user = users.GetValueOrDefault(id);
            var pin = pins.GetValueOrDefault(id);
            return new ListedOperator(id, user, pin, Status(user, pin));
        }).ToList();
    }

    /// <summary>
    /// Pins a listed character's current keys as an operator's (or restores it, after its keys changed), if the fingerprint
    /// typed is exactly theirs: how the command line pins, after the operator compared it with the plugin's Settings.
    /// </summary>
    public OperatorResult PinByFingerprint(OperatorActor actor, string whom, string fingerprint) =>
        this.Pin(actor, whom, user => NormalFingerprint(fingerprint) is { } typed && typed == NormalFingerprint(user.Keys.Fingerprint) ? user.Keys.Hash : null,
            $"typed {VisibleText.Of(fingerprint.Trim(), MaxFingerprintLength)}");

    /// <summary>
    /// Pins a listed character's keys as an operator's if they are still the keys whose full hash is <paramref name="keyHashShown"/>
    /// (what a confirmation page showed), so keys that changed again before the click aren't pinned unseen: how the web page pins.
    /// </summary>
    public OperatorResult PinShownKeys(OperatorActor actor, long userId, byte[] keyHashShown) =>
        this.Pin(actor, userId.ToString(CultureInfo.InvariantCulture), _ => keyHashShown, $"shown {IdentityKeys.FingerprintOfHash(keyHashShown)}");

    /// <summary>
    /// Deletes the pins of characters no longer listed in <c>LookingGlass:Operators:Characters</c>, and logs each, and how every
    /// listed character stands: at startup.
    /// </summary>
    /// <returns>The user IDs whose pins went.</returns>
    public IReadOnlyList<long> StartUp() {
        var gone = db.DeleteOperatorPinsExcept(this._operators);
        foreach (var id in gone) {
            logger?.LogWarning("Deleted the operator pin of user {UserId}: LookingGlass:Operators:Characters doesn't list it any more, so it isn't an operator", id);
        }

        if (this._operators.Count > 0) {
            logger?.LogInformation("Operators: {Operators}", string.Join("; ", this.Operators().Select(listed => listed.Status switch {
                OperatorStatus.Operator => $"{listed.Name}, keys pinned",
                OperatorStatus.KeysChanged => $"{listed.Name}, its keys changed since they were pinned, so not an operator until pinned again",
                OperatorStatus.NotPinned => $"{listed.Name}, keys not pinned yet, so not an operator until they are",
                _ => $"{listed.Name}, not registered on this server",
            })));
        }

        return gone;
    }

    /// <summary>
    /// The Unix user who ran the command: <c>SUDO_USER</c> (the <c>LG</c> helper runs it with sudo), or else the user it runs
    /// as. What sudo says, recorded for the audit trail; not proof (whoever can run it can set it).
    /// </summary>
    public static string CommandLineUser(Func<string, string?>? environment = null) {
        environment ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "SUDO_USER", "USER", "USERNAME" }) {
            if (environment(name) is { } value && !string.IsNullOrWhiteSpace(value)) {
                return CleanText(value);
            }
        }

        return "unknown";
    }

    /// <summary>A fingerprint as 25 digits without spaces, or null if it isn't one.</summary>
    public static string? NormalFingerprint(string fingerprint) {
        var digits = new string(fingerprint.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return digits.Length == 25 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    private OperatorResult Pin(OperatorActor actor, string whom, Func<UserRow, byte[]?> expected, string detail) {
        actor = Clean(actor);
        whom = CleanWhom(whom);
        lock (this._changing) {
            var now = this.Now;
            var subject = this.Resolve(whom);
            if (!subject.Done) {
                return this.Record(actor, Actions.Pin, subject, detail, now);
            }

            OperatorResult Refused(OperatorRefusal refusal, string? fingerprint = null) =>
                this.Record(actor, Actions.Pin, subject with { Refusal = refusal, Why = this.WhyNot(refusal, actor.Tool, subject.Whom, null, fingerprint), Fingerprint = fingerprint },
                    detail, now);

            var policy = OperatorPolicy.Check(actor.Tool, OperatorAction.Pin);
            if (policy != OperatorRefusal.None) {
                return Refused(policy);
            }

            if (subject.UserId is not { } userId || subject.Address != null) {
                return Refused(OperatorRefusal.NotRegistered);
            }

            if (!this._operators.Contains(userId)) {
                return Refused(OperatorRefusal.NotListed);
            }

            if (db.GetUser(userId) is not { } user) {
                return Refused(OperatorRefusal.NotRegistered);
            }

            var current = user.Keys.Fingerprint;
            if (expected(user) is not { } hash) {
                return Refused(OperatorRefusal.KeysDiffer, current);
            }

            static string Was(OperatorPinRow? previous) => previous == null ? "" : $" (was {IdentityKeys.FingerprintOfHash(previous.KeyHash)})";
            switch (db.PinOperator(userId, hash, OperatorPolicy.Code(actor.Tool), actor.Who, now, out var before,
                        replaced => Row(actor, Actions.Pin, subject, $"keys {current}{Was(replaced)}", now))) {
                case PinResult.NotRegistered:
                    return Refused(OperatorRefusal.NotRegistered);
                case PinResult.KeysDiffer:
                    // The keys changed between reading them and pinning, or the hash shown is another's: shown as they are now.
                    return Refused(OperatorRefusal.KeysDiffer, db.GetUser(userId)?.Keys.Fingerprint);
                case PinResult.AlreadyPinned:
                    this.Record(actor, Actions.Pin, subject, $"keys {current} (pinned already; nothing changed)", now);
                    return subject with { Fingerprint = current, PreviousPin = before, Unchanged = true };
                default:
                    return subject with { Fingerprint = current, PreviousPin = before };
            }
        }
    }

    private static OperatorStatus Status(UserRow? user, OperatorPinRow? pin) {
        if (user == null) {
            return OperatorStatus.NotRegistered;
        }

        if (pin == null) {
            return OperatorStatus.NotPinned;
        }

        return CryptographicOperations.FixedTimeEquals(pin.KeyHash, user.Keys.Hash) ? OperatorStatus.Operator : OperatorStatus.KeysChanged;
    }

    // ================================================================ helpers

    /// <summary>
    /// For a plugin action: why it can't go ahead because its character isn't an operator now, checked under the lock that makes
    /// the change (listed, its account's current keys the pinned ones, and the keys the request was checked with); null if it is.
    /// A request checked before keys were registered again, and still on its way, so changes nothing.
    /// </summary>
    private OperatorResult? NoLongerOperator(OperatorActor actor, string whom) {
        if (actor.Tool != OperatorTool.Plugin || actor.KeyHash is not { } signed) {
            return null;
        }

        var still = actor.UserId is { } id && this.StatusOf(id) == OperatorStatus.Operator
                    && db.GetUser(id) is { } user && CryptographicOperations.FixedTimeEquals(user.Keys.Hash, signed);
        return still ? null : new OperatorResult(OperatorRefusal.NotAnOperator, whom, this.WhyNot(OperatorRefusal.NotAnOperator, actor.Tool, whom, null));
    }

    /// <summary>The character or address a target names (as a result to fill in), or why it names none.</summary>
    private OperatorResult Resolve(string target) {
        target = target.Trim();
        if (long.TryParse(target, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId)) {
            // A Lodestone ID is positive. Debug accounts' are negative: one registered can be named by it, and any where they may register.
            var user = userId < 0 ? db.GetUser(userId) : null;
            if (userId == 0 || (userId < 0 && user == null && !settings.DebugAccounts)) {
                var notAnId = $"\"{target}\" isn't a user ID: user IDs (Lodestone IDs) are positive numbers.";
                return new OperatorResult(OperatorRefusal.NotATarget, target, notAnId) { Problem = notAnId };
            }

            user ??= db.GetUser(userId);
            return new OperatorResult(OperatorRefusal.None, user == null ? $"user {userId}" : $"user {userId} ({user.Name}@{user.WorldName})", "") { UserId = userId };
        }

        var at = target.LastIndexOf('@');
        if (at > 0) {
            var found = db.FindUser(target[..at], target[(at + 1)..]);
            return found == null
                ? new OperatorResult(OperatorRefusal.NoSuchCharacter, target, $"No character {target} is registered on this server.")
                : new OperatorResult(OperatorRefusal.None, $"user {found.UserId} ({found.Name}@{found.WorldName})", "") { UserId = found.UserId };
        }

        var prefix = ClientAddresses.BanPrefix(target, out var problem);
        return prefix == null
            ? new OperatorResult(OperatorRefusal.NotATarget, target, problem!) { Problem = problem }
            : new OperatorResult(OperatorRefusal.None, $"address {prefix}", "") { Address = prefix };
    }

    /// <summary>Why a refusal, in words for any tool.</summary>
    private string WhyNot(OperatorRefusal refusal, OperatorTool tool, string whom, string? problem, string? fingerprint = null, string? action = null) => refusal switch {
        OperatorRefusal.OperatorCharacter when action == Actions.Lift =>
            $"{whom} is listed as an operator (LookingGlass:Operators:Characters): its bans are lifted on the web page or the command line. Nothing was lifted.",
        OperatorRefusal.NotRegistered when action == Actions.Ban =>
            $"{whom} isn't registered on this server, so it can't be banned here: ban its Lodestone ID on the web page. Nothing was banned.",
        OperatorRefusal.NotAnOperator => "Only this server's operators can do that, and this character isn't one now (its keys changed, or it isn't listed). Nothing was done.",
        OperatorRefusal.NotThisTool => $"That can't be done {ToolWords(OperatorPolicy.Code(tool), null)}.",
        OperatorRefusal.AddressesNotHere => "Only characters can be banned here; address bans, and lifting them, are on the web page and the command line.",
        OperatorRefusal.ForceNotHere => "The server's own or its proxy's address can't be banned here.",
        OperatorRefusal.ProxyAddress => $"{problem} Nothing was banned.",
        OperatorRefusal.OperatorCharacter => $"{whom} is listed as an operator (LookingGlass:Operators:Characters), and banning it would lock an operator out. Nothing was banned.",
        OperatorRefusal.HourLimit => tool == OperatorTool.Plugin
            ? $"This operator character has made {OperatorPolicy.PluginBansPerHour} bans in the last hour, the most the plugin allows; use the web page for more."
            : $"{OperatorPolicy.For(tool).BansPerHour} bans have been made {ToolWords(OperatorPolicy.Code(tool), null)} in the last hour, the most it allows; use the command line for more.",
        OperatorRefusal.NothingToLift => $"There is no ban on {whom} in force.",
        OperatorRefusal.NotRegistered => $"{whom} isn't a character registered on this server.",
        OperatorRefusal.NotListed => $"{whom} isn't listed in LookingGlass:Operators:Characters: only the server's settings, and a restart, make a character an operator.",
        OperatorRefusal.KeysDiffer => $"Those aren't the keys {whom} has now{(fingerprint != null ? $" ({fingerprint})" : "")}. Nothing was pinned.",
        OperatorRefusal.KeyLoginNotOff => $"Signing in with the identity key isn't turned off for {whom}: nothing to do.",
        _ => refusal.ToString(),
    };

    /// <summary>Records an attempt that changed nothing in the audit trail, and returns its result. (A change writes its entry in its own transaction.)</summary>
    private OperatorResult Record(OperatorActor actor, string action, OperatorResult result, string detail, long now) {
        db.AddOperatorLog(Row(actor, action, result, detail, now));
        return result;
    }

    /// <summary>An attempt's entry in the audit trail.</summary>
    private static OperatorLogRow Row(OperatorActor actor, string action, OperatorResult result, string detail, long now) =>
        new(0, now, OperatorPolicy.Code(actor.Tool), actor.Who, actor.From, actor.UserId, action, result.Whom, detail, result.Done, result.Why);

    /// <summary>Puts bans changed inside the running server into effect at once.</summary>
    private void Apply() {
        try {
            applyBans?.Invoke();
        } catch (Exception ex) {
            // The regular check (every BanCheckSeconds) puts it into effect anyway.
            logger?.LogWarning(ex, "Putting a ban into effect at once failed; it applies at the next check of the bans");
        }
    }

    private static OperatorActor Clean(OperatorActor actor) {
        if (actor.Tool == OperatorTool.Plugin && actor.UserId == null) {
            throw new ArgumentException("The plugin's actions are by an operator character, by its user ID.", nameof(actor));
        }

        return actor with { Who = string.IsNullOrWhiteSpace(actor.Who) ? "unknown" : CleanText(actor.Who), From = CleanText(actor.From) };
    }

    /// <summary>Text from outside (an environment variable, a header) as one short line, anything hidden in it shown as escapes.</summary>
    private static string CleanText(string text) => VisibleText.Of(text.Trim(), MaxWhoLength);

    /// <summary>
    /// Whom, as typed, made visible and cut once, before anything else: everything after (the lookup, the refusal's words, the
    /// audit trail) sees only this. No real target has anything hidden in it, so such a one names nobody.
    /// </summary>
    private static string CleanWhom(string whom) => VisibleText.Of(whom.Trim(), MaxWhomLength);

    private static string Days(int days) => days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";
}
