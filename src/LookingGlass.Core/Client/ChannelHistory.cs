using System.Collections.Immutable;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>What a line in a channel's history is.</summary>
public enum HistoryLineKind {
    /// <summary>A message, from someone else or your own (once the server accepted it).</summary>
    Message,

    /// <summary>
    /// Something LookingGlass says about the channel (someone joined or left, set up LookingGlass again, the channel's new
    /// name, a message that was dropped): a <see cref="SessionNotice"/>, shown in the words of the mode set when it is shown.
    /// </summary>
    Notice,

    /// <summary>Feedback on what you did in a channel window: "Not sent: …". Only this client ever has it.</summary>
    Feedback,
}

/// <summary>Where a channel history's lines go as well (the chat log): called for every line held, in order.</summary>
public interface IChannelHistoryRecorder {
    /// <summary>A line was added. Called inside the history's lock: must only queue it, never block or throw.</summary>
    void Record(HistoryLine line);
}

/// <summary>
/// One line of a channel's history (see <see cref="ChannelHistory"/>).
/// </summary>
/// <param name="Seq">Its place among every line of every channel this client has held: later lines have higher numbers.</param>
/// <param name="Time">When it arrived here, by this computer's clock: lines are in the order they arrived.</param>
public sealed record HistoryLine(long Seq, string ChannelId, HistoryLineKind Kind, DateTimeOffset Time) {
    /// <summary>The sender of a message.</summary>
    public User? Sender { get; init; }

    /// <summary>A message you sent.</summary>
    public bool IsOwn { get; init; }

    /// <summary>When the sender sent a message, by their clock (signed with it).</summary>
    public DateTimeOffset SentAt { get; init; }

    /// <summary>A message's text and links, as received: not yet sanitised (see <see cref="LinkedText.ShownParts"/>).</summary>
    public LinkedText? Message { get; init; }

    /// <summary>A message of a kind this version can't show.</summary>
    public bool Unsupported { get; init; }

    /// <summary>
    /// A message sent while you were away, caught up when you came back (see <see cref="ChannelHistory.AddCaughtUp"/>): it
    /// shows the time it was sent (<see cref="SentAt"/>), not when it arrived.
    /// </summary>
    public bool CaughtUp { get; init; }

    /// <summary>
    /// A line from the chat log kept on this computer (see <see cref="ChatLog"/>), from an earlier session: shown above the
    /// lines since login, with its day. Its <see cref="Seq"/> is below zero, and it never counts as unread.
    /// </summary>
    public bool FromLog { get; init; }

    /// <summary>What a <see cref="HistoryLineKind.Notice"/> says, in both modes' words.</summary>
    public SessionNotice? Notice { get; init; }

    /// <summary>What a <see cref="HistoryLineKind.Feedback"/> line says, in the words of the mode set when it was said.</summary>
    public string? FeedbackText { get; init; }

    /// <summary>The tone of a notice or feedback line (see <see cref="NoticeColours"/>).</summary>
    public NoticeTone Tone { get; init; }

    /// <summary>A message from someone else: what counts as unread.</summary>
    public bool FromOthers => this.Kind == HistoryLineKind.Message && !this.IsOwn;

    /// <summary>A notice's or feedback line's text in a mode's words (see <see cref="SessionNotice.TextFor"/>); null for a message.</summary>
    public string? TextFor(bool advanced) => this.Kind switch {
        HistoryLineKind.Notice => this.Notice?.TextFor(advanced),
        HistoryLineKind.Feedback => this.FeedbackText,
        _ => null,
    };
}

/// <summary>
/// What each channel's window shows: the messages received and sent since login (your own as the server accepted them, and
/// those sent while you were away, caught up when you came back), LookingGlass's notices about the channel, and feedback
/// on what was typed in a window, oldest first, up to
/// <see cref="Capacity"/> lines per channel. Like the game's own chat log it lives only in memory: it starts empty at each
/// login (<see cref="Clear"/>), and nothing of it is written anywhere, unless the player keeps a chat log on their computer
/// (opt-in): then each line also goes to its recorder (<see cref="ChatLog"/>), which keeps the messages and information
/// lines, encrypted, to show again after the next login (<see cref="EarlierLines"/>). It belongs to the session, not to any
/// window, so closing and opening windows loses nothing. Safe from any thread: lines arrive on the session's threads,
/// windows read them on the draw thread, lock-free (<see cref="LinesOf"/> hands out an immutable copy).
/// </summary>
public sealed class ChannelHistory {
    public const int DefaultCapacity = 500;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ImmutableArray<HistoryLine>> _lines = new();
    // The messages held now, so one delivered twice isn't shown twice (see UnreadCounter: messages carry no ID here).
    private readonly HashSet<MessageKey> _held = new();
    // The channels' names as last seen, to say when one changes.
    private readonly Dictionary<string, string> _names = new();
    private long _seq;
    private int _generation;
    // Where each line goes as well as here: the chat log, while the player keeps one (see SetRecorder).
    private IChannelHistoryRecorder? _recorder;

    public ChannelHistory(int capacity = DefaultCapacity, TimeProvider? time = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.Capacity = capacity;
        this._time = time ?? TimeProvider.System;
    }

    /// <summary>The most lines kept per channel: the oldest go first.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Which session the history belongs to: <see cref="Clear"/> starts a new one. Lines added for an older one (a session
    /// that has since been replaced, whose events were already on their way) are dropped.
    /// </summary>
    public int Generation {
        get {
            lock (this._lock) {
                return this._generation;
            }
        }
    }

    /// <summary>A channel's lines, oldest first. An immutable copy: hold it as long as you like.</summary>
    public ImmutableArray<HistoryLine> LinesOf(string channelId) {
        lock (this._lock) {
            return this._lines.GetValueOrDefault(channelId, ImmutableArray<HistoryLine>.Empty);
        }
    }

    /// <summary>The <see cref="HistoryLine.Seq"/> of a channel's newest line, or 0 if it has none.</summary>
    public long LastSeq(string channelId) {
        var lines = this.LinesOf(channelId);
        return lines.IsEmpty ? 0 : lines[^1].Seq;
    }

    /// <summary>
    /// How many messages from others a channel holds after <paramref name="seq"/> and after the player's own newest message
    /// in it (talking in a channel reads what came before, as in the channel list): a window's count on a tab not selected.
    /// </summary>
    public int UnreadAfter(string channelId, long seq) {
        var lines = this.LinesOf(channelId);
        var count = 0;
        for (var i = lines.Length - 1; i >= 0 && lines[i].Seq > seq; i--) {
            if (lines[i] is { Kind: HistoryLineKind.Message, IsOwn: true }) {
                break;
            }

            if (lines[i].FromOthers) {
                count++;
            }
        }

        return count;
    }

    /// <summary>A message was delivered (from someone else, or your own once the server accepted it).</summary>
    /// <param name="generation">The <see cref="Generation"/> it is for; null for the current one.</param>
    /// <returns>False if it was dropped: already held, or for an older session.</returns>
    public bool Add(IncomingMessage message, int? generation = null) {
        lock (this._lock) {
            if (!this.IsCurrent(generation)) {
                return false;
            }

            var key = MessageKey.Of(message);
            if (!this._held.Add(key)) {
                return false;
            }

            this.Append(new HistoryLine(++this._seq, message.ChannelId, HistoryLineKind.Message, this._time.GetUtcNow()) {
                Sender = message.Sender,
                IsOwn = message.IsOwn,
                SentAt = message.Timestamp,
                Message = message.Unsupported ? null : message.Linked,
                Unsupported = message.Unsupported,
            });
            return true;
        }
    }

    /// <summary>
    /// A channel's messages sent while you were away (message catch-up): a dimmed line saying how many, then each, oldest
    /// first, marked <see cref="HistoryLine.CaughtUp"/>. One already held isn't added again (nor counted in that line).
    /// </summary>
    /// <param name="generation">The <see cref="Generation"/> they are for; null for the current one.</param>
    /// <returns>How many were added: none if all were held already, or they're for an older session.</returns>
    public int AddCaughtUp(CaughtUpMessages caughtUp, int? generation = null) {
        lock (this._lock) {
            if (!this.IsCurrent(generation)) {
                return 0;
            }

            var fresh = caughtUp.Messages.Where(message => !this._held.Contains(MessageKey.Of(message))).DistinctBy(MessageKey.Of).ToList();
            if (fresh.Count == 0) {
                return 0;
            }

            var now = this._time.GetUtcNow();
            this.Append(new HistoryLine(++this._seq, caughtUp.ChannelId, HistoryLineKind.Notice, now) {
                Notice = SessionNotice.Of(NoticeLevel.Info, Wording.Same(CatchUpChat.WindowSeparator(fresh.Count)), caughtUp.ChannelId),
                Tone = NoticeTone.Info,
            });
            foreach (var message in fresh) {
                this._held.Add(MessageKey.Of(message));
                this.Append(new HistoryLine(++this._seq, caughtUp.ChannelId, HistoryLineKind.Message, now) {
                    Sender = message.Sender,
                    IsOwn = message.IsOwn,
                    SentAt = message.Timestamp,
                    Message = message.Unsupported ? null : message.Linked,
                    Unsupported = message.Unsupported,
                    CaughtUp = true,
                });
            }

            return fresh.Count;
        }
    }

    /// <summary>A notice about a channel. One about no channel in particular isn't kept.</summary>
    /// <param name="generation">The <see cref="Generation"/> it is for; null for the current one.</param>
    /// <returns>False if it wasn't kept.</returns>
    public bool AddNotice(SessionNotice notice, int? generation = null) {
        if (notice.ChannelId is not { } channelId || notice.Level == NoticeLevel.Debug) {
            return false;
        }

        lock (this._lock) {
            if (!this.IsCurrent(generation)) {
                return false;
            }

            this.Append(new HistoryLine(++this._seq, channelId, HistoryLineKind.Notice, this._time.GetUtcNow()) {
                Notice = notice,
                Tone = NoticeColours.ToneOf(notice.Level, notice.Kind),
            });
            return true;
        }
    }

    /// <summary>Feedback on something done in a channel's window, such as a message that wasn't sent.</summary>
    /// <param name="generation">The <see cref="Generation"/> it was typed in; null for the current one.</param>
    /// <returns>False if it was dropped: about a message typed before the session changed.</returns>
    public bool AddFeedback(string channelId, NoticeTone tone, string text, int? generation = null) {
        lock (this._lock) {
            if (!this.IsCurrent(generation)) {
                return false;
            }

            this.Append(new HistoryLine(++this._seq, channelId, HistoryLineKind.Feedback, this._time.GetUtcNow()) {
                FeedbackText = text,
                Tone = tone,
            });
            return true;
        }
    }

    /// <summary>
    /// Says when a channel's name changes (a rename, or a new name brought by a new key): a notice in its history, the same
    /// in both modes. A name first becoming known (it was still being read) isn't a change.
    /// </summary>
    /// <returns>True if anything was said.</returns>
    public bool NoteNames(SessionSnapshot snapshot) {
        var said = false;
        lock (this._lock) {
            foreach (var channel in snapshot.Channels) {
                if (channel.Name is not { } name) {
                    continue;
                }

                if (this._names.TryGetValue(channel.Id, out var before) && before != name) {
                    var notice = SessionNotice.Of(NoticeLevel.Info, Wording.Same(Renamed(name)), channel.Id);
                    this.Append(new HistoryLine(++this._seq, channel.Id, HistoryLineKind.Notice, this._time.GetUtcNow()) {
                        Notice = notice,
                        Tone = NoticeTone.Info,
                    });
                    said = true;
                }

                this._names[channel.Id] = name;
            }
        }

        return said;
    }

    /// <summary>The line said when a channel's name changes.</summary>
    public static string Renamed(string name) => $"The channel is now called \"{name}\".";

    /// <summary>
    /// Drops the history of channels you're no longer in. Does nothing until the snapshot holds the complete channel list,
    /// as with command slots.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Retain(SessionSnapshot snapshot) {
        if (snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return false;
        }

        var listed = snapshot.Channels.Select(channel => channel.Id).ToHashSet();
        lock (this._lock) {
            var gone = this._lines.Keys.Where(id => !listed.Contains(id)).ToList();
            foreach (var channelId in gone) {
                this.Forget(channelId);
            }

            foreach (var channelId in this._names.Keys.Where(id => !listed.Contains(id)).ToList()) {
                this._names.Remove(channelId);
            }

            return gone.Count > 0;
        }
    }

    /// <summary>A new session (logged out, another character or server): everything goes.</summary>
    /// <param name="recorder">Where the new session's lines go as well (its chat log), or null: none.</param>
    /// <returns>The new <see cref="Generation"/>.</returns>
    public int Clear(IChannelHistoryRecorder? recorder = null) {
        lock (this._lock) {
            this._lines.Clear();
            this._held.Clear();
            this._names.Clear();
            this._recorder = recorder;
            return ++this._generation;
        }
    }

    /// <summary>
    /// Where the current session's lines go from now on as well as here (the chat log, turned on), or null (turned off).
    /// Lines already held aren't handed over.
    /// </summary>
    public void SetRecorder(IChannelHistoryRecorder? recorder) {
        lock (this._lock) {
            this._recorder = recorder;
        }
    }

    /// <summary>Call inside the lock.</summary>
    private bool IsCurrent(int? generation) => generation == null || generation == this._generation;

    /// <summary>Call inside the lock.</summary>
    private void Append(HistoryLine line) {
        var lines = this._lines.GetValueOrDefault(line.ChannelId, ImmutableArray<HistoryLine>.Empty);
        var builder = ImmutableArray.CreateBuilder<HistoryLine>(Math.Min(lines.Length + 1, this.Capacity));
        var drop = Math.Max(0, lines.Length + 1 - this.Capacity);
        for (var i = 0; i < drop; i++) {
            if (lines[i].Kind == HistoryLineKind.Message) {
                this._held.Remove(MessageKey.Of(lines[i]));
            }
        }

        for (var i = drop; i < lines.Length; i++) {
            builder.Add(lines[i]);
        }

        builder.Add(line);
        this._lines[line.ChannelId] = builder.MoveToImmutable();

        // In the lock, so the log has the lines in the order they're held. A recorder only queues it (never blocks), and
        // whatever it does wrong never keeps a line from being shown.
        try {
            this._recorder?.Record(line);
        } catch {
            // Its own problem: see ChatLog.Record.
        }
    }

    /// <summary>Call inside the lock.</summary>
    private void Forget(string channelId) {
        if (this._lines.Remove(channelId, out var lines)) {
            foreach (var line in lines.Where(line => line.Kind == HistoryLineKind.Message)) {
                this._held.Remove(MessageKey.Of(line));
            }
        }
    }

    /// <summary>The same sender, time (signed, to the millisecond) and text in the same channel is the same message.</summary>
    internal readonly record struct MessageKey(string ChannelId, long SenderId, DateTimeOffset Timestamp, string? Text) {
        public static MessageKey Of(IncomingMessage message) => new(message.ChannelId, message.Sender.UserId, message.Timestamp, message.Text);

        public static MessageKey Of(HistoryLine line) => new(line.ChannelId, line.Sender?.UserId ?? 0, line.SentAt, line.Message?.Text);
    }
}
