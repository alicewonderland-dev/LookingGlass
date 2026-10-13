using System.Text.Json;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>
/// The self-test's run file and settings snapshot (docs/self-test-plan.md, "If a run is interrupted"): a round trip for
/// every setting, restoring from a file an interrupted run left, restoring twice changes nothing, a damaged file restores
/// what it can and is set aside.
/// </summary>
public sealed class SelfTestRunFileTests : IDisposable {
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lgt-selftestfile-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(this._folder)) {
            Directory.Delete(TestFolders.CheckDeletable(this._folder), true);
        }
    }

    /// <summary>Settings as JSON values, as the plugin keeps them.</summary>
    private sealed class FakeSettings : ISelfTestSettings {
        public Dictionary<SelfTestSetting, JsonElement> Values { get; } = new();

        public SelfTestSetting? Fails { get; set; }

        public int Writes { get; private set; }

        public JsonElement Read(SelfTestSetting setting) => this.Values[setting];

        public void Write(SelfTestSetting setting, JsonElement value) {
            if (setting == this.Fails) {
                throw new InvalidOperationException("Can't.");
            }

            this.Writes++;
            this.Values[setting] = value.Clone();
        }
    }

    private static JsonElement Value(object? value) => SelfTestSettingsSnapshot.Value(value);

    /// <summary>A value of the right shape for each setting, "before" and "during" the run.</summary>
    private static (JsonElement Before, JsonElement During) ValuesFor(SelfTestSetting setting) => setting switch {
        SelfTestSetting.NewChannelsOpen => (Value(0), Value(1)),
        SelfTestSetting.HistoryMegabytes => (Value(50), Value(5)),
        SelfTestSetting.LocalChatColour => (Value("row:12"), Value("custom:FF66CC")),
        SelfTestSetting.OwnNameColour => (Value((uint?) null), Value(0xFFAA00u)),
        SelfTestSetting.ChannelWindows => (Value(new[] { new { Id = "w1", Tabs = new[] { "sky" } } }), Value(Array.Empty<object>())),
        _ => (Value(false), Value(true)),
    };

    [Fact]
    public void EverySettingRoundTripsThroughTheFile() {
        var settings = new FakeSettings();
        foreach (var setting in Enum.GetValues<SelfTestSetting>()) {
            settings.Values[setting] = ValuesFor(setting).Before;
        }

        var file = new SelfTestRunFile(this._folder);
        var run = new SelfTestRunData { Code = "K7Q2", ContentId = 42, ServerUrl = "wss://example.test/ws", TestChannels = ["c1"] };
        foreach (var setting in Enum.GetValues<SelfTestSetting>()) {
            SelfTestSettingsSnapshot.Change(run, settings, setting, ValuesFor(setting).During, () => file.Write(run));
            // Changed again: the snapshot keeps the first value.
            SelfTestSettingsSnapshot.Change(run, settings, setting, ValuesFor(setting).During, () => file.Write(run));
        }

        // What an interrupted run leaves: read back as the next start would.
        var read = file.Read(out var damaged)!;
        Assert.False(damaged);
        Assert.Equal("K7Q2", read.Code);
        Assert.Equal(42UL, read.ContentId);
        Assert.Equal(["c1"], read.TestChannels);
        Assert.Equal(Enum.GetValues<SelfTestSetting>().Length, read.Settings.Count);

        var (restored, failed) = SelfTestSettingsSnapshot.Restore(read, settings);
        Assert.Equal(Enum.GetValues<SelfTestSetting>().Length, restored);
        Assert.Equal(0, failed);
        foreach (var setting in Enum.GetValues<SelfTestSetting>()) {
            Assert.Equal(JsonSerializer.Serialize(ValuesFor(setting).Before), JsonSerializer.Serialize(settings.Values[setting]));
        }
    }

    [Fact]
    public void RestoringTwiceChangesNothing() {
        var settings = new FakeSettings();
        settings.Values[SelfTestSetting.WindowsOnly] = Value(false);
        var run = new SelfTestRunData();
        SelfTestSettingsSnapshot.Change(run, settings, SelfTestSetting.WindowsOnly, Value(true), () => { });

        Assert.Equal((1, 0), SelfTestSettingsSnapshot.Restore(run, settings));
        var writes = settings.Writes;
        // The tester changes it after the run: a second restore mustn't undo that.
        settings.Values[SelfTestSetting.WindowsOnly] = Value(true);
        Assert.Equal((0, 0), SelfTestSettingsSnapshot.Restore(run, settings));
        Assert.Equal(writes, settings.Writes);
        Assert.True(settings.Values[SelfTestSetting.WindowsOnly].GetBoolean());
    }

    [Fact]
    public void ASettingThatCantBePutBackDoesntStopTheOthers() {
        var settings = new FakeSettings { Fails = SelfTestSetting.AdvancedMode };
        settings.Values[SelfTestSetting.AdvancedMode] = Value(false);
        settings.Values[SelfTestSetting.WindowsOnly] = Value(false);
        var run = new SelfTestRunData();
        run.Settings[SelfTestSetting.AdvancedMode] = Value(false);
        run.Settings[SelfTestSetting.WindowsOnly] = Value(false);
        settings.Values[SelfTestSetting.WindowsOnly] = Value(true);

        Assert.Equal((1, 1), SelfTestSettingsSnapshot.Restore(run, settings));
        Assert.False(settings.Values[SelfTestSetting.WindowsOnly].GetBoolean());
        // Kept, to try again.
        Assert.True(run.Settings.ContainsKey(SelfTestSetting.AdvancedMode));
    }

    [Fact]
    public void ADamagedFileRestoresWhatItCanAndIsSetAside() {
        Directory.CreateDirectory(this._folder);
        var path = Path.Combine(this._folder, SelfTestRunFile.FileName);
        // Valid JSON, with fields of the wrong kind and a setting that doesn't exist.
        File.WriteAllText(path, """
            { "Code": "K7Q2", "ContentId": 42, "TestChannels": ["c1", 5, "c2"], "Phase": "Exploded", "Results": "nonsense",
              "Settings": { "WindowsOnly": false, "NoSuchSetting": true, "AdvancedMode": true } }
            """);
        var file = new SelfTestRunFile(this._folder);

        var read = file.Read(out var damaged)!;

        Assert.True(damaged);
        Assert.Equal("K7Q2", read.Code);
        Assert.Equal(["c1", "c2"], read.TestChannels);
        Assert.Equal(2, read.Settings.Count);
        Assert.False(read.Settings[SelfTestSetting.WindowsOnly].GetBoolean());
        // Set aside: never read again.
        Assert.False(file.Exists);
        Assert.Null(file.Read(out _));
        Assert.Single(Directory.GetFiles(this._folder, SelfTestRunFile.FileName + ".damaged-*"));
    }

    [Fact]
    public void AFileThatIsntJsonAtAllGivesAnEmptyRunAndIsSetAside() {
        Directory.CreateDirectory(this._folder);
        File.WriteAllText(Path.Combine(this._folder, SelfTestRunFile.FileName), "{ not json");
        var file = new SelfTestRunFile(this._folder);

        var read = file.Read(out var damaged)!;

        Assert.True(damaged);
        Assert.Empty(read.Settings);
        Assert.Empty(read.TestChannels);
        Assert.False(file.Exists);
    }

    [Fact]
    public void DeletingTakesTheBackupToo() {
        var file = new SelfTestRunFile(this._folder);
        file.Write(new SelfTestRunData { Code = "AAAA" });
        // A second write keeps the first as its backup.
        file.Write(new SelfTestRunData { Code = "BBBB" });
        Assert.True(File.Exists(file.Path + ".bak"));

        file.Delete();

        Assert.False(file.Exists);
        Assert.Null(file.Read(out _));
    }

    [Fact]
    public void NoFileIsNoRun() => Assert.Null(new SelfTestRunFile(this._folder).Read(out _));

    [Fact]
    public void WhatWasSpentIsKept() {
        var file = new SelfTestRunFile(this._folder);
        var at = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
        file.WriteSpent([new SelfTestSpend(SelfTestAction.CreateChannel, at)]);

        Assert.Equal([new SelfTestSpend(SelfTestAction.CreateChannel, at)], file.ReadSpent());
        File.WriteAllText(Path.Combine(this._folder, SelfTestRunFile.SpendFileName), "garbage");
        File.Delete(Path.Combine(this._folder, SelfTestRunFile.SpendFileName + ".bak"));
        Assert.Empty(file.ReadSpent());
    }
}
