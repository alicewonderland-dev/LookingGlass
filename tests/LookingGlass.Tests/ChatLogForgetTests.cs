using System.Collections.Concurrent;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Removing one channel's lines from the chat history (<see cref="ChatLog.ForgetChannelAsync"/>), for the self-test's
/// clean-up: its records and its text folder go, and nothing of another channel's.
/// </summary>
public sealed class ChatLogForgetTests : IDisposable {
    private const ulong Alice = 0x0040_0000_0000_0001;
    private const string Url = "ws://LookingGlassChat:5180/ws";

    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldId = 41, WorldName = "Lich" };
    private static readonly DateTimeOffset Evening = new(2026, 10, 10, 21, 3, 0, TimeSpan.Zero);

    private static int _sequence;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-chatforget-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly List<ChatLog> _open = [];
    private readonly Dictionary<string, ChannelLabel> _labels = new() {
        ["test"] = new ChannelLabel("[LGC50]", "LG self-test K7Q2", 50),
        ["sky"] = new ChannelLabel("[sky]", "Sky Pirates", 1),
        ["fc"] = new ChannelLabel("[LGC2]", "Free Company", 2),
    };

    public void Dispose() {
        foreach (var log in this._open) {
            log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DeleteDirectory(this._directory);
    }

    private string Folder => ChatLogFiles.Folder(this._directory, Alice, Url);

    private ChatLog Open(long maxBytes = 50L * 1024 * 1024) {
        var log = new ChatLog(new ChatLogOptions {
            Folder = this.Folder,
            Protection = new TestProtection(),
            MaxBytes = maxBytes,
            Log = this._diagnostics.Enqueue,
            PlainText = true,
            Channels = id => this._labels[id],
            TimeZone = TimeZoneInfo.Utc,
        });
        this._open.Add(log);
        return log;
    }

    private async Task Close(ChatLog log) {
        await log.DisposeAsync();
        this._open.Remove(log);
    }

    private static HistoryLine Line(string channelId, string text) {
        var at = Evening.AddSeconds(Interlocked.Increment(ref _sequence));
        return new HistoryLine(1, channelId, HistoryLineKind.Message, at) { Sender = Bob, SentAt = at, Message = LinkedText.Plain(text) };
    }

    private static async Task<List<string?>> All(ChatLog log, string channelId) {
        var lines = new List<HistoryLine>();
        ChatLogPosition? before = null;
        do {
            var page = await log.ReadOlderAsync(channelId, before);
            lines.InsertRange(0, page.Lines);
            before = page.Next;
        } while (before != null);

        return lines.Select(line => line.Message?.Text).ToList();
    }

    private string[] TextFolders() => Directory.Exists(ChatLogFiles.TextFolder(this.Folder))
        ? Directory.GetDirectories(ChatLogFiles.TextFolder(this.Folder)).Select(path => Path.GetFileName(path)).ToArray()
        : [];

    [Fact]
    public async Task OnlyThatChannelsLinesAndTextFilesGo() {
        var log = this.Open();
        foreach (var (channel, text) in new[] { ("sky", "s1"), ("test", "t1"), ("fc", "f1"), ("test", "t2"), ("sky", "s2"), ("test", "t3"), ("fc", "f2") }) {
            log.Record(Line(channel, text));
        }

        await log.FlushAsync();
        Assert.Equal(3, this.TextFolders().Length);

        Assert.Equal(3, await log.ForgetChannelAsync("test"));

        Assert.Equal(2, this.TextFolders().Length);
        Assert.DoesNotContain(this.TextFolders(), name => name.Contains("self-test"));
        await this.Close(log);

        var next = this.Open();
        Assert.Empty(await All(next, "test"));
        Assert.Equal(["s1", "s2"], await All(next, "sky"));
        Assert.Equal(["f1", "f2"], await All(next, "fc"));
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task ThisSessionsLinesStayThisSessionsAfterwards() {
        var first = this.Open();
        first.Record(Line("sky", "s1"));
        first.Record(Line("test", "t1"));
        await this.Close(first);

        // A new session: its lines come after where it started, and earlier reads see only what came before.
        var second = this.Open();
        Assert.Equal(["s1"], await All(second, "sky"));
        second.Record(Line("test", "t2"));
        second.Record(Line("sky", "s2"));
        Assert.Equal(2, await second.ForgetChannelAsync("test"));

        Assert.Equal(["s1"], await All(second, "sky"));
        Assert.Empty(await All(second, "test"));
        // Lines added after it go on as before.
        second.Record(Line("sky", "s3"));
        await this.Close(second);

        var third = this.Open();
        Assert.Equal(["s1", "s2", "s3"], await All(third, "sky"));
        Assert.Empty(await All(third, "test"));
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task SeveralSegmentsAndAChannelWithNoLines() {
        // A small limit makes small segments.
        var log = this.Open(maxBytes: 5L * 1024 * 1024);
        for (var i = 0; i < 2000; i++) {
            log.Record(Line(i % 4 == 0 ? "test" : "sky", $"line {i} " + new string('x', 200)));
        }

        await log.FlushAsync();
        var segments = Directory.GetFiles(this.Folder, "*.lgl").Length;
        Assert.True(segments > 1, $"Only {segments} segment.");

        Assert.Equal(0, await log.ForgetChannelAsync("fc"));
        Assert.Equal(500, await log.ForgetChannelAsync("test"));
        await this.Close(log);

        var next = this.Open(maxBytes: 5L * 1024 * 1024);
        Assert.Empty(await All(next, "test"));
        Assert.Equal(1500, (await All(next, "sky")).Count);
    }

    private sealed class TestProtection : IAtRestProtection {
        public byte[] Protect(byte[] plaintext) => [.. "TP"u8, .. plaintext.Select(b => (byte) (b ^ 0x5A))];

        public byte[] Unprotect(byte[] data) => data.Length > 2 && data[0] == 'T' && data[1] == 'P'
            ? data[2..].Select(b => (byte) (b ^ 0x5A)).ToArray()
            : throw new System.Security.Cryptography.CryptographicException("Not protected here.");
    }

    [Fact]
    public async Task SegmentsKeepTheirTimeAndOtherWindowsReadTheirOlderLinesAgain() {
        var first = this.Open();
        first.Record(Line("sky", "s1"));
        first.Record(Line("test", "t1"));
        await this.Close(first);
        var segment = Directory.GetFiles(this.Folder, "*.lgl").Single();
        var old = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(segment, old);

        var log = this.Open();
        var earlier = log.Earlier("sky");
        await earlier.LoadMoreAsync();
        var version = log.Version;
        Assert.Equal(1, await log.ForgetChannelAsync("test"));

        // The size limit goes by a segment's time: the oldest stays the oldest.
        Assert.Equal(old, File.GetLastWriteTimeUtc(segment));
        // Records moved: every window's place in the older lines starts over, and reads them right.
        Assert.True(log.Version > version);
        var again = log.Earlier("sky");
        Assert.NotSame(earlier, again);
        await again.LoadMoreAsync();
        Assert.Equal(["s1"], again.Lines.Select(line => line.Message?.Text));
    }

    [Fact]
    public async Task ASegmentHalfWrittenAgainIsClearedAndNeverCounted() {
        var first = this.Open();
        first.Record(Line("sky", "s1"));
        await this.Close(first);
        // A crash between writing the new segment and putting it in place.
        var stale = Directory.GetFiles(this.Folder, "*.lgl").Single() + ChatLogStore.ForgetExtension;
        await File.WriteAllBytesAsync(stale, new byte[100_000], TestContext.Current.CancellationToken);
        var withStale = ChatLogFiles.FolderSize(this.Folder);
        Assert.True(withStale < 100_000, "A segment half written again was counted.");

        var log = this.Open();
        Assert.Equal(["s1"], await All(log, "sky"));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task AClosedLogSaysSo() {
        var log = this.Open();
        await this.Close(log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => log.ForgetChannelAsync("test"));
    }
}
