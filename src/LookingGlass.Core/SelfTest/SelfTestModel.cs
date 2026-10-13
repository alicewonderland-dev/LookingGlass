namespace LookingGlass.Core.SelfTest;

/// <summary>How a check is judged (docs/self-test-plan.md, "Coverage").</summary>
public enum CheckMode {
    /// <summary>A: the runner does every step and judges it.</summary>
    Automatic,

    /// <summary>A+G: automatic, with one or more guided steps (a click, a look, a relog).</summary>
    AutomaticAndGuided,

    /// <summary>G: the runner sets things up, says what to do, watches what it can, and asks.</summary>
    Guided,
}

/// <summary>What a check needs before it can run; one it can't have is skipped, with why.</summary>
[Flags]
public enum SelfTestNeeds {
    None = 0,

    /// <summary>A connection to LookingGlass, logged in.</summary>
    Connection = 1,

    /// <summary>A test channel of its own (room for one more channel).</summary>
    TestChannel = 2,

    /// <summary>ChatTwo, on.</summary>
    ChatTwo = 4,

    /// <summary>ChatTwo off.</summary>
    ChatTwoOff = 8,

    /// <summary>The chat box gate's hooks (the runner types through it).</summary>
    Typing = 16,

    /// <summary>A second player with LookingGlass, paired with this run (the host's side of two players).</summary>
    Partner = 32,

    /// <summary>The server offers local chat.</summary>
    LocalChat = 64,

    /// <summary>
    /// The chat history was off with nothing in it before the run (or this run found it so already): checks that record to
    /// it, or would cost the tester history, run only then, and the whole history is deleted at the end.
    /// </summary>
    HistoryOffAndEmpty = 128,
}

/// <summary>What kind of step: the runner acts and judges, or it asks the tester, or it waits for a relog or reload.</summary>
public enum StepKind {
    Automatic,
    Guided,

    /// <summary>The tester logs out and in, or turns the plugin off and on: the run is kept in its file, and carries on afterwards.</summary>
    CarryOnAfter,
}

/// <summary>The buttons a guided step shows.</summary>
public enum PromptKind {
    /// <summary>Done, It didn't work, Skip this step: the tester does something (the runner may see it and go on by itself).</summary>
    DoThis,

    /// <summary>Yes, No, Skip this step: only the tester's eyes can judge (a colour, a menu, a flash).</summary>
    YesNo,

    /// <summary>Skip this step only: the runner waits for something it sees by itself (ChatTwo's input on Say).</summary>
    SkipOnly,
}

/// <summary>The tester's answer to a guided step.</summary>
public enum GuidedAnswer {
    Done,
    DidntWork,
    Skip,
    Yes,
    No,

    /// <summary>The runner saw the expected change by itself.</summary>
    Seen,
}

/// <summary>How one step ended.</summary>
public enum StepOutcome {
    Passed,
    Failed,
    Skipped,
}

/// <summary>A step's result: fixed words only (see <see cref="SelfTestWords"/> and the checks' own), never what anyone typed.</summary>
/// <param name="NotRun">A part of it that wasn't run ("the part next to a stranger").</param>
/// <param name="Confirmed">For a guided step the tester said worked: its short name.</param>
public sealed record StepResult(StepOutcome Outcome, string? Why = null, string? NotRun = null, string? Confirmed = null) {
    /// <summary>What the step did, in fixed words with numbers only (a partner's "answered 23 requests"), for the report.</summary>
    public string? Did { get; init; }

    /// <summary>The rest of the check isn't run after this step (a skip that ends it: "needs both of you in the same house").</summary>
    public bool EndsCheck { get; init; }

    /// <summary>Skips this step and the rest of its check.</summary>
    public static StepResult SkipCheck(string why) => new(StepOutcome.Skipped, why) { EndsCheck = true };

    public static StepResult Pass(string? notRun = null) => new(StepOutcome.Passed, NotRun: notRun);

    public static StepResult Fail(string why) => new(StepOutcome.Failed, why);

    public static StepResult Skip(string why) => new(StepOutcome.Skipped, why);

    /// <summary>Passed if <paramref name="ok"/>, otherwise failed with <paramref name="why"/>.</summary>
    public static StepResult Expect(bool ok, string why) => ok ? Pass() : Fail(why);

    /// <summary>The first failure among several expectations, or a pass.</summary>
    public static StepResult All(params (bool Ok, string Why)[] expectations) =>
        expectations.FirstOrDefault(expectation => !expectation.Ok) is { Why: { } why } ? Fail(why) : Pass();
}

/// <summary>
/// A guided step's question: an instruction, the buttons, and optionally what the runner watches meanwhile (shown as
/// "Watching: ...", and when it sees the expected change, it goes on by itself).
/// </summary>
public sealed record GuidedPrompt(string Text, PromptKind Kind = PromptKind.DoThis) {
    /// <summary>A colour swatch to compare with (0xRRGGBB).</summary>
    public uint? Swatch { get; init; }

    /// <summary>Asked about twice a second while waiting; null for a step only the tester can judge.</summary>
    public Func<Task<WatchUpdate>>? Watch { get; init; }

    /// <summary>The short name for the report's "you said yes: ..." ("Tab", "info bar").</summary>
    public string? Short { get; init; }

    /// <summary>
    /// A box to type something in (its label), such as a player's name@world: what is typed goes to the step only, never to
    /// the notes, the report or the log.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>
    /// The tester types a line in the game's chat (its chat box, or ChatTwo's input): the step gets the typing gate before it
    /// shows (private, nobody near in a house, the game's chat channel Say), and it is taken off the screen whenever that
    /// stops being so, until it is again.
    /// </summary>
    public bool TesterTypes { get; init; }

    /// <summary>ChatTwo's input must be on Say before it shows (the tester is asked to put it there first).</summary>
    public bool ChatTwoOnSay { get; init; }

    /// <summary>
    /// With <see cref="TesterTypes"/>: something else that must hold while the line is asked for (a party of just the two
    /// testers, before /p), checked with the typing gate before it shows and at every poll; while it doesn't, the request is
    /// off the screen, and <see cref="HoldsWhy"/> says why.
    /// </summary>
    public Func<Task<bool>>? Holds { get; init; }

    public string? HoldsWhy { get; init; }
}

/// <summary>What the runner sees while a guided step waits, and whether it is what the step expects.</summary>
public sealed record WatchUpdate(string Seeing, bool Done);

/// <summary>One step of a check.</summary>
public sealed record SelfTestStep {
    /// <summary>What it does, in plain words, for the window.</summary>
    public required string Title { get; init; }

    public required StepKind Kind { get; init; }

    /// <summary>
    /// It types a line through the chat box, or sends to local chat: the private-area check runs right before it, and in a
    /// house or apartment, it waits while anyone else is near.
    /// </summary>
    public bool Types { get; init; }

    /// <summary>The checklist's step number(s), for the report ("32").</summary>
    public string? Checklist { get; init; }

    /// <summary>The longest it may take (automatic steps; a reconnect waits a minute for its turn); guided steps wait for the tester.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public required Func<StepContext, Task<StepResult>> Run { get; init; }
}

/// <summary>One task of the testing list (pending-tests.md), as a list of steps.</summary>
public sealed record SelfTestCheck {
    /// <summary>Its own, stable across versions (for the run file): "local-chat-29".</summary>
    public required string Id { get; init; }

    /// <summary>The task's name in pending-tests.md.</summary>
    public required string Title { get; init; }

    /// <summary>The checklist and steps, as the owner files results: "local-chat 32-33".</summary>
    public required string Steps { get; init; }

    public required CheckMode Mode { get; init; }

    public SelfTestNeeds Needs { get; init; }

    /// <summary>Something else it needs, read at the start of the check: why it can't run, or null.</summary>
    public Func<SelfTestEnvironment, string?>? CantRun { get; init; }

    public required IReadOnlyList<SelfTestStep> StepList { get; init; }
}

/// <summary>A group of checks the start screen lists, each ticked or not.</summary>
public sealed record SelfTestSuite(string Id, string Title, IReadOnlyList<SelfTestCheck> Checks) {
    /// <summary>Everything its checks need.</summary>
    public SelfTestNeeds Needs => this.Checks.Aggregate(SelfTestNeeds.None, (needs, check) => needs | check.Needs);

    /// <summary>How many of its steps wait on a relog or a reload.</summary>
    public int CarryOns => this.Checks.Sum(check => check.StepList.Count(step => step.Kind == StepKind.CarryOnAfter));

    /// <summary>The settings its checks may change for a while (the start screen's "Which ones?").</summary>
    public IReadOnlyList<SelfTestSetting> Changes { get; init; } = [];

    /// <summary>A line about what it needs, for the start screen ("needs a striking dummy for one check").</summary>
    public string? Note { get; init; }

    /// <summary>What it may ask the server for at most, for the hour's budget.</summary>
    public IReadOnlyDictionary<SelfTestAction, int> Planned { get; init; } = new Dictionary<SelfTestAction, int>();
}

/// <summary>What the plugin and the game are like now, read at the start of a run and of each check.</summary>
public sealed record SelfTestEnvironment {
    public string PluginVersion { get; init; } = "";

    public bool ChatTwoLoaded { get; init; }

    public bool AdvancedMode { get; init; }

    public bool Connected { get; init; }

    /// <summary>The tester's channels, the run's own included.</summary>
    public int ChannelCount { get; init; }

    /// <summary>The chat box gate is hooked: lines can be typed through it.</summary>
    public bool CanType { get; init; }

    /// <summary>The server offers local chat.</summary>
    public bool LocalChatAvailable { get; init; }

    /// <summary>The chat history is on now.</summary>
    public bool HistoryOn { get; init; }

    /// <summary>No chat history on this computer at all (nothing to lose).</summary>
    public bool HistoryEmpty { get; init; }

    /// <summary>The game's chat input names its channel (the ChatLog addon is there to read).</summary>
    public bool ChatInputReadable { get; init; }

    /// <summary>The data centre the character is on now (a row number), or 0 if unknown: two players compare theirs.</summary>
    public uint DataCentre { get; init; }

    /// <summary>Most channels a user may be in, and the most a run may need: a test channel needs room under this.</summary>
    public const int MaxChannels = 50;

    /// <summary>At most this many of the tester's own channels for a run that makes test channels (it may make three).</summary>
    public const int MostOwnChannels = 47;

    /// <summary>Why a check needing <paramref name="needs"/> can't run now, or null.</summary>
    public string? Lacks(SelfTestNeeds needs) {
        if (needs.HasFlag(SelfTestNeeds.Partner)) {
            return SelfTestWords.NeedsPartner;
        }

        if (needs.HasFlag(SelfTestNeeds.ChatTwo) && !this.ChatTwoLoaded) {
            return SelfTestWords.NeedsChatTwo;
        }

        if (needs.HasFlag(SelfTestNeeds.ChatTwoOff) && this.ChatTwoLoaded) {
            return SelfTestWords.NeedsChatTwoOff;
        }

        if (needs.HasFlag(SelfTestNeeds.Typing) && !this.CanType) {
            return SelfTestWords.NeedsGateHooks;
        }

        if ((needs.HasFlag(SelfTestNeeds.Connection) || needs.HasFlag(SelfTestNeeds.TestChannel) || needs.HasFlag(SelfTestNeeds.LocalChat)) && !this.Connected) {
            return SelfTestWords.NeedsConnection;
        }

        if (needs.HasFlag(SelfTestNeeds.LocalChat) && !this.LocalChatAvailable) {
            return "the server doesn't offer local chat";
        }

        if (needs.HasFlag(SelfTestNeeds.HistoryOffAndEmpty) && (this.HistoryOn || !this.HistoryEmpty)) {
            return SelfTestWords.HistoryWasYours;
        }

        return null;
    }
}

/// <summary>A link put in a typed line, built by the plugin as the game would build it.</summary>
public enum TestLink {
    None,

    /// <summary>An item link after the text.</summary>
    ItemAfter,

    /// <summary>An item link in the middle of the text.</summary>
    ItemInMiddle,

    /// <summary>Only an item link, no text.</summary>
    ItemOnly,
}
