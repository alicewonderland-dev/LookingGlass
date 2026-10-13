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
    public static SelfTestStep CarryOn(string instruction, string? checklist) =>
        new() { Title = instruction, Checklist = checklist, Kind = StepKind.CarryOnAfter, Run = _ => Task.FromResult(StepResult.Pass()) };

    /// <summary>The instruction for a relog.</summary>
    public const string Relog = "Go to the title screen (Return to Title), then come back in with this character. The self-test carries on by itself.";

    /// <summary>The instruction for turning the plugin off and on.</summary>
    public const string Reload = "In /xlplugins, turn LookingGlass off, then on again. The self-test carries on by itself.";
}

/// <summary>What the checks ask of the host, waiting as the game needs (a line is printed a frame later, a window opens at the next draw).</summary>
public static class StepContextExtensions {
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

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

    /// <summary>Disconnects (paced: reconnects at least a minute apart), and says whether it is disconnected.</summary>
    public static async Task DisconnectAsync(this StepContext context) {
        await context.ServerAsync(SelfTestAction.Reconnect, () => context.Host.DisconnectAsync()).ConfigureAwait(false);
        await context.WaitUntilAsync(async () => !await context.Host.IsReadyAsync().ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Connects again, and waits until ready (up to two minutes).</summary>
    public static async Task<bool> ConnectAsync(this StepContext context) {
        await context.Host.ConnectAsync().ConfigureAwait(false);
        return await context.WaitUntilAsync(() => context.Host.IsReadyAsync(), TimeSpan.FromMinutes(2)).ConfigureAwait(false);
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
