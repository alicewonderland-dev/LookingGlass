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
}

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