using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The fake host's small stand-in for the plugin (<see cref="Simulate"/>): sticky mode and local chat as the checklists say
/// they behave, alone in a private place (/lgl, /lgc, /s, one-offs, the privacy notice, the lines said in game chat), in the
/// plugin's own words from Core. Enough for the talking suite's automatic checks to pass when the plugin does what it
/// should, and fail when it doesn't.
/// </summary>
internal sealed partial class FakeSelfTestHost {
    private readonly List<(long Seq, TapLine Line)> _printed = [];
    private readonly List<(long Seq, GameChatLine Line)> _gameChat = [];
    private long _seq;

    /// <summary>Acts as the plugin would on what is typed.</summary>
    public bool Simulate { get; set; }

    public bool PrivacyOpen { get; set; }

    /// <summary>A mistake to make on purpose: "/s text" while talking in local chat goes to local chat, not Say.</summary>
    public bool OneOffsBroken { get; set; }

    public bool Is(SelfTestSetting setting) => this.Settings[setting].ValueKind == JsonValueKind.True;

    public void Print(TapLine line) {
        lock (this._lock) {
            this._printed.Add((++this._seq, line));
        }
    }

    /// <summary>A line in the game's chat with the run's marker, said by someone (the other tester).</summary>
    public void GameChat(GameChatKind channel, string text, bool fromTester) {
        lock (this._lock) {
            this._gameChat.Add((++this._seq, new GameChatLine(channel, fromTester, text)));
        }
    }

    private void Say(GameChatKind channel, string text) {
        lock (this._lock) {
            this._gameChat.Add((++this._seq, new GameChatLine(channel, true, text)));
        }
    }

    private void Notice(string text, string? tag = null) => this.Print(new TapLine(TapKind.Notice, NoticeTone.Info, text, tag));

    private string TagOf(string? channelId) =>
        channelId == StickyChannel.LocalId ? LocalChat.Tag : channelId != null && this.Slots.TryGetValue(channelId, out var slot) ? $"[LGC{slot}]" : ChannelTag.Fallback;

    // Talking in a channel is the plugin's own state at once; the labels the game shows follow a few frames later (LabelLag).
    private StickyView SimulatedSticky() => this.Labelled is { } labelled
        ? new StickyView(this.Sticky, this.TagOf(labelled), $"LG {this.TagOf(labelled)}", ColouredText.DefaultTag)
        : new StickyView(this.Sticky, "Say", null, null);

    /// <summary>Talking in a channel ends: said with verbose on, or always for an end the player didn't choose.</summary>
    private void SimulateEnd(StickyEnd why) {
        if (!this.Simulate || this.Sticky is not { } channel) {
            return;
        }

        var tag = this.TagOf(channel);
        this.Sticky = null;
        if (StickyMessages.SayEnded(why, this.Is(SelfTestSetting.SayWhenTalking))) {
            this.Notice(StickyMessages.Ended(tag, why), tag);
        }
    }

    private void Enter(string channelId) {
        var tag = this.TagOf(channelId);
        if (!this.Ready) {
            this.Notice(StickyMessages.NotConnected(tag));
            return;
        }

        if (StickyChannel.IsLocal(channelId) && !this.Is(SelfTestSetting.LocalPrivacyAccepted)) {
            this.Notice(LocalChatWords.PrivacyAskedToTalk.Plain);
            this.PrivacyOpen = true;
            return;
        }

        this.Sticky = channelId;
        if (this.Is(SelfTestSetting.SayWhenTalking)) {
            this.Notice(StickyMessages.Entered(tag), tag);
        }

        if (StickyChannel.IsLocal(channelId) && !this.Is(SelfTestSetting.LocalTalkNoteShown)) {
            this.Notice(LocalChatWords.TalkingNote.Plain, tag);
            this.Settings[SelfTestSetting.LocalTalkNoteShown] = JsonSerializer.SerializeToElement(true);
        }
    }

    /// <summary>/lgl with a message, or a line while talking in local chat (with its tag): alone, nobody is near.</summary>
    private void Local(string? stickyTag) {
        if (!this.Is(SelfTestSetting.LocalPrivacyAccepted)) {
            this.Notice(LocalChatWords.Refusal(LocalChatWords.PrivacyAsked, stickyTag).Plain);
            this.PrivacyOpen = true;
            return;
        }

        this.Notice(LocalChatWords.Refusal(LocalChatWords.NobodyNear, stickyTag).Plain);
    }

    private void Message(string channelId, string text) => this.Print(new TapLine(TapKind.Message, null, text, this.TagOf(channelId), channelId, Own: true));

    private void Interpret(string line, TestLink link) {
        var space = line.IndexOf(' ');
        var command = line.StartsWith('/') ? space < 0 ? line : line[..space] : null;
        var rest = command == null ? line : space < 0 ? "" : line[(space + 1)..];
        var something = rest.Length > 0 || link != TestLink.None;
        switch (command) {
            case LocalChat.Command when !something:
                this.Enter(StickyChannel.LocalId);
                return;
            case LocalChat.Command:
                this.Local(null);
                return;
            case "/s" when !something:
                this.SimulateEnd(StickyEnd.ChannelSwitched);
                this.GameChannel = SelfTestGameChannels.Say;
                return;
            case "/s" when this.OneOffsBroken && StickyChannel.IsLocal(this.Sticky):
                this.Local(LocalChat.Tag);
                return;
            case "/s":
                this.Say(GameChatKind.Say, rest);
                return;
            case "/em":
                this.Say(GameChatKind.Emote, rest);
                return;
            case "/lgc":
                this.Notice(ChannelCommand.NicknameUsage);
                return;
            case { } slotCommand when CommandSlots.SlotOfCommand(slotCommand) is { } slot:
                var channel = this.Slots.FirstOrDefault(entry => entry.Value == slot).Key;
                if (channel == null) {
                    this.Notice($"Nothing is on {slotCommand}.");
                } else if (!something) {
                    this.Enter(channel);
                } else {
                    this.Message(channel, rest);
                }

                return;
            case null when this.Sticky is { } sticky && StickyChannel.IsLocal(sticky):
                this.Local(LocalChat.Tag);
                return;
            case null when this.Sticky is { } sticky:
                this.Message(sticky, rest);
                return;
            case null:
                this.Say(GameChatKind.Say, rest);
                return;
        }
    }
}
