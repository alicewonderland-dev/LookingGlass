using System.Text;
using System.Text.Json;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Plugin.Ui;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>The plugin's parts the self-test reaches, made in the plugin's constructor.</summary>
internal sealed record SelfTestParts(
    Configuration Config,
    PlayerTracker Player,
    SessionManager Sessions,
    StickyMode Sticky,
    ChatOutput Chat,
    ChannelSender Sender,
    LocalSender Local,
    ChannelWindows Windows,
    LocalChatPrivacyWindow Privacy,
    SettingsWindow Settings,
    DebugWindow Debug,
    ContextInviter Inviter,
    LogTap LogTap);

/// <summary>
/// The self-test's host in game (see <see cref="ISelfTestHost"/>): every game and plugin call on the framework thread, the
/// taps (what the plugin printed, the game chat lines with the run's marker) held in memory while a run is on and dropped
/// when it ends, and the log tap's counts. Typing checks the place and the game's chat channel once more, right before the
/// line goes through the gate, and types nothing if either isn't as it must be.
/// </summary>
internal sealed class SelfTestHost : ISelfTestHost, IDisposable {
    private readonly SelfTestParts _parts;
    private readonly Lock _lock = new();
    private readonly List<(long Seq, TapLine Line)> _printed = [];
    private readonly List<(long Seq, GameChatLine Line)> _gameChat = [];
    private long _seq;
    private string? _marker;
    private int _logins;
    private int _localSendsAtStart;

    /// <summary>The most lines each tap holds (a run prints far fewer).</summary>
    private const int MostLines = 2000;

    public SelfTestHost(SelfTestParts parts) {
        this._parts = parts;
        Services.ClientState.Login += this.OnLogin;
    }

    private Configuration Config => this._parts.Config;

    private SessionManager Sessions => this._parts.Sessions;

    private void OnLogin() => Interlocked.Increment(ref this._logins);

    /// <summary>Starts the taps for a run: what the plugin prints, game chat lines with the run's marker, the log tap.</summary>
    /// <param name="forbidden">What must never be in the log (names, channel names, the marker).</param>
    public void StartTaps(string markerPrefix, IEnumerable<string> forbidden) {
        lock (this._lock) {
            this._printed.Clear();
            this._gameChat.Clear();
            this._forbiddenExtra.Clear();
            this._marker = markerPrefix;
        }

        this._localSendsAtStart = this._parts.Local.Sends;
        this._parts.Chat.Tap = this.OnPrinted;
        this._parts.LogTap.Watch(forbidden);
        Services.Chat.ChatMessage += this.OnChatMessage;
    }

    /// <summary>Stops the taps and drops everything they held.</summary>
    public void StopTaps() {
        Services.Chat.ChatMessage -= this.OnChatMessage;
        this._parts.Chat.Tap = null;
        this._parts.LogTap.Stop();
        lock (this._lock) {
            this._printed.Clear();
            this._gameChat.Clear();
            this._marker = null;
        }
    }

    private void OnPrinted(TapLine line) {
        lock (this._lock) {
            if (this._printed.Count < MostLines) {
                this._printed.Add((++this._seq, line));
            }
        }
    }

    /// <summary>The game chat watcher: only lines holding the run's marker are kept.</summary>
    private void OnChatMessage(IHandleableChatMessage message) {
        try {
            string? marker;
            lock (this._lock) {
                marker = this._marker;
            }

            var text = message.Message.TextValue;
            if (marker == null) {
                return;
            }

            var kind = message.LogKind switch {
                XivChatType.Say => GameChatKind.Say,
                XivChatType.Party or XivChatType.CrossParty => GameChatKind.Party,
                XivChatType.CustomEmote or XivChatType.StandardEmote => GameChatKind.Emote,
                XivChatType.Echo => GameChatKind.Echo,
                var type when type == this.Config.ChatType => GameChatKind.LookingGlass,
                _ => GameChatKind.Other,
            };
            var me = this._parts.Player.Current?.Name;
            var fromTester = me != null && message.Sender.TextValue.Contains(me, StringComparison.Ordinal);
            var marked = text.Contains(marker, StringComparison.Ordinal);
            // The run's own lines; and the tester's in Say, Party or emotes (a line with only a link has no marker), without their text.
            if (!marked && !(fromTester && kind is GameChatKind.Say or GameChatKind.Party or GameChatKind.Emote)) {
                return;
            }

            lock (this._lock) {
                if (this._gameChat.Count < MostLines) {
                    this._gameChat.Add((++this._seq, new GameChatLine(kind, fromTester, marked ? text : "")));
                }
            }
        } catch {
            // The watcher must never be in the way of game chat.
        }
    }

    private static Task<T> OnFramework<T>(Func<T> read) => Services.Framework.RunOnFrameworkThread(read);

    private static Task OnFramework(Action act) => Services.Framework.RunOnFrameworkThread(act);

    // ================================================================ ISelfTestHost

    public DateTimeOffset Now => DateTimeOffset.Now;

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public Task<PlaceFacts> ReadPlaceAsync() => OnFramework(PrivateAreaReader.Read);

    public Task<int> OthersNearAsync(double yalms) => OnFramework(() => PrivateAreaReader.OthersNear(yalms));

    public int Logins => Volatile.Read(ref this._logins);

    public string ConfigFolder => Services.PluginInterface.ConfigDirectory.FullName;

    public Task<SelfTestEnvironment> ReadEnvironmentAsync() => OnFramework(() => {
        var session = this.Sessions.Session;
        var snapshot = this.Sessions.Snapshot;
        bool historyEmpty;
        try {
            historyEmpty = ChatLogFiles.Folders(this.ConfigFolder).Count == 0;
        } catch (Exception) {
            historyEmpty = false;
        }

        return new SelfTestEnvironment {
            PluginVersion = typeof(SelfTestHost).Assembly.GetName().Version?.ToString(3) ?? "?",
            ChatTwoLoaded = this._parts.Sticky.ChatTwoLoaded,
            AdvancedMode = this.Config.AdvancedMode,
            Connected = snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true },
            ChannelCount = snapshot.Channels.Length,
            CanType = this._parts.Sticky.InputHooked,
            LocalChatAvailable = session?.LocalChatAvailable ?? false,
            HistoryOn = this.Config.KeepChatLog,
            HistoryEmpty = historyEmpty,
            ChatInputReadable = StickyMode.ChatInputLabel() != null,
        };
    });

    public Task<bool> IsReadyAsync() => Task.FromResult(this.Sessions.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true });

    public Task<IReadOnlyList<SelfTestChannel>> ChannelsAsync() =>
        Task.FromResult<IReadOnlyList<SelfTestChannel>>(this.Sessions.Snapshot.Channels.Select(channel => new SelfTestChannel(channel.Id, channel.Name, channel.Members.Length)).ToList());

    private ClientSession Session => this.Sessions.Session ?? throw new SessionDisconnectedException("Not connected.");

    /// <summary>The run's own test channels (set by the service while a run is on): the only ones the host changes or sends to.</summary>
    public Func<IReadOnlyCollection<string>>? TestChannels { get; set; }

    /// <summary>Refuses anything about a channel that isn't one of the run's own test channels: the tester's are never touched.</summary>
    private string Own(string channelId) => this.TestChannels?.Invoke().Contains(channelId) == true
        ? channelId
        : throw new InvalidOperationException("Not one of the run's test channels.");

    public Task<string> CreateChannelAsync(string name) => this.Session.CreateChannelAsync(name);

    public Task LeaveChannelAsync(string channelId) => this.Session.LeaveAsync(this.Own(channelId));

    public Task RenameChannelAsync(string channelId, string name) => this.Session.RenameAsync(this.Own(channelId), name);

    public Task SendAsync(string channelId, string text) => this.Session.SendTextAsync(this.Own(channelId), text);

    public Task SimulateIncomingAsync(string channelId, string text) => OnFramework(() => this.Sessions.SimulateIncoming(this.Own(channelId), text));

    public Task SimulateWarningAsync(string channelId) => OnFramework(() => this.Sessions.SimulateNotice(this.Own(channelId)));

    public Task DisconnectAsync() => OnFramework(this.Sessions.Disconnect);

    public Task ConnectAsync() => OnFramework(this.Sessions.Connect);

    public Task<int?> SlotOfAsync(string channelId) => Task.FromResult(this.Sessions.SlotOf(channelId));

    public Task AssignSlotAsync(string channelId, int slot) => OnFramework(() => {
        // Only to a free number: the tester's own channels never move.
        if (this.Sessions.Slots.Any(entry => entry.Value == slot && entry.Key != channelId)) {
            throw new InvalidOperationException("That number is taken.");
        }

        this.Sessions.AssignSlot(this.Own(channelId), slot);
    });

    public Task<IReadOnlyCollection<int>> SlotsInUseAsync() => Task.FromResult<IReadOnlyCollection<int>>(this.Sessions.Slots.Values.ToList());

    public Task<string?> SetNicknameAsync(string channelId, string nickname) => OnFramework(() => this.Sessions.SetNickname(this.Own(channelId), nickname));

    public Task<string?> NicknameOfAsync(string channelId) => Task.FromResult(this.Sessions.NicknameOf(channelId));

    public Task SetChannelColourAsync(string channelId, ChannelColour? colour) => OnFramework(() => this.Sessions.SetColour(this.Own(channelId), colour));

    public Task<ChannelColour?> SavedChannelColourAsync(string channelId) => OnFramework<ChannelColour?>(() => {
        // As saved: read back from the settings file, not from memory.
        if (this.Sessions.SessionPlayer is not { } player || SavedSettings() is not { } saved) {
            return null;
        }

        using (saved) {
            if (!saved.RootElement.TryGetProperty(nameof(Configuration.Characters), out var characters)
                || !characters.TryGetProperty(player.ContentId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var character)) {
                return null;
            }

            if (character.TryGetProperty(nameof(CharacterSettings.CustomChannelColours), out var custom) && custom.ValueKind == JsonValueKind.Object
                && custom.TryGetProperty(channelId, out var rgb) && rgb.TryGetUInt32(out var value)) {
                return ChannelColour.Custom(value);
            }

            if (character.TryGetProperty(nameof(CharacterSettings.ChannelColours), out var rows) && rows.ValueKind == JsonValueKind.Object
                && rows.TryGetProperty(channelId, out var row) && row.TryGetUInt16(out var number)) {
                return ChannelColour.OfRow(number);
            }

            return null;
        }
    });

    /// <summary>The settings file as saved, or null if it can't be read.</summary>
    private static JsonDocument? SavedSettings() {
        try {
            return JsonDocument.Parse(File.ReadAllBytes(Services.PluginInterface.ConfigFile.FullName));
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    public Task SetShowInGameChatAsync(string channelId, bool show) => OnFramework(() => this._parts.Windows.SetShowInGameChat(this.Own(channelId), show));

    public Task<bool> ShowsInGameChatAsync(string channelId) => Task.FromResult(this.Sessions.ShowsInGameChat(channelId));

    public Task SetOwnNameColourAsync(uint? rgb) => OnFramework(() => {
        if (this.Sessions.Snapshot.Me is { } me) {
            this.Sessions.SetNameColour(me, rgb);
        }
    });

    public Task<IReadOnlyDictionary<string, uint>> SavedNameColoursAsync() => OnFramework<IReadOnlyDictionary<string, uint>>(() => {
        // As saved: read back from the settings file, not from memory.
        using var document = SavedSettings();
        if (document != null && document.RootElement.TryGetProperty(nameof(Configuration.NameColours), out var colours) && colours.ValueKind == JsonValueKind.Object) {
            return colours.EnumerateObject().Where(entry => entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetUInt32(out _))
                .ToDictionary(entry => entry.Name, entry => entry.Value.GetUInt32());
        }

        return new Dictionary<string, uint>();
    });

    public Task<uint?> OwnNameColourAsync() => Task.FromResult(this.Sessions.NameColourOf(this.Sessions.Snapshot.Me));

    public Task<int> InviteOffersForAsync(RightClickKind kind) => OnFramework(() => {
        var me = this.Sessions.Snapshot.Me;
        return kind switch {
            RightClickKind.Yourself when me != null => this._parts.Inviter.OffersFor(new InviteTarget(me.Name, me.WorldName)).Count,
            RightClickKind.OneWordName => this._parts.Inviter.OffersFor(new InviteTarget("Mogret", me?.WorldName ?? "Lich")).Count,
            RightClickKind.ItemMenu => ContextInvites.IsPlayerMenu("Inventory") ? 1 : 0,
            RightClickKind.AnotherPlayer => this._parts.Inviter.OffersFor(new InviteTarget("Testing Person", me?.WorldName ?? "Lich")).Count,
            _ => 0,
        };
    });

    public Task InviteAsync(string channelId, string name, string world) => OnFramework(() => {
        this.Own(channelId);
        var target = new InviteTarget(name, world);
        var offer = this._parts.Inviter.OffersFor(target).FirstOrDefault(offer => offer.ChannelId == channelId)
                    ?? throw new InvalidOperationException("The test channel isn't offered for that player.");
        this._parts.Inviter.Invite(target, offer);
    });

    public Task<JsonElement> ReadSettingAsync(SelfTestSetting setting) => OnFramework(() => this.SettingsNow().Read(setting));

    public Task WriteSettingAsync(SelfTestSetting setting, JsonElement value) => OnFramework(() => this.SettingsNow().Write(setting, value));

    /// <summary>The settings of the character and server the session is for now.</summary>
    private PluginSettings SettingsNow() {
        var player = this.Sessions.SessionPlayer ?? this._parts.Player.Current ?? throw new InvalidOperationException("Not logged in.");
        return new PluginSettings(this.Config, player.ContentId, this.Sessions.ServerUrl, this.Sessions);
    }

    public Task<int?> GameChannelAsync() => OnFramework(() => ChatInterop.CurrentChannel()?.ChatType);

    public Task SetGameChannelAsync(int chatType) => OnFramework(() => ChatInterop.SetChannel(chatType));

    public Task TypeAsync(string line, TestLink link) => OnFramework(() => {
        // Right before it goes: the place, now (not settled: any doubt types nothing), and Say underneath.
        if (!PrivateArea.Judge(PrivateAreaReader.Read()).Private) {
            throw new SelfTestNotTypedException(notPrivate: true);
        }

        if (ChatInterop.CurrentChannel()?.ChatType != SelfTestGameChannels.Say) {
            throw new SelfTestNotTypedException(notPrivate: false);
        }

        this._parts.Sticky.TypeLine(Build(line, link));
    });

    /// <summary>A line's bytes as the game's chat box holds them: its text, and an item link where asked (a Potion).</summary>
    private static byte[] Build(string line, TestLink link) {
        if (link == TestLink.None) {
            return Encoding.UTF8.GetBytes(line);
        }

        const uint potion = 4551;
        var builder = new SeStringBuilder();
        switch (link) {
            case TestLink.ItemOnly:
                // A command before it, if any ("/lgl "), then the link alone.
                if (line.Length > 0) {
                    builder.AddText(line);
                }

                builder.AddItemLink(potion, false);
                break;
            case TestLink.ItemInMiddle:
                // After the marker, before ", please ignore": the marker stays whole, and text follows the link.
                var at = line.LastIndexOf(", please ignore", StringComparison.Ordinal);
                at = at < 0 ? line.Length : at;
                builder.AddText(line[..at] + " ");
                builder.AddItemLink(potion, false);
                builder.AddText(line[at..]);
                break;
            default:
                builder.AddText(line + " ");
                builder.AddItemLink(potion, false);
                break;
        }

        return builder.Build().Encode();
    }

    public long Mark() {
        lock (this._lock) {
            return this._seq;
        }
    }

    public IReadOnlyList<TapLine> PrintedSince(long mark) {
        lock (this._lock) {
            return this._printed.Where(entry => entry.Seq > mark).Select(entry => entry.Line).ToList();
        }
    }

    public IReadOnlyList<GameChatLine> GameChatSince(long mark) {
        lock (this._lock) {
            return this._gameChat.Where(entry => entry.Seq > mark).Select(entry => entry.Line).ToList();
        }
    }

    public LogTapCounts LogCounts() => this._parts.LogTap.Counts;

    public Task<StickyView> StickyAsync() => OnFramework(() => {
        var sticky = this._parts.Sticky;
        return new StickyView(sticky.TalkingIn, StickyMode.ChatInputLabel(), sticky.InfoBarText, sticky.Shown?.Colour);
    });

    public Task StopTalkingAsync() => OnFramework(this._parts.Sticky.StopTalking);

    public Task<bool> PrivacyWindowOpenAsync() => OnFramework(() => this._parts.Privacy.IsOpen);

    public Task OpenPrivacyWindowAsync() => OnFramework(() => this._parts.Privacy.Ask(LocalPrivacyAsked.FromSettings));

    public int LocalSends => this._parts.Local.Sends - this._localSendsAtStart;

    public Task<CommandFacts> CommandAsync(string command) => OnFramework(() => {
        if (!Services.Commands.Commands.TryGetValue(command, out var info)) {
            return new CommandFacts(false, false, false, null);
        }

        var ours = info.Handler.Method.DeclaringType?.Assembly == typeof(SelfTestHost).Assembly;
        return new CommandFacts(true, ours, info.ShowInHelp, info.HelpMessage);
    });

    public Task RunPluginCommandAsync(string command, string arguments) => OnFramework(() => {
        // Only this plugin's own commands, which never reach game chat.
        if (!Services.Commands.Commands.TryGetValue(command, out var info) || info.Handler.Method.DeclaringType?.Assembly != typeof(SelfTestHost).Assembly) {
            throw new InvalidOperationException("Not one of LookingGlass's commands.");
        }

        info.Handler(command, arguments);
    });

    public Task<bool> DebugWindowOpenAsync() => OnFramework(() => this._parts.Debug.IsOpen);

    public Task ShowSettingsAsync(bool open) => OnFramework(() => {
        this._parts.Settings.IsOpen = open;
        if (open) {
            this._parts.Settings.BringToFront();
        }
    });

    public Task<WindowsView> WindowsAsync() => OnFramework(() => {
        var windows = this._parts.Windows;
        var history = this.Sessions.History;
        var views = windows.Open.Select(window => new WindowView(window.Layout.Id, window.Layout.Tabs.ToList(), window.Layout.Selected, window.Layout.Place,
            window.LastFlashPulses, window.TakesFocus, window.Layout.Selected is { } selected ? history.LinesOf(selected).Length : 0)).ToList();
        var tabs = new Dictionary<string, int>();
        foreach (var window in windows.Open) {
            foreach (var channelId in window.Layout.Tabs.Where(tab => tab != window.Layout.Selected)) {
                tabs[channelId] = windows.TabCounts.CountOf(history, channelId);
            }
        }

        var off = this.Sessions.GameChatOff;
        var windowsOnly = this.Sessions.MessagesOnlyInWindows;
        var list = this.Sessions.Snapshot.Channels.ToDictionary(channel => channel.Id, channel => this.Sessions.Unread.ShownCountOf(channel.Id, windowsOnly, off));
        return new WindowsView(views, tabs, list);
    });

    public Task OpenWindowAsync(string channelId) => OnFramework(() => this._parts.Windows.OpenNew(channelId));

    public Task SelectTabAsync(string layoutId, string channelId) => OnFramework(() => this.Window(layoutId).Show(channelId));

    public Task UseWindowAsync(string layoutId) => OnFramework(() => this._parts.Windows.Used(this.Window(layoutId)));

    public Task MoveWindowAsync(string layoutId, float x, float y) => OnFramework(() => this._parts.Windows.Move(this.Window(layoutId), x, y));

    public Task AddTabAsync(string layoutId, string channelId) => OnFramework(() => this._parts.Windows.AddTo(this.Window(layoutId), channelId));

    public Task CloseWindowAsync(string layoutId) => OnFramework(() => this._parts.Windows.Forget(this.Window(layoutId)));

    public Task<WindowPlace> ScreenAsync() => OnFramework(() => {
        var viewport = Dalamud.Bindings.ImGui.ImGui.GetMainViewport();
        return new WindowPlace(viewport.WorkPos.X, viewport.WorkPos.Y, viewport.WorkSize.X, viewport.WorkSize.Y);
    });

    public Task<bool> InCombatAsync() => OnFramework(() => Services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]);

    private ChannelWindow Window(string layoutId) =>
        this._parts.Windows.Open.FirstOrDefault(window => window.Layout.Id == layoutId) ?? throw new InvalidOperationException("That window isn't open.");

    public async Task<HistoryView> HistoryAsync() {
        long size;
        try {
            size = await this.Sessions.ChatLogSize();
        } catch (Exception) {
            size = -1;
        }

        return new HistoryView(this.Config.KeepChatLog, this.Config.KeepChatLogUnencrypted, this.Sessions.ChatLogTextFolder, size, this.Config.ChatLogMegabytes);
    }

    public async Task ForgetHistoryAsync(IReadOnlyCollection<string> channelIds) {
        foreach (var channelId in channelIds) {
            this.Own(channelId);
        }

        if (this.Sessions.ChatLog is not { } log) {
            // Off: nothing of the run was kept (the run turns it on only for the checks that need it).
            return;
        }

        foreach (var channelId in channelIds) {
            await log.ForgetChannelAsync(channelId).ConfigureAwait(false);
        }
    }

    public Task DeleteHistoryAsync() => OnFramework(this.Sessions.DeleteChatLogs).Unwrap();

    public void Log(string line) => Services.Log.Information(line);

    private readonly List<string> _forbiddenExtra = [];

    /// <summary>What the tester typed in during the run (names) that must stay out of the report too.</summary>
    public IReadOnlyList<string> ForbiddenExtra {
        get {
            lock (this._lock) {
                return this._forbiddenExtra.ToList();
            }
        }
    }

    public void Forbid(string text) {
        lock (this._lock) {
            this._forbiddenExtra.Add(text);
        }

        this._parts.LogTap.Forbid(text);
        foreach (var part in text.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            this._parts.LogTap.Forbid(part);
            lock (this._lock) {
                this._forbiddenExtra.Add(part);
            }
        }
    }

    public void Dispose() {
        this.StopTaps();
        Services.ClientState.Login -= this.OnLogin;
    }
}
