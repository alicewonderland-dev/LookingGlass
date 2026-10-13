using System.Globalization;
using LookingGlass.Core.Client;

namespace LookingGlass.Core.SelfTest;

/// <summary>
/// The partner's runner (docs/self-test-plan.md, "Two players"): accepts only the host's invites to the host's test
/// channels ("LG self-test K7Q2", "... b", "... c"), says hello, then answers the host's numbered requests from the fixed
/// list, each within its cap, and nothing else. It never types in game chat by itself: a step that needs its player shows a
/// "Needs you" box, behind the same typing gate as any step. A line with the run's prefix from anyone but the two testers
/// stops it; so do "bye", fifteen minutes without a request, and the host's channel going away. The host's name is held in
/// the run file only while the run lasts, and is compared, never shown or logged.
/// </summary>
public sealed class PartnerSide {
    /// <summary>How long the partner waits for the host's first invite.</summary>
    public static readonly TimeSpan InviteWithin = TimeSpan.FromMinutes(10);

    /// <summary>The most invites it accepts in a run: the host's three channels, and each leave's invite back.</summary>
    public const int MostAccepts = 3 + SelfTestProtocol.MostLeaves;

    private StepContext _context;
    private readonly long _start;
    private readonly HashSet<string> _left = [];
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, DateTimeOffset> _acceptedAt = new();
    private readonly HashSet<string> _links = [];
    private readonly List<string> _saidYes = [];
    private long _mark;
    private long _reportMark;
    private DateTimeOffset _heard;
    private int _answered, _marker, _lines, _locals, _reports, _leaves, _disconnects, _withdraws, _accepts, _asks;
    private int _got, _own, _caught;
    private bool _invitedHost, _bye;

    public PartnerSide(StepContext context) {
        this._context = context;
        this._start = context.Host.Mark();
        this._mark = this._start;
        this._reportMark = this._start;
        this._heard = context.Host.Now;
    }

    /// <summary>Each step has its own context (its own time-out): the step running now.</summary>
    internal void Use(StepContext context) => this._context = context;

    private ISelfTestHost Host => this._context.Host;

    private string Code => this._context.Code;

    private string HostName => this._context.Data.Partner ?? "";

    /// <summary>The one channel a partner may make (windows-only 10): "LG self-test K7Q2 p".</summary>
    public static string OwnChannelName(string code) => $"LG self-test {code} p";

    /// <summary>Which of the host's test channels a name is (0, 1, 2), or null.</summary>
    public static int? HostChannelIndex(string? name, string code) =>
        Enumerable.Range(0, 3).Cast<int?>().FirstOrDefault(index => name == SelfTestRun.ChannelName(code, index!.Value));

    private bool IsHost(string who, long userId) =>
        (this.HostName.Length > 0 && string.Equals(who, this.HostName, StringComparison.OrdinalIgnoreCase))
        || (this._context.Data.OtherUserId != 0 && userId == this._context.Data.OtherUserId);

    // ================================================================ joining

    /// <summary>Waits for the host's invite (ten minutes), joins, and says hello with its facts.</summary>
    public async Task<StepResult> JoinAsync() {
        this._context.ShowPairLine(SelfTestWords.PartnerWaiting);
        var joined = await this._context.WaitUntilAsync(async () => {
            await this.AcceptInvitesAsync().ConfigureAwait(false);
            return this.FirstChannel() != null;
        }, InviteWithin).ConfigureAwait(false);
        if (!joined || this.FirstChannel() is not { } channel) {
            return StepResult.Skip(SelfTestWords.NoInvite);
        }

        // The host's user ID, from the channel's verified members: lines and the member list are told apart by it too.
        if ((await this.Host.MembersAsync(channel).ConfigureAwait(false)).FirstOrDefault(member => this.IsHost(member.Who, 0)) is { } host) {
            this._context.Data.OtherUserId = host.UserId;
            this._context.Save();
        }

        await this.AnswerAsync(channel, new PartnerAnswer(0, PartnerOutcome.Hello, await this.FactsAsync().ConfigureAwait(false))).ConfigureAwait(false);
        this._heard = this.Host.Now;
        this._context.ShowPairLine(SelfTestWords.PartnerJoined);
        return StepResult.Pass();
    }

    /// <summary>The host's first test channel, once joined.</summary>
    private string? FirstChannel() => this._context.Data.Joined.FirstOrDefault();

    /// <summary>Accepts the host's invites to the host's own test channels, verified, and no other; at most <see cref="MostAccepts"/>.</summary>
    private async Task AcceptInvitesAsync() {
        foreach (var invite in await this.Host.InvitesAsync().ConfigureAwait(false)) {
            if (this._accepts >= MostAccepts) {
                return;
            }

            if (!invite.Verified || !this.IsHost(invite.Inviter, 0) || HostChannelIndex(invite.ChannelName, this.Code) == null
                || (this._context.Data.Joined.Contains(invite.ChannelId) && !this._left.Contains(invite.ChannelId))) {
                continue;
            }

            await this.Host.AcceptInviteAsync(invite.ChannelId).ConfigureAwait(false);
            this._accepts++;
            this._left.Remove(invite.ChannelId);
            this._acceptedAt[invite.ChannelId] = this.Host.Now;
            this._context.MarkJoined(invite.ChannelId);
            this._heard = this.Host.Now;
        }
    }

    /// <summary>Its facts, for hello and still-there: version, private place, place kind, ChatTwo, mode, data centre.</summary>
    private async Task<IReadOnlyDictionary<string, string>> FactsAsync() {
        var environment = await this.Host.ReadEnvironmentAsync().ConfigureAwait(false);
        var place = PrivateArea.Judge(await this.Host.ReadPlaceAsync().ConfigureAwait(false));
        return new Dictionary<string, string> {
            ["v"] = SelfTestProtocol.IsPlain(environment.PluginVersion) ? environment.PluginVersion : "unknown",
            ["private"] = place.Private ? "yes" : "no",
            ["place"] = place.Kind.ToString().ToLowerInvariant(),
            ["chattwo"] = environment.ChatTwoLoaded ? "on" : "off",
            ["mode"] = environment.AdvancedMode ? "advanced" : "simple",
            ["dc"] = environment.DataCentre.ToString(CultureInfo.InvariantCulture),
        };
    }

    private Task AnswerAsync(string channel, PartnerAnswer answer) =>
        this._context.ServerAsync(SelfTestAction.PartnerMessage, () => this.Host.SendAsync(channel, SelfTestProtocol.Write(this.Code, answer)));

    // ================================================================ answering

    /// <summary>
    /// Answers the host's requests until "bye" (then the clean-up runs), fifteen minutes without one, or the host's test
    /// channel going away. Every delivered line is read once, in order.
    /// </summary>
    public async Task<StepResult> AnswerAllAsync() {
        while (true) {
            this._context.Token.ThrowIfCancellationRequested();
            await this.AcceptInvitesAsync().ConfigureAwait(false);
            foreach (var line in this.Host.ReceivedSince(this._mark).OrderBy(line => line.Seq)) {
                this._mark = Math.Max(this._mark, line.Seq);
                await this.ReadAsync(line).ConfigureAwait(false);
                if (this._bye) {
                    return this.Done(null);
                }
            }

            if (this.Host.Now - this._heard > SelfTestProtocol.PartnerGivesUpAfter) {
                return this.Done(SelfTestWords.HostSilent);
            }

            if (await this.HostChannelGoneAsync().ConfigureAwait(false)) {
                return this.Done(SelfTestWords.HostLeft);
            }

            await this._context.DelayAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    private StepResult Done(string? stopped) {
        var did = $"answered {this._answered} requests: {this._lines} test lines sent, {this._locals} to local chat, {this._reports} reports, " +
                  $"{this._leaves} leaves, {this._disconnects} disconnects, {this._asks} steps asked of you" +
                  (this._saidYes.Count > 0 ? $"; you said yes: {string.Join(", ", this._saidYes)}" : "");
        return (stopped == null ? StepResult.Pass() : StepResult.Skip(stopped)) with { Did = did };
    }

    /// <summary>A channel of the host's it joined, gone from its list without being asked to leave: disbanded, or it was removed.</summary>
    private async Task<bool> HostChannelGoneAsync() {
        var listed = (await this.Host.ChannelsAsync().ConfigureAwait(false)).Select(channel => channel.Id).ToHashSet();
        var gone = false;
        foreach (var channel in this._context.Data.Joined) {
            if (listed.Contains(channel)) {
                this._seen.Add(channel);
            } else if (!this._left.Contains(channel) && (this._seen.Contains(channel) || this.Host.Now - this._acceptedAt.GetValueOrDefault(channel, this.Host.Now) > SelfTestProtocol.AnswerWithin)
                       && await this.Host.IsReadyAsync().ConfigureAwait(false)) {
                // Seen in its list since it joined (or a minute since, the list being slow), and gone: not by its own leave.
                gone = true;
            }
        }

        return gone;
    }

    /// <summary>Reads one delivered line: the run's lines from the host are acted on; with its prefix from anyone else, the run stops.</summary>
    private async Task ReadAsync(ReceivedLine line) {
        var parsed = SelfTestProtocol.Parse(this.Code, line.Text);
        if (parsed is ProtocolLine.NotOurs) {
            return;
        }

        if (!line.Own && !this.IsHost(line.Sender, line.SenderId)) {
            throw new PartnerMeddlingException();
        }

        if (line.Own) {
            if (parsed is ProtocolLine.TestLine) {
                this._own++;
            }

            return;
        }

        if (line.ChannelId != null && !this._context.Data.Joined.Contains(line.ChannelId)) {
            return;
        }

        this._heard = this.Host.Now;
        switch (parsed) {
            case ProtocolLine.TestLine:
                this._got++;
                this._caught += line.CaughtUp ? 1 : 0;
                foreach (var link in line.Links) {
                    this._links.Add(LinkKind(link));
                }

                break;
            case ProtocolLine.Request { Value: var request } when line.ChannelId != null && request.Number > this._context.Data.ProtocolNumber:
                await this.HandleAsync(request, line.ChannelId).ConfigureAwait(false);
                break;
        }
    }

    private static string LinkKind(ChatLink link) => link switch {
        ChatLink.Item => "item",
        ChatLink.MapFlag => "map",
        ChatLink.Status => "status",
        _ => "pf",
    };

    /// <summary>Answers one request (never twice: its number is kept before it is acted on), within its cap.</summary>
    private async Task HandleAsync(PartnerRequest request, string channel) {
        this._context.Data.ProtocolNumber = request.Number;
        this._context.Save();
        this._answered++;
        Func<Task>? after = null;
        PartnerAnswer answer;
        try {
            (answer, after) = await this.AnswerOfAsync(request, channel).ConfigureAwait(false);
        } catch (SelfTestSpentException) {
            answer = Refused(request);
        } catch (Exception ex) when (ex is not (OperationCanceledException or SelfTestStopException or PartnerMeddlingException) && !SelfTestPacer.IsRefusal(ex)) {
            // Never the message: it may hold a name.
            this.Host.Log($"[selftest] a partner's request couldn't be done ({ex.GetType().Name})");
            answer = Refused(request);
        }

        await this.AnswerAsync(channel, answer).ConfigureAwait(false);
        if (after != null) {
            await after().ConfigureAwait(false);
        }

        this._context.ShowPairLine($"{SelfTestWords.PartnerJoined} {SelfTestWords.PartnerAnswered(this._answered)}");
    }

    private static PartnerAnswer Refused(PartnerRequest request) => new(request.Number, PartnerOutcome.Refused, new Dictionary<string, string>());

    private static PartnerAnswer Done(PartnerRequest request, params (string Key, string Value)[] fields) =>
        new(request.Number, PartnerOutcome.Done, fields.Where(field => SelfTestProtocol.IsPlain(field.Value)).ToDictionary(field => field.Key, field => field.Value));

    private async Task<(PartnerAnswer Answer, Func<Task>? After)> AnswerOfAsync(PartnerRequest request, string channel) {
        switch (request.Verb) {
            case PartnerVerb.Hello:
            case PartnerVerb.StillThere:
                return (new PartnerAnswer(request.Number, PartnerOutcome.Done, await this.FactsAsync().ConfigureAwait(false)), null);
            case PartnerVerb.Send:
            case PartnerVerb.SendWhileAway: {
                var target = await this.TargetAsync(request.Option("in"), channel).ConfigureAwait(false);
                var link = request.Option("link");
                if (target == null || link == "pf") {
                    // A party finder listing is the tester's own, sent by hand: never by the plugin.
                    return (Refused(request), null);
                }

                if (request.Verb == PartnerVerb.SendWhileAway) {
                    // Sent once the host shows offline (they're relogging): they get it as missed messages.
                    var away = await this._context.WaitUntilAsync(async () =>
                        (await this.Host.MembersAsync(target).ConfigureAwait(false)).Any(member => this.IsHost(member.Who, member.UserId) && !member.Online),
                        SelfTestProtocol.GuidedAnswerWithin).ConfigureAwait(false);
                    if (!away) {
                        return (Done(request, ("sent", "0"), ("away", "no")), null);
                    }
                }

                var sent = await this.SendLinesAsync(target, request.Count, link).ConfigureAwait(false);
                return (sent == 0 ? Refused(request) : Done(request, ("sent", sent.ToString(CultureInfo.InvariantCulture))), null);
            }

            case PartnerVerb.Local: {
                var sent = 0;
                try {
                    for (var i = 0; i < request.Count; i++) {
                        // Its own private-place check (and nobody near but the host) before each line, as for a typed one.
                        await this._context.GateAsync(typing: true).ConfigureAwait(false);
                        var marker = SelfTestProtocol.PartnerMarker(this.Code, ++this._marker);
                        await this._context.ServerAsync(SelfTestAction.LocalMessage, () => this.Host.SendLocalAsync(marker)).ConfigureAwait(false);
                        sent++;
                        this._locals++;
                    }
                } catch (SelfTestSpentException) {
                    // Its twenty: what was sent is said.
                }

                return (sent == 0 ? Refused(request) : Done(request, ("sent", sent.ToString(CultureInfo.InvariantCulture))), null);
            }

            case PartnerVerb.ReportReceived:
                return (await this.ReportReceivedAsync(request).ConfigureAwait(false), null);
            case PartnerVerb.ReportCheckCode: {
                this._reports++;
                var target = await this.TargetAsync(request.Option("in"), channel).ConfigureAwait(false) ?? channel;
                var code = SelfTestProtocol.CheckCodeField(await this.Host.CheckCodeAsync(target).ConfigureAwait(false));
                return (code == null ? Done(request) : Done(request, ("check", code)), null);
            }

            case PartnerVerb.ReportSaw:
                this._reports++;
                return (this.ReportSaw(request), null);
            case PartnerVerb.ReportWindows:
                this._reports++;
                return (await this.ReportWindowsAsync(request).ConfigureAwait(false), null);
            case PartnerVerb.ReportLog: {
                this._reports++;
                var counts = this.Host.LogCounts();
                var jargon = this.Host.PrintedSince(this._start).Count(line => !line.Advanced && SelfTestWords.JargonIn(line.Text).Count > 0);
                return (Done(request, ("errors", Number(counts.Errors)), ("warnings", Number(counts.Warnings)), ("forbidden", Number(counts.ForbiddenHits)),
                    ("jargon", Number(jargon))), null);
            }

            case PartnerVerb.Leave: {
                var target = await this.TargetAsync(request.Option("in"), channel).ConfigureAwait(false);
                if (target == null || this._leaves >= SelfTestProtocol.MostLeaves) {
                    return (Refused(request), null);
                }

                this._leaves++;
                // Answered first (it can't be once out), then left; the host's next invite to it is accepted.
                return (Done(request), async () => {
                    this._left.Add(target);
                    await this.Host.LeaveChannelAsync(target).ConfigureAwait(false);
                });
            }

            case PartnerVerb.WithdrawLocal:
                if (this._withdraws >= SelfTestProtocol.MostWithdraws) {
                    return (Refused(request), null);
                }

                this._withdraws++;
                await this._context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, false).ConfigureAwait(false);
                return (Done(request), null);
            case PartnerVerb.RestoreLocal:
                if (this._withdraws == 0) {
                    return (Refused(request), null);
                }

                await this._context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, true).ConfigureAwait(false);
                return (Done(request), null);
            case PartnerVerb.Disconnect: {
                var pacer = this._context.Pacer;
                if (this._disconnects >= SelfTestProtocol.MostDisconnects || pacer.CountThisRun(SelfTestAction.Reconnect) >= pacer.MostOf(SelfTestAction.Reconnect)) {
                    return (Refused(request), null);
                }

                this._disconnects++;
                await this._context.DisconnectAsync().ConfigureAwait(false);
                try {
                    await this.Host.Delay(TimeSpan.FromSeconds(request.Count), this._context.Token).ConfigureAwait(false);
                } finally {
                    await this._context.ConnectAsync().ConfigureAwait(false);
                }

                return (Done(request), null);
            }

            case PartnerVerb.Ask:
                return (await this.AskPlayerAsync(request).ConfigureAwait(false), null);
            case PartnerVerb.InviteMe: {
                if (this._invitedHost || !PartnerLink.SplitName(this.HostName, out var name, out var world)) {
                    return (Refused(request), null);
                }

                this._invitedHost = true;
                // Its one channel of its own, in the file before anything else, and its one invite: to the host only.
                var own = await this._context.ServerAsync(SelfTestAction.CreateChannel, () => this.Host.CreateChannelAsync(OwnChannelName(this.Code))).ConfigureAwait(false);
                this._context.AddTestChannel(own);
                await this._context.ServerAsync(SelfTestAction.Lookup, () => Task.CompletedTask).ConfigureAwait(false);
                await this._context.ServerAsync(SelfTestAction.Invite, () => this.Host.InviteAsync(own, name, world)).ConfigureAwait(false);
                return (Done(request), null);
            }

            case PartnerVerb.Bye:
                this._bye = true;
                return (Done(request), null);
            default:
                return (Refused(request), null);
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The host's test channel a request names ("in=b"), or the one it came in; null if not joined.</summary>
    private async Task<string?> TargetAsync(string? letter, string channel) {
        if (letter == null) {
            return channel;
        }

        var index = letter[0] - 'a';
        var listed = await this.Host.ChannelsAsync().ConfigureAwait(false);
        return listed.FirstOrDefault(found => this._context.Data.Joined.Contains(found.Id) && HostChannelIndex(found.Name, this.Code) == index)?.Id;
    }

    /// <summary>Sends marked lines (with a link where asked), paced as channel messages, at most its 80 in a run: how many went.</summary>
    private async Task<int> SendLinesAsync(string channel, int count, string? link) {
        var sent = 0;
        try {
            for (var i = 0; i < count; i++) {
                var marker = SelfTestProtocol.PartnerMarker(this.Code, ++this._marker);
                await this._context.ServerAsync(SelfTestAction.ChannelMessage,
                    () => link == null ? this.Host.SendAsync(channel, marker) : this.Host.SendWithLinkAsync(channel, marker, link)).ConfigureAwait(false);
                sent++;
                this._lines++;
            }
        } catch (SelfTestSpentException) {
            // Its eighty: what was sent is said.
        }

        return sent;
    }

    /// <summary>What it received since the last report: the host's marked lines, its own, caught up, the links that worked, in game chat, warnings.</summary>
    private Task<PartnerAnswer> ReportReceivedAsync(PartnerRequest request) {
        this._reports++;
        var printed = this.Host.PrintedSince(this._reportMark);
        var prefix = SelfTestProtocol.Prefix(this.Code);
        var inChat = printed.Where(line => line.Kind is TapKind.Message or TapKind.LocalMessage && !line.Own && line.Text.Contains(prefix, StringComparison.Ordinal)).ToList();
        var tag = inChat.Select(line => line.Tag?.Trim('[', ']')).LastOrDefault(found => SelfTestProtocol.IsPlain(found));
        var warnings = printed.Count(line => line.Kind == TapKind.Notice && line.Tone is NoticeTone.Warning or NoticeTone.Critical);
        var fields = new List<(string, string)> {
            ("got", Number(this._got)), ("own", Number(this._own)), ("caught", Number(this._caught)), ("chat", Number(inChat.Count)), ("warn", Number(warnings)),
        };
        if (SelfTestProtocol.Joined(this._links.Order()) is { } links) {
            fields.Add(("links", links));
        }

        if (tag != null) {
            fields.Add(("tag", tag));
        }

        this._got = this._own = this._caught = 0;
        this._links.Clear();
        this._reportMark = this.Host.Mark();
        return Task.FromResult(Done(request, [.. fields]));
    }

    /// <summary>Whether the host's test line n showed in its game chat, where, and whether its plugin printed it (local chat).</summary>
    private PartnerAnswer ReportSaw(PartnerRequest request) {
        var marker = $"{SelfTestProtocol.Prefix(this.Code)}{request.Args[0]}, please ignore";
        var seen = this.Host.GameChatSince(this._start).FirstOrDefault(line => !line.FromTester && line.Text.Contains(marker, StringComparison.Ordinal));
        var printed = this.Host.PrintedSince(this._start).FirstOrDefault(line => line.Kind is TapKind.LocalMessage or TapKind.Message && line.Text.Contains(marker, StringComparison.Ordinal));
        var where = seen?.Channel.ToString().ToLowerInvariant() ?? (printed != null ? "lookingglass" : "none");
        var fields = new List<(string, string)> { ("in", where), ("printed", printed != null ? "yes" : "no") };
        if (printed?.Tag?.Trim('[', ']') is { } tag && SelfTestProtocol.IsPlain(tag)) {
            fields.Add(("tag", tag));
        }

        return Done(request, [.. fields]);
    }

    /// <summary>Its windows for the host's channels: open or not, the count on a tab or in the list, the last flash.</summary>
    private async Task<PartnerAnswer> ReportWindowsAsync(PartnerRequest request) {
        var windows = await this.Host.WindowsAsync().ConfigureAwait(false);
        var listed = await this.Host.ChannelsAsync().ConfigureAwait(false);
        var fields = new List<(string, string)>();
        foreach (var channel in listed.Where(found => this._context.Data.Joined.Contains(found.Id))) {
            if (HostChannelIndex(channel.Name, this.Code) is not { } index) {
                continue;
            }

            var letter = (char) ('a' + index);
            var window = windows.Showing(channel.Id);
            fields.Add(($"open-{letter}", window != null ? "yes" : "no"));
            var count = windows.TabCounts.TryGetValue(channel.Id, out var tab) ? tab : windows.ListCounts.GetValueOrDefault(channel.Id);
            fields.Add(($"count-{letter}", Number(count)));
            if (window != null) {
                fields.Add(($"flash-{letter}", Number(window.FlashPulses)));
            }
        }

        return Done(request, [.. fields]);
    }

    /// <summary>A fixed step for its player, behind the typing gate where it types: their answer, as yes, no or skipped.</summary>
    private async Task<PartnerAnswer> AskPlayerAsync(PartnerRequest request) {
        var ask = SelfTestProtocol.AskOf(request.Args[0])!.Value;
        this._asks++;
        var marker = SelfTestProtocol.PartnerMarker(this.Code, ++this._marker);
        GuidedAnswer given;
        if (ask == PartnerAsk.Relog) {
            var logins = this.Host.Logins;
            given = await this._context.AskAsync(new GuidedPrompt(SelfTestWords.PartnerAskText(ask, marker)) {
                Short = SelfTestProtocol.NameOf(ask),
                Watch = StepContextExtensions.Until(() => Task.FromResult(this.Host.Logins != logins), "not logged in again yet", "logged in again"),
            }).ConfigureAwait(false);
            // Back: connected again, in the private place, before answering.
            await this._context.GateAsync().ConfigureAwait(false);
        } else {
            given = await this._context.AskAsync(new GuidedPrompt(SelfTestWords.PartnerAskText(ask, marker), ask == PartnerAsk.ClickLink ? PromptKind.YesNo : PromptKind.DoThis) {
                Short = SelfTestProtocol.NameOf(ask),
                TesterTypes = ask is PartnerAsk.SayHello or PartnerAsk.SayHelloChatTwo,
                ChatTwoOnSay = ask == PartnerAsk.SayHelloChatTwo,
            }).ConfigureAwait(false);
        }

        var outcome = given switch {
            GuidedAnswer.Done or GuidedAnswer.Yes or GuidedAnswer.Seen => PartnerOutcome.Yes,
            GuidedAnswer.Skip => PartnerOutcome.Skipped,
            _ => PartnerOutcome.No,
        };
        if (outcome == PartnerOutcome.Yes) {
            this._saidYes.Add(SelfTestProtocol.NameOf(ask));
        }

        var fields = new Dictionary<string, string>();
        if (ask is PartnerAsk.SayHello or PartnerAsk.SayHelloChatTwo) {
            fields["line"] = $"p{Number(this._marker)}";
        }

        return new PartnerAnswer(request.Number, outcome, fields);
    }
}
