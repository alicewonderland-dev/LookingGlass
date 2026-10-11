using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using LookingGlass.Protocol;
using NSec.Cryptography;

namespace LookingGlass.Core.Client;

/// <summary>
/// How something small and secret is kept on this computer: the plugin's is the secrets file's (Windows DPAPI, or the local
/// key file under Wine), so the chat log's key is kept exactly as safe as the identity keys. Tests use their own.
/// </summary>
public interface IAtRestProtection {
    /// <summary>The data, protected. May throw.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>What <see cref="Protect"/> protected. Throws if it can't be read here (another computer, a missing key file).</summary>
    byte[] Unprotect(byte[] data);
}

/// <summary>How big the chat log may grow (the player's choice, in megabytes) and how it is cut into segments.</summary>
public static class ChatLogLimits {
    public const int MinMegabytes = 5;
    public const int MaxMegabytes = 1024;
    public const int DefaultMegabytes = 50;

    /// <summary>Within <see cref="MinMegabytes"/> and <see cref="MaxMegabytes"/>.</summary>
    public static int ClampMegabytes(int megabytes) => Math.Clamp(megabytes, MinMegabytes, MaxMegabytes);

    /// <summary>A size in megabytes (clamped) as bytes.</summary>
    public static long Bytes(int megabytes) => ClampMegabytes(megabytes) * 1024L * 1024;

    /// <summary>
    /// How big one segment file grows before the next is started: a sixteenth of the limit (so dropping the oldest drops
    /// about 6% of the log), at least 4 KiB and at most 16 MiB (so reading one back stays quick).
    /// </summary>
    public static long SegmentBytesFor(long maxBytes) => Math.Clamp(maxBytes / 16, 4 * 1024, 16 * 1024 * 1024);

    /// <summary>A size for people: "840 KB", "12.3 MB", "1.0 GB".</summary>
    public static string Describe(long bytes) => bytes switch {
        < 1024 * 1024 => $"{Math.Max(0, (bytes + 1023) / 1024)} KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB",
    };
}

/// <summary>A place in a chat log: a segment and a byte offset in it. Later records have greater positions.</summary>
public readonly record struct ChatLogPosition(long Segment, long Offset) : IComparable<ChatLogPosition> {
    public int CompareTo(ChatLogPosition other) => this.Segment != other.Segment ? this.Segment.CompareTo(other.Segment) : this.Offset.CompareTo(other.Offset);

    public static bool operator <(ChatLogPosition a, ChatLogPosition b) => a.CompareTo(b) < 0;

    public static bool operator >(ChatLogPosition a, ChatLogPosition b) => a.CompareTo(b) > 0;

    public static bool operator <=(ChatLogPosition a, ChatLogPosition b) => a.CompareTo(b) <= 0;

    public static bool operator >=(ChatLogPosition a, ChatLogPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>A page of a channel's older lines from the chat log.</summary>
/// <param name="Lines">Oldest first, marked <see cref="HistoryLine.FromLog"/>.</param>
/// <param name="Next">Where to read on from for the page before this one; null if the start of the log was reached.</param>
public sealed record LogPage(IReadOnlyList<HistoryLine> Lines, ChatLogPosition? Next) {
    public static readonly LogPage Empty = new([], null);
}

/// <summary>The chat log on disk can't be read on this computer (its key can't be unlocked, or is missing).</summary>
public sealed class ChatLogUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where chat logs are kept: one folder per character and server address, beside the secrets files, named as they are
/// (the character's content ID and 128 bits of the address's hash; see <see cref="ServerSecretFiles"/>).
/// </summary>
public static partial class ChatLogFiles {
    /// <summary>A character's chat log folder for a server address.</summary>
    public static string FolderName(ulong contentId, string serverUrl) => $"chatlog-{contentId:X16}-{ServerSecretFiles.AddressHash(serverUrl)}";

    public static string Folder(string configDirectory, ulong contentId, string serverUrl) => Path.Combine(configDirectory, FolderName(contentId, serverUrl));

    /// <summary>
    /// Where a chat log's text files are, if the player keeps it unencrypted too (see <see cref="ChatLogText"/>): a folder
    /// inside its own, with a folder per channel.
    /// </summary>
    public static string TextFolder(string logFolder) => Path.Combine(logFolder, ChatLogTextFiles.FolderName);

    /// <summary>Every chat log folder in the config folder (every character, every server).</summary>
    public static IReadOnlyList<string> Folders(string configDirectory) {
        try {
            return Directory.Exists(configDirectory)
                ? Directory.EnumerateDirectories(configDirectory, "chatlog-*").Where(path => OurFolder().IsMatch(Path.GetFileName(path))).ToList()
                : [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>How much room every chat log takes on disk, together. Files that can't be looked at count as nothing.</summary>
    public static long TotalSize(string configDirectory) => Folders(configDirectory).Sum(FolderSize);

    /// <summary>How much room one chat log folder takes: the encrypted log's files and its text files (never through a link).</summary>
    public static long FolderSize(string folder) {
        var info = new DirectoryInfo(folder);
        return info.Exists && !IsLink(info) ? SizeOf(info) : 0;
    }

    /// <summary>A folder's files and folders inside it; one that can't be looked at counts as nothing, the rest still count.</summary>
    private static long SizeOf(DirectoryInfo folder) {
        long size = 0;
        try {
            foreach (var file in folder.EnumerateFiles()) {
                size += SafeLength(file);
            }

            foreach (var inner in folder.EnumerateDirectories()) {
                if (!IsLink(inner)) {
                    size += SizeOf(inner);
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // What was counted before it stopped still counts.
        }

        return size;
    }

    private static bool IsLink(FileSystemInfo info) {
        try {
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>
    /// Deletes every chat log folder but <paramref name="except"/> (one a log has open, which deletes its own), with every
    /// file in it. Nothing outside the chat log folders is touched.
    /// </summary>
    /// <exception cref="IOException">Some couldn't be deleted (in use, say): the message says how many.</exception>
    public static void DeleteAll(string configDirectory, string? except = null) {
        var failed = 0;
        foreach (var folder in Folders(configDirectory)) {
            if (except != null && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            lock (AtomicFile.LockFor(folder)) {
                if (!DeleteFolder(folder)) {
                    failed++;
                }
            }
        }

        if (failed > 0) {
            throw new IOException(failed == 1
                ? "One chat log folder couldn't be deleted (it may be in use). Try again later."
                : $"{failed} chat log folders couldn't be deleted (they may be in use). Try again later.");
        }
    }

    /// <summary>
    /// Carries a character's chat log to a server's new address, with its identity (see <see cref="ServerMove"/>): its
    /// folder is renamed, so the log goes on there, and windows show its older lines at the new address. Only while no log
    /// is open for either address. Nothing happens if there is none at the old address, or the new address has one of its
    /// own already: two logs are never merged, and one is never written over.
    /// </summary>
    /// <returns>Whether it moved.</returns>
    public static bool MoveToAddress(string configDirectory, ulong contentId, string oldUrl, string newUrl) {
        var from = Path.GetFullPath(Folder(configDirectory, contentId, oldUrl));
        var to = Path.GetFullPath(Folder(configDirectory, contentId, newUrl));
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // Both folders' locks, always in the same order.
        var (first, second) = string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from);
        lock (AtomicFile.LockFor(first)) {
            lock (AtomicFile.LockFor(second)) {
                if (!Directory.Exists(from) || Directory.Exists(to) || File.Exists(to)) {
                    return false;
                }

                Directory.Move(from, to);
                return true;
            }
        }
    }

    /// <summary>
    /// Deletes a chat log folder: its files, its text files' folders, then the folder. A link inside it (made by hand) is
    /// removed, never followed, so nothing outside the folder is touched.
    /// </summary>
    /// <returns>False if anything was left.</returns>
    internal static bool DeleteFolder(string folder) {
        if (!Directory.Exists(folder)) {
            return true;
        }

        // A chat log folder that is itself a link (made by hand): the link goes, what it points at stays.
        var info = new DirectoryInfo(folder);
        if (IsLink(info)) {
            try {
                info.Delete(recursive: false);
                return true;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return false;
            }
        }

        var ok = DeleteInside(info);
        try {
            if (ok) {
                Directory.Delete(folder, recursive: false);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ok = false;
        }

        return ok;
    }

    private static bool DeleteInside(DirectoryInfo folder) {
        var ok = true;
        try {
            foreach (var file in folder.EnumerateFiles()) {
                try {
                    file.Delete();
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    ok = false;
                }
            }

            foreach (var inner in folder.EnumerateDirectories()) {
                try {
                    // A link goes as a link: what it points at stays.
                    if (IsLink(inner) || DeleteInside(inner)) {
                        inner.Delete(recursive: false);
                    } else {
                        ok = false;
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    ok = false;
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ok = false;
        }

        return ok;
    }

    private static long SafeLength(FileInfo file) {
        try {
            // Asked afresh: a folder listing's size of a file open for writing (the log's newest segment) lags behind on Windows.
            file.Refresh();
            return file.Length;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return 0;
        }
    }

    [GeneratedRegex("^chatlog-[0-9A-F]{16}-[0-9A-F]{32}$")]
    private static partial Regex OurFolder();
}

/// <summary>
/// The bytes of one line in the chat log, before it is encrypted. A version byte, then the line: messages (with their
/// sender, times, text and links, as the message's own content encoding, so links come back through the same checks as
/// a received message's) and LookingGlass's information lines about a channel, in both modes' words.
/// </summary>
internal static class ChatLogFormat {
    private const byte Version = 1;
    private const byte MessageKind = 0;
    private const byte NoticeKindByte = 1;

    /// <summary>
    /// Whether a line goes in the log: every message (others' and your own, live and caught up) and LookingGlass's
    /// information lines about the channel (joins, leaves, renames, invites, "sent while you were away"). Not warnings:
    /// they are about that moment, and shown when it happens. Not feedback ("Not sent"): it is about what was typed then.
    /// </summary>
    public static bool Keeps(HistoryLine line) => line.Kind switch {
        HistoryLineKind.Message => true,
        HistoryLineKind.Notice => line.Notice != null && line.Tone == NoticeTone.Info && line.Notice.Level == NoticeLevel.Info,
        _ => false,
    };

    public static byte[] Encode(HistoryLine line) {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Version);
            writer.Write(line.Kind == HistoryLineKind.Message ? MessageKind : NoticeKindByte);
            writer.Write(line.ChannelId);
            writer.Write(line.Time.UtcTicks);
            if (line.Kind == HistoryLineKind.Message) {
                var sender = line.Sender ?? new User();
                writer.Write(sender.UserId);
                writer.Write(sender.Name ?? "");
                writer.Write(sender.WorldId);
                writer.Write(sender.WorldName ?? "");
                var unsupported = line.Unsupported || line.Message == null;
                writer.Write((byte) ((line.IsOwn ? 1 : 0) | (unsupported ? 2 : 0) | (line.CaughtUp ? 4 : 0)));
                writer.Write(line.SentAt.UtcTicks);
                var content = unsupported ? [] : MessageContent.Encode(line.Message!).ToByteArray();
                writer.Write(content.Length);
                writer.Write(content);
            } else {
                var notice = line.Notice ?? throw new ArgumentException("A notice line without its notice.", nameof(line));
                writer.Write((byte) notice.Level);
                writer.Write(notice.Kind.ToString());
                writer.Write((byte) line.Tone);
                writer.Write(notice.Text);
                writer.Write(notice.Plain != null);
                writer.Write(notice.Plain ?? "");
            }
        }

        return buffer.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a line this version can read.</exception>
    public static HistoryLine Decode(byte[] data) {
        try {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
            if (reader.ReadByte() != Version) {
                throw new InvalidDataException("A chat log record of an unknown version.");
            }

            var kind = reader.ReadByte();
            var channelId = reader.ReadString();
            var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            if (kind == MessageKind) {
                var sender = new User { UserId = reader.ReadInt64(), Name = reader.ReadString(), WorldId = reader.ReadUInt32(), WorldName = reader.ReadString() };
                var flags = reader.ReadByte();
                var sentAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                var length = reader.ReadInt32();
                if (length < 0 || length > data.Length) {
                    throw new InvalidDataException("A chat log record's message is cut short.");
                }

                var content = reader.ReadBytes(length);
                LinkedText? message = null;
                if ((flags & 2) == 0) {
                    // Through the same checks as a received message's links (MessageContent.ValidLinks).
                    message = MessageContent.Decode(Content.Parser.ParseFrom(content));
                }

                return new HistoryLine(0, channelId, HistoryLineKind.Message, time) {
                    Sender = sender,
                    IsOwn = (flags & 1) != 0,
                    SentAt = sentAt,
                    Message = message,
                    Unsupported = message == null,
                    CaughtUp = (flags & 4) != 0,
                    FromLog = true,
                };
            }

            if (kind == NoticeKindByte) {
                var level = (NoticeLevel) reader.ReadByte();
                var noticeKind = Enum.TryParse<NoticeKind>(reader.ReadString(), out var parsed) ? parsed : NoticeKind.General;
                var tone = (NoticeTone) reader.ReadByte();
                var text = reader.ReadString();
                var hasPlain = reader.ReadBoolean();
                var plain = reader.ReadString();
                return new HistoryLine(0, channelId, HistoryLineKind.Notice, time) {
                    Notice = new SessionNotice(level, text, channelId) { Plain = hasPlain ? plain : null, Kind = noticeKind },
                    Tone = tone,
                    FromLog = true,
                };
            }

            throw new InvalidDataException("A chat log record of an unknown kind.");
        } catch (Exception ex) when (ex is EndOfStreamException or InvalidProtocolBufferException or ArgumentException or FormatException or DecoderFallbackException) {
            // Never the record's bytes in the message: it holds what was said.
            throw new InvalidDataException("A chat log record couldn't be read.", ex);
        }
    }
}

/// <summary>
/// A chat log on disk: one folder of segment files, each a header and then encrypted records, appended to in order, and a
/// key file. Not thread-safe: <see cref="ChatLog"/> runs it on one background thread.
/// <para>
/// <b>Encryption.</b> A random 256-bit key per log, kept in <c>chatlog.key</c> protected as the secrets file is
/// (<see cref="IAtRestProtection"/>: DPAPI, or the local key file under Wine). Each record is the line's bytes
/// (<see cref="ChatLogFormat"/>) sealed with XChaCha20-Poly1305 under a random nonce, bound to its segment and offset (the
/// associated data), so a record can't be moved, and one that is cut short or damaged is found and skipped on its own.
/// </para>
/// <para>
/// <b>Crash safety.</b> A record is appended in one write: a length, the nonce, the ciphertext. A crash can leave only the
/// last record cut short; opening the log again cuts the last segment back to its last whole record, so what follows is
/// readable too.
/// </para>
/// <para>
/// <b>Size.</b> Segments are started once one reaches <see cref="ChatLogLimits.SegmentBytesFor"/>; whenever the folder
/// would grow past the limit, the oldest segments are deleted, whole, first.
/// </para>
/// <para>
/// <b>Text files.</b> If the player keeps the chat log unencrypted too, its text files (<see cref="ChatLogTextFiles"/>, in
/// a folder inside this one) count towards the same limit. When the two together would pass it, the oldest lines go
/// first, from either: the text files' oldest lines, a line at a time, while they arrived no later than the oldest
/// segment's newest record, otherwise the oldest segment (see <see cref="FreeOldest"/>). So both reach back about as far,
/// within a segment, at any size: a month of text bigger than the whole limit is trimmed from its front, never wiped.
/// </para>
/// </summary>
internal sealed class ChatLogStore : IDisposable {
    public const string KeyFileName = "chatlog.key";
    public const string SegmentExtension = ".lgl";

    private const int HeaderSize = 8;
    private const int LengthSize = 4;
    private const int MaxPlaintext = 64 * 1024;
    private static readonly byte[] SegmentMagic = "LGCL"u8.ToArray();
    private static readonly byte[] KeyMagic = "LGCK"u8.ToArray();
    private const byte FormatVersion = 1;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    private static readonly int Overhead = LengthSize + Aead.NonceSize + Aead.TagSize;

    private readonly string _folder;
    private readonly IAtRestProtection _protection;
    private readonly ChatLogTextFiles _text;
    private Exception? _textProblem;
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<long, HashSet<string>> _channelsIn = new();
    private (long Number, List<(long Offset, HistoryLine Line)> Records)? _cached;
    private long _maxBytes;
    private Key? _key;
    private FileStream? _active;
    private bool _opened;
    private string? _unreadable;
    // The key file's size (with its backup), counted in the log's size; measured when first needed after it changes.
    private long? _keyBytes;

    /// <param name="zone">The computer's time zone, for the text files' times and months; tests give their own.</param>
    public ChatLogStore(string folder, IAtRestProtection protection, long maxBytes, TimeZoneInfo? zone = null) {
        this._folder = folder;
        this._protection = protection;
        this._maxBytes = maxBytes;
        this._text = new ChatLogTextFiles(folder, zone ?? TimeZoneInfo.Local);
    }

    public string Folder => this._folder;

    /// <summary>Where the text files go (see <see cref="ChatLogFiles.TextFolder"/>).</summary>
    public string TextFolder => this._text.Root;

    /// <summary>Where this session's lines start: everything before it is from earlier sessions.</summary>
    public ChatLogPosition SessionStart { get; private set; }

    /// <summary>Why the log on disk can't be read here, or null.</summary>
    public string? Unreadable => this._unreadable;

    /// <summary>How much room the folder takes: the segments, the key file and the text files.</summary>
    public long Size => this.SegmentBytes + this.KeyFileBytes() + this._text.Size;

    private long SegmentBytes => this._segments.Sum(segment => segment.Size);

    /// <summary>Segments may be deleted to make room only once the log is open, and only if it can be read here.</summary>
    private bool CanTrim => this._opened && this._unreadable == null;

    /// <summary>How many records were skipped as damaged since opening (for tests and diagnostics).</summary>
    public int Skipped { get; private set; }

    private long SegmentLimit => ChatLogLimits.SegmentBytesFor(this._maxBytes);

    /// <summary>
    /// Opens what is on disk, if anything: unlocks the key, lists the segments, and cuts a last record left short by a crash.
    /// Nothing is created until something is appended.
    /// </summary>
    /// <exception cref="ChatLogUnreadableException">There are segments, but their key can't be unlocked here.</exception>
    public void Open() {
        if (this._opened) {
            return;
        }

        // The text files are counted whatever becomes of the rest: they need no key.
        this._text.TryScan();
        try {
            this.OpenFiles();
            this._opened = true;
        } catch (ChatLogUnreadableException) {
            // Stays so until it is deleted.
            this._opened = true;
            throw;
        }
    }

    private void OpenFiles() {
        this._segments.Clear();
        this._key?.Dispose();
        this._key = null;
        if (!Directory.Exists(this._folder)) {
            this.SessionStart = new ChatLogPosition(0, 0);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(this._folder, "*" + SegmentExtension)) {
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.None, null, out var number) && number > 0) {
                var info = new FileInfo(file);
                // As old as its newest record: when it was last written.
                this._segments.Add(new Segment(number, info.Length) { Last = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) });
            }
        }

        this._segments.Sort((a, b) => a.Number.CompareTo(b.Number));
        this._key = this.LoadKey();
        if (this._key == null) {
            if (this._segments.Count > 0) {
                this._unreadable = "the file that unlocks it is missing";
                throw new ChatLogUnreadableException(this._unreadable);
            }
        } else if (this._segments.Count > 0) {
            this.Repair(this._segments[^1]);
        }

        this.SessionStart = this._segments.Count > 0 ? new ChatLogPosition(this._segments[^1].Number, this._segments[^1].Size) : new ChatLogPosition(0, 0);
    }

    /// <summary>Appends a line (see <see cref="ChatLogFormat.Keeps"/> for which), making room first.</summary>
    /// <returns>False if it wasn't kept: the log can't be read here, or the line is too big for the limit.</returns>
    public bool Append(HistoryLine line) {
        this.Open();
        if (this._unreadable != null) {
            return false;
        }

        var plaintext = ChatLogFormat.Encode(line);
        if (plaintext.Length > MaxPlaintext) {
            return false;
        }

        this._key ??= this.CreateKey();
        var frame = Overhead + plaintext.Length;
        if (HeaderSize + frame > this._maxBytes - this.KeyFileBytes()) {
            return false;
        }

        var active = this._segments.Count > 0 ? this._segments[^1] : null;
        var newSegment = active == null || (active.Size + frame > this.SegmentLimit && active.Size > HeaderSize);
        var counted = false;
        while (this.Size + frame + (newSegment ? HeaderSize : 0) > this._maxBytes) {
            if (!counted) {
                // Full: the text files counted afresh first (some may have been deleted by hand).
                counted = true;
                this._text.TryScan();
                continue;
            }

            if (!this.FreeOldest(this.Size + frame + (newSegment ? HeaderSize : 0) - this._maxBytes, out var deleted)) {
                break;
            }

            if (deleted != null && deleted == active) {
                newSegment = true;
            }
        }

        if (this.Size + frame + (newSegment ? HeaderSize : 0) > this._maxBytes) {
            return false;
        }

        if (!newSegment && this._active == null) {
            try {
                this._active = this.OpenActive(active!);
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                // Gone from under it (deleted by hand, say): forget it, and start the next.
                this._segments.Remove(active!);
                newSegment = true;
            }
        }

        if (newSegment) {
            active = this.StartSegment();
        }

        var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
        var bytes = new byte[frame];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, Aead.NonceSize + plaintext.Length + Aead.TagSize);
        nonce.CopyTo(bytes, LengthSize);
        Aead.Encrypt(this._key, nonce, AssociatedData(active!.Number, active.Size), plaintext, bytes.AsSpan(LengthSize + Aead.NonceSize));
        try {
            this._active!.Write(bytes);
        } catch {
            // Whatever reached the file is cut off when it is next opened.
            this.CloseActive();
            throw;
        }

        active.Size += frame;
        active.Last = line.Time;
        return true;
    }

    /// <summary>
    /// Queues a line for the text files (see <see cref="ChatLogText.Format"/>), making room first under the limit both share.
    /// Needs no key: done whether the encrypted log is open, failing for now, or can't be read here (then its segments are
    /// counted, never deleted). Written by <see cref="FlushText"/>.
    /// </summary>
    /// <returns>False if it wasn't kept: too big for the limit, or no room could be made.</returns>
    /// <exception cref="IOException">The text folder couldn't be looked at.</exception>
    public bool AppendText(HistoryLine line, ChannelLabel label) {
        this._text.EnsureScanned();
        var bytes = Encoding.UTF8.GetBytes(ChatLogText.Format(line, label.Tag, this._text.Zone) + "\r\n");
        if (bytes.Length > this._maxBytes - this.KeyFileBytes()) {
            return false;
        }

        var counted = false;
        while (this.Size + bytes.Length > this._maxBytes) {
            if (!counted) {
                counted = true;
                this._text.TryScan();
                continue;
            }

            if (!this.FreeOldest(this.Size + bytes.Length - this._maxBytes, out _)) {
                break;
            }
        }

        if (this.Size + bytes.Length > this._maxBytes) {
            return false;
        }

        this._text.Add(line, label, bytes);
        return true;
    }

    /// <summary>Appends the text lines queued to their files, and hands them to the operating system.</summary>
    /// <exception cref="IOException">Some couldn't be written (they are dropped).</exception>
    public void FlushText() => this._text.Write();

    /// <summary>Text lines are queued, not written yet.</summary>
    public bool TextPending => this._text.HasPending;

    /// <summary>
    /// Frees some room, the oldest lines first across both (see "Chat log on this computer" in docs/design.md): the text
    /// files' oldest lines while they arrived no later than the oldest segment's newest record (trimmed a line at a time, at
    /// least <paramref name="over"/> bytes, at most about a segment's worth, so files aren't rewritten for every line), and
    /// otherwise the oldest segment, whole. So the text files reach back about as far as the encrypted log, within a segment,
    /// at any size. Segments only if this log may change them (<see cref="CanTrim"/>); a text file that can't be trimmed is
    /// left (and the next oldest tried), never in the way of the encrypted log.
    /// </summary>
    /// <param name="deleted">The segment deleted, if one was.</param>
    /// <returns>False if nothing could be freed.</returns>
    private bool FreeOldest(long over, out Segment? deleted) {
        deleted = null;
        if (this._text.HasPending) {
            // Lines queued in this batch are only trimmed once on disk: written first (a big batch would otherwise crowd out
            // everything else, and then itself).
            try {
                this._text.Write();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                this._textProblem ??= ex;
            }
        }

        var segment = this.CanTrim && this._segments.Count > 0 ? this._segments[0] : null;
        if (this._text.OldestLine() is { } oldest && (segment == null || oldest <= segment.Last)) {
            var stuck = this._text.StuckCount;
            long freed = 0;
            try {
                freed = this._text.Trim(segment?.Last, Math.Max(over, this.SegmentLimit));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Said once by the log (TakeTextProblem); the segment goes instead.
                this._textProblem ??= ex;
            }

            if (freed > 0 || this._text.StuckCount > stuck) {
                return true;
            }
        }

        if (segment == null) {
            return false;
        }

        deleted = segment;
        this.DeleteOldest();
        return true;
    }

    /// <summary>What went wrong making room in the text files since this was last asked, if anything.</summary>
    public Exception? TakeTextProblem() {
        var problem = this._textProblem ?? this._text.TakeProblem();
        this._textProblem = null;
        return problem;
    }

    /// <summary>
    /// Starts the next segment: its file first, and only once that holds its header is it one of the log's, so a file that
    /// couldn't be made leaves nothing behind that later lines would go to. A number whose file (or anything else) is
    /// already there, left by a deletion that failed, is skipped, never written over.
    /// </summary>
    private Segment StartSegment() {
        this.CloseActive();
        var number = this._segments.Count > 0 ? this._segments[^1].Number + 1 : Math.Max(1, this.SessionStart.Segment + 1);
        while (File.Exists(this.PathOf(number)) || Directory.Exists(this.PathOf(number))) {
            number++;
        }

        Directory.CreateDirectory(this._folder);
        var path = this.PathOf(number);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        try {
            stream.Write([.. SegmentMagic, FormatVersion, 0, 0, 0]);
        } catch {
            stream.Dispose();
            TryDelete(path);
            throw;
        }

        this._active = stream;
        var segment = new Segment(number, HeaderSize);
        this._segments.Add(segment);
        return segment;
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Skipped by number next time.
        }
    }

    /// <summary>Hands what was appended to the operating system, so a crash of the game loses none of it.</summary>
    public void Flush() => this._active?.Flush();

    /// <summary>
    /// A new limit: the oldest go at once if the log is over it, from the segments and the text files alike (only the text
    /// files if the log can't be read here, or couldn't be opened).
    /// </summary>
    public void SetLimit(long maxBytes) {
        this._maxBytes = maxBytes;
        // Even while the log couldn't be opened (its segments are counted, not deleted): the text files keep to the limit.
        this._text.TryScan();
        while (this.Size > this._maxBytes && this.FreeOldest(this.Size - this._maxBytes, out _)) {
        }
    }

    /// <summary>
    /// A channel's lines from before <paramref name="before"/> (and from before this session), newest
    /// <paramref name="max"/>, oldest first. Damaged records are skipped.
    /// </summary>
    public LogPage ReadBefore(string channelId, ChatLogPosition before, int max) {
        this.Open();
        if (this._unreadable != null || this._key == null || max <= 0) {
            return LogPage.Empty;
        }

        if (before > this.SessionStart) {
            before = this.SessionStart;
        }

        var found = new List<(ChatLogPosition Position, HistoryLine Line)>();
        foreach (var segment in this._segments.Where(segment => segment.Number <= before.Segment).OrderByDescending(segment => segment.Number).ToList()) {
            if (this._channelsIn.TryGetValue(segment.Number, out var channels) && !channels.Contains(channelId)) {
                continue;
            }

            var records = this.Records(segment);
            for (var i = records.Count - 1; i >= 0; i--) {
                var position = new ChatLogPosition(segment.Number, records[i].Offset);
                if (position >= before || records[i].Line.ChannelId != channelId) {
                    continue;
                }

                found.Add((position, records[i].Line));
                if (found.Count == max) {
                    found.Reverse();
                    return new LogPage(found.Select(entry => entry.Line).ToList(), found[0].Position);
                }
            }
        }

        found.Reverse();
        return new LogPage(found.Select(entry => entry.Line).ToList(), null);
    }

    /// <summary>
    /// Deletes the whole log: every file in its folder, its text files too, and the folder. Logging goes on afterwards in a
    /// new log, with a new key, which shows nothing from before.
    /// </summary>
    /// <exception cref="IOException">Something couldn't be deleted.</exception>
    public void DeleteAll() {
        this.CloseActive();
        this._text.Reset();
        this._key?.Dispose();
        this._key = null;
        this._segments.Clear();
        this._channelsIn.Clear();
        this._cached = null;
        this._unreadable = null;
        this._keyBytes = null;
        this._opened = true;
        this.SessionStart = new ChatLogPosition(0, 0);
        lock (AtomicFile.LockFor(this._folder)) {
            if (!ChatLogFiles.DeleteFolder(this._folder)) {
                throw new IOException("Some of the chat log's files couldn't be deleted (they may be in use). Try again later.");
            }
        }
    }

    public void Dispose() {
        this.CloseActive();
        this._key?.Dispose();
        this._key = null;
    }

    // ================================================================ key

    private string KeyPath => Path.Combine(this._folder, KeyFileName);

    private long KeyFileBytes() => this._keyBytes ??= this.MeasureKeyFile();

    private long MeasureKeyFile() {
        long size = 0;
        foreach (var path in new[] { this.KeyPath, AtomicFile.BackupPath(this.KeyPath) }) {
            try {
                if (File.Exists(path)) {
                    size += new FileInfo(path).Length;
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Counted as nothing.
            }
        }

        return size;
    }

    /// <returns>The key, or null if there is no key file.</returns>
    /// <exception cref="ChatLogUnreadableException">There is one, but it can't be unlocked here.</exception>
    private Key? LoadKey() {
        byte[]? raw;
        try {
            raw = AtomicFile.Read(this.KeyPath, data => {
                if (data.Length <= KeyMagic.Length || !data.AsSpan(0, KeyMagic.Length).SequenceEqual(KeyMagic)) {
                    throw new InvalidDataException("The chat log's key file isn't one.");
                }

                var key = this._protection.Unprotect(data[KeyMagic.Length..]);
                return key.Length == Aead.KeySize ? key : throw new InvalidDataException("The chat log's key file is damaged.");
            });
        } catch (Exception ex) when (ex is InvalidDataException or CryptographicException or PlatformNotSupportedException) {
            // It was read, and can't be unlocked here. Not so a file that couldn't be read just now (in use, access denied):
            // that goes to whoever opened the log, to try again, never mistaken for a log to give up on, or a key to replace.
            if (this._segments.Count == 0) {
                // Nothing it could unlock: a new key replaces it when something is written.
                return null;
            }

            this._unreadable = ex.Message;
            throw new ChatLogUnreadableException(this._unreadable, ex);
        }

        return raw == null ? null : Key.Import(Aead, raw, KeyBlobFormat.RawSymmetricKey);
    }

    private Key CreateKey() {
        Directory.CreateDirectory(this._folder);
        var raw = RandomNumberGenerator.GetBytes(Aead.KeySize);
        AtomicFile.Write(this.KeyPath, [.. KeyMagic, .. this._protection.Protect(raw)]);
        this._keyBytes = null;
        var key = Key.Import(Aead, raw, KeyBlobFormat.RawSymmetricKey);
        CryptographicOperations.ZeroMemory(raw);
        return key;
    }

    // ================================================================ segments

    private string PathOf(long number) => Path.Combine(this._folder, number.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + SegmentExtension);

    private static byte[] AssociatedData(long segment, long offset) {
        var data = new byte[SegmentMagic.Length + 1 + 16];
        SegmentMagic.CopyTo(data, 0);
        data[SegmentMagic.Length] = FormatVersion;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(SegmentMagic.Length + 1), segment);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(SegmentMagic.Length + 9), offset);
        return data;
    }

    private FileStream OpenActive(Segment segment) {
        var stream = new FileStream(this.PathOf(segment.Number), FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
        stream.Seek(segment.Size, SeekOrigin.Begin);
        return stream;
    }

    private void CloseActive() {
        try {
            this._active?.Dispose();
        } catch (IOException) {
            // What didn't reach the file is cut off when it is next opened.
        }

        this._active = null;
    }

    private void DeleteOldest() {
        var oldest = this._segments[0];
        if (this._segments.Count == 1) {
            this.CloseActive();
        }

        File.Delete(this.PathOf(oldest.Number));
        this._segments.RemoveAt(0);
        this._channelsIn.Remove(oldest.Number);
        if (this._cached?.Number == oldest.Number) {
            this._cached = null;
        }
    }

    /// <summary>
    /// Cuts a segment back to the end of its last record that opens, as a crash may have left it: only a tail that can't be
    /// read goes. A damaged record before whole ones is left (it is skipped when read), and so are they.
    /// </summary>
    private void Repair(Segment segment) {
        var bytes = this.ReadSegment(segment.Number, segment.Size);
        var end = ValidHeader(bytes) ? HeaderSize : 0;
        if (end > 0) {
            foreach (var (offset, length, _) in this.Frames(segment.Number, bytes)) {
                end = (int) offset + length;
            }
        }

        if (end == 0) {
            // Not even a header: nothing of it can be read.
            File.Delete(this.PathOf(segment.Number));
            this._segments.Remove(segment);
            return;
        }

        if (end < segment.Size) {
            using var stream = new FileStream(this.PathOf(segment.Number), FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
            stream.SetLength(end);
            segment.Size = end;
        }
    }

    /// <summary>A segment's records up to where this session's start (the part that no longer changes), decrypted and read.</summary>
    private List<(long Offset, HistoryLine Line)> Records(Segment segment) {
        if (this._cached is { } cached && cached.Number == segment.Number) {
            return cached.Records;
        }

        var upTo = segment.Number == this.SessionStart.Segment ? this.SessionStart.Offset : segment.Size;
        var records = new List<(long, HistoryLine)>();
        var channels = new HashSet<string>(StringComparer.Ordinal);
        byte[] bytes;
        try {
            bytes = this.ReadSegment(segment.Number, upTo);
        } catch (FileNotFoundException) {
            bytes = [];
        } catch (DirectoryNotFoundException) {
            bytes = [];
        }

        if (ValidHeader(bytes)) {
            foreach (var (offset, _, plaintext) in this.Frames(segment.Number, bytes)) {
                try {
                    var line = ChatLogFormat.Decode(plaintext);
                    records.Add((offset, line));
                    channels.Add(line.ChannelId);
                } catch (InvalidDataException) {
                    this.Skipped++;
                }
            }
        }

        this._channelsIn[segment.Number] = channels;
        this._cached = (segment.Number, records);
        return records;
    }

    private byte[] ReadSegment(long number, long upTo) {
        using var stream = new FileStream(this.PathOf(number), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int) Math.Min(upTo, stream.Length);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool ValidHeader(byte[] bytes) =>
        bytes.Length >= HeaderSize && bytes.AsSpan(0, SegmentMagic.Length).SequenceEqual(SegmentMagic) && bytes[SegmentMagic.Length] == FormatVersion;

    /// <summary>
    /// The records of a segment that decrypt: each one's offset, length (with its length field) and plaintext. Where one
    /// doesn't (damaged, its length field too, or cut short at the end), the next whole record after it is looked for, byte
    /// by byte: a record only opens at its own offset (the associated data), so nothing in between can pass for one.
    /// </summary>
    private IEnumerable<(long Offset, int Length, byte[] Plaintext)> Frames(long segment, byte[] bytes) {
        var at = HeaderSize;
        while (at + LengthSize <= bytes.Length) {
            if (this.TryOpen(segment, bytes, at) is { } record) {
                yield return (at, record.Length, record.Plaintext);
                at += record.Length;
                continue;
            }

            this.Skipped++;
            var next = -1;
            for (var candidate = at + 1; candidate + LengthSize <= bytes.Length; candidate++) {
                if (this.TryOpen(segment, bytes, candidate) != null) {
                    next = candidate;
                    break;
                }
            }

            if (next < 0) {
                yield break;
            }

            at = next;
        }
    }

    /// <summary>The record at <paramref name="at"/>, if one opens there: its length (with its length field) and plaintext.</summary>
    private (int Length, byte[] Plaintext)? TryOpen(long segment, byte[] bytes, int at) {
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (length < Aead.NonceSize + Aead.TagSize || length > MaxPlaintext + Aead.NonceSize + Aead.TagSize || at + LengthSize + length > bytes.Length) {
            return null;
        }

        var nonce = bytes.AsSpan(at + LengthSize, Aead.NonceSize);
        var ciphertext = bytes.AsSpan(at + LengthSize + Aead.NonceSize, length - Aead.NonceSize);
        var plaintext = new byte[ciphertext.Length - Aead.TagSize];
        return Aead.Decrypt(this._key!, nonce, AssociatedData(segment, at), ciphertext, plaintext) ? (LengthSize + length, plaintext) : null;
    }

    private sealed class Segment(long number, long size) {
        public long Number { get; } = number;
        public long Size { get; set; } = size;

        /// <summary>When its newest record arrived (or, for one from an earlier session, when its file was last written).</summary>
        public DateTimeOffset Last { get; set; } = DateTimeOffset.MinValue;
    }
}
