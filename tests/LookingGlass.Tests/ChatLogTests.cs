using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The chat log on the player's computer (opt-in): every channel's messages and information lines, encrypted at rest with
/// a key kept as the secrets file is, appended in order on a background task, cut to a size limit oldest first, and read
/// back a page at a time for channel windows, above the lines since login and never twice. See "Chat log on this computer"
/// in docs/design.md.
/// </summary>
public sealed class ChatLogTests : IDisposable {
    private const ulong Alice = 0x0040_0000_0000_0001;
    private const ulong Carol = 0x0040_0000_0000_0003;
    private const string Url = "ws://LookingGlassChat:5180/ws";
    private const string OtherUrl = "wss://chat.example/ws";

    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldId = 40, WorldName = "Twintania" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldId = 41, WorldName = "Lich" };

    private static int _sequence;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-chatlog-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private readonly List<ChatLog> _open = [];

    public void Dispose() {
        foreach (var log in this._open) {
            log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DeleteDirectory(this._directory);
    }

    private string Folder(ulong contentId = Alice, string url = Url) => ChatLogFiles.Folder(this._directory, contentId, url);

    private ChatLog Open(string? folder = null, long maxBytes = 50L * 1024 * 1024, IAtRestProtection? protection = null) {
        var log = new ChatLog(new ChatLogOptions {
            Folder = folder ?? this.Folder(),
            Protection = protection ?? new TestProtection(),
            MaxBytes = maxBytes,
            Log = this._diagnostics.Enqueue,
        });
        this._open.Add(log);
        return log;
    }

    private async Task Close(ChatLog log) {
        await log.DisposeAsync();
        this._open.Remove(log);
    }

    /// <summary>A message with a time of its own, so no two are alike unless a test wants them to be.</summary>
    private static IncomingMessage From(User sender, string channelId, string text, bool own = false, DateTimeOffset? at = null) =>
        new(channelId, "Channel " + channelId, sender, own, text, false, at ?? Start.AddMilliseconds(Interlocked.Increment(ref _sequence)));

    /// <summary>Every line a log holds for a channel, oldest first, read a page at a time from the start of this session back.</summary>
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

    private static IEnumerable<string?> Texts(IEnumerable<HistoryLine> lines) =>
        lines.Select(line => line.Kind == HistoryLineKind.Message ? line.Message?.Text : line.TextFor(advanced: true));

    // ================================================================ round trip

    [Fact]
    public async Task MessagesLinksAndChannelLinesComeBackAsTheyWereShown() {
        var log = this.Open();
        var history = new ChannelHistory();
        history.Clear(log);

        var item = new MessageLink(5, 8, new ChatLink.Item(1_004_551));
        var flag = new MessageLink(5, 6, new ChatLink.MapFlag(132, 2, 21_500, -7_250));
        var status = new MessageLink(0, 7, new ChatLink.Status(48));
        history.Add(From(Bob, "aaa", "look [Potion] here") with { Links = [item] });
        history.Add(From(Me, "aaa", "meet [Flag]", own: true) with { Links = [flag] });
        history.Add(From(Bob, "aaa", "[Haste] is up") with { Links = [status] });
        history.Add(new IncomingMessage("aaa", "A", Bob, false, null, true, Start.AddSeconds(5)));
        history.AddNotice(SessionNotice.Of(NoticeLevel.Info, PlainMessages.ReVerifiedWording("Bob Hatter@Lich", false), "aaa"));
        history.AddCaughtUp(new CaughtUpMessages("aaa", "A", [From(Bob, "aaa", "while you were away") with { CaughtUp = true }]));
        history.NoteNames(Named(("aaa", "Sky")));
        history.NoteNames(Named(("aaa", "Mad Sky")));
        // Not kept: warnings (about that moment) and feedback (about what was typed then).
        history.AddNotice(SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob Hatter"), "aaa"));
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent: you're sending too fast.");
        history.Add(From(Bob, "bbb", "elsewhere"));
        var shown = history.LinesOf("aaa");
        await this.Close(log);

        // The next login reads them back.
        var next = this.Open();
        var lines = await All(next, "aaa");
        var kept = shown.Where(line => line.Kind != HistoryLineKind.Feedback && line.Tone == NoticeTone.Info).ToList();
        Assert.Equal(8, kept.Count);
        Assert.Equal(kept.Count, lines.Count);
        for (var i = 0; i < kept.Count; i++) {
            var (was, now) = (kept[i], lines[i]);
            Assert.True(now.FromLog);
            Assert.Equal(was.ChannelId, now.ChannelId);
            Assert.Equal(was.Kind, now.Kind);
            Assert.Equal(was.Time, now.Time);
            Assert.Equal(was.Sender, now.Sender);
            Assert.Equal(was.IsOwn, now.IsOwn);
            Assert.Equal(was.SentAt, now.SentAt);
            Assert.Equal(was.CaughtUp, now.CaughtUp);
            Assert.Equal(was.Unsupported, now.Unsupported);
            Assert.Equal(was.Message?.Text, now.Message?.Text);
            Assert.Equal(was.Message?.Links ?? [], now.Message?.Links ?? []);
            Assert.Equal(was.Tone, now.Tone);
            Assert.Equal(was.Notice?.Text, now.Notice?.Text);
            Assert.Equal(was.Notice?.Plain, now.Notice?.Plain);
            Assert.Equal(was.Notice?.Kind, now.Notice?.Kind);
            Assert.Equal(was.Notice?.Level, now.Notice?.Level);
            Assert.Equal(was.TextFor(advanced: false), now.TextFor(advanced: false));
        }

        Assert.Equal(item, lines[0].Message!.Links.Single());
        Assert.Equal(flag, lines[1].Message!.Links.Single());
        Assert.Equal(status, lines[2].Message!.Links.Single());
        Assert.Contains(lines, line => line.TextFor(advanced: true) == ChannelHistory.Renamed("Mad Sky"));
        Assert.Contains(lines, line => line.TextFor(advanced: true) == CatchUpChat.WindowSeparator(1));
        Assert.Equal(["elsewhere"], Texts(await All(next, "bbb")));
        Assert.Empty(await All(next, "ccc"));
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public void OnlyMessagesAndInformationLinesAreKept() {
        var history = new ChannelHistory();
        var recorder = new ListRecorder();
        history.Clear(recorder);
        history.Add(From(Bob, "aaa", "hi"));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob Hatter@Lich joined.", "aaa"));
        history.AddNotice(SessionNotice.Of(NoticeLevel.Warning, PlainMessages.MessageFailedChecks("Bob Hatter"), "aaa"));
        history.AddNotice(SessionNotice.Of(NoticeLevel.Info, Wording.Same("x") with { Kind = NoticeKind.MembershipForked }, "aaa"));
        history.AddFeedback("aaa", NoticeTone.Info, "Not sent: not connected.");

        // The history hands every line over; the log keeps only these.
        Assert.Equal(5, recorder.Lines.Count);
        Assert.Equal([true, true, false, false, false], recorder.Lines.Select(ChatLogFormat.Keeps));
    }

    [Fact]
    public async Task LinksThatDontCheckOutComeBackAsText() {
        // What the log holds is checked as a received message is: a link that isn't well formed shows as its text.
        var log = this.Open();
        log.Record(new HistoryLine(1, "aaa", HistoryLineKind.Message, Start) {
            Sender = Bob,
            SentAt = Start,
            Message = new LinkedText("look [Thing]", [new MessageLink(5, 7, new ChatLink.Status(0))]),
        });
        await this.Close(log);

        var line = Assert.Single(await All(this.Open(), "aaa"));
        Assert.Equal("look [Thing]", line.Message!.Text);
        Assert.Empty(line.Message.Links);
    }

    // ================================================================ at rest

    [Fact]
    public async Task NothingOnDiskCanBeReadWithoutTheKey() {
        var protection = new TestProtection();
        var log = this.Open(protection: protection);
        var history = new ChannelHistory();
        history.Clear(log);
        var carol = new User { UserId = 3, Name = "Carolyn Queenofhearts", WorldId = 42, WorldName = "Ragnarok" };
        history.Add(From(carol, "secret-channel-id-123", "the eagle lands at midnight [Potion]") with {
            Links = [new MessageLink(28, 8, new ChatLink.Item(4551))],
        });
        history.Add(From(Me, "secret-channel-id-123", "meet me behind the airship landing", own: true));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Carolyn Queenofhearts@Ragnarok joined.", "secret-channel-id-123"));
        history.NoteNames(Named(("secret-channel-id-123", "Wonderland Whisperers")));
        history.NoteNames(Named(("secret-channel-id-123", "Looking Glass Society")));
        await log.FlushAsync();
        await this.Close(log);

        var files = Directory.GetFiles(this.Folder());
        Assert.Contains(files, file => file.EndsWith(".lgl", StringComparison.Ordinal));
        Assert.Contains(files, file => Path.GetFileName(file) == ChatLogStore.KeyFileName);
        var raw = files.Select(File.ReadAllBytes).ToList();
        foreach (var secret in new[] {
                     "the eagle lands", "midnight", "Potion", "airship landing", "Carolyn", "Queenofhearts", "Ragnarok", "Alice Liddell",
                     "Twintania", "secret-channel-id-123", "Wonderland Whisperers", "Looking Glass Society", "joined",
                 }) {
            foreach (var bytes in raw) {
                Assert.DoesNotContain(Encoding.UTF8.GetBytes(secret), Windows(bytes, secret.Length));
                Assert.DoesNotContain(Encoding.Unicode.GetBytes(secret), Windows(bytes, secret.Length * 2));
            }
        }

        // The log's key is kept as the protection made it, never as it is.
        var keyFile = File.ReadAllBytes(Path.Combine(this.Folder(), ChatLogStore.KeyFileName));
        Assert.Equal("LGCK"u8.ToArray(), keyFile[..4]);
        Assert.Equal(1, protection.Protected);
        Assert.StartsWith("TP", Encoding.ASCII.GetString(keyFile, 4, 2));
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task ALogFromAnotherComputerIsntReadOrAddedToUntilItIsDeleted() {
        var log = this.Open();
        log.Record(Line("aaa", "from the old computer"));
        await this.Close(log);
        var before = Directory.GetFiles(this.Folder()).ToDictionary(file => file, File.ReadAllBytes);

        var elsewhere = this.Open(protection: new OtherComputer());
        await elsewhere.FlushAsync();
        Assert.Equal(ChatLogState.Unreadable, elsewhere.State);
        Assert.NotNull(elsewhere.Problem);
        elsewhere.Record(Line("aaa", "new here"));
        await elsewhere.FlushAsync();
        Assert.Empty(await All(elsewhere, "aaa"));
        // Nothing on disk changed: it may still be read where it was made.
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(this.Folder()).Order());
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
        Assert.Single(this._diagnostics);
        Assert.DoesNotContain("old computer", this._diagnostics.Single());

        // Deleting it starts a new one.
        await elsewhere.DeleteAsync();
        Assert.Equal(ChatLogState.Ready, elsewhere.State);
        elsewhere.Record(Line("aaa", "new here"));
        await this.Close(elsewhere);
        Assert.Equal(["new here"], Texts(await All(this.Open(protection: new OtherComputer()), "aaa")));
    }

    [Fact]
    public async Task AMissingKeyFileMakesTheLogUnreadableNotEmpty() {
        var log = this.Open();
        log.Record(Line("aaa", "kept"));
        await this.Close(log);
        File.Delete(Path.Combine(this.Folder(), ChatLogStore.KeyFileName));

        var again = this.Open();
        await again.FlushAsync();
        Assert.Equal(ChatLogState.Unreadable, again.State);
        Assert.Contains(Directory.GetFiles(this.Folder()), file => file.EndsWith(".lgl", StringComparison.Ordinal));
    }

    // ================================================================ crashes

    [Fact]
    public async Task ARecordCutShortByACrashIsDroppedAndTheRestKept() {
        var log = this.Open();
        for (var i = 1; i <= 10; i++) {
            log.Record(Line("aaa", $"m{i}"));
        }

        await this.Close(log);
        var segment = Directory.GetFiles(this.Folder(), "*.lgl").Single();
        using (var stream = new FileStream(segment, FileMode.Open)) {
            // Partway through the last record, as a crash during its write leaves it.
            stream.SetLength(stream.Length - 7);
        }

        var again = this.Open();
        Assert.Equal(Enumerable.Range(1, 9).Select(i => $"m{i}"), Texts(await All(again, "aaa")));

        // What is added afterwards follows the whole records: the cut one was cut off, not left in the way.
        again.Record(Line("aaa", "after"));
        await this.Close(again);
        Assert.Equal([.. Enumerable.Range(1, 9).Select(i => $"m{i}"), "after"], Texts(await All(this.Open(), "aaa")));
        Assert.Empty(this._diagnostics);
    }

    [Fact]
    public async Task ADamagedRecordIsSkippedAndTheOthersRead() {
        var log = this.Open();
        for (var i = 1; i <= 5; i++) {
            log.Record(Line("aaa", $"m{i}"));
        }

        await this.Close(log);
        var segment = Directory.GetFiles(this.Folder(), "*.lgl").Single();
        var bytes = File.ReadAllBytes(segment);
        // A byte in the middle of the file (a record's ciphertext): only that record fails its check.
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(segment, bytes);

        var texts = Texts(await All(this.Open(), "aaa")).ToList();
        Assert.Equal(4, texts.Count);
        Assert.Equal("m1", texts[0]);
        Assert.Equal("m5", texts[^1]);
    }

    [Fact]
    public async Task RecordsCantBeMovedOrSwapped() {
        var log = this.Open();
        log.Record(Line("aaa", "first"));
        log.Record(Line("aaa", "secnd"));
        await this.Close(log);

        // Two records of the same length, swapped on disk: each is bound to its place, so neither reads.
        var segment = Directory.GetFiles(this.Folder(), "*.lgl").Single();
        var bytes = File.ReadAllBytes(segment);
        var length = (bytes.Length - 8) / 2;
        var swapped = bytes[..8].Concat(bytes.AsSpan(8 + length, length).ToArray()).Concat(bytes.AsSpan(8, length).ToArray()).ToArray();
        File.WriteAllBytes(segment, swapped);

        Assert.Empty(await All(this.Open(), "aaa"));
    }

    // ================================================================ size

    [Fact]
    public async Task WhenFullTheOldestGoFirstAndItNeverGrowsPastItsLimit() {
        const long limit = 64 * 1024;
        var log = this.Open(maxBytes: limit);
        for (var i = 1; i <= 1500; i++) {
            log.Record(Line(i % 3 == 0 ? "bbb" : "aaa", $"message number {i} with some words to take up room"));
            if (i % 100 == 0) {
                await log.FlushAsync();
                Assert.InRange(ChatLogFiles.FolderSize(this.Folder()), 1, limit);
                Assert.Equal(ChatLogFiles.FolderSize(this.Folder()), log.Size);
            }
        }

        await this.Close(log);
        Assert.True(Directory.GetFiles(this.Folder(), "*.lgl").Length > 2);

        // What is left is the newest, without a gap.
        var texts = Texts(await All(this.Open(maxBytes: limit), "aaa")).ToList();
        Assert.InRange(texts.Count, 100, 999);
        Assert.Equal("message number 1499 with some words to take up room", texts[^1]);
        var numbers = texts.Select(text => int.Parse(text!.Split(' ')[2])).ToList();
        Assert.Equal(numbers.Where(n => n % 3 != 0), numbers);
        Assert.Equal(Enumerable.Range(numbers[0], 1500 - numbers[0]).Where(n => n % 3 != 0), numbers);
    }

    [Fact]
    public async Task LoweringTheLimitDeletesTheOldestAtOnce() {
        var log = this.Open(maxBytes: 256 * 1024);
        for (var i = 1; i <= 2000; i++) {
            log.Record(Line("aaa", $"message number {i} with some words to take up room"));
        }

        await log.FlushAsync();
        Assert.True(log.Size > 128 * 1024);

        log.SetLimit(32 * 1024);
        await log.FlushAsync();
        Assert.InRange(log.Size, 1, 32 * 1024);
        Assert.InRange(ChatLogFiles.FolderSize(this.Folder()), 1, 32 * 1024);
        await this.Close(log);
        Assert.Equal("message number 2000 with some words to take up room", Texts(await All(this.Open(), "aaa")).Last());
    }

    [Fact]
    public void TheLimitIsBetweenFiveMegabytesAndAGigabyte() {
        Assert.Equal(50, ChatLogLimits.DefaultMegabytes);
        Assert.Equal(5, ChatLogLimits.ClampMegabytes(0));
        Assert.Equal(5, ChatLogLimits.ClampMegabytes(-3));
        Assert.Equal(1024, ChatLogLimits.ClampMegabytes(99_999));
        Assert.Equal(200, ChatLogLimits.ClampMegabytes(200));
        Assert.Equal(50L * 1024 * 1024, ChatLogLimits.Bytes(50));
        // Segments: a sixteenth, so the oldest go a little at a time; never too big to read back quickly.
        Assert.Equal(50L * 1024 * 1024 / 16, ChatLogLimits.SegmentBytesFor(ChatLogLimits.Bytes(50)));
        Assert.Equal(16L * 1024 * 1024, ChatLogLimits.SegmentBytesFor(ChatLogLimits.Bytes(1024)));
        Assert.Equal("1 KB", ChatLogLimits.Describe(1));
        Assert.Equal("12.3 MB", ChatLogLimits.Describe(12_900_000));
        Assert.Equal("1.0 GB", ChatLogLimits.Describe(1024L * 1024 * 1024));
    }

    // ================================================================ windows

    [Fact]
    public async Task WindowsReadOlderLinesAPageAtATimeAboveThisSessionsAndNeverTwice() {
        var first = this.Open();
        for (var i = 1; i <= 450; i++) {
            first.Record(Line("aaa", $"old {i}"));
            first.Record(Line("bbb", $"other {i}"));
        }

        await this.Close(first);

        // The next login: this session's lines are in its history (and the log), and not read back as older ones.
        var log = this.Open();
        var history = new ChannelHistory();
        history.Clear(log);
        history.Add(From(Bob, "aaa", "new 1"));
        history.Add(From(Bob, "aaa", "new 2"));
        await log.FlushAsync();

        var earlier = log.Earlier("aaa");
        Assert.Same(earlier, log.Earlier("aaa"));
        Assert.True(earlier.HasMore);
        Assert.Empty(earlier.Lines);

        await earlier.LoadMoreAsync();
        Assert.Equal(Enumerable.Range(251, 200).Select(i => $"old {i}"), Texts(earlier.Lines));
        await earlier.LoadMoreAsync();
        Assert.Equal(400, earlier.Lines.Length);
        Assert.True(earlier.HasMore);
        await earlier.LoadMoreAsync();
        Assert.Equal(Enumerable.Range(1, 450).Select(i => $"old {i}"), Texts(earlier.Lines));
        Assert.False(earlier.HasMore);
        await earlier.LoadMoreAsync();
        Assert.Equal(450, earlier.Lines.Length);

        // Below zero, in order, and unlike any line since login.
        var seqs = earlier.Lines.Select(line => line.Seq).ToList();
        Assert.All(seqs, seq => Assert.True(seq < 0));
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.All(earlier.Lines, line => Assert.True(line.FromLog));
        Assert.All(earlier.Lines, line => Assert.DoesNotContain(line, history.LinesOf("aaa")));
        Assert.Equal(["new 1", "new 2"], Texts(history.LinesOf("aaa")));
        Assert.Equal(450, (await All(log, "bbb")).Count);
    }

    [Fact]
    public async Task AMessageInTheLogAndSinceLoginIsShownOnce() {
        var message = From(Bob, "aaa", "seen twice");
        var logged = From(Bob, "aaa", "only logged");
        var first = this.Open();
        var history = new ChannelHistory();
        history.Clear(first);
        history.Add(logged);
        history.Add(message);
        await this.Close(first);

        // Shown again this session (caught up again, say): the window shows it once, as this session's.
        var log = this.Open();
        history.Clear(log);
        history.AddCaughtUp(new CaughtUpMessages("aaa", "A", [message with { CaughtUp = true }]));
        var earlier = log.Earlier("aaa");
        await earlier.LoadMoreAsync();

        Assert.Equal(["only logged", "seen twice"], Texts(earlier.Lines));
        Assert.Equal(["only logged"], Texts(earlier.ShownWith(history.LinesOf("aaa"))));
        // Worked out once for the same lines.
        Assert.Equal(earlier.ShownWith(history.LinesOf("aaa")), earlier.ShownWith(history.LinesOf("aaa")));
    }

    [Fact]
    public async Task AMessageTheLogHoldsTwiceIsShownOnce() {
        var message = From(Bob, "aaa", "twice in the log");
        foreach (var _ in Enumerable.Range(0, 2)) {
            var log = this.Open();
            var history = new ChannelHistory();
            history.Clear(log);
            history.Add(message);
            await this.Close(log);
        }

        var earlier = this.Open().Earlier("aaa");
        await earlier.LoadMoreAsync();
        Assert.Equal(["twice in the log"], Texts(earlier.Lines));
    }

    // ================================================================ settings and lifecycle

    [Fact]
    public async Task TurnedOffNothingIsKeptAndTurnedOnAgainItCarriesOn() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection(), this._diagnostics.Enqueue);
        var history = new ChannelHistory();

        // On for the first session.
        history.Clear(keeper.Open(Alice, Url, ChatLogLimits.Bytes(50)));
        history.Add(From(Bob, "aaa", "while on"));

        // Turned off mid-session: what comes next isn't kept.
        history.SetRecorder(null);
        keeper.Close();
        await keeper.Settled;
        var size = ChatLogFiles.TotalSize(this._directory);
        history.Add(From(Bob, "aaa", "while off"));
        Assert.Null(keeper.Current);

        // A session with it off keeps nothing.
        history.Clear();
        history.Add(From(Bob, "aaa", "next session, off"));
        await keeper.Settled;
        Assert.Equal(size, ChatLogFiles.TotalSize(this._directory));

        // On again: what was kept is there, and new lines are added.
        var log = keeper.Open(Alice, Url, ChatLogLimits.Bytes(50));
        history.SetRecorder(log);
        history.Add(From(Bob, "aaa", "on again"));
        Assert.Equal(["while on"], Texts(await All(log, "aaa")));
        keeper.Close();
        await keeper.Settled;
        Assert.Equal(["while on", "on again"], Texts(await All(this.Open(), "aaa")));
    }

    [Fact]
    public async Task EachCharacterAndServerHasItsOwnLog() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection());
        foreach (var (contentId, url, text) in new[] { (Alice, Url, "alice here"), (Alice, OtherUrl, "alice there"), (Carol, Url, "carol here") }) {
            var history = new ChannelHistory();
            history.Clear(keeper.Open(contentId, url, ChatLogLimits.Bytes(50)));
            history.Add(From(Bob, "aaa", text));
        }

        keeper.Close();
        await keeper.Settled;
        Assert.Equal(3, ChatLogFiles.Folders(this._directory).Count);
        Assert.Equal(["alice here"], Texts(await All(this.Open(this.Folder(Alice, Url)), "aaa")));
        Assert.Equal(["alice there"], Texts(await All(this.Open(this.Folder(Alice, OtherUrl)), "aaa")));
        Assert.Equal(["carol here"], Texts(await All(this.Open(this.Folder(Carol, Url)), "aaa")));
        Assert.StartsWith("chatlog-0040000000000001-", Path.GetFileName(this.Folder(Alice, Url)));
        // Named as the secrets files are: the address's hash, never the address.
        Assert.EndsWith(ServerSecretFiles.AddressHash(Url), this.Folder(Alice, Url));
        Assert.Equal(ChatLogFiles.Folder(this._directory, Alice, " WS://lookingglasschat:5180/ws "), this.Folder(Alice, Url));
    }

    [Fact]
    public async Task DeletingRemovesEveryChatLogAndNothingElse() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection());
        var history = new ChannelHistory();
        history.Clear(keeper.Open(Carol, Url, ChatLogLimits.Bytes(50)));
        history.Add(From(Bob, "aaa", "carol's"));
        var log = keeper.Open(Alice, Url, ChatLogLimits.Bytes(50));
        history.Clear(log);
        history.Add(From(Bob, "aaa", "before deleting"));
        await log.FlushAsync();
        var earlier = log.Earlier("aaa");
        var secrets = Path.Combine(this._directory, ServerSecretFiles.FileName(Alice, Url));
        File.WriteAllText(secrets, "not a chat log");
        Assert.True(await keeper.SizeAsync() > 0);
        var version = log.Version;

        await keeper.DeleteAllAsync();
        Assert.Equal(0, await keeper.SizeAsync());
        Assert.Empty(ChatLogFiles.Folders(this._directory));
        Assert.True(File.Exists(secrets));
        Assert.NotEqual(version, log.Version);
        Assert.NotSame(earlier, log.Earlier("aaa"));

        // Still on: it goes on as a new log, with nothing from before.
        history.Add(From(Bob, "aaa", "after deleting"));
        await log.FlushAsync();
        Assert.Empty(await All(log, "aaa"));
        keeper.Close();
        await keeper.Settled;
        Assert.Equal(["after deleting"], Texts(await All(this.Open(), "aaa")));
    }

    [Fact]
    public async Task DeletingWithTheLogOffWaitsForItToClose() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection());
        var log = keeper.Open(Alice, Url, ChatLogLimits.Bytes(50));
        for (var i = 0; i < 200; i++) {
            log.Record(Line("aaa", $"m{i}"));
        }

        // Turned off and deleted straight away: what was queued is written first, then all of it deleted.
        keeper.Close();
        await keeper.DeleteAllAsync();
        Assert.Empty(ChatLogFiles.Folders(this._directory));

        // A closed log deletes its own folder too.
        var closed = this.Open();
        closed.Record(Line("aaa", "x"));
        await this.Close(closed);
        await closed.DeleteAsync();
        Assert.False(Directory.Exists(this.Folder()));
    }

    // ================================================================ never in the way

    [Fact]
    public async Task ALogThatCantWriteNeverGetsInTheWayAndSaysNothingThatWasSaid() {
        // A file where the log's folder should be: nothing can be written.
        Directory.CreateDirectory(this._directory);
        File.WriteAllText(this.Folder(), "in the way");
        var log = this.Open();
        var history = new ChannelHistory();
        history.Clear(log);

        for (var i = 0; i < 20; i++) {
            Assert.True(history.Add(From(Bob, "aaa", $"private words {i}")));
        }

        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob Hatter@Lich joined Secret Garden.", "aaa"));
        await log.FlushAsync();
        Assert.Equal(21, history.LinesOf("aaa").Length);
        Assert.Empty(await All(log, "aaa"));
        await log.Earlier("aaa").LoadMoreAsync();
        Assert.Empty(log.Earlier("aaa").Lines);

        // Said once (not with every line), without what was said or who.
        Assert.InRange(this._diagnostics.Count, 1, 3);
        foreach (var said in this._diagnostics) {
            Assert.DoesNotContain("private words", said);
            Assert.DoesNotContain("Bob", said);
            Assert.DoesNotContain("Secret Garden", said);
        }

        await this.Close(log);
        // Recorded after closing: dropped, quietly.
        log.Record(Line("aaa", "late"));
        Assert.Empty((await log.ReadOlderAsync("aaa")).Lines);
    }

    [Fact]
    public void ARecorderThatThrowsNeverKeepsALineFromBeingShown() {
        var history = new ChannelHistory();
        history.Clear(new ThrowingRecorder());
        Assert.True(history.Add(From(Bob, "aaa", "still shown")));
        Assert.True(history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob joined.", "aaa")));
        Assert.Equal(2, history.LinesOf("aaa").Length);
    }

    [Fact]
    public async Task RecordingNeverWaitsOnTheDisk() {
        // A log that can't start until something else is done (an earlier one closing): lines are queued meanwhile.
        var gate = new TaskCompletionSource();
        var log = new ChatLog(new ChatLogOptions { Folder = this.Folder(), Protection = new TestProtection(), StartAfter = gate.Task });
        this._open.Add(log);
        var history = new ChannelHistory();
        history.Clear(log);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) {
            history.Add(From(Bob, "aaa", $"m{i}"));
        }

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(ChatLogState.Starting, log.State);
        gate.SetResult();
        await this.Close(log);
        Assert.Equal(1000, (await All(this.Open(), "aaa")).Count);
    }

    // ================================================================ words

    [Fact]
    public void SettingsAndWindowsSayItInPlainWordsInSimpleMode() {
        foreach (var wording in ChatLogWords.Examples()) {
            Assert.False(string.IsNullOrWhiteSpace(wording.Technical));
            PlainLanguage.AssertPlain(wording.Plain);
        }

        // Advanced mode keeps the owner's words; simple mode says "chat history" ("log" is one of its banned words).
        Assert.Equal("Keep a chat log on this computer", ChatLogWords.KeepIt.Technical);
        Assert.Equal("Keep chat history on this computer", ChatLogWords.KeepIt.Plain);
        Assert.Equal("Delete my chat log", ChatLogWords.Delete.Technical);
        Assert.Equal("Delete my chat history", ChatLogWords.Delete.Plain);
        Assert.Contains("never uploaded", ChatLogWords.Explanation("Windows DPAPI").Plain);
        Assert.Contains("never uploaded", ChatLogWords.Explanation("Windows DPAPI").Technical);
        Assert.Contains("Windows DPAPI", ChatLogWords.Explanation("Windows DPAPI").Technical);
        Assert.Contains("12.3 MB", ChatLogWords.Uses(12_900_000).Plain);
        Assert.Equal("Earlier: Tuesday 6 October 2026", ChatLogWords.Earlier(Start, TimeZoneInfo.Utc).Plain);
    }

    // ================================================================ after review

    [Fact]
    public async Task ASegmentThatCantBeMadeLeavesNothingBehindAndLoggingGoesOn() {
        // Something where the first segment's file would go: it can't be made there.
        Directory.CreateDirectory(Path.Combine(this.Folder(), "0000000001.lgl"));
        var log = this.Open();
        for (var i = 1; i <= 3; i++) {
            log.Record(Line("aaa", $"m{i}"));
        }

        await this.Close(log);
        Assert.Equal(["m1", "m2", "m3"], Texts(await All(this.Open(), "aaa")));
        Assert.True(File.Exists(Path.Combine(this.Folder(), "0000000002.lgl")));
    }

    [Fact]
    public async Task ADeletionThatPartlyFailedDoesntStopLogging() {
        if (!OperatingSystem.IsWindows()) {
            // Only Windows refuses to delete a file someone has open.
            return;
        }

        var log = this.Open();
        log.Record(Line("aaa", "before"));
        await log.FlushAsync();
        var segment = Directory.GetFiles(this.Folder(), "*.lgl").Single();
        using (new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            await Assert.ThrowsAsync<IOException>(log.DeleteAsync);
            log.Record(Line("aaa", "after"));
            await log.FlushAsync();
        }

        await this.Close(log);
        // The file left over is skipped, never written over: what came after is all there is (its key went).
        Assert.True(File.Exists(segment));
        Assert.Equal(["after"], Texts(await All(this.Open(), "aaa")));
    }

    [Fact]
    public async Task AKeyFileThatCantBeReadJustNowIsTriedAgainNotGivenUpOn() {
        var log = this.Open();
        log.Record(Line("aaa", "before"));
        await this.Close(log);
        var keyPath = Path.Combine(this.Folder(), ChatLogStore.KeyFileName);
        var key = File.ReadAllBytes(keyPath);

        var again = this.Open();
        using (new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
            await again.FlushAsync();
            Assert.Equal(ChatLogState.Failed, again.State);
            Assert.Null(again.Problem);
            again.Record(Line("aaa", "while it's in use"));
            await again.FlushAsync();
        }

        again.Record(Line("aaa", "after"));
        await again.FlushAsync();
        Assert.Equal(ChatLogState.Ready, again.State);
        await this.Close(again);
        Assert.Equal(key, File.ReadAllBytes(keyPath));
        Assert.Equal(["before", "after"], Texts(await All(this.Open(), "aaa")));
    }

    [Fact]
    public async Task ADamagedLengthKeepsTheRecordsAfterIt() {
        var log = this.Open();
        for (var i = 1; i <= 9; i++) {
            log.Record(Line("aaa", $"m{i}"));
        }

        await this.Close(log);
        var segment = Directory.GetFiles(this.Folder(), "*.lgl").Single();
        var bytes = File.ReadAllBytes(segment);
        var length = (bytes.Length - 8) / 9;
        // The third record's length field: nothing after it can be found by its length.
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 8 + 2 * length);
        File.WriteAllBytes(segment, bytes);

        var again = this.Open();
        Assert.Equal(["m1", "m2", "m4", "m5", "m6", "m7", "m8", "m9"], Texts(await All(again, "aaa")));
        again.Record(Line("aaa", "after"));
        await this.Close(again);
        Assert.Equal(["m1", "m2", "m4", "m5", "m6", "m7", "m8", "m9", "after"], Texts(await All(this.Open(), "aaa")));
    }

    [Fact]
    public async Task ARecordMovedToAnotherSegmentIsntRead() {
        var log = this.Open(maxBytes: 64 * 1024);
        for (var i = 1; i <= 100; i++) {
            log.Record(Line("aaa", $"n{i:D4}"));
        }

        await this.Close(log);
        var segments = Directory.GetFiles(this.Folder(), "*.lgl").Order().ToList();
        Assert.True(segments.Count >= 2);
        var first = File.ReadAllBytes(segments[0]);
        var second = File.ReadAllBytes(segments[1]);
        var length = BitConverter.ToInt32(first, 8) + 4;
        Assert.Equal(length, BitConverter.ToInt32(second, 8) + 4);
        // The first segment's first record, at the same offset in the second: only the segment number differs.
        first.AsSpan(8, length).CopyTo(second.AsSpan(8, length));
        File.WriteAllBytes(segments[1], second);

        var texts = Texts(await All(this.Open(maxBytes: 64 * 1024), "aaa")).ToList();
        Assert.Equal(99, texts.Count);
        Assert.Single(texts, text => text == "n0001");
    }

    [Fact]
    public async Task EveryRecordHasANonceOfItsOwn() {
        var log = this.Open();
        for (var i = 0; i < 50; i++) {
            log.Record(Line("aaa", "the same words"));
        }

        await this.Close(log);
        var bytes = File.ReadAllBytes(Directory.GetFiles(this.Folder(), "*.lgl").Single());
        var nonces = new List<string>();
        for (var at = 8; at + 4 <= bytes.Length; at += 4 + BitConverter.ToInt32(bytes, at)) {
            nonces.Add(Convert.ToHexString(bytes, at + 4, 24));
        }

        Assert.Equal(50, nonces.Count);
        Assert.Equal(50, nonces.Distinct().Count());
    }

    [Fact]
    public async Task ALastSegmentWithoutItsHeaderIsDeletedWhenOpened() {
        var log = this.Open(maxBytes: 64 * 1024);
        for (var i = 1; i <= 100; i++) {
            log.Record(Line("aaa", $"n{i:D4}"));
        }

        await this.Close(log);
        var segments = Directory.GetFiles(this.Folder(), "*.lgl").Order().ToList();
        var bytes = File.ReadAllBytes(segments[^1]);
        bytes.AsSpan(0, 8).Clear();
        File.WriteAllBytes(segments[^1], bytes);

        var again = this.Open(maxBytes: 64 * 1024);
        await again.FlushAsync();
        Assert.False(File.Exists(segments[^1]));
        var texts = Texts(await All(again, "aaa")).ToList();
        Assert.InRange(texts.Count, 1, 99);
        Assert.Equal("n0001", texts[0]);
    }

    [Fact]
    public async Task TurningItOffAndOnInOneSessionShowsNothingTwice() {
        await using var keeper = new ChatLogKeeper(this._directory, new TestProtection());
        var history = new ChannelHistory();
        history.Clear(keeper.Open(Alice, Url, ChatLogLimits.Bytes(50)));
        history.AddNotice(new SessionNotice(NoticeLevel.Info, "Bob Hatter@Lich joined.", "aaa"));
        history.Add(From(Bob, "aaa", "hello"));

        history.SetRecorder(null);
        keeper.Close();
        var log = keeper.Open(Alice, Url, ChatLogLimits.Bytes(50));
        history.SetRecorder(log);
        var earlier = log.Earlier("aaa");
        await earlier.LoadMoreAsync();

        // The first log of this session kept them, so the second reads them as older: shown once, as this session's.
        Assert.Equal(2, earlier.Lines.Length);
        Assert.Empty(earlier.ShownWith(history.LinesOf("aaa")));
        // Words alike at another time are another line.
        history.Clear();
        Assert.Equal(2, earlier.ShownWith(history.LinesOf("aaa")).Length);
    }

    [Fact]
    public async Task SomeoneBlockedSinceIsntShownFromTheLogEither() {
        var carol = new User { UserId = 3, Name = "Carol Queen", WorldName = "Odin" };
        var first = this.Open();
        var history = new ChannelHistory();
        history.Clear(first);
        history.Add(From(Bob, "aaa", "from bob"));
        history.Add(From(carol, "aaa", "from carol"));
        await this.Close(first);

        var earlier = this.Open().Earlier("aaa");
        await earlier.LoadMoreAsync();
        var session = ImmutableArray<HistoryLine>.Empty;
        Assert.Equal(["from bob", "from carol"], Texts(earlier.ShownWith(session)));
        Assert.Equal(["from carol"], Texts(earlier.ShownWith(session, [Bob])));
        // Unblocked: shown again.
        Assert.Equal(["from bob", "from carol"], Texts(earlier.ShownWith(session, [])));
    }

    [Fact]
    public async Task ALogMovesWithItsIdentityToTheServersNewAddress() {
        var log = this.Open(this.Folder(Alice, Url));
        log.Record(Line("aaa", "said at the old address"));
        await this.Close(log);

        Assert.True(ChatLogFiles.MoveToAddress(this._directory, Alice, Url, OtherUrl));
        Assert.False(Directory.Exists(this.Folder(Alice, Url)));
        Assert.Equal(["said at the old address"], Texts(await All(this.Open(this.Folder(Alice, OtherUrl)), "aaa")));
        foreach (var open in this._open.ToList()) {
            await this.Close(open);
        }

        // Nothing to move, or a log there already (never merged, never written over): nothing happens.
        Assert.False(ChatLogFiles.MoveToAddress(this._directory, Alice, Url, OtherUrl));
        var other = this.Open(this.Folder(Alice, Url));
        other.Record(Line("aaa", "a new one at the old address"));
        await this.Close(other);
        Assert.False(ChatLogFiles.MoveToAddress(this._directory, Alice, Url, OtherUrl));
        Assert.Equal(["said at the old address"], Texts(await All(this.Open(this.Folder(Alice, OtherUrl)), "aaa")));
        Assert.Equal(["a new one at the old address"], Texts(await All(this.Open(this.Folder(Alice, Url)), "aaa")));
        Assert.False(ChatLogFiles.MoveToAddress(this._directory, Alice, Url, " WS://lookingglasschat:5180/ws "));
    }

    // ================================================================ helpers

    private static HistoryLine Line(string channelId, string text) => new(1, channelId, HistoryLineKind.Message, Start) {
        Sender = Bob,
        SentAt = Start.AddMilliseconds(Interlocked.Increment(ref _sequence)),
        Message = LinkedText.Plain(text),
    };

    private static SessionSnapshot Named(params (string Id, string? Name)[] channels) => SessionSnapshot.Empty with {
        State = ConnectionState.Ready,
        ChannelsLoaded = true,
        Channels = [.. channels.Select(channel => new ChannelView(channel.Id, channel.Name, 0, 0, true, false, Rank.Member, []))],
    };

    /// <summary>Every run of <paramref name="length"/> bytes in <paramref name="bytes"/>, for looking for one.</summary>
    private static IEnumerable<byte[]> Windows(byte[] bytes, int length) {
        for (var i = 0; i + length <= bytes.Length; i++) {
            yield return bytes[i..(i + length)];
        }
    }

    /// <summary>Stands in for DPAPI: reversible here, and marked so a test can see the key went through it.</summary>
    private sealed class TestProtection : IAtRestProtection {
        public int Protected;

        public byte[] Protect(byte[] plaintext) {
            Interlocked.Increment(ref this.Protected);
            return [.. "TP"u8, .. plaintext.Select(b => (byte) (b ^ 0x5A))];
        }

        public byte[] Unprotect(byte[] data) => data.Length > 2 && data[0] == 'T' && data[1] == 'P'
            ? data[2..].Select(b => (byte) (b ^ 0x5A)).ToArray()
            : throw new CryptographicException("Not protected here.");
    }

    /// <summary>DPAPI on another computer or Windows account: what this one protected can't be unprotected.</summary>
    private sealed class OtherComputer : IAtRestProtection {
        public byte[] Protect(byte[] plaintext) => [.. "OC"u8, .. plaintext];

        public byte[] Unprotect(byte[] data) => data.Length > 2 && data[0] == 'O' && data[1] == 'C'
            ? data[2..]
            : throw new CryptographicException("Key not valid for use in specified state.");
    }

    private sealed class ListRecorder : IChannelHistoryRecorder {
        public List<HistoryLine> Lines { get; } = [];

        public void Record(HistoryLine line) => this.Lines.Add(line);
    }

    private sealed class ThrowingRecorder : IChannelHistoryRecorder {
        public void Record(HistoryLine line) => throw new InvalidOperationException("broken");
    }
}
