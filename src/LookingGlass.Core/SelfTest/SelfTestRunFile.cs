using System.Text.Json;
using System.Text.Json.Serialization;
using LookingGlass.Core.Client;

namespace LookingGlass.Core.SelfTest;

/// <summary>
/// The settings the self-test may change for a while (docs/self-test-plan.md, "Settings it changes"), each snapshotted in
/// the run file before it is first changed and put back at the end, or at the next start after a crash. Values are kept as
/// JSON, in the shape <see cref="ISelfTestSettings"/> reads and writes them.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelfTestSetting>))]
public enum SelfTestSetting {
    /// <summary>"Show LookingGlass only in windows" (bool).</summary>
    WindowsOnly,

    /// <summary>"New channels open" (the <see cref="WindowOpening"/> number).</summary>
    NewChannelsOpen,

    /// <summary>"Say when I start or stop talking in a channel" (bool).</summary>
    SayWhenTalking,

    /// <summary>"Colour the whole line" (bool).</summary>
    ColourWholeLine,

    /// <summary>"Advanced mode" (bool).</summary>
    AdvancedMode,

    /// <summary>"Keep chat history on this computer" (bool).</summary>
    KeepHistory,

    /// <summary>"Keep my chat history unencrypted" (bool).</summary>
    KeepHistoryUnencrypted,

    /// <summary>The chat history's size limit, in megabytes (number).</summary>
    HistoryMegabytes,

    /// <summary>Local chat's privacy notice accepted (bool).</summary>
    LocalPrivacyAccepted,

    /// <summary>Local chat's once-only note shown (bool).</summary>
    LocalTalkNoteShown,

    /// <summary>ChatTwo's once-only note shown (bool).</summary>
    ChatTwoNoteShown,

    /// <summary>Local chat's colour (a string: "row:12", "custom:FF66CC", or null for the default).</summary>
    LocalChatColour,

    /// <summary>The tester's own name colour (0xRRGGBB, or null for the default).</summary>
    OwnNameColour,

    /// <summary>The tester's channel windows on this server, for this character (their layouts, as saved).</summary>
    ChannelWindows,
}

/// <summary>Reads and writes the settings the self-test may change, as JSON values (the plugin's settings; a fake in tests).</summary>
public interface ISelfTestSettings {
    /// <summary>The setting's value now.</summary>
    JsonElement Read(SelfTestSetting setting);

    /// <summary>Sets it (and saves the settings). May throw for a value it can't take.</summary>
    void Write(SelfTestSetting setting, JsonElement value);
}

/// <summary>Where a run stands, as the run file says.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelfTestPhase>))]
public enum SelfTestPhase {
    /// <summary>Running (or interrupted while running: cleaned up at the next start).</summary>
    Running,

    /// <summary>Waiting for the tester to log out and in, or turn the plugin off and on: it carries on afterwards.</summary>
    WaitingToCarryOn,
}

/// <summary>A check's result as kept in the run file and shown in the report: fixed words only, never names or what was typed.</summary>
public sealed record CheckRecord {
    public required string Check { get; init; }

    public required CheckOutcome Outcome { get; init; }

    /// <summary>Why, for a failure or a skip: the runner's own fixed words.</summary>
    public string? Why { get; init; }

    /// <summary>The checklist step it failed at ("32"), if known.</summary>
    public string? Step { get; init; }

    /// <summary>When it failed, or ended.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Parts that weren't run, each in the runner's words ("the part next to a stranger").</summary>
    public List<string> NotRun { get; init; } = [];

    /// <summary>Guided steps the tester said worked, by their short names ("Tab", "info bar").</summary>
    public List<string> Confirmed { get; init; } = [];

    /// <summary>The tester's own notes, as typed (the window asks for no names).</summary>
    public List<string> Notes { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckOutcome>))]
public enum CheckOutcome {
    Passed,
    Failed,
    Skipped,
    Stopped,
}

/// <summary>
/// The run file (<c>selftest-run.json</c> in the plugin's config folder): enough to carry a run on after a relog or a
/// plugin reload, and to put everything back after a crash. No message text; the partner's name only while a run lasts.
/// </summary>
public sealed class SelfTestRunData {
    public int Version { get; set; } = 1;

    /// <summary>The run's code (four letters and digits), in the test channel's name and the test lines.</summary>
    public string Code { get; set; } = "";

    /// <summary>The character the run is for (its settings are this character's).</summary>
    public ulong ContentId { get; set; }

    /// <summary>The server address the run's channels are on.</summary>
    public string ServerUrl { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public SelfTestPhase Phase { get; set; }

    /// <summary>Since when it has waited for a relog or reload (it stops waiting after a while).</summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>The suites chosen, by ID.</summary>
    public List<string> Suites { get; set; } = [];

    /// <summary>Only these checks of the suites, by ID (the failed ones, run again); empty for all.</summary>
    public List<string> OnlyChecks { get; set; } = [];

    /// <summary>The check to carry on with, and its step (0-based), after a relog or reload.</summary>
    public string? NextCheck { get; set; }

    public int NextStep { get; set; }

    public List<CheckRecord> Results { get; set; } = [];

    /// <summary>The run's test channels, by ID: written as soon as each is made, before anything is sent to it.</summary>
    public List<string> TestChannels { get; set; } = [];

    /// <summary>The settings to put back: each one's value before the run first changed it.</summary>
    public Dictionary<SelfTestSetting, JsonElement> Settings { get; set; } = new();

    /// <summary>The chat history was off with nothing in it before the run: everything in it at the end is the test's.</summary>
    public bool HistoryWasOffAndEmpty { get; set; }

    /// <summary>The partner's name@world, for two players (while the run lasts only). Not used yet.</summary>
    public string? Partner { get; set; }

    /// <summary>The game's chat channel before the run first set it to Say, to put back at the end (never a tell).</summary>
    public int? GameChannelBefore { get; set; }

    /// <summary>What this run asked the server for so far, and when: its pace and its most carry over a reload.</summary>
    public List<SelfTestSpend> Spent { get; set; } = [];

    /// <summary>The results of the steps before a relog or reload, for the check carried on (see <see cref="NextCheck"/>).</summary>
    public List<StepRecord> InProgress { get; set; } = [];

    /// <summary>The run disconnected on purpose and hasn't connected again yet: the clean-up connects first.</summary>
    public bool DisconnectedByRun { get; set; }

    /// <summary>When the run last saved this file: nothing of a history newer than this (and a little) is the test's.</summary>
    public DateTimeOffset LastActive { get; set; }

    /// <summary>
    /// The test's chat history couldn't be deleted by itself (the history was turned on since, or holds something newer):
    /// the self-test window asks the tester, never deleting anything without them.
    /// </summary>
    public bool HistoryNeedsAsking { get; set; }
}

/// <summary>A step's result as the run file keeps it over a relog or reload: fixed words only.</summary>
public sealed record StepRecord(StepOutcome Outcome, string? Why, string? NotRun, string? Confirmed, DateTimeOffset At);

/// <summary>
/// Reads and writes the run file, atomically (see <see cref="AtomicFile"/>): a crash leaves the old file or the new one.
/// A damaged file gives back what can be read of it (the settings to put back and the test channels first), and is set
/// aside, so it is never acted on twice.
/// </summary>
public sealed class SelfTestRunFile(string folder) {
    public const string FileName = "selftest-run.json";

    /// <summary>Kept beside the run file: what the last hour's runs spent (see <see cref="SelfTestPacer.Spent"/>).</summary>
    public const string SpendFileName = "selftest-spent.json";

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path => System.IO.Path.Combine(folder, FileName);

    private string SpendPath => System.IO.Path.Combine(folder, SpendFileName);

    public bool Exists => File.Exists(this.Path) || File.Exists(AtomicFile.BackupPath(this.Path));

    public void Write(SelfTestRunData data) => AtomicFile.Write(this.Path, JsonSerializer.SerializeToUtf8Bytes(data, Json));

    /// <summary>The run file, or null if there is none.</summary>
    /// <param name="damaged">Set when the file couldn't be read whole: what could be read is returned, and the file is set aside.</param>
    public SelfTestRunData? Read(out bool damaged) {
        damaged = false;
        try {
            var data = AtomicFile.Read(this.Path, bytes => JsonSerializer.Deserialize<SelfTestRunData>(bytes, Json) ?? throw new JsonException("Empty run file."));
            if (data != null) {
                return data;
            }
        } catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException) {
            // Damaged: read what can be, below.
        }

        if (!this.Exists) {
            return null;
        }

        damaged = true;
        var partial = ReadWhatCanBe(this.Path) ?? ReadWhatCanBe(AtomicFile.BackupPath(this.Path)) ?? new SelfTestRunData();
        this.SetAside();
        return partial;
    }

    /// <summary>Deletes the run file (and its backup, which would otherwise be read in its place).</summary>
    public void Delete() {
        lock (AtomicFile.LockFor(this.Path)) {
            File.Delete(this.Path);
            File.Delete(AtomicFile.BackupPath(this.Path));
        }
    }

    /// <summary>Moves a damaged run file out of the way (and its backup): it is never read again.</summary>
    private void SetAside() {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var path in new[] { this.Path, AtomicFile.BackupPath(this.Path) }) {
            try {
                if (File.Exists(path)) {
                    File.Move(path, $"{path}.damaged-{stamp}", overwrite: true);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Deleted then, rather than read again.
                try {
                    File.Delete(path);
                } catch (Exception again) when (again is IOException or UnauthorizedAccessException) {
                    // Nothing more to do.
                }
            }
        }
    }

    /// <summary>What can be read of a damaged file, field by field: never throws.</summary>
    private static SelfTestRunData? ReadWhatCanBe(string path) {
        try {
            if (!File.Exists(path)) {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return null;
            }

            var data = new SelfTestRunData();
            if (root.TryGetProperty(nameof(SelfTestRunData.Code), out var code) && code.ValueKind == JsonValueKind.String) {
                data.Code = code.GetString() ?? "";
            }

            if (root.TryGetProperty(nameof(SelfTestRunData.ContentId), out var contentId) && contentId.TryGetUInt64(out var id)) {
                data.ContentId = id;
            }

            if (root.TryGetProperty(nameof(SelfTestRunData.ServerUrl), out var url) && url.ValueKind == JsonValueKind.String) {
                data.ServerUrl = url.GetString() ?? "";
            }

            if (root.TryGetProperty(nameof(SelfTestRunData.TestChannels), out var channels) && channels.ValueKind == JsonValueKind.Array) {
                data.TestChannels = channels.EnumerateArray().Where(channel => channel.ValueKind == JsonValueKind.String)
                    .Select(channel => channel.GetString()!).ToList();
            }

            if (root.TryGetProperty(nameof(SelfTestRunData.Settings), out var settings) && settings.ValueKind == JsonValueKind.Object) {
                foreach (var setting in settings.EnumerateObject()) {
                    if (Enum.TryParse<SelfTestSetting>(setting.Name, out var which) && Enum.IsDefined(which)) {
                        data.Settings[which] = setting.Value.Clone();
                    }
                }
            }

            if (root.TryGetProperty(nameof(SelfTestRunData.HistoryWasOffAndEmpty), out var history) && history.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                data.HistoryWasOffAndEmpty = history.GetBoolean();
            }

            return data;
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>What the last hour's runs spent (empty if the file is missing or unreadable).</summary>
    public IReadOnlyList<SelfTestSpend> ReadSpent() {
        try {
            return AtomicFile.Read(this.SpendPath, bytes => JsonSerializer.Deserialize<List<SelfTestSpend>>(bytes, Json) ?? []) ?? [];
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            return [];
        }
    }

    public void WriteSpent(IReadOnlyList<SelfTestSpend> spent) => AtomicFile.Write(this.SpendPath, JsonSerializer.SerializeToUtf8Bytes(spent, Json));
}

/// <summary>
/// Snapshotting and putting back the settings a run changes. A setting is snapshotted once, before the first change, so
/// what is put back is always what it was before the run (also if the tester changed it by hand meanwhile).
/// </summary>
public static class SelfTestSettingsSnapshot {
    /// <summary>
    /// Changes a setting, snapshotting it first if this run hasn't yet: the snapshot is saved (by <paramref name="save"/>)
    /// before the setting is written, so a crash between the two still puts it back.
    /// </summary>
    public static void Change(SelfTestRunData run, ISelfTestSettings settings, SelfTestSetting setting, JsonElement value, Action save) {
        if (!run.Settings.ContainsKey(setting)) {
            run.Settings[setting] = settings.Read(setting).Clone();
            save();
        }

        settings.Write(setting, value);
    }

    /// <summary>
    /// Puts back every setting in the snapshot, each on its own (one that fails doesn't stop the rest), and empties the
    /// snapshot of those put back, so restoring twice changes nothing.
    /// </summary>
    /// <returns>How many were put back, and how many couldn't be.</returns>
    public static (int Restored, int Failed) Restore(SelfTestRunData run, ISelfTestSettings settings) {
        var restored = 0;
        var failed = 0;
        foreach (var (setting, value) in run.Settings.ToList()) {
            try {
                settings.Write(setting, value);
                run.Settings.Remove(setting);
                restored++;
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                failed++;
            }
        }

        return (restored, failed);
    }

    /// <summary>A JSON value for a setting.</summary>
    public static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);
}

/// <summary>How some settings' values are written in the run file (see <see cref="SelfTestSetting"/>).</summary>
public static class SelfTestValues {
    /// <summary>A colour as a setting's value: "row:12", "custom:FF66CC", or null for the default.</summary>
    public static string? Colour(ChannelColour? colour) => colour switch {
        null => null,
        { IsCustom: true } custom => $"custom:{custom.Rgb:X6}",
        { } row => $"row:{row.Row}",
    };

    /// <summary>The colour a <see cref="Colour(ChannelColour?)"/> value stands for; null for the default or anything else.</summary>
    public static ChannelColour? Colour(string? value) {
        if (value?.StartsWith("row:", StringComparison.Ordinal) == true && ushort.TryParse(value[4..], out var row) && row > 0) {
            return ChannelColour.OfRow(row);
        }

        if (value?.StartsWith("custom:", StringComparison.Ordinal) == true && uint.TryParse(value[7..], System.Globalization.NumberStyles.HexNumber, null, out var rgb)) {
            return ChannelColour.Custom(rgb);
        }

        return null;
    }
}

/// <summary>The tester's own name colour, as the run file keeps it: the key it is saved under (a number) and the colour, or null.</summary>
public sealed record OwnNameColourValue(string Key, uint? Rgb);

/// <summary>
/// What the plugin does at its start with a run file an interrupted run left (one not waiting for a relog or reload), before
/// any session: the settings put back at once, and the test's chat history deleted if the history was off and empty before
/// the run, but only while it is still off and nothing in it is newer than the run (so the tester's own history, started
/// later, is never touched; then the window asks). Its test channels are left later, once connected.
/// </summary>
public static class SelfTestRecovery {
    /// <summary>How long after the run's last save a history may still have been written by it (its last lines, closing).</summary>
    public static readonly TimeSpan HistoryGrace = TimeSpan.FromMinutes(2);

    /// <summary>A run file that lost whose it was (damaged): it can't be finished by itself.</summary>
    public static bool Unknown(SelfTestRunData data) => data.ContentId == 0 || string.IsNullOrEmpty(data.ServerUrl);

    /// <summary>
    /// What to do with the chat history a run left, when it was off and empty before the run: every chat log folder there is
    /// was made since, by the run, unless the history was turned on again or something in it is newer than the run (then
    /// ask, and delete nothing).
    /// </summary>
    /// <param name="historyOnNow">"Keep chat history on this computer" is on now.</param>
    /// <param name="folders">Every chat log folder, with when it was last written.</param>
    /// <param name="lastActive">When the run last saved its file.</param>
    public static (IReadOnlyList<string> Delete, bool Ask) HistoryCleanup(bool historyOnNow, IReadOnlyList<(string Folder, DateTimeOffset Newest)> folders,
        DateTimeOffset lastActive) {
        if (folders.Count == 0) {
            return ([], false);
        }

        if (historyOnNow || folders.Any(folder => folder.Newest > lastActive + HistoryGrace)) {
            return ([], true);
        }

        return (folders.Select(folder => folder.Folder).ToList(), false);
    }

    /// <summary>
    /// For the tester's own choice (the window's "Delete the test's chat history"): the folders nothing has written to since
    /// the run, but the one a log has open now.
    /// </summary>
    public static IReadOnlyList<string> UntouchedSince(IReadOnlyList<(string Folder, DateTimeOffset Newest)> folders, DateTimeOffset lastActive, string? open) =>
        folders.Where(folder => folder.Newest <= lastActive + HistoryGrace
                                && (open == null || !string.Equals(Path.GetFullPath(folder.Folder), Path.GetFullPath(open), StringComparison.OrdinalIgnoreCase)))
            .Select(folder => folder.Folder).ToList();

    /// <summary>Puts back what it can now; the file is to be kept (and finished once connected, or asked) unless this returns true.</summary>
    /// <param name="historyFolders">Every chat log folder, with when it was last written.</param>
    /// <param name="deleteFolders">Deletes chat log folders.</param>
    /// <returns>Whether nothing is left to do (the file can go).</returns>
    public static bool PutBack(SelfTestRunData data, ISelfTestSettings settings, Func<IReadOnlyList<(string Folder, DateTimeOffset Newest)>> historyFolders,
        Action<IReadOnlyList<string>> deleteFolders, Action<string>? log = null) {
        if (Unknown(data)) {
            // The windows' layouts are a character's own: whose isn't known, so they can't be put back.
            data.Settings.Remove(SelfTestSetting.ChannelWindows);
        }

        var (restored, failed) = SelfTestSettingsSnapshot.Restore(data, settings);
        log?.Invoke($"[selftest] an interrupted run's settings were put back ({restored}, {failed} couldn't be)");
        CleanUpHistory(data, () => settings.Read(SelfTestSetting.KeepHistory).ValueKind == System.Text.Json.JsonValueKind.True, historyFolders, deleteFolders, log);
        data.Phase = SelfTestPhase.Running;
        data.WaitingSince = null;
        data.NextCheck = null;
        return data.TestChannels.Count == 0 && data.Settings.Count == 0 && !data.HistoryWasOffAndEmpty && !data.HistoryNeedsAsking && data.GameChannelBefore == null;
    }

    /// <summary>
    /// The test's chat history, after its settings are back: deleted if it may be (see <see cref="HistoryCleanup"/>);
    /// otherwise marked to ask the tester, and never deleted by itself again.
    /// </summary>
    /// <param name="historyOnNow">Whether "Keep chat history on this computer" is on now (read only if needed).</param>
    public static void CleanUpHistory(SelfTestRunData data, Func<bool> historyOnNow, Func<IReadOnlyList<(string Folder, DateTimeOffset Newest)>> historyFolders,
        Action<IReadOnlyList<string>> deleteFolders, Action<string>? log = null) {
        if (!data.HistoryWasOffAndEmpty || data.Settings.ContainsKey(SelfTestSetting.KeepHistory)) {
            return;
        }

        var (delete, ask) = HistoryCleanup(historyOnNow(), historyFolders(), data.LastActive);
        if (ask) {
            data.HistoryWasOffAndEmpty = false;
            data.HistoryNeedsAsking = true;
            log?.Invoke("[selftest] an earlier run's chat history wasn't deleted: the history was turned on since, or holds something newer");
            return;
        }

        try {
            if (delete.Count > 0) {
                deleteFolders(delete);
            }

            data.HistoryWasOffAndEmpty = false;
            log?.Invoke("[selftest] an earlier run's chat history was deleted (it was off and empty before)");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            log?.Invoke($"[selftest] an earlier run's chat history couldn't be deleted ({ex.GetType().Name})");
        }
    }
}