using System.Text.Json;
using LookingGlass.Core.Client;

namespace LookingGlass.Core.SelfTest;

/// <summary>
/// Everything the self-test runner needs from the plugin and the game: the plugin's <c>SelfTestHost</c> in game, a fake in
/// the tests. Every method may be called from any thread; the plugin's does the game's part on the framework thread. None
/// of it changes what the plugin does when no run is going: the taps and the watcher only record while a run is on.
/// </summary>
public interface ISelfTestHost {
    // ---------------------------------------------------------------- time

    DateTimeOffset Now { get; }

    /// <summary>The computer's time zone, for the report's times.</summary>
    TimeZoneInfo TimeZone { get; }

    Task Delay(TimeSpan delay, CancellationToken ct);

    // ---------------------------------------------------------------- where the player is

    /// <summary>The facts the private-area check judges (see <see cref="PrivateArea"/>); <see cref="PlaceFacts.Unreadable"/> if unreadable.</summary>
    Task<PlaceFacts> ReadPlaceAsync();

    /// <summary>How many player characters other than the tester (and the partner) are within <paramref name="yalms"/>; any failure counts as someone.</summary>
    Task<int> OthersNearAsync(double yalms);

    /// <summary>The tester's logins so far this plugin start: goes up when they log in again (a relog).</summary>
    int Logins { get; }

    // ---------------------------------------------------------------- the plugin and the session

    Task<SelfTestEnvironment> ReadEnvironmentAsync();

    /// <summary>Connected, logged in, and the channel list is in.</summary>
    Task<bool> IsReadyAsync();

    /// <summary>The tester's channels now (the run's own too): ID, name, and the members' count.</summary>
    Task<IReadOnlyList<SelfTestChannel>> ChannelsAsync();

    /// <summary>Creates a channel; its ID.</summary>
    Task<string> CreateChannelAsync(string name);

    /// <summary>Leaves a channel (as its only member, that deletes it on the server).</summary>
    Task LeaveChannelAsync(string channelId);

    /// <summary>Sends a message to a channel for real, as the debug window's "Send for real".</summary>
    Task SendAsync(string channelId, string text);

    /// <summary>A fake incoming message, printed and kept locally only (as the debug window's "Simulate incoming").</summary>
    Task SimulateIncomingAsync(string channelId, string text);

    /// <summary>A fake warning about a channel (light red), locally only.</summary>
    Task SimulateWarningAsync(string channelId);

    /// <summary>The Disconnect and Connect buttons.</summary>
    Task DisconnectAsync();

    Task ConnectAsync();

    /// <summary>A channel's /lgc number, or null.</summary>
    Task<int?> SlotOfAsync(string channelId);

    /// <summary>Moves a channel to a /lgc number (one that is free: nothing is swapped).</summary>
    Task AssignSlotAsync(string channelId, int slot);

    /// <summary>The /lgc numbers in use now.</summary>
    Task<IReadOnlyCollection<int>> SlotsInUseAsync();

    /// <summary>Sets a channel's nickname: why it was refused, or null.</summary>
    Task<string?> SetNicknameAsync(string channelId, string nickname);

    /// <summary>A channel's nickname, or null.</summary>
    Task<string?> NicknameOfAsync(string channelId);

    /// <summary>Sets a channel's colour, or with null its default.</summary>
    Task SetChannelColourAsync(string channelId, ChannelColour? colour);

    /// <summary>A channel's colour as saved in the settings now, or null.</summary>
    Task<ChannelColour?> SavedChannelColourAsync(string channelId);

    /// <summary>A channel's "Show in game chat".</summary>
    Task SetShowInGameChatAsync(string channelId, bool show);

    Task<bool> ShowsInGameChatAsync(string channelId);

    /// <summary>Sets the tester's own name colour (0xRRGGBB), or with null the default.</summary>
    Task SetOwnNameColourAsync(uint? rgb);

    /// <summary>What the saved settings file holds under NameColours: each key and colour (keys are numbers only).</summary>
    Task<IReadOnlyDictionary<string, uint>> SavedNameColoursAsync();

    /// <summary>The tester's own name colour, as the plugin would show it now.</summary>
    Task<uint?> OwnNameColourAsync();

    /// <summary>What invite submenu the right-click menus would offer for a kind of thing right-clicked (see ContextInvites).</summary>
    Task<int> InviteOffersForAsync(RightClickKind kind);

    /// <summary>Invites a player to a channel, through the same path as the right-click menu; what it said in chat comes through the tap.</summary>
    Task InviteAsync(string channelId, string name, string world);

    // ---------------------------------------------------------------- settings (see SelfTestSetting)

    Task<JsonElement> ReadSettingAsync(SelfTestSetting setting);

    Task WriteSettingAsync(SelfTestSetting setting, JsonElement value);

    /// <summary>The run file's folder (the plugin's config folder).</summary>
    string ConfigFolder { get; }

    // ---------------------------------------------------------------- typing

    /// <summary>The game's chat channel now (<c>RaptureShellModule.ChatType</c>), or null if unreadable.</summary>
    Task<int?> GameChannelAsync();

    /// <summary>Sets the game's chat channel (as /s would). Ends talking in a channel, if on.</summary>
    Task SetGameChannelAsync(int chatType);

    /// <summary>
    /// Puts a line through the game chat box's gate (<c>ExecuteCommandInner</c>), as typed in the game, on the framework
    /// thread, outside any other line. The host checks again, right before, that the place is private now and the game's
    /// chat channel is Say (<see cref="SelfTestGameChannels.Say"/>), and throws <see cref="SelfTestNotTypedException"/> if
    /// not: nothing is typed then.
    /// </summary>
    Task TypeAsync(string line, TestLink link);

    // ---------------------------------------------------------------- what the runner sees

    /// <summary>A position in the taps: lines after it are those printed or seen since.</summary>
    long Mark();

    /// <summary>What the plugin printed to game chat since <paramref name="mark"/> (the ChatOutput tap; held in memory for the run only).</summary>
    IReadOnlyList<TapLine> PrintedSince(long mark);

    /// <summary>Lines with the run's marker seen in the game's chat since <paramref name="mark"/> (the game chat watcher).</summary>
    IReadOnlyList<GameChatLine> GameChatSince(long mark);

    /// <summary>What the log tap counted since the run started.</summary>
    LogTapCounts LogCounts();

    /// <summary>Sticky mode now: where typing goes, and the labels.</summary>
    Task<StickyView> StickyAsync();

    /// <summary>Stops talking in a channel, if on (clean-up).</summary>
    Task StopTalkingAsync();

    /// <summary>Whether local chat's privacy window is open.</summary>
    Task<bool> PrivacyWindowOpenAsync();

    /// <summary>Opens local chat's privacy window, as Settings' "Read it" does.</summary>
    Task OpenPrivacyWindowAsync();

    /// <summary>How many local chat sends reached the session (looked up or sent) since the run started.</summary>
    int LocalSends { get; }

    /// <summary>A plugin command's registration: registered, once, by LookingGlass, and its help.</summary>
    Task<CommandFacts> CommandAsync(string command);

    /// <summary>Runs one of the plugin's own commands as Dalamud would (never one that reaches game chat).</summary>
    Task RunPluginCommandAsync(string command, string arguments);

    /// <summary>Whether the debug window is open.</summary>
    Task<bool> DebugWindowOpenAsync();

    /// <summary>Opens or closes the Settings window.</summary>
    Task ShowSettingsAsync(bool open);

    // ---------------------------------------------------------------- channel windows

    Task<WindowsView> WindowsAsync();

    /// <summary>Opens a channel in a new window, as the channel list's menu does.</summary>
    Task OpenWindowAsync(string channelId);

    /// <summary>Selects a window's tab, as clicking it does.</summary>
    Task SelectTabAsync(string layoutId, string channelId);

    /// <summary>Marks a window as the one used last (as clicking into it does).</summary>
    Task UseWindowAsync(string layoutId);

    /// <summary>Moves a window (screen pixels).</summary>
    Task MoveWindowAsync(string layoutId, float x, float y);

    // ---------------------------------------------------------------- chat history

    Task<HistoryView> HistoryAsync();

    /// <summary>Removes the channels' lines from the chat history and their text folders (see ChatLog.ForgetChannelAsync).</summary>
    Task ForgetHistoryAsync(IReadOnlyCollection<string> channelIds);

    /// <summary>Deletes the whole chat history ("Delete my chat history"): only when it was off and empty before the run.</summary>
    Task DeleteHistoryAsync();

    // ---------------------------------------------------------------- the log

    /// <summary>Writes a line to dalamud.log, as "[selftest] ...": the runner gives no names and no text.</summary>
    void Log(string line);
}

/// <summary>The host didn't type a line: the place wasn't private, or the game's chat channel wasn't Say, right then.</summary>
public sealed class SelfTestNotTypedException(bool notPrivate) : Exception(notPrivate ? SelfTestWords.NotPrivate : SelfTestWords.NotSay) {
    public bool NotPrivate { get; } = notPrivate;
}

/// <summary>The game's chat channel numbers the runner needs (<c>RaptureShellModule.ChatType</c>).</summary>
public static class SelfTestGameChannels {
    /// <summary>Say.</summary>
    public const int Say = 1;
}

/// <summary>A channel as the runner sees it.</summary>
public sealed record SelfTestChannel(string Id, string? Name, int Members);

/// <summary>Kinds of thing right-clicked, for the invite item's rule (see ContextInvites).</summary>
public enum RightClickKind {
    OwnName,
    OwnCharacter,
    Minion,
    Item,
    ItemLink,
    NonPlayer,
    OtherPlayer,
}

/// <summary>What the plugin printed to game chat (the ChatOutput tap).</summary>
/// <param name="Kind">A channel message, a local chat message, or a notice.</param>
/// <param name="Tone">A notice's colour; null for a message.</param>
/// <param name="Text">A notice's text, or a message's text, as printed.</param>
/// <param name="Tag">The channel tag in the line, if any ("[sky]", "[Local]").</param>
/// <param name="ChannelId">A channel message's channel.</param>
/// <param name="Own">A message the tester sent.</param>
/// <param name="Advanced">Printed while advanced mode was on.</param>
public sealed record TapLine(TapKind Kind, NoticeTone? Tone, string Text, string? Tag = null, string? ChannelId = null, bool Own = false, bool Advanced = false);

public enum TapKind {
    Message,
    LocalMessage,
    Notice,
}

/// <summary>A line with the run's marker seen in the game's own chat (the game chat watcher).</summary>
/// <param name="Channel">Where it showed: Say, Party, Emote, or the plugin's own lines (LookingGlass), or another.</param>
/// <param name="FromTester">Said by the tester (not someone else).</param>
/// <param name="Text">The line's text, held for the run only to match markers; never logged or reported.</param>
public sealed record GameChatLine(GameChatKind Channel, bool FromTester, string Text);

public enum GameChatKind {
    Say,
    Party,
    Emote,
    Echo,
    LookingGlass,
    Other,
}

/// <summary>What the log tap counted: never a line itself.</summary>
/// <param name="Errors">Errors LookingGlass wrote.</param>
/// <param name="Warnings">Warnings LookingGlass wrote.</param>
/// <param name="ForbiddenHits">Lines holding something that must never be in the log: the run's marker, a name, the test channel's name.</param>
/// <param name="StickyLocalLines">"[sticky]" lines that say "talking in [Local]".</param>
/// <param name="StickyLines">"[sticky]" lines.</param>
public sealed record LogTapCounts(int Errors, int Warnings, int ForbiddenHits, int StickyLocalLines, int StickyLines);

/// <summary>Sticky mode as the runner sees it.</summary>
/// <param name="ChannelId">The channel typing goes to, or <see cref="StickyChannel.LocalId"/> for local chat, or null.</param>
/// <param name="ChatInputLabel">What the game's chat input names its channel now (null if unreadable).</param>
/// <param name="InfoBar">The server info bar entry's text, if shown.</param>
/// <param name="ShownColour">The colour the labels are shown in.</param>
public sealed record StickyView(string? ChannelId, string? ChatInputLabel, string? InfoBar, ChannelColour? ShownColour) {
    public bool TalkingInLocal => StickyChannel.IsLocal(this.ChannelId);
}

/// <summary>The channel windows open, and the counts.</summary>
/// <param name="Windows">Each open window.</param>
/// <param name="TabCounts">Each channel's count on a window's tab not selected.</param>
/// <param name="ListCounts">Each channel's unread count in the channel list (only channels seen only in windows).</param>
public sealed record WindowsView(IReadOnlyList<WindowView> Windows, IReadOnlyDictionary<string, int> TabCounts, IReadOnlyDictionary<string, int> ListCounts) {
    public WindowView? Showing(string channelId) => this.Windows.FirstOrDefault(window => window.Tabs.Contains(channelId));
}

/// <summary>One channel window.</summary>
/// <param name="FlashPulses">The pulses of the last flash it started, or 0.</param>
/// <param name="TookFocus">It took the keyboard when it opened (it never should, opened by itself).</param>
/// <param name="Lines">How many lines its selected tab shows.</param>
public sealed record WindowView(string LayoutId, IReadOnlyList<string> Tabs, string? Selected, WindowPlace? Place, int FlashPulses, bool TookFocus, int Lines);

/// <summary>The chat history as the runner sees it.</summary>
/// <param name="TextFolder">The open log's text folder, or null.</param>
/// <param name="Size">How much room every chat history takes.</param>
public sealed record HistoryView(bool On, bool Unencrypted, string? TextFolder, long Size, int Megabytes);

/// <summary>A plugin command's registration.</summary>
public sealed record CommandFacts(bool Registered, bool Ours, bool ShownInHelp, string? Help);
