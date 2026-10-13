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
internal sealed class FakeSelfTestHost : ISelfTestHost, IDisposable {
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

    public string? Sticky { get; set; }

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

    public FakeSelfTestHost() {
        foreach (var setting in Enum.GetValues<SelfTestSetting>()) {
            this.Settings[setting] = JsonSerializer.SerializeToElement(setting.ToString() + " before");
        }
    }

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

    public Task<SelfTestEnvironment> ReadEnvironmentAsync() => Task.FromResult(new SelfTestEnvironment {
        PluginVersion = "0.2.17", ChatTwoLoaded = this.ChatTwo, Connected = this.Ready, ChannelCount = this.Channels.Count, CanType = true,
        LocalChatAvailable = true, ChatInputReadable = true,
    });

    public Task<bool> IsReadyAsync() => Task.FromResult(this.Ready);

    public Task<IReadOnlyList<SelfTestChannel>> ChannelsAsync() => Task.FromResult<IReadOnlyList<SelfTestChannel>>(this.Channels.ToList());

    public Task<string> CreateChannelAsync(string name) {
        this.Creates++;
        this.CreatedNames.Add(name);
        var id = $"channel{Interlocked.Increment(ref this._ids)}";
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

    public Task SendAsync(string channelId, string text) {
        if (this.SendFails is { } fails) {
            this.SendFails = null;
            throw fails;
        }

        this.Sent.Add((channelId, text));
        return Task.CompletedTask;
    }

    public Task SimulateIncomingAsync(string channelId, string text) => Task.CompletedTask;

    public Task SimulateWarningAsync(string channelId) => Task.CompletedTask;

    public Task DisconnectAsync() {
        this.Ready = false;
        return Task.CompletedTask;
    }

    public Task ConnectAsync() {
        this.Ready = true;
        return Task.CompletedTask;
    }

    public Task<int?> SlotOfAsync(string channelId) => Task.FromResult(this.Slots.TryGetValue(channelId, out var slot) ? slot : (int?) null);

    public Task AssignSlotAsync(string channelId, int slot) {
        this.Slots[channelId] = slot;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<int>> SlotsInUseAsync() => Task.FromResult<IReadOnlyCollection<int>>(this.Slots.Values.ToList());

    public Task<string?> SetNicknameAsync(string channelId, string nickname) => Task.FromResult<string?>(null);

    public Task<string?> NicknameOfAsync(string channelId) => Task.FromResult<string?>(null);

    public Task SetChannelColourAsync(string channelId, ChannelColour? colour) => Task.CompletedTask;

    public Task<ChannelColour?> SavedChannelColourAsync(string channelId) => Task.FromResult<ChannelColour?>(null);

    public Task SetShowInGameChatAsync(string channelId, bool show) => Task.CompletedTask;

    public Task<bool> ShowsInGameChatAsync(string channelId) => Task.FromResult(true);

    public Task SetOwnNameColourAsync(uint? rgb) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, uint>> SavedNameColoursAsync() => Task.FromResult<IReadOnlyDictionary<string, uint>>(new Dictionary<string, uint>());

    public Task<uint?> OwnNameColourAsync() => Task.FromResult<uint?>(null);

    public Task<int> InviteOffersForAsync(RightClickKind kind) => Task.FromResult(0);

    public Task InviteAsync(string channelId, string name, string world) => Task.CompletedTask;

    public Task<JsonElement> ReadSettingAsync(SelfTestSetting setting) => Task.FromResult(this.Settings[setting]);

    public Task WriteSettingAsync(SelfTestSetting setting, JsonElement value) {
        this.Settings[setting] = value.Clone();
        return Task.CompletedTask;
    }

    public Task<int?> GameChannelAsync() => Task.FromResult(this.GameChannel);

    public Task SetGameChannelAsync(int chatType) {
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
        return Task.CompletedTask;
    }

    /// <summary>Called by a test that bypasses the host's own check, to prove the runner's check alone is enough.</summary>
    public void RecordIfOutside(string line) {
        if (!PrivateArea.Judge(this.Place).Private) {
            this.TypedOutside.Add(line);
        }
    }

    public long Mark() => 0;

    public IReadOnlyList<TapLine> PrintedSince(long mark) => [];

    public IReadOnlyList<GameChatLine> GameChatSince(long mark) => [];

    public LogTapCounts LogCounts() => new(0, 0, 0, 0, 0);

    public Task<StickyView> StickyAsync() => Task.FromResult(new StickyView(this.Sticky, null, null, null));

    public Task StopTalkingAsync() {
        this.Sticky = null;
        return Task.CompletedTask;
    }

    public Task<bool> PrivacyWindowOpenAsync() => Task.FromResult(false);

    public Task OpenPrivacyWindowAsync() => Task.CompletedTask;

    public int LocalSends => 0;

    public Task<CommandFacts> CommandAsync(string command) => Task.FromResult(new CommandFacts(true, true, true, "help"));

    public Task RunPluginCommandAsync(string command, string arguments) => Task.CompletedTask;

    public Task<bool> DebugWindowOpenAsync() => Task.FromResult(false);

    public Task ShowSettingsAsync(bool open) => Task.CompletedTask;

    public Task<WindowsView> WindowsAsync() => Task.FromResult(new WindowsView([], new Dictionary<string, int>(), new Dictionary<string, int>()));

    public Task OpenWindowAsync(string channelId) => Task.CompletedTask;

    public Task SelectTabAsync(string layoutId, string channelId) => Task.CompletedTask;

    public Task UseWindowAsync(string layoutId) => Task.CompletedTask;

    public Task MoveWindowAsync(string layoutId, float x, float y) => Task.CompletedTask;

    public Task<HistoryView> HistoryAsync() => Task.FromResult(new HistoryView(false, false, null, 0, 50));

    public Task ForgetHistoryAsync(IReadOnlyCollection<string> channelIds) {
        this.Forgotten.Add(channelIds.ToList());
        return Task.CompletedTask;
    }

    public Task DeleteHistoryAsync() {
        this.Deleted++;
        return Task.CompletedTask;
    }

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
