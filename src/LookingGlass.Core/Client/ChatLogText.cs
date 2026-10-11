using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// What the chat history's text files call a channel, as it is when a line is kept: its tag in front of each line ("[sky]",
/// "[LGC3]", as in game chat), and its name and command number for its folder's name.
/// </summary>
/// <param name="Tag">As <see cref="ChannelTag.For"/> makes it.</param>
/// <param name="Name">The channel's name, or null if not known (yet, or any more).</param>
/// <param name="Slot">Its command number, if it has one.</param>
public sealed record ChannelLabel(string Tag, string? Name = null, int? Slot = null);

/// <summary>
/// The chat history kept unencrypted (opt-in, beside the encrypted log; see "Chat log on this computer" in docs/design.md):
/// what a line says in a text file, and what a channel's folder is called. One line per entry, in local time, links as
/// their "[name]" text and information lines in simple mode's words; everyone else's words sanitised as they are wherever
/// they are shown, so nothing they wrote can break a line, start one, or hide in it.
/// </summary>
public static partial class ChatLogText {
    /// <summary>The longest a channel folder's readable part may be, in characters.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>What a message this version can't show says, as in channel windows.</summary>
    public const string UnsupportedMessage = "(a message type this version can't show)";

    private const string NoName = "Channel";

    // Not allowed in a file or folder name on Windows (and so not when a backup or sync copies it there).
    private const string Forbidden = "<>:\"/\\|?*";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// A line as written to its text file (without its line end): "[2026-10-10 21:03] [sky] Alice Liddell@Twintania: pulling
    /// at 9". A message caught up from while the player was away shows when it was sent, as channel windows show it, and when
    /// it arrived: "[2026-10-10 20:15, arrived 21:03]".
    /// </summary>
    /// <param name="tag">The channel's tag, as in game chat.</param>
    /// <param name="zone">The computer's time zone; tests give their own.</param>
    public static string Format(HistoryLine line, string tag, TimeZoneInfo? zone = null) {
        zone ??= TimeZoneInfo.Local;
        var arrived = TimeZoneInfo.ConvertTime(line.Time, zone);
        string time;
        if (line is { Kind: HistoryLineKind.Message, CaughtUp: true }) {
            var sent = TimeZoneInfo.ConvertTime(line.SentAt, zone);
            time = $"[{Stamp(sent)}, arrived {(sent.Date == arrived.Date ? Clock(arrived) : Stamp(arrived))}]";
        } else {
            time = $"[{Stamp(arrived)}]";
        }

        var shownTag = OneLine(TextSanitizer.Clean(tag, TextSanitizer.MaxNameLength));
        if (line.Kind != HistoryLineKind.Message) {
            // LookingGlass's words, but they hold others' (a name, a channel's name): sanitised as in windows.
            return $"{time} {shownTag} {OneLine(TextSanitizer.Clean(line.TextFor(advanced: false)))}";
        }

        var name = OneLine(TextSanitizer.Name(line.Sender?.Name));
        var world = OneLine(TextSanitizer.Name(line.Sender?.WorldName));
        string body;
        if (line.Unsupported || line.Message == null) {
            body = UnsupportedMessage;
        } else {
            // As windows show a link that doesn't check out against the game's data: its "[name]", sanitised.
            body = string.Concat(line.Message.ShownParts().Select(part => part switch {
                MessagePart.Text text => text.Value,
                MessagePart.Link link => link.Fallback,
                _ => "",
            }));
        }

        return $"{time} {shownTag} {name}@{world}: {OneLine(body)}";
    }

    /// <summary>The month a line's file is for ("2026-10"): when it arrived, by the computer's clock and zone.</summary>
    public static string MonthOf(DateTimeOffset time, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(time, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// A channel's folder: its name and number as they are now, readable ("Sky Pirates (LGC3)"), then 16 hex digits of its
    /// ID's hash, which never change, so a rename finds (and renames) the same folder and two channels called the same never
    /// share one. Safe as a folder name on Windows (and on whatever a backup copies it to): no forbidden or invisible
    /// characters, no reserved device name (CON, NUL, COM1...), no leading or trailing spaces or dots, and short.
    /// </summary>
    public static string ChannelFolderName(string channelId, string? name, int? slot) {
        var label = SafeLabel(name);
        if (slot is >= 1 and <= CommandSlots.Count) {
            label = $"{label} (LGC{slot})";
        }

        return $"{label} {ChannelHash(channelId)}";
    }

    /// <summary>
    /// The part of a channel's folder name that never changes: 64 bits of its ID's SHA-256, in hex. Channel IDs are chosen by
    /// whoever creates a channel, so fewer bits could be made to clash on purpose (32 bits take minutes); 64 can't.
    /// </summary>
    public static string ChannelHash(string channelId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channelId)), 0, 8).ToLowerInvariant();

    /// <summary>Whether a folder name is a channel folder's, and if so its <see cref="ChannelHash"/>.</summary>
    internal static string? HashOf(string folderName) => ChannelFolder().Match(folderName) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Whether a file name is a month's text file's ("2026-10.txt").</summary>
    internal static bool IsMonthFile(string fileName) => MonthFile().IsMatch(fileName);

    /// <summary>
    /// When a line in a text file arrived, read back from its start (to the minute): its time, or for a caught-up message
    /// the time after "arrived". Null if it doesn't start as <see cref="Format"/> starts a line (edited, or cut short).
    /// </summary>
    internal static DateTimeOffset? ArrivalOf(string line, TimeZoneInfo zone) {
        var match = LineStart().Match(line);
        if (!match.Success) {
            return null;
        }

        var stamp = match.Groups[3].Success
            ? (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value[..10]) + " " + match.Groups[3].Value
            : match.Groups[1].Value;
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) {
            return null;
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string Stamp(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Clock(DateTimeOffset time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// <see cref="TextSanitizer.Clean"/> leaves Unicode's own line and paragraph separators, which some editors break lines
    /// at: a space instead, so one entry is always one line.
    /// </summary>
    private static string OneLine(string text) {
        if (!text.Any(c => char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text) {
            builder.Append(char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }

        return builder.ToString();
    }

    private static string SafeLabel(string? name) {
        var builder = new StringBuilder();
        foreach (var rune in TextSanitizer.Name(name).EnumerateRunes()) {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.IsAscii && (rune.Value < 32 || Forbidden.Contains((char) rune.Value))) {
                builder.Append('_');
            } else if (category == UnicodeCategory.PrivateUse) {
                // The game's own icons: nothing a file manager can show.
            } else if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) {
                if (builder.Length > 0 && builder[^1] != ' ') {
                    builder.Append(' ');
                }
            } else {
                builder.Append(rune.ToString());
            }
        }

        // Short, cut between characters, never inside one.
        var label = builder.ToString();
        if (label.Length > MaxLabelLength) {
            var cut = MaxLabelLength;
            if (char.IsLowSurrogate(label[cut]) && char.IsHighSurrogate(label[cut - 1])) {
                cut--;
            }

            label = label[..cut];
        }

        // Windows drops trailing dots and spaces from a name, and leading spaces are easy to miss.
        label = label.Trim(' ').TrimEnd('.', ' ');
        if (label.Length == 0) {
            return NoName;
        }

        // "CON", or "nul.txt": device names, even before a dot.
        var dot = label.IndexOf('.', StringComparison.Ordinal);
        return Reserved.Contains((dot < 0 ? label : label[..dot]).TrimEnd(' ')) ? "_" + label : label;
    }

    [GeneratedRegex("^.+ ([0-9a-f]{16})$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelFolder();

    [GeneratedRegex(@"^\d{4}-\d{2}\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthFile();

    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2})(?:, arrived (?:(\d{4}-\d{2}-\d{2}) )?(\d{2}:\d{2}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex LineStart();
}

/// <summary>
/// The text files of one chat log (see <see cref="ChatLogText"/>): a <c>text</c> folder inside the log's own (so another
/// character or server never shares it, it moves with the log to a server's new address, and "Delete my chat log" deletes
/// it), with a folder per channel and a file per month in it. Lines are queued (<see cref="Add"/>) and appended in batches
/// (<see cref="Write"/>), each file opened, appended to and closed again, so a crash loses at most the batch being written
/// and nothing holds the files between batches. Keeps count of how big each file is, for the size limit the encrypted log
/// shares (see <see cref="ChatLogStore"/>), and makes room a line at a time from the front of the oldest files
/// (<see cref="Trim"/>). Not thread-safe: the log's own task runs it.
/// </summary>
internal sealed class ChatLogTextFiles {
    public const string FolderName = "text";

    /// <summary>A file being trimmed is written here first, then put in its place: a crash leaves one or the other whole.</summary>
    private const string TrimExtension = ".trim";

    /// <summary>The longest a line is read as when trimming; anything longer (an edit) goes in pieces this size.</summary>
    private const int MaxLineBytes = 64 * 1024;

    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    private readonly string _root;
    private readonly TimeZoneInfo _zone;
    // Each file's size on disk, by its channel's hash and its month ("5f2a9c01d3e4b6a7/2026-10"), so a renamed folder is
    // still the same file.
    private Dictionary<string, long> _files = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = [];
    private long _pendingBytes;
    // Each channel's folder this session, and the name it was last given (or tried): renamed only when that changes.
    private readonly Dictionary<string, (string Path, string Tried)> _folders = new(StringComparer.Ordinal);
    // Files that couldn't be trimmed (open in a program that locks them): counted, never picked again this session.
    private readonly HashSet<string> _stuck = new(StringComparer.Ordinal);
    private Exception? _problem;
    private bool _scanned;

    public ChatLogTextFiles(string logFolder, TimeZoneInfo zone) {
        this._root = Path.Combine(logFolder, FolderName);
        this._zone = zone;
    }

    public string Root => this._root;

    public TimeZoneInfo Zone => this._zone;

    /// <summary>How much room the text files take, with the lines queued and not yet written.</summary>
    public long Size => this._files.Values.Sum() + this._pendingBytes;

    /// <summary>Lines are queued, not written yet.</summary>
    public bool HasPending => this._pending.Count > 0;

    /// <summary>How many files couldn't be trimmed this session.</summary>
    public int StuckCount => this._stuck.Count;

    /// <summary>What went wrong trimming since this was last asked, if anything (for the diagnostic log).</summary>
    public Exception? TakeProblem() {
        var problem = this._problem;
        this._problem = null;
        return problem;
    }

    /// <summary>Counts what is on disk, once.</summary>
    /// <exception cref="IOException">The folder couldn't be looked at.</exception>
    public void EnsureScanned() {
        if (!this._scanned) {
            this.Scan();
        }
    }

    /// <summary><see cref="Scan"/>, if it can: otherwise the count stays as it was, and it is tried again later.</summary>
    public void TryScan() {
        try {
            this.Scan();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Counted again when it next can be.
        }
    }

    /// <summary>
    /// Counts each file on disk afresh (someone may have deleted or edited some). A file a trim left behind (a crash between
    /// writing it and putting it in place) is deleted: the file it was for is still whole.
    /// </summary>
    public void Scan() {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var folder in this.ChannelFolders()) {
            var hash = ChatLogText.HashOf(folder.Name)!;
            foreach (var file in folder.EnumerateFiles()) {
                if (file.Name.EndsWith(TrimExtension, StringComparison.Ordinal)) {
                    TryDelete(file.FullName);
                } else if (ChatLogText.IsMonthFile(file.Name)) {
                    var key = Key(hash, file.Name[..7]);
                    found[key] = found.GetValueOrDefault(key) + file.Length;
                }
            }
        }

        this._files = found;
        this._scanned = true;
    }

    /// <summary>
    /// When the oldest line of the text files arrived (the first line of the oldest month's files, read back), or null if
    /// there is nothing on disk to trim. A first line that can't be read back (edited, or cut short) counts as the oldest.
    /// </summary>
    public DateTimeOffset? OldestLine() {
        foreach (var month in this.Months()) {
            DateTimeOffset? oldest = null;
            foreach (var (_, path) in this.FilesOf(month)) {
                try {
                    using var stream = OpenToRead(path);
                    if (this.ReadLine(stream) is { } line && (oldest == null || line.Arrived < oldest)) {
                        oldest = line.Arrived;
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // Looked at again next time.
                }
            }

            if (oldest != null) {
                return oldest;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes room from the front of the oldest month's files, oldest line first across its channels' files, a line at a
    /// time: until <paramref name="want"/> bytes are freed, the month's files run out, or (with <paramref name="upTo"/>) the
    /// next line arrived after it. A file left with nothing is deleted; otherwise its remaining lines are written to a new
    /// file that then takes its place. A file that can't be is left as it is, counted, and not picked again this session.
    /// </summary>
    /// <returns>How many bytes were freed.</returns>
    public long Trim(DateTimeOffset? upTo, long want) {
        var month = this.Months().FirstOrDefault(key => this.FilesOf(key).Count > 0);
        if (month == null) {
            return 0;
        }

        var cursors = new List<Cursor>();
        try {
            foreach (var (key, path) in this.FilesOf(month)) {
                try {
                    var stream = OpenToRead(path);
                    var cursor = new Cursor(key, path, stream, stream.Length);
                    cursors.Add(cursor);
                    cursor.Next = this.ReadLine(stream);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    this.Stuck(key, ex);
                }
            }

            long freed = 0;
            while (freed < want && cursors.Where(cursor => cursor.Next != null).MinBy(cursor => (cursor.Next!.Value.Arrived, cursor.Key)) is { } oldest) {
                var line = oldest.Next!.Value;
                if (upTo is { } limit && line.Arrived > limit && freed > 0) {
                    break;
                }

                freed += line.Length;
                oldest.Cut += line.Length;
                oldest.Next = this.ReadLine(oldest.Stream);
            }
        } finally {
            foreach (var cursor in cursors) {
                cursor.Stream.Dispose();
            }
        }

        long done = 0;
        foreach (var cursor in cursors.Where(cursor => cursor.Cut > 0)) {
            try {
                this.Cut(cursor);
                done += cursor.Cut;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                this.Stuck(cursor.Key, ex);
            }
        }

        return done;
    }

    /// <summary>Queues a line for its channel's file for the month it arrived in.</summary>
    public void Add(HistoryLine line, ChannelLabel label, byte[] bytes) {
        this._pending.Add(new Pending(line.ChannelId, label, ChatLogText.MonthOf(line.Time, this._zone), bytes));
        this._pendingBytes += bytes.Length;
    }

    /// <summary>Appends the queued lines to their files. What couldn't be written is dropped.</summary>
    /// <exception cref="IOException">Some couldn't be written: the first failure.</exception>
    public void Write() {
        if (this._pending.Count == 0) {
            return;
        }

        var batch = this._pending.ToList();
        this._pending.Clear();
        this._pendingBytes = 0;
        Exception? failure = null;
        // In order within each file; files are apart, so their order among each other doesn't matter.
        foreach (var group in batch.GroupBy(pending => (pending.ChannelId, pending.Month))) {
            var lines = group.ToList();
            var (channelId, month) = group.Key;
            try {
                var folder = this.FolderFor(channelId, lines[^1].Label);
                var written = Append(Path.Combine(folder, month + ".txt"), lines);
                var key = Key(ChatLogText.ChannelHash(channelId), month);
                this._files[key] = this._files.GetValueOrDefault(key) + written;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Not written (or only some of it: the next count says how much).
                failure ??= ex;
            }
        }

        if (failure != null) {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Forgets everything: the folder was deleted. What is left of it, if anything, is counted afresh when next needed.</summary>
    public void Reset() {
        this._files.Clear();
        this._pending.Clear();
        this._pendingBytes = 0;
        this._folders.Clear();
        this._stuck.Clear();
        this._scanned = false;
    }

    private static string Key(string hash, string month) => hash + "/" + month;

    private void Stuck(string key, Exception ex) {
        this._stuck.Add(key);
        this._problem ??= ex;
    }

    /// <summary>The months on disk, oldest first ("2026-09", "2026-10").</summary>
    private List<string> Months() =>
        this._files.Keys.Where(key => !this._stuck.Contains(key)).Select(key => key[(key.IndexOf('/') + 1)..]).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>A month's files that are there, and not stuck: each one's key and path.</summary>
    private List<(string Key, string Path)> FilesOf(string month) {
        var files = new List<(string, string)>();
        foreach (var folder in this.ChannelFolders()) {
            var key = Key(ChatLogText.HashOf(folder.Name)!, month);
            var path = Path.Combine(folder.FullName, month + ".txt");
            if (!this._stuck.Contains(key) && File.Exists(path)) {
                files.Add((key, path));
            }
        }

        return files;
    }

    /// <summary>Takes a cursor's lines off the front of its file (crash-safe: a new file, then put in its place).</summary>
    private void Cut(Cursor cursor) {
        if (cursor.Cut >= cursor.Length) {
            File.Delete(cursor.Path);
            var folder = Path.GetDirectoryName(cursor.Path)!;
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) {
                try {
                    Directory.Delete(folder);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // Empty, and harmless.
                }
            }
        } else {
            var temporary = cursor.Path + TrimExtension;
            try {
                using (var from = OpenToRead(cursor.Path))
                using (var to = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    from.Seek(cursor.Cut, SeekOrigin.Begin);
                    from.CopyTo(to);
                    // On the disk before it takes the old one's place: a crash leaves one whole file or the other.
                    to.Flush(flushToDisk: true);
                }

                File.Move(temporary, cursor.Path, overwrite: true);
            } catch {
                TryDelete(temporary);
                throw;
            }
        }

        // Whatever was in two folders of the same channel is counted again at the next count.
        var left = this._files.GetValueOrDefault(cursor.Key) - cursor.Cut;
        if (left > 0) {
            this._files[cursor.Key] = left;
        } else {
            this._files.Remove(cursor.Key);
        }
    }

    private static FileStream OpenToRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024);

    /// <summary>The next line from where a stream is: its length (with its line end) and when it arrived; null at the end.</summary>
    private (int Length, DateTimeOffset Arrived)? ReadLine(Stream stream) {
        var start = new List<byte>(96);
        var length = 0;
        int b;
        while (length < MaxLineBytes && (b = stream.ReadByte()) >= 0) {
            length++;
            if (start.Count < 96) {
                start.Add((byte) b);
            }

            if (b == '\n') {
                break;
            }
        }

        if (length == 0) {
            return null;
        }

        var text = Encoding.UTF8.GetString(start.ToArray());
        return (length, ChatLogText.ArrivalOf(text, this._zone) ?? DateTimeOffset.MinValue);
    }

    /// <returns>The bytes written: the lines, and a line end after a last line a crash left without one.</returns>
    private static long Append(string path, List<Pending> lines) {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        long extra = 0;
        if (stream.Length > 0) {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n') {
                // A line cut short by a crash (or an edit): the new ones start on a line of their own.
                stream.Write(LineEnd);
                extra = LineEnd.Length;
            }
        }

        var bytes = new byte[lines.Sum(line => line.Bytes.Length)];
        var at = 0;
        foreach (var line in lines) {
            line.Bytes.CopyTo(bytes, at);
            at += line.Bytes.Length;
        }

        stream.Write(bytes);
        // Handed to the operating system: a crash of the game loses none of it.
        stream.Flush();
        return extra + bytes.Length;
    }

    /// <summary>
    /// A channel's folder: the one it has (found by its ID's hash, whatever it is called), renamed if its name or number
    /// changed, or a new one. A rename that fails (the folder in use) leaves it as it is, and isn't tried again until the
    /// name changes again. While its name isn't known, the folder keeps the name it has.
    /// </summary>
    private string FolderFor(string channelId, ChannelLabel label) {
        var wanted = ChatLogText.ChannelFolderName(channelId, label.Name, label.Slot);
        if (!this._folders.TryGetValue(channelId, out var known)) {
            var hash = ChatLogText.ChannelHash(channelId);
            var found = this.ChannelFolders().Select(folder => folder.FullName)
                .Where(path => ChatLogText.HashOf(Path.GetFileName(path)) == hash).Order(StringComparer.Ordinal).FirstOrDefault();
            known = (found ?? Path.Combine(this._root, wanted), found == null ? wanted : Path.GetFileName(found));
        }

        if (label.Name != null && known.Tried != wanted && Path.GetFileName(known.Path) != wanted) {
            var target = Path.Combine(this._root, wanted);
            try {
                if (!Directory.Exists(known.Path)) {
                    // Gone (deleted by hand, or by the size limit once empty): a new one, named as it is now.
                    known = (target, wanted);
                } else if (!Directory.Exists(target) || string.Equals(known.Path, target, StringComparison.OrdinalIgnoreCase)) {
                    Directory.Move(known.Path, target);
                    known = (target, wanted);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // In use: it keeps its name for now.
            }

            known.Tried = wanted;
        }

        this._folders[channelId] = known;
        Directory.CreateDirectory(known.Path);
        return known.Path;
    }

    /// <summary>The channel folders in the text folder (never a link to somewhere else).</summary>
    private IEnumerable<DirectoryInfo> ChannelFolders() {
        if (!Directory.Exists(this._root)) {
            return [];
        }

        return new DirectoryInfo(this._root).EnumerateDirectories()
            .Where(folder => ChatLogText.HashOf(folder.Name) != null && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToList();
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Deleted at the next count.
        }
    }

    private sealed class Cursor(string key, string path, FileStream stream, long length) {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public FileStream Stream { get; } = stream;
        public long Length { get; } = length;
        public long Cut { get; set; }
        public (int Length, DateTimeOffset Arrived)? Next { get; set; }
    }

    private sealed record Pending(string ChannelId, ChannelLabel Label, string Month, byte[] Bytes);
}
