using System.Globalization;
using System.Text.Json;
using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// What the two-player checks share (docs/self-test-plan.md, "Group 2"): asking the partner to send and report, waiting for
/// its lines, and the steps that make sure both are where a check needs them (the same house, friends, a party of two).
/// </summary>
internal static class PairSteps {
    /// <summary>What every two-player check needs: the partner, paired; a connection; the test channel.</summary>
    public const SelfTestNeeds Needs = SelfTestNeeds.Partner | SelfTestNeeds.Connection | SelfTestNeeds.TestChannel;

    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan WindowOpens = TimeSpan.FromSeconds(8);

    /// <summary>The number in a test line's marker: "4" of "LG self-test K7Q2 4, please ignore".</summary>
    public static string NumberOf(string marker) {
        var rest = marker[(marker.LastIndexOf(' ', marker.IndexOf(',', StringComparison.Ordinal)) + 1)..];
        return rest[..rest.IndexOf(',', StringComparison.Ordinal)];
    }

    /// <summary>The partner's marked test lines delivered since a mark, in a channel (null: local chat).</summary>
    public static IReadOnlyList<ReceivedLine> FromPartner(StepContext context, long mark, string? channelId) =>
        context.Host.ReceivedSince(mark).Where(line => !line.Own && line.ChannelId == channelId && context.Partner.IsPartner(line.Sender)
                                                       && SelfTestProtocol.Parse(context.Code, line.Text) is ProtocolLine.TestLine).ToList();

    /// <summary>The partner's marked lines the plugin printed in game chat since a mark (a channel's, or local chat's with null).</summary>
    public static IReadOnlyList<TapLine> PrintedFromPartner(StepContext context, long mark, string? channelId) =>
        context.Host.PrintedSince(mark).Where(line => !line.Own && (channelId == null ? line.Kind == TapKind.LocalMessage : line.Kind == TapKind.Message && line.ChannelId == channelId)
                                                      && line.Text.Contains($"{context.MarkerPrefix} p", StringComparison.Ordinal)).ToList();

    /// <summary>A test channel's letter in the protocol ("a" for the first).</summary>
    public static string LetterOf(StepContext context, string channelId) => ((char) ('a' + context.TestChannels.ToList().IndexOf(channelId))).ToString();

    /// <summary>
    /// Asks the partner to send <paramref name="count"/> marked lines to a test channel (the first, by default), and waits for
    /// them to arrive: how many it sent, and how many arrived.
    /// </summary>
    public static async Task<(int Sent, int Arrived)> PartnerSendsAsync(StepContext context, int count, string? channelId = null, string? link = null) {
        channelId ??= context.Partner.ChannelId!;
        var mark = context.Host.Mark();
        var args = new List<string> { count.ToString(CultureInfo.InvariantCulture) };
        if (link != null) {
            args.Add($"link={link}");
        }

        if (channelId != context.Partner.ChannelId) {
            args.Add($"in={LetterOf(context, channelId)}");
        }

        var answer = await context.Partner.AskAsync(context, PartnerVerb.Send, [.. args]).ConfigureAwait(false);
        var sent = answer.Int("sent") ?? 0;
        await context.WaitUntilAsync(() => Task.FromResult(FromPartner(context, mark, channelId).Count >= sent), Soon).ConfigureAwait(false);
        return (sent, FromPartner(context, mark, channelId).Count);
    }

    /// <summary>Asks the partner to send marked lines to local chat, and waits for them: how many it sent, and how many arrived.</summary>
    public static async Task<(int Sent, int Arrived)> PartnerLocalAsync(StepContext context, int count) {
        var mark = context.Host.Mark();
        var answer = await context.Partner.AskAsync(context, PartnerVerb.Local, count.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        var sent = answer.Int("sent") ?? 0;
        await context.WaitUntilAsync(() => Task.FromResult(PrintedFromPartner(context, mark, null).Count >= sent), Soon).ConfigureAwait(false);
        return (sent, PrintedFromPartner(context, mark, null).Count);
    }

    /// <summary>Where the partner saw one of the host's test lines (its "report saw"): "in" (say, party, lookingglass, none...), "printed", "tag".</summary>
    public static async Task<PartnerAnswer> PartnerSawAsync(StepContext context, string marker) {
        // A moment for the line to reach them.
        await context.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return await context.Partner.AskAsync(context, PartnerVerb.ReportSaw, NumberOf(marker)).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a line to local chat with /lgl (typed: the gate, Say underneath), local chat accepted first: its marker. Anything
    /// <paramref name="insert"/>ed goes before ", please ignore", so the line stays a test line (" <t>", say).
    /// </summary>
    public static async Task<string> SayLocalAsync(StepContext context, TestLink link = TestLink.None, string? insert = null) {
        await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, true).ConfigureAwait(false);
        var marker = context.NextMarker();
        await context.TypeAsync($"{LocalChat.Command} {Insert(marker, insert)}", link, SelfTestAction.LocalMessage).ConfigureAwait(false);
        return marker;
    }

    /// <summary>A marker with something before its ", please ignore".</summary>
    public static string Insert(string marker, string? insert) => insert == null ? marker : marker.Replace(", please ignore", $"{insert}, please ignore", StringComparison.Ordinal);

    /// <summary>Sends a line to a test channel with its /lgc number (typed): its marker.</summary>
    public static async Task<string> SayInChannelAsync(StepContext context, string channelId, TestLink link = TestLink.None) {
        var marker = context.NextMarker();
        await context.TypeAsync($"{await context.CommandOfAsync(channelId).ConfigureAwait(false)} {marker}", link, SelfTestAction.ChannelMessage).ConfigureAwait(false);
        return marker;
    }

    /// <summary>Whether a notice with these words was printed since a mark (waiting a moment).</summary>
    public static async Task<bool> ToldAsync(StepContext context, long mark, Wording words) => await context.NoticeAsync(mark, words).ConfigureAwait(false) != null;

    /// <summary>
    /// The first step of a check that needs both testers together: the partner in a private place, both inside the same
    /// house or apartment (in sight of each other), and, for local chat, on each other's friends list. Otherwise the check
    /// is skipped, with why.
    /// </summary>
    public static SelfTestStep Together(bool friends = true) => SetupAuto("You're both inside the same house or apartment", null, async context => {
        var hello = context.Partner.Hello;
        if (!hello.TryGetValue("private", out var place) || place != "yes") {
            return StepResult.SkipCheck(SelfTestWords.PartnerNotPrivate);
        }

        var mine = PrivateArea.Judge(await context.Host.ReadPlaceAsync().ConfigureAwait(false)).Kind;
        var (distance, friend) = await context.Host.OtherAsync().ConfigureAwait(false);
        // Inn rooms are one player each: a house (any floor, chambers, workshop) or an apartment, in sight of each other.
        if (mine is not (PlaceKind.House or PlaceKind.Chambers or PlaceKind.Workshop or PlaceKind.Apartment) || distance == null
            || !hello.TryGetValue("place", out var theirs) || theirs is not ("house" or "chambers" or "workshop" or "apartment")) {
            return StepResult.SkipCheck(SelfTestWords.NeedsSamePlace);
        }

        return friends && friend != true ? StepResult.SkipCheck(SelfTestWords.NeedsFriends) : StepResult.Pass();
    });

    /// <summary>The party is exactly the two testers (/p is typed only then, and checked again right before).</summary>
    public static async Task<bool> PartyOfTwoAsync(StepContext context) => await context.Host.PartyAsync().ConfigureAwait(false) is (2, true);

    /// <summary>A step that gets the two testers into a party of their own (skipping the rest of the check if not).</summary>
    public static SelfTestStep PartyOfTwo(string? checklist) => SetupGuided("A party of just the two of you", checklist, async context => {
        if (await PartyOfTwoAsync(context).ConfigureAwait(false)) {
            return StepResult.Pass();
        }

        if ((await context.Host.PartyAsync().ConfigureAwait(false)).Count > 0) {
            // Someone else's party, or one with others in it: never typed in.
            return StepResult.SkipCheck(SelfTestWords.NeedsPartyOfTwo);
        }

        var asked = await context.AskAsync(new GuidedPrompt("Invite your partner to a party (right-click them, Invite to Party). Nobody else may be in it.") {
            Short = "the party",
            Watch = StepContextExtensions.Until(() => PartyOfTwoAsync(context), "no party of two yet", "a party of the two of you"),
        }).ConfigureAwait(false);
        if (asked == GuidedAnswer.Skip) {
            return StepResult.SkipCheck(SelfTestWords.YouSkipped);
        }

        // Their player accepts (their plugin asks them).
        if (!await PartyOfTwoAsync(context).ConfigureAwait(false)) {
            var joined = await context.Partner.AskPlayerAsync(context, PartnerAsk.JoinParty).ConfigureAwait(false);
            if (joined.Outcome != StepOutcome.Passed) {
                return joined with { EndsCheck = true };
            }
        }

        return await context.WaitUntilAsync(() => PartyOfTwoAsync(context), Soon).ConfigureAwait(false) ? StepResult.Pass() : StepResult.SkipCheck(SelfTestWords.NeedsPartyOfTwo);
    });

    /// <summary>The partner leaves a test channel when asked, and is gone from its members.</summary>
    public static Task<bool> PartnerLeavesAsync(StepContext context, string channelId) => context.Partner.LeaveAsync(context, channelId);

    /// <summary>Whether a report's "links" field has a link kind ("item", "map", "status", "pf").</summary>
    public static bool HasLink(PartnerAnswer answer, string kind) => answer.Fields.TryGetValue("links", out var links) && links.Split('.').Contains(kind);

    /// <summary>A test channel the partner is in: made if need be, and the partner invited (its plugin accepts by itself).</summary>
    public static async Task<string> ChannelWithPartnerAsync(StepContext context, int index) {
        var channel = await context.TestChannelAsync(index).ConfigureAwait(false);
        if ((await context.Host.MembersAsync(channel).ConfigureAwait(false)).All(member => member.UserId != context.Partner.UserId || member.Invited)) {
            if (!await context.Partner.InviteAndWaitAsync(context, channel).ConfigureAwait(false)) {
                throw new PartnerSilentException(gone: false);
            }
        }

        return channel;
    }

    /// <summary>Closes every channel window (the tester's come back at the end, where they were).</summary>
    public static async Task CloseAllWindowsAsync(StepContext context) {
        await context.ChangeSettingAsync(SelfTestSetting.ChannelWindows, Array.Empty<ChannelWindowLayout>()).ConfigureAwait(false);
        await context.WaitUntilAsync(async () => (await context.Host.WindowsAsync().ConfigureAwait(false)).Windows.Count == 0).ConfigureAwait(false);
    }

    /// <summary>
    /// The host relogs while the partner sends (its "send-away": once it sees the host offline): the request goes before the
    /// relog, and its answer is read after. The step after this one reads <c>State["away"]</c> for how many were sent.
    /// </summary>
    public static SelfTestStep[] RelogWhileThePartnerSends(string? checklist, int count, Func<StepContext, Task<string>>? channel = null, string? also = null) => [
        SetupAuto("Your partner sends once you're away", checklist, async context => {
            var target = channel != null ? await channel(context).ConfigureAwait(false) : context.Partner.ChannelId!;
            context.State["away-mark"] = context.Host.Mark();
            var args = new List<string> { count.ToString(CultureInfo.InvariantCulture) };
            if (target != context.Partner.ChannelId) {
                args.Add($"in={LetterOf(context, target)}");
            }

            context.State["away-number"] = await context.Partner.SendAsync(context, PartnerVerb.SendWhileAway, [.. args]).ConfigureAwait(false);
            context.State["away-channel"] = target;
            if (also != null) {
                context.State["also-number"] = await context.Partner.SendAsync(context, PartnerVerb.SendWhileAway, count.ToString(CultureInfo.InvariantCulture), $"in={also}")
                    .ConfigureAwait(false);
            }

            return StepResult.Pass();
        }),
        CarryOn(Relog, checklist),
        SetupAuto("What your partner sent while you were away", checklist, async context => {
            var mark = (long) context.State["away-mark"];
            var answer = await context.Partner.AnswerOfAsync(context, mark, (int) context.State["away-number"], PartnerVerb.SendWhileAway).ConfigureAwait(false);
            var sent = answer.Int("sent") ?? 0;
            if (context.State.TryGetValue("also-number", out var also)) {
                sent += (await context.Partner.AnswerOfAsync(context, mark, (int) also, PartnerVerb.SendWhileAway).ConfigureAwait(false)).Int("sent") ?? 0;
            }

            context.State["away"] = sent;
            return sent > 0 ? StepResult.Pass() : StepResult.SkipCheck("your partner didn't see you go offline, so nothing was sent while you were away");
        }),
    ];

    /// <summary>The partner's marked lines delivered since the relog began that were caught up (sent while away).</summary>
    public static int CaughtUp(StepContext context) =>
        context.Host.ReceivedSince((long) context.State["away-mark"]).Count(line => line.CaughtUp && context.Partner.IsPartner(line.Sender)
                                                                                     && SelfTestProtocol.Parse(context.Code, line.Text) is ProtocolLine.TestLine);

    /// <summary>Sets the partner's name colour (or with null, the default) for the run: put back at the end.</summary>
    public static async Task SetPartnerColourAsync(StepContext context, uint? rgb) {
        var now = (await context.Host.ReadSettingAsync(SelfTestSetting.PartnerNameColour).ConfigureAwait(false)).Deserialize<OwnNameColourValue>()
                  ?? throw new InvalidOperationException("The partner's name colour isn't known.");
        await context.ChangeSettingAsync(SelfTestSetting.PartnerNameColour, now with { Rgb = rgb }).ConfigureAwait(false);
    }

    /// <summary>No warning (light or dark red) printed since a mark.</summary>
    public static bool NoWarnings(StepContext context, long mark) =>
        !context.Host.PrintedSince(mark).Any(line => line.Kind == TapKind.Notice && line.Tone is NoticeTone.Warning or NoticeTone.Critical);
}
