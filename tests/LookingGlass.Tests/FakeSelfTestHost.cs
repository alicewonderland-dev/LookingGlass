using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// A fake plugin and game for the self-test runner: a virtual clock (every wait moves it on at once, and runs
/// <see cref="OnTick"/>, where a test plays the tester), a place that can change, a session with channels, settings, and
/// what was typed and where. Typing checks the place and the chat channel itself, as the plugin's host does, and records
/// any line that would have been typed outside a private place (there must never be one).
/// </summary>
internal sealed partial class FakeSelfTestHost : ISelfTestHost, IDisposable {
    private readonly Lock _lock = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lgt-selftest-" + Guid.NewGuid().ToString("N"));
    private long _ids;

    public static readonly PlaceFacts InnRoom = new(177, 2, 2, HousingKind.None, ulong.MaxValue, -1, 0, false, "Mizzenmast Inn");
    public static readonly PlaceFacts House = new(339, 14, 14, HousingKind.Indoor, 0x1234, 5, 0, false, "Mist");
    public static readonly PlaceFacts Ward = new(339, 13, 13, HousingKind.Outdoor, null, 5, 0, false, "Mist");

    public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 18, 5, 0, TimeSpan.Zero);

    public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(2), "Test", "Test");

    /// <summary>Run at every wait, after the clock moved: the test's "tester".</summary>
    public Action? OnTick { get; set; }

    /// <summary>Run right before a line is typed, after the runner's own check: a place change sneaking in between.</summary>
    public Action? BeforeType { get; set; }

    public PlaceFacts Place { get; set; } = InnRoom;

    public int Others { get; set; }

    public bool Ready { get; set; } = true;

    public int Logins { get; set; } = 1;

    public bool ChatTwo { get; set; }

    public int? GameChannel { get; set; } = 2;

    /// <summary>Where typing goes now (the plugin's own state, at once).</summary>
    public string? Sticky {
        get => this._sticky;
        set {
            if (value != this._sticky) {
                this._labelsFor = this._sticky;
                this._stickyChangedAt = this.Now;
            }

            this._sticky = value;
        }
    }

    private string? _sticky;
    private string? _labelsFor;
    private DateTimeOffset _stickyChangedAt;

    /// <summary>How long the game takes to rename its chat box, and the info bar to follow (a few frames in game).</summary>
    public TimeSpan LabelLag { get; set; }

    /// <summary>The channel the labels show now: the one before, until <see cref="LabelLag"/> has passed.</summary>
    private string? Labelled => this.Now - this._stickyChangedAt < this.LabelLag ? this._labelsFor : this._sticky;

    public List<(string Line, PlaceKind Place)> Typed { get; } = [];

    /// <summary>Lines typed while the place wasn't private: must stay empty.</summary>
    public List<string> TypedOutside { get; } = [];

    public List<SelfTestChannel> Channels { get; } = [];

    public Dictionary<string, int> Slots { get; } = new();

    public List<string> Left { get; } = [];

    public List<(string Channel, string Text)> Sent { get; } = [];

    public Dictionary<SelfTestSetting, JsonElement> Settings { get; } = new();

    public List<string> Logged { get; } = [];

    public List<IReadOnlyCollection<string>> Forgotten { get; } = [];

    public int Deleted { get; set; }

    /// <summary>Thrown by the next send, if set.</summary>
    public Exception? SendFails { get; set; }

    /// <summary>Thrown by leaving a channel, if set.</summary>
    public Exception? LeaveFails { get; set; }

    public int Creates { get; private set; }

    public List<string> CreatedNames { get; } = [];

    /// <summary>The channels the run made, by ID.</summary>
    public List<string> Created { get; } = [];

    public FakeSelfTestHost() {
        foreach (var setting in Enum.GetValues<SelfTestSetting>()) {
            this.Settings[setting] = JsonSerializer.SerializeToElement(setting.ToString() + " before");
        }

        // The name colours in their own shape: the key (a number) and the colour.
        this.Settings[SelfTestSetting.OwnNameColour] = JsonSerializer.SerializeToElement(new OwnNameColourValue("1001", null));
        this.Settings[SelfTestSetting.PartnerNameColour] = JsonSerializer.SerializeToElement(new OwnNameColourValue("42", null));
        this.Before = this.Settings.ToDictionary(entry => entry.Key, entry => entry.Value.GetRawText());
    }

    /// <summary>Every setting's value before the test (as JSON): the clean-up puts each back.</summary>
    public IReadOnlyDictionary<SelfTestSetting, string> Before { get; }

    public string ConfigFolder => this._folder;

    public SelfTestRunFile File => new(this.ConfigFolder);

    public Task Delay(TimeSpan delay, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        lock (this._lock) {
            this.Now += delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        }

        this.OnTick?.Invoke();
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<PlaceFacts> ReadPlaceAsync() => Task.FromResult(this.Place);

    public Task<int> OthersNearAsync(double yalms) => Task.FromResult(this.Others);

    public bool HistoryOn { get; set; }

    public bool HistoryEmpty { get; set; }

    /// <summary>What the clean-up did, in order: "write:Setting", "delete-history", "forget", "connect".</summary>
    public List<string> Ops { get; } = [];

    public bool ConnectFails { get; set; }

    public Exception? ForgetFails { get; set; }

    public Task<SelfTestEnvironment> ReadEnvironmentAsync() => Task.FromResult(new SelfTestEnvironment {
        PluginVersion = "0.2.17", ChatTwoLoaded = this.ChatTwo, Connected = this.Ready, ChannelCount = this.Channels.Count, CanType = true,
        LocalChatAvailable = true, ChatInputReadable = true,
        HistoryOn = this.HistoryOn || this.Is(SelfTestSetting.KeepHistory), HistoryEmpty = this.HistoryEmpty,
    });

    public Task<bool> IsReadyAsync() => Task.FromResult(this.Ready);

    /// <summary>The character logged in now; null: whoever the run is for.</summary>
    public ulong? Character { get; set; }

    public Task<bool> IsForAsync(ulong contentId, string serverUrl) => Task.FromResult(this.Character == null || this.Character == contentId);

    /// <summary>ChatTwo's input channel (its own numbers).</summary>
    public int? ChatTwoInput { get; set; } = SelfTestGameChannels.ChatTwoSay;

    public Task<int?> ChatTwoInputAsync() => Task.FromResult(this.ChatTwoInput);

    /// <summary>Whether the person looked up is registered.</summary>
    public bool? Registered { get; set; } = false;

    public int Lookups { get; private set; }

    public Task<bool?> IsRegisteredAsync(string name, string world) {
        this.Lookups++;
        return Task.FromResult(this.Registered);
    }

    public List<string> Disbanded { get; } = [];

    public Task DisbandChannelAsync(string channelId) {
        this.Disbanded.Add(channelId);
        this.Channels.RemoveAll(channel => channel.Id == channelId);
        this.Slots.Remove(channelId);
        return Task.CompletedTask;
    }

    public (ulong ContentId, string ServerUrl)? RanFor { get; private set; }

    public void RunFor(ulong contentId, string serverUrl) => this.RanFor = (contentId, serverUrl);

    public List<(string Channel, string Name)> Invited { get; } = [];

    // Disconnected, the session lists no channels (as the plugin's snapshot is empty then).
    public Task<IReadOnlyList<SelfTestChannel>> ChannelsAsync() => Task.FromResult<IReadOnlyList<SelfTestChannel>>(this.Ready ? this.Channels.ToList() : []);

    public Task<string> CreateChannelAsync(string name) {
        this.Creates++;
        this.CreatedNames.Add(name);
        var id = $"channel{Interlocked.Increment(ref this._ids)}";
        this.Created.Add(id);
        this.Channels.Add(new SelfTestChannel(id, name, 1));
        this.Slots[id] = Enumerable.Range(1, 50).First(slot => !this.Slots.ContainsValue(slot));
        return Task.FromResult(id);
    }

    public Task LeaveChannelAsync(string channelId) {
        if (this.LeaveFails is { } fails) {
            throw fails;
        }

        this.Left.Add(channelId);
        this.Channels.RemoveAll(channel => channel.Id == channelId);
        this.Slots.Remove(channelId);
        return Task.CompletedTask;
    }

    public Task RenameChannelAsync(string channelId, string name) {
        var at = this.Channels.FindIndex(channel => channel.Id == channelId);
        if (at >= 0) {
            this.Channels[at] = this.Channels[at] with { Name = name };
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string channelId, string text) {
        if (this.SendFails is { } fails) {
            this.SendFails = null;
            throw fails;
        }

        this.Sent.Add((channelId, text));
        this.Deliver(channelId, MyId, Me, text, own: true);
        this.OnSent?.Invoke(channelId, text);
        return Task.CompletedTask;
    }

    public Task SimulateIncomingAsync(string channelId, string text) => Task.CompletedTask;

    public Task SimulateWarningAsync(string channelId) => Task.CompletedTask;

    public Task DisconnectAsync() {
        this.Ready = false;
        this.SimulateEnd(StickyEnd.Disconnected);
        return Task.CompletedTask;
    }

    public Task ConnectAsync() {
        this.Ops.Add("connect");
        this.Ready = !this.ConnectFails;
        return Task.CompletedTask;
    }

    public Task<int?> SlotOfAsync(string channelId) => Task.FromResult(this.Slots.TryGetValue(channelId, out var slot) ? slot : (int?) null);

    public Task AssignSlotAsync(string channelId, int slot) {
        this.Slots[channelId] = slot;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<int>> SlotsInUseAsync() => Task.FromResult<IReadOnlyCollection<int>>(this.Slots.Values.ToList());

    public Task<string?> SetNicknameAsync(string channelId, string nickname) =>
        Task.FromResult<string?>(nickname.Equals("local", StringComparison.OrdinalIgnoreCase) ? "Local is used by local chat; choose another nickname." : null);

    public Task<string?> NicknameOfAsync(string channelId) => Task.FromResult<string?>(null);

    public Task SetChannelColourAsync(string channelId, ChannelColour? colour) => Task.CompletedTask;

    public Task<ChannelColour?> SavedChannelColourAsync(string channelId) => Task.FromResult<ChannelColour?>(null);

    public Task SetShowInGameChatAsync(string channelId, bool show) => Task.CompletedTask;

    public Task<bool> ShowsInGameChatAsync(string channelId) => Task.FromResult(true);

    public Task SetOwnNameColourAsync(uint? rgb) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, uint>> SavedNameColoursAsync() => Task.FromResult<IReadOnlyDictionary<string, uint>>(new Dictionary<string, uint>());

    public Task<uint?> OwnNameColourAsync() => Task.FromResult<uint?>(null);

    public Task<int> InviteOffersForAsync(RightClickKind kind) => Task.FromResult(0);

    public Task InviteAsync(string channelId, string name, string world) {
        this.Invited.Add((channelId, name));
        if (this.OnInvite is { } invite) {
            invite(channelId, name, world);
            return Task.CompletedTask;
        }

        if (this.Registered == true) {
            // Registered: the invite goes, and the channel has someone else in it (invited).
            var at = this.Channels.FindIndex(channel => channel.Id == channelId);
            this.Channels[at] = this.Channels[at] with { Members = 2 };
            this.Print(new TapLine(TapKind.Notice, NoticeTone.Info, $"Invited {name}@{world} to [LGC50]."));
        } else {
            this.Print(new TapLine(TapKind.Notice, NoticeTone.Info,
                $"Couldn't invite {name}@{world} to [LGC50]: {name}@{world} isn't registered with LookingGlass on this server" +
                (this.Is(SelfTestSetting.AdvancedMode) ? "." : ": they need to install it and register their character first.")));
        }

        return Task.CompletedTask;
    }

    public Task<JsonElement> ReadSettingAsync(SelfTestSetting setting) => Task.FromResult(this.Settings[setting]);

    /// <summary>A setting that can't be written once the run has ended (put back), if set.</summary>
    public SelfTestSetting? FailWriteBack { get; set; }

    public Task WriteSettingAsync(SelfTestSetting setting, JsonElement value) {
        if (setting == this.FailWriteBack && this.Ops.Contains($"write:{setting}")) {
            throw new IOException("Can't.");
        }

        this.Ops.Add($"write:{setting}");
        this.Settings[setting] = value.Clone();
        if (setting == SelfTestSetting.LocalPrivacyAccepted && !this.Is(setting) && this.Sticky == StickyChannel.LocalId) {
            // As sticky mode's frame check does: withdrawn while talking in local chat, it ends at once, and says so.
            this.SimulateEnd(StickyEnd.PrivacyWithdrawn);
        }

        return Task.CompletedTask;
    }

    public Task<int?> GameChannelAsync() => Task.FromResult(this.GameChannel);

    public Task SetGameChannelAsync(int chatType) {
        if (SelfTestGameChannels.IsTell(chatType)) {
            throw new InvalidOperationException("A tell can't be set without a name.");
        }

        this.GameChannel = chatType;
        this.Sticky = null;
        return Task.CompletedTask;
    }

    public Task TypeAsync(string line, TestLink link) {
        this.BeforeType?.Invoke();
        var place = PrivateArea.Judge(this.Place);
        if (!place.Private) {
            throw new SelfTestNotTypedException(notPrivate: true);
        }

        if (this.GameChannel != SelfTestGameChannels.Say) {
            throw new SelfTestNotTypedException(notPrivate: false);
        }

        this.Typed.Add((line, place.Kind));
        if (this.Simulate) {
            this.Interpret(line, link);
        }

        return Task.CompletedTask;
    }

    /// <summary>Called by a test that bypasses the host's own check, to prove the runner's check alone is enough.</summary>
    public void RecordIfOutside(string line) {
        if (!PrivateArea.Judge(this.Place).Private) {
            this.TypedOutside.Add(line);
        }
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

    public LogTapCounts LogCounts() => new(0, 0, 0, 0, 0);

    public Task<StickyView> StickyAsync() => Task.FromResult(this.Simulate ? this.SimulatedSticky() : new StickyView(this.Sticky, null, null, null));

    public Task StopTalkingAsync() {
        this.SimulateEnd(StickyEnd.Stopped);
        this.Sticky = null;
        return Task.CompletedTask;
    }

    public Task<bool> PrivacyWindowOpenAsync() => Task.FromResult(this.PrivacyOpen);

    public Task OpenPrivacyWindowAsync() {
        this.PrivacyOpen = true;
        return Task.CompletedTask;
    }

    public int LocalSends => 0;

    public Task<CommandFacts> CommandAsync(string command) => Task.FromResult(new CommandFacts(true, true, true, this.Simulate ? LocalChatWords.Usage.Plain : "help"));

    public Task RunPluginCommandAsync(string command, string arguments) => Task.CompletedTask;

    public Task<bool> DebugWindowOpenAsync() => Task.FromResult(false);

    public Task ShowSettingsAsync(bool open) => Task.CompletedTask;

    public Task<WindowsView> WindowsAsync() => Task.FromResult(new WindowsView([], new Dictionary<string, int>(), new Dictionary<string, int>()));

    public Task OpenWindowAsync(string channelId) => Task.CompletedTask;

    public Task SelectTabAsync(string layoutId, string channelId) => Task.CompletedTask;

    public Task UseWindowAsync(string layoutId) => Task.CompletedTask;

    public Task MoveWindowAsync(string layoutId, float x, float y) => Task.CompletedTask;

    public Task AddTabAsync(string layoutId, string channelId) => Task.CompletedTask;

    public Task CloseWindowAsync(string layoutId) => Task.CompletedTask;

    public Task<WindowPlace> ScreenAsync() => Task.FromResult(new WindowPlace(0, 0, 1920, 1080));

    public Task<bool> InCombatAsync() => Task.FromResult(false);

    public Task<HistoryView> HistoryAsync() => Task.FromResult(new HistoryView(false, false, null, 0, 50));

    public Task ForgetHistoryAsync(IReadOnlyCollection<string> channelIds) {
        this.Ops.Add("forget");
        if (this.ForgetFails is { } fails) {
            throw fails;
        }

        this.Forgotten.Add(channelIds.ToList());
        return Task.CompletedTask;
    }

    public Task DeleteHistoryAsync() {
        this.Ops.Add("delete-history");
        this.Deleted++;
        return Task.CompletedTask;
    }

    public List<string> Forbidden { get; } = [];

    public void Forbid(string text) => this.Forbidden.Add(text);

    public void Log(string line) {
        lock (this._lock) {
            this.Logged.Add(line);
        }
    }

    public void Dispose() {
        try {
            if (Directory.Exists(this._folder)) {
                Directory.Delete(TestFolders.CheckDeletable(this._folder), true);
            }
        } catch (IOException) {
            // Cleared by a later run (see TestFolders).
        }
    }
}
