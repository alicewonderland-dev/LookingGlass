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
    /// A channel's folder: its name and number as they are now, readable ("Sky Pirates (LGC3)"), then 8 hex digits of its
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

    /// <summary>The part of a channel's folder name that never changes: 32 bits of its ID's SHA-256, in hex.</summary>
    public static string ChannelHash(string channelId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channelId)), 0, 4).ToLowerInvariant();

    /// <summary>Whether a folder name is a channel folder's, and if so its <see cref="ChannelHash"/>.</summary>
    internal static string? HashOf(string folderName) => ChannelFolder().Match(folderName) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Whether a file name is a month's text file's ("2026-10.txt").</summary>
    internal static bool IsMonthFile(string fileName) => MonthFile().IsMatch(fileName);

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

    [GeneratedRegex("^.+ ([0-9a-f]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelFolder();

    [GeneratedRegex(@"^\d{4}-\d{2}\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthFile();
}

/// <summary>
/// The text files of one chat log (see <see cref="ChatLogText"/>): a <c>text</c> folder inside the log's own (so another
/// character or server never shares it, it moves with the log to a server's new address, and "Delete my chat log" deletes
/// it), with a folder per channel and a file per month in it. Lines are queued (<see cref="Add"/>) and appended in batches
/// (<see cref="Write"/>), each file opened, appended to and closed again, so a crash loses at most the batch being written
/// and nothing holds the files between batches. Keeps count of how big each month's files are, for the size limit the
/// encrypted log shares (see <see cref="ChatLogStore"/>). Not thread-safe: the log's own task runs it.
/// </summary>
internal sealed class ChatLogTextFiles {
    public const string FolderName = "text";

    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    private readonly string _root;
    private readonly TimeZoneInfo _zone;
    private Dictionary<string, Month> _months = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = [];
    // Each channel's folder this session, and the name it was last given (or tried): renamed only when that changes.
    private readonly Dictionary<string, (string Path, string Tried)> _folders = new(StringComparer.Ordinal);
    // Months some of whose files couldn't be deleted (open in another program, say): counted, never picked again this session.
    private readonly HashSet<string> _stuck = new(StringComparer.Ordinal);
    private bool _scanned;

    public ChatLogTextFiles(string logFolder, TimeZoneInfo zone) {
        this._root = Path.Combine(logFolder, FolderName);
        this._zone = zone;
    }

    public string Root => this._root;

    public TimeZoneInfo Zone => this._zone;

    /// <summary>How much room the text files take, with the lines queued and not yet written.</summary>
    public long Size => this._months.Values.Sum(month => month.Bytes);

    /// <summary>Lines are queued, not written yet.</summary>
    public bool HasPending => this._pending.Count > 0;

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

    /// <summary>Counts each month's files on disk afresh (someone may have deleted or edited some), and the queued lines.</summary>
    public void Scan() {
        var found = new Dictionary<string, Month>(StringComparer.Ordinal);
        foreach (var folder in this.ChannelFolders()) {
            foreach (var file in folder.EnumerateFiles("*.txt")) {
                if (!ChatLogText.IsMonthFile(file.Name)) {
                    continue;
                }

                var key = file.Name[..7];
                var month = found.TryGetValue(key, out var known) ? known : found[key] = new Month();
                month.Bytes += file.Length;
                // As old as its newest line: when it was last written, but never later than the month's end (an edit in Notepad
                // doesn't make a month new again).
                var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                var end = this.EndOf(key);
                month.Newest = Max(month.Newest, end is { } e && written > e ? e : written);
            }
        }

        foreach (var pending in this._pending) {
            var month = found.TryGetValue(pending.Month, out var known) ? known : found[pending.Month] = new Month();
            month.Bytes += pending.Bytes.Length;
            month.Newest = Max(month.Newest, pending.Time);
        }

        // A month written this session is as old as the newest line written to it, which says more than a file's time.
        foreach (var (key, month) in found) {
            if (this._months.TryGetValue(key, out var before)) {
                month.Newest = Max(month.Newest, before.Newest);
            }
        }

        this._months = found;
        this._scanned = true;
    }

    /// <summary>The month whose newest line is the oldest (its files the next to go when full), or null if there are none.</summary>
    public (string Month, DateTimeOffset Newest)? Oldest() {
        (string, DateTimeOffset)? oldest = null;
        foreach (var (key, month) in this._months) {
            if (this._stuck.Contains(key)) {
                continue;
            }

            if (oldest is not { } o || month.Newest < o.Item2 || (month.Newest == o.Item2 && string.CompareOrdinal(key, o.Item1) < 0)) {
                oldest = (key, month.Newest);
            }
        }

        return oldest;
    }

    /// <summary>Queues a line for its channel's file for the month it arrived in.</summary>
    public void Add(HistoryLine line, ChannelLabel label, byte[] bytes) {
        var key = ChatLogText.MonthOf(line.Time, this._zone);
        this._pending.Add(new Pending(line.ChannelId, label, key, bytes, line.Time));
        var month = this._months.TryGetValue(key, out var known) ? known : this._months[key] = new Month();
        month.Bytes += bytes.Length;
        month.Newest = Max(month.Newest, line.Time);
    }

    /// <summary>
    /// Deletes a month's files, in every channel's folder (and a channel folder left empty), and its queued lines. A file
    /// that can't be deleted leaves the month counted as it is, and it isn't picked again this session.
    /// </summary>
    /// <exception cref="IOException">Some couldn't be deleted.</exception>
    public void DeleteMonth(string key) {
        this._pending.RemoveAll(pending => pending.Month == key);
        var failed = false;
        foreach (var folder in this.ChannelFolders()) {
            try {
                var path = Path.Combine(folder.FullName, key + ".txt");
                if (File.Exists(path)) {
                    File.Delete(path);
                }

                if (!folder.EnumerateFileSystemInfos().Any()) {
                    folder.Delete();
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                failed = true;
            }
        }

        this._months.Remove(key);
        if (failed) {
            this._stuck.Add(key);
            this.TryScan();
            throw new IOException("Some of a month's text files couldn't be deleted (they may be open in another program).");
        }
    }

    /// <summary>Appends the queued lines to their files. What couldn't be written is dropped (and no longer counted).</summary>
    /// <exception cref="IOException">Some couldn't be written: the first failure.</exception>
    public void Write() {
        if (this._pending.Count == 0) {
            return;
        }

        var batch = this._pending.ToList();
        this._pending.Clear();
        Exception? failure = null;
        // In order within each file; files are apart, so their order among each other doesn't matter.
        foreach (var group in batch.GroupBy(pending => (pending.ChannelId, pending.Month))) {
            var lines = group.ToList();
            var (channelId, key) = group.Key;
            try {
                var folder = this.FolderFor(channelId, lines[^1].Label);
                var extra = Append(Path.Combine(folder, key + ".txt"), lines);
                if (extra > 0 && this._months.TryGetValue(key, out var month)) {
                    month.Bytes += extra;
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Not written (or only some of it: the next count says how much).
                if (this._months.TryGetValue(key, out var month)) {
                    month.Bytes = Math.Max(0, month.Bytes - lines.Sum(line => line.Bytes.Length));
                }

                failure ??= ex;
            }
        }

        if (failure != null) {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Forgets everything: the folder was deleted. What is left of it, if anything, is counted afresh when next needed.</summary>
    public void Reset() {
        this._months.Clear();
        this._pending.Clear();
        this._folders.Clear();
        this._stuck.Clear();
        this._scanned = false;
    }

    /// <returns>The bytes written besides the lines: a line end after a last line a crash left without one.</returns>
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
        return extra;
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

    /// <summary>The first moment after a month ("2026-10"), in the computer's zone.</summary>
    private DateTimeOffset? EndOf(string key) {
        if (!DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)) {
            return null;
        }

        var next = first.AddMonths(1);
        return new DateTimeOffset(next, this._zone.GetUtcOffset(next));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed class Month {
        public long Bytes;
        public DateTimeOffset Newest = DateTimeOffset.MinValue;
    }

    private sealed record Pending(string ChannelId, ChannelLabel Label, string Month, byte[] Bytes, DateTimeOffset Time);
}
