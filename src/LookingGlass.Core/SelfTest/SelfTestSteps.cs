using LookingGlass.Core.Client;

namespace LookingGlass.Core.SelfTest;

/// <summary>Short ways to write the checks' steps.</summary>
public static class SelfTestSteps {
    /// <summary>An automatic step.</summary>
    /// <param name="types">It types a line (the private-area check, and nobody near, right before).</param>
    public static SelfTestStep Auto(string title, string? checklist, Func<StepContext, Task<StepResult>> run, bool types = false, TimeSpan? timeout = null) =>
        new() { Title = title, Checklist = checklist, Kind = StepKind.Automatic, Types = types, Run = run, Timeout = timeout ?? TimeSpan.FromMinutes(5) };

    /// <summary>A guided step whose question is made when it runs (it may set something up first).</summary>
    public static SelfTestStep Ask(string title, string? checklist, Func<StepContext, Task<GuidedPrompt>> prompt) =>
        new() { Title = title, Checklist = checklist, Kind = StepKind.Guided, Run = async context => await context.AskForResultAsync(await prompt(context).ConfigureAwait(false)).ConfigureAwait(false) };

    /// <summary>A guided step with a fixed question.</summary>
    public static SelfTestStep Ask(string title, string? checklist, GuidedPrompt prompt) => Ask(title, checklist, _ => Task.FromResult(prompt));

    /// <summary>A guided step that may decide its own result (a "No" that is a skip, say).</summary>
    public static SelfTestStep Guided(string title, string? checklist, Func<StepContext, Task<StepResult>> run) =>
        new() { Title = title, Checklist = checklist, Kind = StepKind.Guided, Run = run };

    /// <summary>A relog or a plugin reload: the run waits in its file, and carries on by itself afterwards.</summary>
    /// <param name="instruction">What the tester does, in full (the window shows it as the step's text).</param>
    /// <param name="types">The tester types in game chat on the way (the typing gate runs before it shows).</param>
    /// <param name="relog">A relog (not a reload): the window adds that they may come back outside, and to walk back in.</param>
    public static SelfTestStep CarryOn(string instruction, string? checklist, bool types = false, bool relog = false) =>
        new() { Title = instruction, Checklist = checklist, Kind = StepKind.CarryOnAfter, Types = types, Relogs = relog, Run = _ => Task.FromResult(StepResult.Pass()) };

    /// <summary>
    /// A guided question answered across a relog (another of the tester's characters, then back with this one): the window
    /// adds that they may come back outside, and the step ends only once they're back inside, settled, with no time limit
    /// (owner, 2026-10-13).
    /// </summary>
    public static SelfTestStep AskAcrossRelog(string title, string? checklist, GuidedPrompt prompt) => new() {
        Title = title, Checklist = checklist, Kind = StepKind.Guided, Relogs = true, Run = async context => {
            var place = PrivateArea.Judge(await context.Host.ReadPlaceAsync().ConfigureAwait(false)).Kind;
            var result = await context.AskForResultAsync(prompt with { Text = $"{prompt.Text} {SelfTestWords.ComeBackInside(place)}" }).ConfigureAwait(false);
            await context.GateAfterRelogAsync().ConfigureAwait(false);
            return result;
        },
    };

    /// <summary>The parts of a task a check doesn't run, as a note in the report: a setup step, never a pass by itself.</summary>
    public static SelfTestStep Rest(string? checklist, string notRun) =>
        Auto("The rest", checklist, _ => Task.FromResult(StepResult.Pass(notRun))) with { Setup = true };

    /// <summary>An automatic step that only sets up for the check (see <see cref="SelfTestStep.Setup"/>).</summary>
    public static SelfTestStep SetupAuto(string title, string? checklist, Func<StepContext, Task<StepResult>> run, bool types = false, TimeSpan? timeout = null) =>
        Auto(title, checklist, run, types, timeout) with { Setup = true };

    /// <summary>A guided step that only sets up for the check (a recruitment, the partner in place).</summary>
    public static SelfTestStep SetupGuided(string title, string? checklist, Func<StepContext, Task<StepResult>> run) => Guided(title, checklist, run) with { Setup = true };

    /// <summary>A guided question that only sets up for the check (target someone, end a recruitment).</summary>
    public static SelfTestStep SetupAsk(string title, string? checklist, GuidedPrompt prompt) => Ask(title, checklist, prompt) with { Setup = true };

    /// <summary>A step that sets up for the check (see <see cref="SelfTestStep.Setup"/>).</summary>
    public static SelfTestStep AsSetup(this SelfTestStep step) => step with { Setup = true };

    /// <summary>The instruction for a relog.</summary>
    public const string Relog = "Go to the title screen (Return to Title), then come back in with this character. The self-test carries on by itself.";

    /// <summary>The instruction for turning the plugin off and on.</summary>
    public const string Reload = "In /xlplugins, turn LookingGlass off, then on again. The self-test carries on by itself.";
}

/// <summary>What the checks ask of the host, waiting as the game needs (a line is printed a frame later, a window opens at the next draw).</summary>
public static class StepContextExtensions {
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    /// <summary>How long a check waits for what the game shows a frame or more after an action (a label, a window).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// For what the game (or the plugin's windows) applies a frame or more after an action: the chat box's channel name, the
    /// info bar, the labels' colour, a window's tabs. Checks again until it passes, at most <paramref name="within"/> (a
    /// few seconds), and gives the last result: never read once right after the action (owner's second run, 2026-10-13).
    /// </summary>
    public static async Task<StepResult> SettledAsync(this StepContext context, Func<Task<StepResult>> check, TimeSpan? within = null) {
        var result = StepResult.Fail(SelfTestWords.TimedOut);
        await context.WaitUntilAsync(async () => (result = await check().ConfigureAwait(false)).Outcome == StepOutcome.Passed, within ?? Settle).ConfigureAwait(false);
        return result;
    }

    /// <summary>The first line printed since <paramref name="mark"/> that matches, waiting up to <paramref name="within"/>; null if none.</summary>
    public static async Task<TapLine?> PrintedAsync(this StepContext context, long mark, Func<TapLine, bool> match, TimeSpan? within = null) {
        TapLine? found = null;
        await context.WaitUntilAsync(() => {
            found = context.Host.PrintedSince(mark).FirstOrDefault(match);
            return Task.FromResult(found != null);
        }, within ?? Soon).ConfigureAwait(false);
        return found;
    }

    /// <summary>How many lines printed since <paramref name="mark"/> match, after waiting <paramref name="wait"/> for any more to come.</summary>
    public static async Task<int> CountPrintedAsync(this StepContext context, long mark, Func<TapLine, bool> match, TimeSpan? wait = null) {
        await context.DelayAsync(wait ?? TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return context.Host.PrintedSince(mark).Count(match);
    }

    /// <summary>A notice printed since <paramref name="mark"/> with exactly this text (in either mode's words).</summary>
    public static Task<TapLine?> NoticeAsync(this StepContext context, long mark, Wording words, TimeSpan? within = null) =>
        context.PrintedAsync(mark, line => line.Kind == TapKind.Notice && (line.Text == words.Plain || line.Text == words.Technical), within);

    /// <summary>A notice printed since <paramref name="mark"/> with exactly this text.</summary>
    public static Task<TapLine?> NoticeAsync(this StepContext context, long mark, string text, TimeSpan? within = null) =>
        context.NoticeAsync(mark, Wording.Same(text), within);

    /// <summary>The first game chat line since <paramref name="mark"/> that matches, waiting up to <paramref name="within"/>; null if none.</summary>
    public static async Task<GameChatLine?> InGameChatAsync(this StepContext context, long mark, Func<GameChatLine, bool> match, TimeSpan? within = null) {
        GameChatLine? found = null;
        await context.WaitUntilAsync(() => {
            found = context.Host.GameChatSince(mark).FirstOrDefault(match);
            return Task.FromResult(found != null);
        }, within ?? Soon).ConfigureAwait(false);
        return found;
    }

    /// <summary>How many game chat lines since <paramref name="mark"/> match, after waiting for any more to come.</summary>
    public static async Task<int> CountInGameChatAsync(this StepContext context, long mark, Func<GameChatLine, bool> match, TimeSpan? wait = null) {
        await context.DelayAsync(wait ?? TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        return context.Host.GameChatSince(mark).Count(match);
    }

    /// <summary>The tester's own line in Say holding <paramref name="marker"/>.</summary>
    public static bool SaidInSay(GameChatLine line, string marker) => line.Channel == GameChatKind.Say && line.FromTester && line.Text.Contains(marker, StringComparison.Ordinal);

    /// <summary>Any line of the tester's in Say since the mark (a link-only line has no marker).</summary>
    public static bool AnySay(GameChatLine line) => line.Channel == GameChatKind.Say && line.FromTester;

    /// <summary>Waits until typing goes to <paramref name="channelId"/> (null: nowhere but game chat); whether it did.</summary>
    public static Task<bool> TalkingInAsync(this StepContext context, string? channelId, TimeSpan? within = null) =>
        context.WaitUntilAsync(async () => (await context.Host.StickyAsync().ConfigureAwait(false)).ChannelId == channelId, within ?? Soon);

    /// <summary>Talking in local chat: starts it with /lgl if it isn't on (accepted, and Say underneath, first).</summary>
    /// <returns>Whether it is on now.</returns>
    public static async Task<bool> TalkInLocalAsync(this StepContext context) {
        if ((await context.Host.StickyAsync().ConfigureAwait(false)).TalkingInLocal) {
            return true;
        }

        await context.ChangeSettingAsync(SelfTestSetting.LocalPrivacyAccepted, true).ConfigureAwait(false);
        await context.Host.StopTalkingAsync().ConfigureAwait(false);
        await context.TypeAsync(LocalChat.Command).ConfigureAwait(false);
        return await context.TalkingInAsync(StickyChannel.LocalId).ConfigureAwait(false);
    }

    /// <summary>Not talking in any channel.</summary>
    public static async Task StopTalkingAsync(this StepContext context) {
        await context.Host.StopTalkingAsync().ConfigureAwait(false);
        await context.TalkingInAsync(null).ConfigureAwait(false);
    }

    /// <summary>The test channel's command: "/lgc50".</summary>
    public static async Task<string> CommandOfAsync(this StepContext context, string channelId) {
        int? slot = null;
        await context.WaitUntilAsync(async () => (slot = await context.Host.SlotOfAsync(channelId).ConfigureAwait(false)) != null).ConfigureAwait(false);
        return slot is { } number ? $"{CommandSlots.Prefix}{number}" : throw new InvalidOperationException("The test channel has no number.");
    }

    /// <summary>The test channel's tag as game chat shows it ("[LGC50]").</summary>
    public static async Task<string> TagOfAsync(this StepContext context, string channelId) {
        var slot = await context.Host.SlotOfAsync(channelId).ConfigureAwait(false);
        var nickname = await context.Host.NicknameOfAsync(channelId).ConfigureAwait(false);
        return ChannelTag.For(slot, nickname, true);
    }

    /// <summary>A bool setting's value now.</summary>
    public static async Task<bool> SettingIsAsync(this StepContext context, SelfTestSetting setting) =>
        (await context.Host.ReadSettingAsync(setting).ConfigureAwait(false)).ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>
    /// Disconnects (paced: reconnects at least a minute apart). The run file says so first: until it connects again, its
    /// gates don't wait for a connection, and if it stops meanwhile the clean-up connects first.
    /// </summary>
    public static async Task DisconnectAsync(this StepContext context) {
        context.MarkDisconnected(true);
        await context.ServerAsync(SelfTestAction.Reconnect, () => context.Host.DisconnectAsync()).ConfigureAwait(false);
        await context.WaitUntilAsync(async () => !await context.Host.IsReadyAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Connects again, and waits until ready (up to two minutes).</summary>
    public static async Task<bool> ConnectAsync(this StepContext context) {
        await context.Host.ConnectAsync().ConfigureAwait(false);
        var ready = await context.WaitUntilAsync(() => context.Host.IsReadyAsync(), TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        context.MarkDisconnected(false);
        return ready;
    }

    /// <summary>A guided step's watch: done when <paramref name="seen"/> holds, saying <paramref name="before"/> until then.</summary>
    public static Func<Task<WatchUpdate>> Until(Func<Task<bool>> seen, string before, string after) => async () =>
        await seen().ConfigureAwait(false) ? new WatchUpdate(after, true) : new WatchUpdate(before, false);

    /// <summary>A window showing the channel, waiting up to <paramref name="within"/>; null if none.</summary>
    public static async Task<WindowView?> WindowOfAsync(this StepContext context, string channelId, TimeSpan? within = null) {
        WindowView? found = null;
        await context.WaitUntilAsync(async () => (found = (await context.Host.WindowsAsync().ConfigureAwait(false)).Showing(channelId)) != null, within ?? Soon)
            .ConfigureAwait(false);
        return found;
    }
}
