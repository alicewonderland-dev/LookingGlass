using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// The fake host with the plugin's real chat history, if a test wants it (<see cref="UseRealHistory"/>): a
/// <see cref="ChannelHistory"/> recording to a real <see cref="ChatLog"/> in the host's temp folder, its text files too while
/// both settings are on. Simulated lines go to it, and a rename says so in it, as the plugin's own do; each is written
/// before the call returns (the plugin writes a moment later: the checks wait for it either way).
/// </summary>
internal sealed partial class FakeSelfTestHost {
    private ChannelHistory? _history;
    private ChatLog? _chatLog;

    /// <summary>Who a simulated line is from.</summary>
    private static readonly User Someone = new() { UserId = 7, Name = "Bob Hatter", WorldId = 41, WorldName = "Lich" };

    /// <summary>The real chat log, once <see cref="UseRealHistory"/> was called.</summary>
    public ChatLog? ChatLog => this._chatLog;

    /// <summary>Records to a real chat history from now on.</summary>
    public void UseRealHistory() {
        this._chatLog = new ChatLog(new ChatLogOptions {
            Folder = Path.Combine(this._folder, "chatlog"),
            Protection = new XorProtection(),
            Channels = this.LabelOf,
            TimeZone = this.TimeZone,
        });
        this._history = new ChannelHistory(time: new HostClock(this));
        this._history.Clear(this._chatLog);
    }

    private ChannelLabel LabelOf(string channelId) {
        lock (this._lock) {
            var slot = this.Slots.TryGetValue(channelId, out var found) ? found : (int?) null;
            return new ChannelLabel(ChannelTag.For(slot, null, false), this.Channels.FirstOrDefault(channel => channel.Id == channelId)?.Name, slot);
        }
    }

    /// <summary>The channels' names as the plugin's session has them now (what its history notes renames against).</summary>
    private SessionSnapshot Snapshot() => SessionSnapshot.Empty with {
        State = ConnectionState.Ready,
        ChannelsLoaded = true,
        Channels = [.. this.Channels.Select(channel => new ChannelView(channel.Id, channel.Name, 0, 0, true, false, Rank.Admin, []))],
    };

    private HistoryView RealHistoryView() =>
        new(this.Is(SelfTestSetting.KeepHistory), this.Is(SelfTestSetting.KeepHistoryUnencrypted), this._chatLog!.TextFolder, this._chatLog.Size, 50);

    private async Task SimulateIncomingRealAsync(string channelId, string text) {
        this._history!.NoteNames(this.Snapshot());
        this._history.Add(new IncomingMessage(channelId, null, Someone, false, text, false, this.Now));
        await this._chatLog!.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Run when a channel is renamed, before the real history has written anything of it: something in the way on purpose.</summary>
    public Action? BeforeRename { get; set; }

    private async Task NoteRenameAsync() {
        if (this._history != null) {
            this.BeforeRename?.Invoke();
            this._history.NoteNames(this.Snapshot());
            await this._chatLog!.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>As the plugin does when the settings change: its text files only while the history and they are both on.</summary>
    private void HistorySettingChanged() =>
        this._chatLog?.SetPlainText(this.Is(SelfTestSetting.KeepHistory) && this.Is(SelfTestSetting.KeepHistoryUnencrypted));

    private void DisposeRealHistory() => this._chatLog?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class HostClock(FakeSelfTestHost host) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => host.Now;
    }

    private sealed class XorProtection : IAtRestProtection {
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Select(b => (byte) (b ^ 0x5A))];

        public byte[] Unprotect(byte[] data) => [.. data.Select(b => (byte) (b ^ 0x5A))];
    }
}
