using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// The Settings window's words (see <see cref="SettingsWords"/>): short labels, and for a setting its label doesn't explain,
/// a "?" whose bubble says a few short sentences, in plain words in simple mode.
/// </summary>
public sealed class SettingsWordsTests {
    private static readonly string[] Protections = ["Windows DPAPI", "local key file"];

    public static TheoryData<SettingHelp> Settings() => new(Enum.GetValues<SettingHelp>());

    [Theory]
    [MemberData(nameof(Settings))]
    public void EveryQuestionMarkHasShortWordsInBothModes(SettingHelp setting) {
        foreach (var protection in Protections) {
            var help = SettingsWords.Help(setting, protection);
            foreach (var advanced in new[] { false, true }) {
                var text = help.For(advanced);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{setting}'s \"?\" would show nothing.");
                Assert.True(text.Length <= SettingsWords.MaxHelpLength, $"{setting}'s \"?\" says {text.Length} characters, more than {SettingsWords.MaxHelpLength}: {text}");
                // A few whole sentences, not a manual.
                Assert.EndsWith(".", text);
                Assert.True(text.Split(". ").Length <= 3, $"{setting}'s \"?\" says more than three sentences: {text}");
                Assert.DoesNotContain("{0}", text);
                Assert.DoesNotContain("\n", text);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void SimpleModesBubblesArePlain(SettingHelp setting) {
        var help = SettingsWords.Help(setting, "Windows DPAPI");
        if (setting == SettingHelp.AdvancedMode) {
            // The one exception: it says what turning advanced mode on shows, which is its words. The same in both modes.
            Assert.Equal(help.Technical, help.Plain);
            Assert.Contains("fingerprints", help.Plain);
            return;
        }

        PlainLanguage.AssertPlain(help.Plain);
    }

    [Fact]
    public void TheOwnersWordsAreKept() {
        Assert.Equal("The LookingGlass server you connect to. Only change it if you were given a different address. Your channels come with you if it's the same server.",
            SettingsWords.Help(SettingHelp.ServerAddress, "x").Plain);
        Assert.Equal("The game chat channel LookingGlass uses, so your chat tabs can show or hide its lines.",
            SettingsWords.Help(SettingHelp.ShowMessagesIn, "x").Plain);
        Assert.Equal("Channel messages appear only in channel windows, not game chat. Warnings and replies to your commands still show in game chat.",
            SettingsWords.Help(SettingHelp.WindowsOnly, "x").Plain);
        Assert.Equal("/lgl message talks to friends near you who use LookingGlass. /lgl on its own keeps talking there until you type /s.",
            SettingsWords.Help(SettingHelp.LocalChat, "x").Plain);
        Assert.Equal("To find friends near you, the server learns their names when you use /lgl. It never sees what you say.",
            SettingsWords.Help(SettingHelp.LocalPrivacy, "x").Plain);
        Assert.Equal("Lets channel windows show older messages next time you play. Stored scrambled, never uploaded. The oldest messages go first when it's full.",
            SettingsWords.Help(SettingHelp.ChatHistory, "x").Plain);
        Assert.Equal("Shows encryption details, like fingerprints to compare with friends, and technical wording in warnings.",
            SettingsWords.Help(SettingHelp.AdvancedMode, "x").Plain);
        Assert.Equal("Sets LookingGlass up again for this character, if its files were lost or someone may have copied them. Your channels come back when you register again.",
            SettingsWords.Help(SettingHelp.ResetIdentity, "x").Plain);
        Assert.Equal("Show LookingGlass only in windows", WindowsOnly.SettingName);
        Assert.Equal("Say when I start or stop talking in a channel", SettingsWords.SayWhenTalking);
    }

    [Fact]
    public void AdvancedModeNamesHowTheChatHistoryIsProtected() {
        var help = SettingsWords.Help(SettingHelp.ChatHistory, "Windows DPAPI");

        Assert.Contains("Windows DPAPI", help.Technical);
        Assert.Contains("encrypted", help.Technical);
        Assert.DoesNotContain("Windows DPAPI", help.Plain);
    }

    [Fact]
    public void LabelsAreShortAndPlain() {
        var labels = SettingsWords.Labels();

        Assert.Equal(labels.Count, labels.Distinct().Count());
        foreach (var label in labels) {
            PlainLanguage.AssertPlain(label);
            Assert.True(label.Length <= SettingsWords.MaxLabelLength, $"The label \"{label}\" is longer than {SettingsWords.MaxLabelLength} characters.");
            // A name, not a sentence; its "?" is a button of its own.
            Assert.False(label.EndsWith('.'), $"The label \"{label}\" ends with a full stop.");
            Assert.DoesNotContain("?", label);
            Assert.DoesNotContain(":", label);
        }

        // Advanced mode's name for the chat history is short too.
        Assert.True(ChatLogWords.KeepIt.Technical.Length <= SettingsWords.MaxLabelLength);
        PlainLanguage.AssertPlain(SettingsWords.NobodyBlocked);
    }

    [Fact]
    public void ASettingWithoutWordsIsAMistake() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsWords.Help((SettingHelp) 999, "x"));
}
