using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LookingGlass.Core.SelfTest;

/// <summary>What the host may ask the partner's plugin: the fixed list (docs/self-test-plan.md, "The protocol").</summary>
public enum PartnerVerb {
    /// <summary>"hello": its version, private place, ChatTwo, mode.</summary>
    Hello,

    /// <summary>"still-there": the same, after a silence.</summary>
    StillThere,

    /// <summary>"send n [link=item|map|status|pf] [in=a|b|c]": n (1 to 5) marked lines to a test channel.</summary>
    Send,

    /// <summary>"send-away n [in=...]": the same, once the host shows offline in the member list (the host is relogging).</summary>
    SendWhileAway,

    /// <summary>"local n": n (1 to 3) marked lines to local chat, after its own private-place check.</summary>
    Local,

    /// <summary>"report received": what it got since the last report.</summary>
    ReportReceived,

    /// <summary>"report check-code": the test channel's check code.</summary>
    ReportCheckCode,

    /// <summary>"report saw n": whether the host's test line n showed in its game chat, and where.</summary>
    ReportSaw,

    /// <summary>"report windows": its windows and counts for the test channels.</summary>
    ReportWindows,

    /// <summary>"report log": what its log tap and its words checks counted (numbers only).</summary>
    ReportLog,

    /// <summary>"leave": leaves the test channel; the host invites it again.</summary>
    Leave,

    /// <summary>"withdraw-local": its local chat privacy acceptance off (put back by restore-local, or at the end).</summary>
    WithdrawLocal,

    RestoreLocal,

    /// <summary>"disconnect s": disconnects for s seconds (at most 60), then connects.</summary>
    Disconnect,

    /// <summary>"ask id": shows its player a fixed guided step (<see cref="PartnerAsk"/>); answers yes, no or skipped.</summary>
    Ask,

    /// <summary>"invite-me": makes one channel of its own and invites the host (windows-only 10); the host accepts only it.</summary>
    InviteMe,

    /// <summary>"bye": cleans up and stops.</summary>
    Bye,
}

/// <summary>The guided steps a host may ask the partner's player to do: a fixed list, each with fixed words.</summary>
public enum PartnerAsk {
    /// <summary>Types a marked line in Say in the game's chat box (the host right-clicks the name).</summary>
    SayHello,

    /// <summary>The same in ChatTwo's input.</summary>
    SayHelloChatTwo,

    /// <summary>Walks away, past 20 yalms.</summary>
    WalkAway,

    /// <summary>Comes back next to the host.</summary>
    ComeBack,

    /// <summary>Opens the friends list once.</summary>
    OpenFriends,

    /// <summary>Accepts the host's party invite.</summary>
    JoinParty,

    /// <summary>Leaves the party.</summary>
    LeaveParty,

    /// <summary>Clicks the party finder link from the host, in chat and in its window.</summary>
    ClickLink,

    /// <summary>Goes to the title screen and back.</summary>
    Relog,
}

/// <summary>How a partner answered.</summary>
public enum PartnerOutcome {
    /// <summary>"done": done, with its fields.</summary>
    Done,

    /// <summary>"yes" / "no" / "skipped": its player's answer to an ask.</summary>
    Yes,
    No,
    Skipped,

    /// <summary>"refused": past a cap, or not something it does now.</summary>
    Refused,

    /// <summary>The partner's own line: it joined (with its facts).</summary>
    Hello,

    /// <summary>The partner's own line: back from a relog or reload.</summary>
    Back,
}

/// <summary>A request from the host, numbered.</summary>
public sealed record PartnerRequest(int Number, PartnerVerb Verb, IReadOnlyList<string> Args) {
    public static PartnerRequest Of(int number, PartnerVerb verb, params string[] args) => new(number, verb, args);

    /// <summary>An argument "key=value"'s value, or null.</summary>
    public string? Option(string key) => this.Args.Where(arg => arg.StartsWith(key + "=", StringComparison.Ordinal)).Select(arg => arg[(key.Length + 1)..]).FirstOrDefault();

    /// <summary>The first argument as a count.</summary>
    public int Count => this.Args.Count > 0 && int.TryParse(this.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;

    public bool Equals(PartnerRequest? other) => other != null && other.Number == this.Number && other.Verb == this.Verb && other.Args.SequenceEqual(this.Args);

    public override int GetHashCode() => HashCode.Combine(this.Number, this.Verb, this.Args.Count);
}

/// <summary>An answer from the partner (or its own hello and back, numbered 0), with fixed fields: short keys, plain values.</summary>
public sealed record PartnerAnswer(int Number, PartnerOutcome Outcome, IReadOnlyDictionary<string, string> Fields) {
    public int? Int(string key) => this.Fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
        ? number
        : null;

    public bool Is(string key, string value) => this.Fields.TryGetValue(key, out var found) && found == value;
}

/// <summary>A line read in the test channel.</summary>
public abstract record ProtocolLine {
    /// <summary>Not this run's (no exact prefix): ignored.</summary>
    public sealed record NotOurs : ProtocolLine;

    /// <summary>This run's prefix, but nothing the protocol knows: ignored from the paired player, ends the run from anyone else.</summary>
    public sealed record Malformed : ProtocolLine;

    /// <summary>A marked test line ("LG self-test K7Q2 4, please ignore", or the partner's "p4").</summary>
    public sealed record TestLine : ProtocolLine;

    public sealed record Request(PartnerRequest Value) : ProtocolLine;

    public sealed record Answer(PartnerAnswer Value) : ProtocolLine;
}

/// <summary>
/// The two-player protocol: ordinary channel messages in the test channel (encrypted and signed as any message, nothing new
/// for the server), each a line a person can read and a parser can check strictly. "LG self-test K7Q2 #12 send 2 link=item"
/// asks; "LG self-test K7Q2 #12 done sent=2" answers. Anything without the run's exact prefix is not the protocol's;
/// anything with it that doesn't parse, or asks past a cap, is refused. No names and no free text ever travel in it.
/// </summary>
public static partial class SelfTestProtocol {
    /// <summary>The longest line.</summary>
    public const int MaxLength = 200;

    public const int MostPerSend = 5;
    public const int MostPerLocal = 3;
    public const int MostDisconnectSeconds = 60;

    /// <summary>Per run, on the partner's side.</summary>
    public const int MostLeaves = 5;
    public const int MostDisconnects = 3;
    public const int MostWithdraws = 1;

    /// <summary>How long the host waits for an answer (a loading screen included), or for one that waits on the partner's player.</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan GuidedAnswerWithin = TimeSpan.FromMinutes(10);

    /// <summary>A partner hearing nothing this long leaves the test channel and stops by itself.</summary>
    public static readonly TimeSpan PartnerGivesUpAfter = TimeSpan.FromMinutes(15);

    /// <summary>How long pairing waits for the partner to join.</summary>
    public static readonly TimeSpan PairingWithin = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<PartnerVerb, string> Verbs = new() {
        [PartnerVerb.Hello] = "hello",
        [PartnerVerb.StillThere] = "still-there",
        [PartnerVerb.Send] = "send",
        [PartnerVerb.SendWhileAway] = "send-away",
        [PartnerVerb.Local] = "local",
        [PartnerVerb.ReportReceived] = "report received",
        [PartnerVerb.ReportCheckCode] = "report check-code",
        [PartnerVerb.ReportSaw] = "report saw",
        [PartnerVerb.ReportWindows] = "report windows",
        [PartnerVerb.ReportLog] = "report log",
        [PartnerVerb.Leave] = "leave",
        [PartnerVerb.WithdrawLocal] = "withdraw-local",
        [PartnerVerb.RestoreLocal] = "restore-local",
        [PartnerVerb.Disconnect] = "disconnect",
        [PartnerVerb.Ask] = "ask",
        [PartnerVerb.InviteMe] = "invite-me",
        [PartnerVerb.Bye] = "bye",
    };

    private static readonly Dictionary<PartnerOutcome, string> Outcomes = new() {
        [PartnerOutcome.Done] = "done",
        [PartnerOutcome.Yes] = "yes",
        [PartnerOutcome.No] = "no",
        [PartnerOutcome.Skipped] = "skipped",
        [PartnerOutcome.Refused] = "refused",
        [PartnerOutcome.Hello] = "hello-from-partner",
        [PartnerOutcome.Back] = "back",
    };

    private static readonly Dictionary<PartnerAsk, string> Asks = new() {
        [PartnerAsk.SayHello] = "say-hello",
        [PartnerAsk.SayHelloChatTwo] = "say-hello-chattwo",
        [PartnerAsk.WalkAway] = "walk-away",
        [PartnerAsk.ComeBack] = "come-back",
        [PartnerAsk.OpenFriends] = "open-friends",
        [PartnerAsk.JoinParty] = "join-party",
        [PartnerAsk.LeaveParty] = "leave-party",
        [PartnerAsk.ClickLink] = "click-link",
        [PartnerAsk.Relog] = "relog",
    };

    private static readonly HashSet<string> LinkKinds = ["item", "map", "status", "pf"];
    private static readonly HashSet<string> Channels = ["a", "b", "c"];

    /// <summary>The run's prefix: "LG self-test K7Q2 ".</summary>
    public static string Prefix(string code) => $"LG self-test {code} ";

    /// <summary>The line has this run's exact prefix.</summary>
    public static bool IsOurs(string code, string? text) => text != null && text.StartsWith(Prefix(code), StringComparison.Ordinal);

    /// <summary>A partner's marked test line: "LG self-test K7Q2 p4, please ignore".</summary>
    public static string PartnerMarker(string code, int n) => $"{Prefix(code)}p{n.ToString(CultureInfo.InvariantCulture)}, please ignore";

    /// <summary>A check code ("#41 12345 67890 13579 24680 11223") as an answer's field ("41:12345.67890.13579.24680.11223"), or null.</summary>
    public static string? CheckCodeField(string? checkCode) {
        var parts = checkCode?.TrimStart('#').Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length < 2) {
            return null;
        }

        var field = $"{parts[0]}:{string.Join('.', parts[1..])}";
        return Value().IsMatch(field) ? field : null;
    }

    /// <summary>Whether a value can travel in an answer's field as it is.</summary>
    public static bool IsPlain(string? value) => value != null && Value().IsMatch(value);

    /// <summary>A plain field value made of a few words (link kinds, channels): joined with dots, or null if none.</summary>
    public static string? Joined(IEnumerable<string> words) {
        var field = string.Join('.', words.Distinct());
        return field.Length > 0 && Value().IsMatch(field) ? field : null;
    }

    /// <summary>The words an ask has on the wire.</summary>
    public static string NameOf(PartnerAsk ask) => Asks[ask];

    public static PartnerAsk? AskOf(string name) => Asks.FirstOrDefault(entry => entry.Value == name) is { Value: not null } found ? found.Key : null;

    public static string Write(string code, PartnerRequest request) {
        var text = new StringBuilder(Prefix(code)).Append('#').Append(request.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Verbs[request.Verb]);
        foreach (var arg in request.Args) {
            text.Append(' ').Append(arg);
        }

        return text.ToString();
    }

    public static string Write(string code, PartnerAnswer answer) {
        var text = new StringBuilder(Prefix(code)).Append('#').Append(answer.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Outcomes[answer.Outcome]);
        foreach (var (key, value) in answer.Fields.OrderBy(field => field.Key, StringComparer.Ordinal)) {
            if (!Token().IsMatch(key) || !Value().IsMatch(value)) {
                throw new ArgumentException("A field that isn't plain.", nameof(answer));
            }

            text.Append(' ').Append(key).Append('=').Append(value);
        }

        return text.ToString();
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,19}$")]
    private static partial Regex Token();

    [GeneratedRegex("^[A-Za-z0-9.:_-]{1,40}$")]
    private static partial Regex Value();

    [GeneratedRegex(@"^p?\d{1,4}\b")]
    private static partial Regex Marker();

    /// <summary>Reads a line of the test channel, strictly. Never throws.</summary>
    public static ProtocolLine Parse(string code, string? text) {
        if (!IsOurs(code, text)) {
            return new ProtocolLine.NotOurs();
        }

        if (text!.Length > MaxLength) {
            return new ProtocolLine.Malformed();
        }

        var rest = text[Prefix(code).Length..];
        if (Marker().IsMatch(rest) && rest.EndsWith(", please ignore", StringComparison.Ordinal)) {
            return new ProtocolLine.TestLine();
        }

        if (!rest.StartsWith('#')) {
            return new ProtocolLine.Malformed();
        }

        var words = rest[1..].Split(' ');
        if (words.Length < 2 || words[0].Length is 0 or > 6 || !words[0].All(char.IsAsciiDigit) || !int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            return new ProtocolLine.Malformed();
        }

        var tail = words.Skip(1).ToList();
        if (Outcomes.FirstOrDefault(entry => entry.Value == tail[0]) is { Value: not null } outcome) {
            return ParseAnswer(number, outcome.Key, tail.Skip(1).ToList());
        }

        // "report" verbs are two words.
        var verbText = tail[0] == "report" && tail.Count > 1 ? $"report {tail[1]}" : tail[0];
        if (Verbs.FirstOrDefault(entry => entry.Value == verbText) is not { Value: not null } verb) {
            return new ProtocolLine.Malformed();
        }

        var args = tail.Skip(verbText.Contains(' ') ? 2 : 1).ToList();
        return Valid(verb.Key, args) ? new ProtocolLine.Request(new PartnerRequest(number, verb.Key, args)) : new ProtocolLine.Malformed();
    }

    private static ProtocolLine ParseAnswer(int number, PartnerOutcome outcome, List<string> fields) {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields) {
            var at = field.IndexOf('=');
            if (at <= 0 || !Token().IsMatch(field[..at]) || !Value().IsMatch(field[(at + 1)..]) || !parsed.TryAdd(field[..at], field[(at + 1)..])) {
                return new ProtocolLine.Malformed();
            }
        }

        return new ProtocolLine.Answer(new PartnerAnswer(number, outcome, parsed));
    }

    private static bool Valid(PartnerVerb verb, List<string> args) {
        bool Count(int most) => args.Count >= 1 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= most;
        bool Options(int from, params string[] keys) => args.Skip(from).All(arg => {
            var at = arg.IndexOf('=');
            if (at <= 0) {
                return false;
            }

            var (key, value) = (arg[..at], arg[(at + 1)..]);
            return keys.Contains(key) && (key == "link" ? LinkKinds.Contains(value) : Channels.Contains(value));
        }) && args.Skip(from).Select(arg => arg[..arg.IndexOf('=')]).Distinct().Count() == args.Count - from;

        return verb switch {
            PartnerVerb.Send or PartnerVerb.SendWhileAway => Count(MostPerSend) && Options(1, "link", "in"),
            PartnerVerb.Local => args.Count == 1 && Count(MostPerLocal),
            PartnerVerb.ReportSaw => args.Count == 1 && args[0].Length <= 4 && args[0].All(char.IsAsciiDigit),
            PartnerVerb.Disconnect => args.Count == 1 && Count(MostDisconnectSeconds),
            PartnerVerb.Ask => args.Count == 1 && AskOf(args[0]) != null,
            _ => args.Count == 0,
        };
    }
}
