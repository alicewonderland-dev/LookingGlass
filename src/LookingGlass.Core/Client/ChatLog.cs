using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>Where a <see cref="ChatLog"/> stands.</summary>
public enum ChatLogState {
    /// <summary>Opening what is on disk (in the background).</summary>
    Starting,

    /// <summary>Keeping lines.</summary>
    Ready,

    /// <summary>The log on disk can't be unlocked here (another computer or Windows account): nothing is added until it is deleted.</summary>
    Unreadable,

    /// <summary>Opening it failed (the disk, say): tried again with the next line.</summary>
    Failed,

    /// <summary>Closed: the session ended, or the player turned the log off.</summary>
    Closed,
}

public sealed class ChatLogOptions {
    /// <summary>The log's folder (see <see cref="ChatLogFiles.Folder"/>).</summary>
    public required string Folder { get; init; }

    /// <summary>How its key is kept: as the secrets file is.</summary>
    public required IAtRestProtection Protection { get; init; }

    /// <summary>The most room it may take on disk: the oldest lines go first.</summary>
    public long MaxBytes { get; init; } = ChatLogLimits.Bytes(ChatLogLimits.DefaultMegabytes);

    /// <summary>Diagnostics. Never given anything that was said, nor any name: only what went wrong with the files.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>It opens the files only once this has finished: an earlier log closing, or a deletion.</summary>
    public Task? StartAfter { get; init; }
}

/// <summary>
/// One session's chat log on this computer (opt-in; see "Chat log on this computer" in docs/design.md): it records a
/// channel history's lines (<see cref="IChannelHistoryRecorder"/>) to an encrypted log on disk (<see cref="ChatLogStore"/>),
/// and reads the lines of earlier sessions back for channel windows (<see cref="Earlier"/>).
/// <para>
/// Everything that touches the disk runs on one background task, in order: <see cref="Record"/> only queues the line, so the
/// game never waits on the disk, and nothing that goes wrong with the log ever reaches whoever recorded the line, or keeps it
/// from being shown. A failure is written to the diagnostic log (once, until it works again), without what was said.
/// </para>
/// </summary>
public sealed class ChatLog : IChannelHistoryRecorder, IAsyncDisposable {
    /// <summary>How many older lines a window asks for at a time.</summary>
    public const int PageSize = 200;

    private readonly ChatLogOptions _options;
    private readonly ChatLogStore _store;
    private readonly Channel<Op> _ops = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly ConcurrentDictionary<string, EarlierLines> _earlier = new(StringComparer.Ordinal);
    // The worker's only: what has failed since it last worked, so a failing disk isn't written about with every line.
    private readonly HashSet<string> _failing = [];
    private volatile ChatLogState _state = ChatLogState.Starting;
    private volatile string? _problem;
    private long _size;
    private int _version;
    private int _disposed;

    public ChatLog(ChatLogOptions options) {
        this._options = options;
        this._store = new ChatLogStore(options.Folder, options.Protection, options.MaxBytes);
        this._worker = Task.Run(this.RunAsync);
    }

    public string Folder => this._options.Folder;

    public ChatLogState State => this._state;

    /// <summary>Why the log can't be read here (<see cref="ChatLogState.Unreadable"/>), in technical words; null otherwise.</summary>
    public string? Problem => this._problem;

    /// <summary>The room it takes on disk, as of the last thing it did.</summary>
    public long Size => Interlocked.Read(ref this._size);

    /// <summary>Goes up when the log is deleted: older lines shown from before are gone.</summary>
    public int Version => Volatile.Read(ref this._version);

    /// <summary>
    /// Queues a line to be kept, if it is one the log keeps (see <see cref="ChatLogFormat.Keeps"/>). Never blocks, never throws.
    /// </summary>
    public void Record(HistoryLine line) {
        try {
            if (Volatile.Read(ref this._disposed) == 0 && ChatLogFormat.Keeps(line)) {
                this._ops.Writer.TryWrite(new Op.Append(line));
            }
        } catch {
            // Never into the history: see the class summary.
        }
    }

    /// <summary>A new size limit; the oldest lines go at once if the log is over it.</summary>
    public void SetLimit(long maxBytes) => this._ops.Writer.TryWrite(new Op.Limit(maxBytes));

    /// <summary>
    /// A channel's lines from before <paramref name="before"/> (from before this session to begin with), the newest
    /// <paramref name="max"/> of them, oldest first. Never fails: a log that can't be read has nothing to show.
    /// </summary>
    public Task<LogPage> ReadOlderAsync(string channelId, ChatLogPosition? before = null, int max = PageSize) {
        var op = new Op.Read(channelId, before, max, new TaskCompletionSource<LogPage>(TaskCreationOptions.RunContinuationsAsynchronously));
        return this._ops.Writer.TryWrite(op) ? op.Done.Task : Task.FromResult(LogPage.Empty);
    }

    /// <summary>
    /// Deletes the whole log from disk, after what was queued before. If the log is still on, it goes on afterwards, as a new
    /// log with nothing older in it. Fails (with a message for the player) if some of it couldn't be deleted.
    /// </summary>
    public Task DeleteAsync() {
        var op = new Op.Delete(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (this._ops.Writer.TryWrite(op)) {
            return op.Done.Task;
        }

        // Closed, or closing: once nothing writes to it any more, delete the folder as it is.
        return this._worker.ContinueWith(_ => {
            lock (AtomicFile.LockFor(this.Folder)) {
                if (!ChatLogFiles.DeleteFolder(this.Folder)) {
                    throw new IOException("Some of the chat log's files couldn't be deleted (they may be in use). Try again later.");
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Done once everything queued before it is done, and handed to the operating system.</summary>
    public Task FlushAsync() {
        var op = new Op.Flush(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return this._ops.Writer.TryWrite(op) ? op.Done.Task : this._worker;
    }

    /// <summary>A channel's older lines for its windows, loaded a page at a time (see <see cref="EarlierLines"/>).</summary>
    public EarlierLines Earlier(string channelId) => this._earlier.GetOrAdd(channelId, id => new EarlierLines(this, id));

    /// <summary>Stops taking lines, writes what was queued, and closes the files.</summary>
    public async ValueTask DisposeAsync() {
        Interlocked.Exchange(ref this._disposed, 1);
        this._ops.Writer.TryComplete();
        await this._worker.ConfigureAwait(false);
    }

    // ================================================================ the worker

    private async Task RunAsync() {
        try {
            if (this._options.StartAfter is { } after) {
                try {
                    await after.ConfigureAwait(false);
                } catch {
                    // Whatever went wrong there was said there.
                }
            }

            this.Open();
            var reader = this._ops.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false)) {
                while (reader.TryRead(out var op)) {
                    this.Handle(op);
                }

                this.Try("write the chat log", this._store.Flush);
                Interlocked.Exchange(ref this._size, this._store.Size);
            }
        } catch (Exception ex) {
            this.Warn("keep the chat log", ex);
        } finally {
            // Anything still queued (only if the loop itself failed) gets an answer.
            while (this._ops.Reader.TryRead(out var left)) {
                Answer(left);
            }

            try {
                this._store.Dispose();
            } catch (Exception ex) {
                this.Warn("close the chat log", ex);
            }

            this._state = ChatLogState.Closed;
        }
    }

    private void Open() {
        try {
            this._store.Open();
            this._state = ChatLogState.Ready;
            this._failing.Remove("open the chat log");
        } catch (ChatLogUnreadableException ex) {
            this._problem = ex.Message;
            this._state = ChatLogState.Unreadable;
            this._options.Log?.Invoke($"The chat log on this computer can't be read here ({ex.Message}): nothing is added to it until it is deleted.");
        } catch (Exception ex) {
            this._state = ChatLogState.Failed;
            this.Warn("open the chat log", ex);
        }
    }

    private void Handle(Op op) {
        switch (op) {
            case Op.Append append:
                if (this._state == ChatLogState.Failed) {
                    this.Open();
                }

                if (this._state == ChatLogState.Ready) {
                    // Not kept if too big for the limit: nothing to warn about.
                    this.Try("add to the chat log", () => { this._store.Append(append.Line); });
                }

                break;
            case Op.Limit limit:
                this.Try("trim the chat log", () => this._store.SetLimit(limit.MaxBytes));
                break;
            case Op.Read read:
                LogPage page;
                try {
                    page = this._store.ReadBefore(read.ChannelId, read.Before ?? this._store.SessionStart, read.Max);
                } catch (Exception ex) {
                    this.Warn("read the chat log", ex);
                    page = LogPage.Empty;
                }

                read.Done.TrySetResult(page);
                break;
            case Op.Delete delete:
                Exception? failure = null;
                try {
                    this._store.DeleteAll();
                    this._problem = null;
                    this._state = ChatLogState.Ready;
                } catch (Exception ex) {
                    this.Warn("delete the chat log", ex);
                    failure = ex;
                }

                // Whatever windows showed of it goes, even if only some of it could be deleted.
                Interlocked.Increment(ref this._version);
                this._earlier.Clear();
                Interlocked.Exchange(ref this._size, this._store.Size);
                if (failure != null) {
                    delete.Done.TrySetException(failure);
                } else {
                    delete.Done.TrySetResult();
                }

                break;
            case Op.Flush flush:
                this.Try("write the chat log", this._store.Flush);
                Interlocked.Exchange(ref this._size, this._store.Size);
                flush.Done.TrySetResult();
                break;
        }

        Interlocked.Exchange(ref this._size, this._store.Size);
    }

    /// <summary>Runs something on the files; a failure is written to the diagnostic log, once until it works again.</summary>
    private bool Try(string what, Action action) {
        try {
            action();
            this._failing.Remove(what);
            return true;
        } catch (Exception ex) {
            this.Warn(what, ex);
            return false;
        }
    }

    private void Warn(string what, Exception ex) {
        if (!this._failing.Add(what)) {
            return;
        }

        // The kind of failure and the file system's words (a path, "access denied"): never a record's bytes or text, which
        // no exception here holds (ChatLogFormat's say only that a record couldn't be read).
        try {
            this._options.Log?.Invoke($"Couldn't {what}: {ex.GetType().Name}: {ex.Message}");
        } catch {
            // Diagnostics only.
        }
    }

    private static void Answer(Op op) {
        switch (op) {
            case Op.Read read:
                read.Done.TrySetResult(LogPage.Empty);
                break;
            case Op.Delete delete:
                delete.Done.TrySetException(new InvalidOperationException("The chat log closed before it could be deleted."));
                break;
            case Op.Flush flush:
                flush.Done.TrySetResult();
                break;
        }
    }

    private abstract record Op {
        public sealed record Append(HistoryLine Line) : Op;

        public sealed record Limit(long MaxBytes) : Op;

        public sealed record Read(string ChannelId, ChatLogPosition? Before, int Max, TaskCompletionSource<LogPage> Done) : Op;

        public sealed record Delete(TaskCompletionSource Done) : Op;

        public sealed record Flush(TaskCompletionSource Done) : Op;
    }
}

/// <summary>
/// A channel's lines from the chat log, for its windows: loaded a page (<see cref="ChatLog.PageSize"/>) at a time, older
/// pages above newer ones, when the player scrolls up or asks for them, so opening a window stays quick. Shown above the
/// lines since login, never twice: a message the session holds too (<see cref="ShownWith"/>), or that the log holds twice,
/// is shown once. Safe from any thread: pages arrive on the log's, windows read on the draw thread.
/// </summary>
public sealed class EarlierLines {
    // Every older line gets a number below zero, lower for older pages, never used twice in the process: windows keep what
    // they have drawn by it, and a line since login has a number above zero.
    private static long _nextSeq;

    private readonly ChatLog _log;
    private readonly string _channelId;
    private readonly Lock _lock = new();
    private readonly HashSet<ChannelHistory.MessageKey> _keys = [];
    private ImmutableArray<HistoryLine> _lines = ImmutableArray<HistoryLine>.Empty;
    private ChatLogPosition? _cursor;
    private Task? _loading;
    private bool _more = true;
    private (ImmutableArray<HistoryLine> Session, ImmutableArray<HistoryLine> Earlier, ImmutableArray<User> Blocked, ImmutableArray<HistoryLine> Shown)? _shown;

    internal EarlierLines(ChatLog log, string channelId) {
        this._log = log;
        this._channelId = channelId;
    }

    /// <summary>The older lines loaded so far, oldest first.</summary>
    public ImmutableArray<HistoryLine> Lines {
        get {
            lock (this._lock) {
                return this._lines;
            }
        }
    }

    /// <summary>A page is being read.</summary>
    public bool Loading {
        get {
            lock (this._lock) {
                return this._loading is { IsCompleted: false };
            }
        }
    }

    /// <summary>There may be older lines still (until a page reaches the start of the log).</summary>
    public bool HasMore {
        get {
            lock (this._lock) {
                return this._more;
            }
        }
    }

    /// <summary>Reads the next older page, unless one is being read or there is nothing older. Never fails.</summary>
    public Task LoadMoreAsync() {
        lock (this._lock) {
            if (this._loading is { IsCompleted: false } loading) {
                return loading;
            }

            if (!this._more) {
                return Task.CompletedTask;
            }

            this._loading = this.LoadAsync(this._cursor);
            return this._loading;
        }
    }

    private async Task LoadAsync(ChatLogPosition? before) {
        var page = await this._log.ReadOlderAsync(this._channelId, before).ConfigureAwait(false);
        lock (this._lock) {
            // A message the log holds twice (shown in two sessions) is shown once: the newer page's.
            var fresh = page.Lines.Where(line => line.Kind != HistoryLineKind.Message || this._keys.Add(ChannelHistory.MessageKey.Of(line))).ToList();
            var first = Interlocked.Add(ref _nextSeq, -fresh.Count);
            var builder = ImmutableArray.CreateBuilder<HistoryLine>(fresh.Count + this._lines.Length);
            for (var i = 0; i < fresh.Count; i++) {
                builder.Add(fresh[i] with { Seq = first + i });
            }

            builder.AddRange(this._lines);
            this._lines = builder.MoveToImmutable();
            this._cursor = page.Next;
            this._more = page.Next != null;
        }
    }

    /// <summary>
    /// The older lines to show above <paramref name="session"/> (the channel's lines since login): without what it holds
    /// too, a message (the same sender, signed time and text) or an information line (the same time and words: kept by an
    /// earlier log of this session, when the log was turned off and on again), and without messages from anyone in
    /// <paramref name="blocked"/>, as for live ones. Worked out again only when any of them changes.
    /// </summary>
    public ImmutableArray<HistoryLine> ShownWith(ImmutableArray<HistoryLine> session, ImmutableArray<User> blocked = default) {
        lock (this._lock) {
            var earlier = this._lines;
            blocked = blocked.IsDefault ? ImmutableArray<User>.Empty : blocked;
            if (this._shown is { } cached && cached.Session == session && cached.Earlier == earlier && cached.Blocked == blocked) {
                return cached.Shown;
            }

            var held = session.Where(line => line.Kind == HistoryLineKind.Message).Select(ChannelHistory.MessageKey.Of).ToHashSet();
            var said = session.Where(line => line.Kind == HistoryLineKind.Notice).Select(NoticeKey).ToHashSet();
            var hidden = blocked.Select(user => user.UserId).ToHashSet();
            var shown = (held.Count == 0 && said.Count == 0 && hidden.Count == 0) || earlier.IsEmpty
                ? earlier
                : earlier.Where(line => line.Kind switch {
                    HistoryLineKind.Message => !held.Contains(ChannelHistory.MessageKey.Of(line)) && !hidden.Contains(line.Sender?.UserId ?? 0),
                    HistoryLineKind.Notice => !said.Contains(NoticeKey(line)),
                    _ => true,
                }).ToImmutableArray();
            this._shown = (session, earlier, blocked, shown);
            return shown;
        }
    }

    private static (string ChannelId, DateTimeOffset Time, string? Text) NoticeKey(HistoryLine line) => (line.ChannelId, line.Time, line.Notice?.Text);
}

/// <summary>
/// The chat logs of a plugin: at most one open at a time (the session's character and server), opened when a session
/// starts with the setting on, or the setting is turned on, and closed when the session ends or it is turned off. A log
/// opens only once every earlier one has closed and every deletion asked for before is done, so two never write the same
/// files.
/// </summary>
public sealed class ChatLogKeeper : IAsyncDisposable {
    private readonly string _configDirectory;
    private readonly IAtRestProtection _protection;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private ChatLog? _current;
    // Closings and deletions, in the order asked.
    private Task _settled = Task.CompletedTask;

    /// <param name="log">Diagnostics, as <see cref="ChatLogOptions.Log"/>: never anything that was said.</param>
    public ChatLogKeeper(string configDirectory, IAtRestProtection protection, Action<string>? log = null) {
        this._configDirectory = configDirectory;
        this._protection = protection;
        this._log = log;
    }

    /// <summary>The log open now, or null.</summary>
    public ChatLog? Current {
        get {
            lock (this._lock) {
                return this._current;
            }
        }
    }

    /// <summary>Done once every closing and deletion asked for so far is.</summary>
    public Task Settled {
        get {
            lock (this._lock) {
                return this._settled;
            }
        }
    }

    /// <summary>Opens a character's log for a server address, closing the one open, if any.</summary>
    public ChatLog Open(ulong contentId, string serverUrl, long maxBytes) {
        lock (this._lock) {
            this.CloseLocked();
            var log = new ChatLog(new ChatLogOptions {
                Folder = ChatLogFiles.Folder(this._configDirectory, contentId, serverUrl),
                Protection = this._protection,
                MaxBytes = maxBytes,
                Log = this._log,
                StartAfter = this._settled,
            });
            this._current = log;
            return log;
        }
    }

    /// <summary>Closes the log open, if any, in the background: what it was given is still written.</summary>
    public void Close() {
        lock (this._lock) {
            this.CloseLocked();
        }
    }

    public void SetLimit(long maxBytes) => this.Current?.SetLimit(maxBytes);

    /// <summary>
    /// "Delete my chat log": every character's, on every server, on this computer. The open one (if any) deletes its own
    /// files and goes on, empty; the others are deleted once every earlier closing is done.
    /// </summary>
    /// <exception cref="IOException">Some of it couldn't be deleted.</exception>
    public Task DeleteAllAsync() {
        lock (this._lock) {
            var current = this._current;
            var before = this._settled;
            var work = Task.Run(async () => {
                try {
                    await before.ConfigureAwait(false);
                } catch {
                    // Said where it happened.
                }

                IOException? failed = null;
                if (current != null) {
                    try {
                        await current.DeleteAsync().ConfigureAwait(false);
                    } catch (IOException ex) {
                        failed = ex;
                    }
                }

                ChatLogFiles.DeleteAll(this._configDirectory, except: current?.Folder);
                if (failed != null) {
                    throw failed;
                }
            });
            this._settled = Settle(work);
            return work;
        }
    }

    /// <summary>How much room every chat log on this computer takes. Reads the disk: not on the game thread.</summary>
    public Task<long> SizeAsync() => Task.Run(() => ChatLogFiles.TotalSize(this._configDirectory));

    /// <summary>Closes the log open, and waits for every closing and deletion.</summary>
    public async ValueTask DisposeAsync() {
        this.Close();
        await this.Settled.ConfigureAwait(false);
    }

    private void CloseLocked() {
        if (this._current is not { } log) {
            return;
        }

        this._current = null;
        var before = this._settled;
        this._settled = Settle(Task.Run(async () => {
            await Settle(before).ConfigureAwait(false);
            await log.DisposeAsync().ConfigureAwait(false);
        }));
    }

    /// <summary>A task that finishes when <paramref name="task"/> does, and never fails.</summary>
    private static Task Settle(Task task) => task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
}
