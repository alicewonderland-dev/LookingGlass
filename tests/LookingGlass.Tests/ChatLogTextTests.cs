using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The chat history kept unencrypted (opt-in, beside the encrypted one): new lines written to plain text files other tools
/// can open, one per channel per month, under the same size limit and the same "Delete my chat log". See "Chat log on this
/// computer" in docs/design.md.
/// </summary>
public sealed class ChatLogTextTests : IDisposable {
    private const ulong Alice = 0x0040_0000_0000_0001;
    private const string Url = "ws://LookingGlassChat:5180/ws";
    private const string OtherUrl = "wss://chat.example/ws";

    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldId = 40, WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldId = 41, WorldName = "Lich" };
    private static readonly DateTimeOffset Evening = new(2026, 10, 10, 21, 3, 0, TimeSpan.Zero);

    private static int _sequence;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-chattext-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly List<ChatLog> _open = [];
    private readonly ConcurrentDictionary<string, ChannelLabel> _labels = new() {
        ["aaa"] = new ChannelLabel("[sky]", "Sky Pirates", 3),
        ["bbb"] = new ChannelLabel("[LGC4]", "Sky Pirates", 4),
    };

    public void Dispose() {
        foreach (var log in this._open) {
            log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DeleteDirectory(this._directory);
    }

    private string Folder(ulong contentId = Alice, string url = Url) => ChatLogFiles.Folder(this._directory, contentId, url);

    private string TextRoot(string? folder = null) => ChatLogFiles.TextFolder(folder ?? this.Folder());

    private ChannelLabel LabelOf(string channelId) => this._labels.TryGetValue(channelId, out var label) ? label : new ChannelLabel(ChannelTag.Fallback);

    private ChatLog Open(string? folder = null, long maxBytes = 50L * 1024 * 1024, bool plainText = true, IAtRestProtection? protection = null) {
        var log = new ChatLog(new ChatLogOptions {
            Folder = folder ?? this.Folder(),
            Protection = protection ?? new TestProtection(),
            MaxBytes = maxBytes,
            Log = this._diagnostics.Enqueue,
            PlainText = plainText,
            Channels = this.LabelOf,
            TimeZone = TimeZoneInfo.Utc,
        });
        this._open.Add(log);
        return log;
    }

    private async Task Close(ChatLog log) {
        await log.DisposeAsync();
        this._open.Remove(log);
    }

    /// <summary>The folder of a channel's text files, as named for its label now.</summary>
    private string ChannelFolder(string channelId, string? folder = null) {
        var label = this.LabelOf(channelId);
        return Path.Combine(this.TextRoot(folder), ChatLogText.ChannelFolderName(channelId, label.Name, label.Slot));
    }

    private string[] LinesOf(string channelId, string month, string? folder = null) {
        var path = Path.Combine(this.ChannelFolder(channelId, folder), month + ".txt");
        return File.ReadAllText(path, Encoding.UTF8).Split("\r\n")[..^1];
    }

    private static HistoryLine Message(string channelId, User sender, string text, DateTimeOffset at, bool own = false) => new(1, channelId, HistoryLineKind.Message, at) {
        Sender = sender,
        IsOwn = own,
        SentAt = at.AddMilliseconds(-Interlocked.Increment(ref _sequence)),
        Message = LinkedText.Plain(text),
    };

    private static HistoryLine Notice(string channelId, string technical, string plain, DateTimeOffset at, NoticeLevel level = NoticeLevel.Info) =>
        new(1, channelId, HistoryLineKind.Notice, at) {
            Notice = new SessionNotice(level, technical, channelId) { Plain = plain },
            Tone = NoticeColours.ToneOf(level, NoticeKind.General),
        };

    // ================================================================ what a line says

    [Fact]
    public void EachLineReadsAsTheWindowsShowIt() {
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("[2026-10-10 21:03] [sky] Alice Liddell@Twintania: pulling at 9",
            ChatLogText.Format(Message("aaa", Me, "pulling at 9", Evening, own: true), "[sky]", zone));

        // Links as their "[name]" text.
        var potion = Message("aaa", Bob, "", Evening.AddMinutes(1)) with {
            Message = new LinkedText("ok [Potion]", [new MessageLink(3, 8, new ChatLink.Item(4551))]),
        };
        Assert.Equal("[2026-10-10 21:04] [sky] Bob Hatter@Lich: ok [Potion]", ChatLogText.Format(potion, "[sky]", zone));

        // Information lines in simple mode's words, whichever mode is set.
        Assert.Equal("[2026-10-10 21:05] [sky] Bob Hatter joined the channel.",
            ChatLogText.Format(Notice("aaa", "Bob Hatter@Lich joined (key 1A2B).", "Bob Hatter joined the channel.", Evening.AddMinutes(2)), "[sky]", zone));

        // Caught up: when it was sent, as the windows show it, and when it arrived.
        var caughtUp = Message("aaa", Bob, "while you were away", Evening) with { SentAt = Evening.AddMinutes(-48), CaughtUp = true };
        Assert.Equal("[2026-10-10 20:15, arrived 21:03] [sky] Bob Hatter@Lich: while you were away", ChatLogText.Format(caughtUp, "[sky]", zone));
        var daysLater = caughtUp with { Time = Evening.AddDays(2) };
        Assert.Equal("[2026-10-10 20:15, arrived 2026-10-12 21:03] [sky] Bob Hatter@Lich: while you were away",
            ChatLogText.Format(daysLater, "[sky]", zone));

        var unsupported = Message("aaa", Bob, "", Evening) with { Message = null, Unsupported = true };
        Assert.Equal("[2026-10-10 21:03] [sky] Bob Hatter@Lich: (a message type this version can't show)", ChatLogText.Format(unsupported, "[sky]", zone));

        // Local time: the computer's zone.
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        Assert.Equal("[2026-10-10 23:03] [LGC3] Alice Liddell@Twintania: late", ChatLogText.Format(Message("aaa", Me, "late", Evening), "[LGC3]", plusTwo));
        Assert.Equal("2026-11", ChatLogText.MonthOf(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero), plusTwo));
        Assert.Equal("2026-10", ChatLogText.MonthOf(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void RemoteTextNeverBreaksALineOrAddsOne() {
        // Everything someone else wrote, made to look like more lines: it stays one line, without anything invisible.
        var forged = "hi\r\n[2026-10-10 21:04] [sky] Alice Liddell@Twintania: I owe Bob 1M gil\u2028[x]\u2029[y]\u0085[z]\u000b\u000c\u001b[31m\u0002\u202e\u200b\tend";
        var line = Message("aaa", new User { UserId = 9, Name = "Mallory\nEvil", WorldName = "Lich\r\n[fake]" }, forged, Evening) with {
            Message = new LinkedText(forged + " [Po\ntion]", [new MessageLink(forged.Length + 1, 9, new ChatLink.Item(4551))]),
        };
        var text = ChatLogText.Format(line, "[sky]", TimeZoneInfo.Utc);
        Assert.StartsWith("[2026-10-10 21:03] [sky] Mallory Evil@Lich  [fake]: hi  [2026-10-10 21:04]", text);
        Assert.EndsWith("end [Po tion]", text);
        foreach (var c in text) {
            Assert.False(char.IsControl(c), $"U+{(int) c:X4} in a line");
            Assert.NotEqual(System.Globalization.UnicodeCategory.LineSeparator, char.GetUnicodeCategory(c));
            Assert.NotEqual(System.Globalization.UnicodeCategory.ParagraphSeparator, char.GetUnicodeCategory(c));
            Assert.NotEqual(System.Globalization.UnicodeCategory.Format, char.GetUnicodeCategory(c));
        }

        // A notice holds others' words too (a name, a channel's name).
        var notice = Notice("aaa", "x", "Bob\njoined\u2028the channel.", Evening);
        Assert.Equal("[2026-10-10 21:03] [sky] Bob joined the channel.", ChatLogText.Format(notice, "[sky]", TimeZoneInfo.Utc));
    }

    // ================================================================ channel folders

    [Fact]
    public void ChannelFoldersAreReadableAndStableAndNeverMix() {
        var hash = ChatLogText.ChannelHash("aaa");
        Assert.Matches("^[0-9a-f]{8}$", hash);
        Assert.Equal(hash, ChatLogText.ChannelHash("aaa"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData("aaa"u8))[..8].ToLowerInvariant(), hash);
        Assert.Equal($"Sky Pirates (LGC3) {hash}", ChatLogText.ChannelFolderName("aaa", "Sky Pirates", 3));
        Assert.Equal($"Sky Pirates {hash}", ChatLogText.ChannelFolderName("aaa", "Sky Pirates", null));
        // Its name not known yet.
        Assert.Equal($"Channel (LGC3) {hash}", ChatLogText.ChannelFolderName("aaa", null, 3));
        // Two channels with the same name (and number, after a change) never share a folder.
        Assert.NotEqual(ChatLogText.ChannelFolderName("aaa", "Sky Pirates", 3), ChatLogText.ChannelFolderName("bbb", "Sky Pirates", 3));
    }

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("aux .backup", "_aux .backup")]
    [InlineData("Console", "Console")]
    [InlineData("Dots...  ", "Dots")]
    [InlineData("   leading", "leading")]
    [InlineData("...", "Channel")]
    [InlineData("  ", "Channel")]
    [InlineData("", "Channel")]
    [InlineData("line\nbreak\ttab\u0002x\u202e", "line break tabx")]
    [InlineData("many     spaces", "many spaces")]
    [InlineData("\ue05dicon", "icon")]
    [InlineData("Ünïcødé Sky ☆", "Ünïcødé Sky ☆")]
    public void ChannelFolderNamesAreSafeOnEveryFileSystem(string name, string expected) {
        var folder = ChatLogText.ChannelFolderName("aaa", name, null);
        Assert.Equal($"{expected} {ChatLogText.ChannelHash("aaa")}", folder);

        // Windows takes it as it is: made, found, and named so.
        Directory.CreateDirectory(this._directory);
        var path = Path.Combine(this._directory, folder);
        Directory.CreateDirectory(path);
        Assert.Equal(folder, Path.GetFileName(Directory.GetDirectories(this._directory).Single(dir => Path.GetFileName(dir) == folder)));
        Directory.Delete(path);
    }

    [Fact]
    public void ALongNameIsCutAndTheFolderStaysShort() {
        var name = string.Concat(Enumerable.Repeat("Sky😀", 40));
        var folder = ChatLogText.ChannelFolderName("aaa", name, 50);
        var label = folder[..^" (LGC50) 12345678".Length];
        Assert.InRange(label.Length, 1, ChatLogText.MaxLabelLength);
        // Never half a character.
        Assert.False(char.IsHighSurrogate(label[^1]));
        Assert.EndsWith($" (LGC50) {ChatLogText.ChannelHash("aaa")}", folder);
        Directory.CreateDirectory(Path.Combine(this._directory, folder));
    }

    // ================================================================ writing

    [Fact]
    public async Task NewLinesGoToOneFilePerChannelPerMonth() {
        var log = this.Open();
        var lastOfSeptember = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
        log.Record(Message("aaa", Bob, "end of the month", lastOfSeptember));
        log.Record(Message("aaa", Me, "a new month", lastOfSeptember.AddMinutes(2), own: true));
        log.Record(Message("bbb", Bob, "the other Sky Pirates", lastOfSeptember.AddMinutes(3)));
        log.Record(Notice("aaa", "Bob Hatter@Lich left.", "Bob Hatter left the channel.", lastOfSeptember.AddMinutes(4)));
        await log.FlushAsync();

        Assert.Equal(["[2026-09-30 23:59] [sky] Bob Hatter@Lich: end of the month"], this.LinesOf("aaa", "2026-09"));
        Assert.Equal(["[2026-10-01 00:01] [sky] Alice Liddell@Twintania: a new month", "[2026-10-01 00:03] [sky] Bob Hatter left the channel."],
            this.LinesOf("aaa", "2026-10"));
        Assert.Equal(["[2026-10-01 00:02] [LGC4] Bob Hatter@Lich: the other Sky Pirates"], this.LinesOf("bbb", "2026-10"));
        Assert.Equal(2, Directory.GetDirectories(this.TextRoot()).Length);

        // UTF-8 without a byte order mark, Windows line ends: Notepad, search tools and backups read it as it is.
        var bytes = File.ReadAllBytes(Path.Combine(this.ChannelFolder("aaa"), "2026-10.txt"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("\r\n"u8.ToArray(), bytes[^2..]);

        // The encrypted history keeps them as it always has.
        await this.Close(log);
        Assert.Equal(3, (await All(this.Open(plainText: false), "aaa")).Count);
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task OnlyTheLinesTheEncryptedHistoryKeepsAreWritten() {
        var log = this.Open();
        var history = new ChannelHistory();
        history.Clear(log);
        history.Add(new IncomingMessage("aaa", "Sky Pirates", Bob, false, "hello", false, Evening));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob Hatter@Lich joined.", "aaa") { Plain = "Bob Hatter joined." });
        history.AddCaughtUp(new CaughtUpMessages("aaa", "Sky Pirates", [new IncomingMessage("aaa", "Sky Pirates", Bob, false, "missed", false, Evening.AddMinutes(-5))]));
        // Not kept: warnings (about that moment) and feedback (about what was typed then).
        history.AddNotice(SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob Hatter"), "aaa"));
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent: you're sending too fast.");
        await log.FlushAsync();

        var month = ChatLogText.MonthOf(DateTimeOffset.Now, TimeZoneInfo.Utc);
        var lines = this.LinesOf("aaa", month);
        Assert.Equal(4, lines.Length);
        Assert.EndsWith("[sky] Bob Hatter@Lich: hello", lines[0]);
        Assert.EndsWith("[sky] Bob Hatter joined.", lines[1]);
        Assert.EndsWith("[sky] " + CatchUpChat.WindowSeparator(1), lines[2]);
        Assert.Contains(", arrived ", lines[3]);
        Assert.EndsWith("Bob Hatter@Lich: missed", lines[3]);
        Assert.DoesNotContain(lines, line => line.Contains("Not sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurnedOnOnlyNewLinesAreWrittenAndTurnedOffNoMore() {
        // An earlier session, kept encrypted only.
        var earlier = this.Open(plainText: false);
        earlier.Record(Message("aaa", Bob, "from last week", Evening.AddDays(-7)));
        await this.Close(earlier);

        var log = this.Open(plainText: false);
        log.Record(Message("aaa", Bob, "before turning it on", Evening));
        await log.FlushAsync();
        // Nothing already kept is converted, now or when it is turned on.
        Assert.False(Directory.Exists(this.TextRoot()));
        log.SetPlainText(true);
        Assert.True(log.PlainText);
        await log.FlushAsync();
        Assert.False(Directory.Exists(this.TextRoot()));

        log.Record(Message("aaa", Bob, "while on", Evening.AddMinutes(1)));
        log.SetPlainText(false);
        log.Record(Message("aaa", Bob, "after turning it off", Evening.AddMinutes(2)));
        await log.FlushAsync();
        Assert.Equal(["[2026-10-10 21:04] [sky] Bob Hatter@Lich: while on"], this.LinesOf("aaa", "2026-10"));

        // Off, the text files stay as they are, until deleted; the encrypted history has everything.
        await this.Close(log);
        Assert.Single(this.LinesOf("aaa", "2026-10"));
        Assert.Equal(4, (await All(this.Open(plainText: false), "aaa")).Count);
    }

    [Fact]
    public async Task ARenamedChannelKeepsItsFolder() {
        var log = this.Open();
        log.Record(Message("aaa", Bob, "under the old name", Evening));
        await log.FlushAsync();
        var before = this.ChannelFolder("aaa");
        Assert.True(Directory.Exists(before));

        // Renamed, and given another number: its folder is renamed with it, and its history goes on in it.
        this._labels["aaa"] = new ChannelLabel("[sky]", "Mad Sky", 5);
        log.Record(Message("aaa", Bob, "under the new name", Evening.AddMinutes(1)));
        await log.FlushAsync();
        Assert.False(Directory.Exists(before));
        Assert.Equal(Path.GetFileName(this.ChannelFolder("aaa")), $"Mad Sky (LGC5) {ChatLogText.ChannelHash("aaa")}");
        Assert.Equal(2, this.LinesOf("aaa", "2026-10").Length);

        // Its name not known for a moment (just left, say): the folder stays as it is.
        this._labels["aaa"] = new ChannelLabel(ChannelTag.Fallback);
        log.Record(Notice("aaa", "You left.", "You left the channel.", Evening.AddMinutes(2)));
        await log.FlushAsync();
        this._labels["aaa"] = new ChannelLabel("[sky]", "Mad Sky", 5);
        Assert.Equal(3, this.LinesOf("aaa", "2026-10").Length);
        Assert.Single(Directory.GetDirectories(this.TextRoot()));

        // The next session finds it by the channel, whatever it is called.
        await this.Close(log);
        this._labels["aaa"] = new ChannelLabel("[sky]", "Sky Again", 5);
        var next = this.Open();
        next.Record(Message("aaa", Bob, "next session", Evening.AddMinutes(3)));
        await next.FlushAsync();
        Assert.Single(Directory.GetDirectories(this.TextRoot()));
        Assert.Equal(4, this.LinesOf("aaa", "2026-10").Length);
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task ALineCutShortByACrashIsLeftOnALineOfItsOwn() {
        var log = this.Open();
        log.Record(Message("aaa", Bob, "whole", Evening));
        await this.Close(log);
        var path = Path.Combine(this.ChannelFolder("aaa"), "2026-10.txt");
        File.AppendAllText(path, "[2026-10-10 21:0", new UTF8Encoding(false));

        var again = this.Open();
        again.Record(Message("aaa", Bob, "after the crash", Evening.AddMinutes(5)));
        await again.FlushAsync();
        Assert.Equal(["[2026-10-10 21:03] [sky] Bob Hatter@Lich: whole", "[2026-10-10 21:0", "[2026-10-10 21:08] [sky] Bob Hatter@Lich: after the crash"],
            this.LinesOf("aaa", "2026-10"));
        Assert.Equal(ChatLogFiles.FolderSize(this.Folder()), again.Size);
    }

    // ================================================================ one limit

    [Fact]
    public async Task TheSizeShownCountsTheTextFiles() {
        var log = this.Open();
        for (var i = 0; i < 50; i++) {
            log.Record(Message(i % 2 == 0 ? "aaa" : "bbb", Bob, $"line {i}", Evening.AddMinutes(i)));
        }

        await log.FlushAsync();
        var text = Directory.GetFiles(this.TextRoot(), "*.txt", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        var encrypted = Directory.GetFiles(this.Folder()).Sum(file => new FileInfo(file).Length);
        Assert.True(text > 0);
        Assert.Equal(text + encrypted, ChatLogFiles.FolderSize(this.Folder()));
        Assert.Equal(text + encrypted, ChatLogFiles.TotalSize(this._directory));
        Assert.Equal(text + encrypted, log.Size);
    }

    [Fact]
    public async Task TogetherTheyStayUnderTheLimitAndTheOldestGoFirstFromEither() {
        const long limit = 64 * 1024;
        var log = this.Open(maxBytes: limit);
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var at = start;
        for (var i = 1; i <= 700; i++) {
            at = start.AddHours(4 * i);
            log.Record(Message(i % 3 == 0 ? "bbb" : "aaa", Bob, $"message number {i} with some words to take up room", at));
            if (i % 50 == 0) {
                await log.FlushAsync();
                Assert.InRange(ChatLogFiles.FolderSize(this.Folder()), 1, limit);
                Assert.Equal(ChatLogFiles.FolderSize(this.Folder()), log.Size);
            }
        }

        await log.FlushAsync();
        var months = Directory.GetFiles(this.TextRoot(), "*.txt", SearchOption.AllDirectories).Select(file => Path.GetFileNameWithoutExtension(file)).Distinct().Order().ToList();
        // The oldest months went, whole; the newest is there, up to the last line.
        Assert.DoesNotContain("2026-08", months);
        Assert.Equal(ChatLogText.MonthOf(at, TimeZoneInfo.Utc), months[^1]);
        Assert.EndsWith("message number 700 with some words to take up room", this.LinesOf("aaa", months[^1]).Last());
        Assert.EndsWith("message number 699 with some words to take up room", this.LinesOf("bbb", months[^1]).Last());

        // The encrypted history wasn't pushed out by the text: both reach back about as far. The text keeps whole months, so
        // it may reach back a little further, by up to a month.
        await this.Close(log);
        var encrypted = await All(this.Open(maxBytes: limit, plainText: false), "aaa");
        Assert.True(encrypted.Count > 50, $"only {encrypted.Count} lines left in the encrypted history");
        Assert.Equal("message number 700 with some words to take up room", encrypted[^1].Message!.Text);
        var oldestText = DateTimeOffset.ParseExact(months[0] + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        var oldestEncrypted = encrypted[0].Time;
        Assert.InRange(oldestEncrypted, oldestText.AddDays(-5), oldestText.AddDays(36));
    }

    [Fact]
    public async Task LoweringTheLimitTrimsBoth() {
        var log = this.Open(maxBytes: 256 * 1024);
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 800; i++) {
            log.Record(Message("aaa", Bob, $"message number {i} with some words to take up room", start.AddMinutes(270 * i)));
        }

        await log.FlushAsync();
        var monthsBefore = Directory.GetFiles(this.ChannelFolder("aaa"), "*.txt").Length;
        Assert.Equal(5, monthsBefore);
        Assert.True(log.Size > 128 * 1024);

        log.SetLimit(64 * 1024);
        await log.FlushAsync();
        Assert.InRange(log.Size, 1, 64 * 1024);
        Assert.InRange(ChatLogFiles.FolderSize(this.Folder()), 1, 64 * 1024);
        var monthsAfter = Directory.GetFiles(this.ChannelFolder("aaa"), "*.txt").Select(file => Path.GetFileNameWithoutExtension(file)).Order().ToList();
        Assert.True(monthsAfter.Count < monthsBefore);
        Assert.EndsWith("message number 800 with some words to take up room", this.LinesOf("aaa", monthsAfter[^1]).Last());
        await this.Close(log);
        Assert.Equal("message number 800 with some words to take up room", (await All(this.Open(plainText: false), "aaa")).Last().Message!.Text);
    }

    [Fact]
    public async Task TextFilesKeptBeforeCountAndGoOldestFirstWhileItIsOff() {
        // Text written in August, then turned off: those files still count, and go when they are the oldest.
        var log = this.Open(maxBytes: 64 * 1024);
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 60; i++) {
            log.Record(Message("aaa", Bob, $"august {i} with some words to take up room", start.AddHours(i)));
        }

        log.SetPlainText(false);
        for (var i = 1; i <= 600; i++) {
            log.Record(Message("aaa", Bob, $"september {i} with some words to take up room", start.AddDays(31).AddHours(i)));
        }

        await log.FlushAsync();
        Assert.InRange(log.Size, 1, 64 * 1024);
        Assert.False(File.Exists(Path.Combine(this.ChannelFolder("aaa"), "2026-08.txt")));
    }

    // ================================================================ one delete

    [Fact]
    public async Task DeletingDeletesTheTextFilesToo() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection(), this._diagnostics.Enqueue);
        var other = keeper.Open(Alice, OtherUrl, ChatLogLimits.Bytes(50), plainText: true, channels: this.LabelOf);
        other.Record(Message("aaa", Bob, "at the other address", DateTimeOffset.Now));
        var log = keeper.Open(Alice, Url, ChatLogLimits.Bytes(50), plainText: true, channels: this.LabelOf);
        log.Record(Message("aaa", Bob, "before deleting", DateTimeOffset.Now));
        await log.FlushAsync();
        Assert.True(Directory.Exists(ChatLogFiles.TextFolder(this.Folder(Alice, OtherUrl))));
        Assert.True(Directory.Exists(this.TextRoot()));
        Assert.Equal(this.TextRoot(), log.TextFolder);

        await keeper.DeleteAllAsync();
        Assert.Empty(ChatLogFiles.Folders(this._directory));
        Assert.Equal(0, await keeper.SizeAsync());

        // Still on: what comes next is written, and nothing from before.
        log.Record(Message("aaa", Bob, "after deleting", DateTimeOffset.Now));
        await log.FlushAsync();
        var files = Directory.GetFiles(this.TextRoot(), "*.txt", SearchOption.AllDirectories);
        var line = Assert.Single(files.SelectMany(File.ReadAllLines));
        Assert.EndsWith("after deleting", line);
    }

    [Fact]
    public async Task DeletingNeverFollowsALinkOutOfTheFolder() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        var log = this.Open();
        log.Record(Message("aaa", Bob, "x", Evening));
        await this.Close(log);
        var size = ChatLogFiles.FolderSize(this.Folder());
        var outside = Path.Combine(this._directory, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not a chat log");
        var link = Path.Combine(this.TextRoot(), "link 12345678");
        try {
            Directory.CreateSymbolicLink(link, outside);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A symbolic link needs developer mode or an administrator; a junction doesn't.
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            mklink?.WaitForExit(10_000);
            if (!Directory.Exists(link)) {
                return;
            }
        }

        // Neither counted nor followed.
        Assert.Equal(size, ChatLogFiles.FolderSize(this.Folder()));
        var again = this.Open();
        again.Record(Message("aaa", Bob, "y", Evening.AddMinutes(1)));
        await again.FlushAsync();
        await again.DeleteAsync();
        Assert.False(Directory.Exists(this.Folder()));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    // ================================================================ never in the way

    [Fact]
    public async Task TheTextFilesAreWrittenWhenTheEncryptedHistoryCantBeReadHere() {
        var log = this.Open(plainText: false);
        log.Record(Message("aaa", Bob, "from the old computer", Evening));
        await this.Close(log);
        var before = Directory.GetFiles(this.Folder()).ToDictionary(file => file, File.ReadAllBytes);

        var elsewhere = this.Open(protection: new OtherComputer());
        elsewhere.Record(Message("aaa", Bob, "new here", Evening.AddMinutes(1)));
        await elsewhere.FlushAsync();
        Assert.Equal(ChatLogState.Unreadable, elsewhere.State);
        Assert.Equal(["[2026-10-10 21:04] [sky] Bob Hatter@Lich: new here"], this.LinesOf("aaa", "2026-10"));
        // Nothing of the encrypted history changed.
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(this.Folder()).Order());
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
    }

    [Fact]
    public async Task TextFilesThatCantBeWrittenNeverGetInTheWayAndTheDiagnosticsNameNoChannel() {
        // A file where the text folder should be: nothing can be written there.
        Directory.CreateDirectory(this.Folder());
        File.WriteAllText(this.TextRoot(), "in the way");
        this._labels["aaa"] = new ChannelLabel("[sky]", "Secret Garden", 3);
        var log = this.Open();
        var history = new ChannelHistory();
        history.Clear(log);
        for (var i = 0; i < 20; i++) {
            Assert.True(history.Add(new IncomingMessage("aaa", "Secret Garden", Bob, false, $"private words {i}", false, Evening.AddSeconds(i))));
        }

        await log.FlushAsync();
        Assert.Equal(20, history.LinesOf("aaa").Length);
        Assert.Equal(ChatLogState.Ready, log.State);
        // Said once, not with every line, without what was said, who said it or the channel's name.
        Assert.Single(this._diagnostics);
        foreach (var said in this._diagnostics) {
            Assert.DoesNotContain("private words", said);
            Assert.DoesNotContain("Bob", said);
            Assert.DoesNotContain("Secret Garden", said);
        }

        // Once it can be written, it is, from the next line: retried, not given up on.
        File.Delete(this.TextRoot());
        history.Add(new IncomingMessage("aaa", "Secret Garden", Bob, false, "now it works", false, Evening.AddMinutes(1)));
        await this.Close(log);
        Assert.EndsWith("now it works", Assert.Single(this.LinesOf("aaa", "2026-10")));
        Assert.Equal(21, (await All(this.Open(plainText: false), "aaa")).Count);
    }

    [Fact]
    public async Task TheTextFilesMoveWithTheLogToTheServersNewAddress() {
        var log = this.Open(this.Folder(Alice, Url));
        log.Record(Message("aaa", Bob, "at the old address", Evening));
        await this.Close(log);

        Assert.True(ChatLogFiles.MoveToAddress(this._directory, Alice, Url, OtherUrl));
        Assert.Equal(["[2026-10-10 21:03] [sky] Bob Hatter@Lich: at the old address"], this.LinesOf("aaa", "2026-10", this.Folder(Alice, OtherUrl)));
    }

    // ================================================================ words

    [Fact]
    public void SettingsSayItPlainlyAndWarnAboutWhoCanReadIt() {
        foreach (var wording in new[] { ChatLogWords.KeepUnencrypted, ChatLogWords.UnencryptedWarning, ChatLogWords.OpenFolder, ChatLogWords.TurnOn }) {
            Assert.False(string.IsNullOrWhiteSpace(wording.Technical));
            PlainLanguage.AssertPlain(wording.Plain);
        }

        Assert.Contains(ChatLogWords.KeepUnencrypted.Plain, SettingsWords.Labels());
        Assert.Contains(ChatLogWords.OpenFolder.Plain, SettingsWords.Labels());
        Assert.Contains("unencrypted", ChatLogWords.KeepUnencrypted.Technical);
        foreach (var advanced in new[] { false, true }) {
            var warning = ChatLogWords.UnencryptedWarning.For(advanced);
            Assert.Contains("anyone or anything that can read your files", warning);
            Assert.Contains("backup", warning);
            Assert.Contains("cloud", warning);
            Assert.Contains("text files", warning);
            Assert.Contains("text files", SettingsWords.Help(SettingHelp.UnencryptedHistory, "Windows DPAPI").For(advanced));
            Assert.Contains("text files", ChatLogWords.DeleteConfirm(1_000_000).For(advanced));
        }
    }

    // ================================================================ helpers

    private static async Task<List<HistoryLine>> All(ChatLog log, string channelId) {
        var lines = new List<HistoryLine>();
        ChatLogPosition? before = null;
        do {
            var page = await log.ReadOlderAsync(channelId, before);
            lines.InsertRange(0, page.Lines);
            before = page.Next;
        } while (before != null);

        return lines;
    }

    private sealed class TestProtection : IAtRestProtection {
        public byte[] Protect(byte[] plaintext) => [.. "TP"u8, .. plaintext.Select(b => (byte) (b ^ 0x5A))];

        public byte[] Unprotect(byte[] data) => data.Length > 2 && data[0] == 'T' && data[1] == 'P'
            ? data[2..].Select(b => (byte) (b ^ 0x5A)).ToArray()
            : throw new CryptographicException("Not protected here.");
    }

    private sealed class OtherComputer : IAtRestProtection {
        public byte[] Protect(byte[] plaintext) => [.. "OC"u8, .. plaintext];

        public byte[] Unprotect(byte[] data) => data.Length > 2 && data[0] == 'O' && data[1] == 'C'
            ? data[2..]
            : throw new CryptographicException("Key not valid for use in specified state.");
    }
}
