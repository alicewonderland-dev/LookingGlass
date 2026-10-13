using LookingGlass.Core.Client;
using static LookingGlass.Core.SelfTest.SelfTestSteps;

namespace LookingGlass.Core.SelfTest.Suites;

/// <summary>
/// "Settings" (one player): settings-checklist.md 1 to 9 and device-notices-checklist.md 1. The runner opens Settings,
/// checks the "?" bubbles' words in both modes, and turns advanced mode on for step 6; the look is the tester's to judge.
/// Nothing is pressed for them (never Sign out everywhere else, never Reset my identity).
/// </summary>
public static class SettingsSuite {
    public const string Id = "settings";

    public static SelfTestSuite Create() => new(Id, "Settings", [Labels(), Computers()]) {
        Changes = [SelfTestSetting.AdvancedMode],
    };

    private static SelfTestCheck Labels() => new() {
        Id = "settings-1-9", Title = "The labels and the \"?\"s", Steps = "settings 1-9", Mode = CheckMode.Guided, Needs = SelfTestNeeds.Connection,
        StepList = [
            Auto("The nine \"?\"s' words, in both modes", "4, 6", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, false).ConfigureAwait(false);
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                SettingHelp[] nine = [SettingHelp.ServerAddress, SettingHelp.ShowMessagesIn, SettingHelp.WindowsOnly, SettingHelp.LocalChat, SettingHelp.LocalPrivacy,
                    SettingHelp.ChatHistory, SettingHelp.AdvancedMode, SettingHelp.SignedInComputers, SettingHelp.ResetIdentity];
                foreach (var setting in nine) {
                    var help = SettingsWords.Help(setting, "Windows DPAPI");
                    foreach (var advanced in new[] { false, true }) {
                        var text = help.For(advanced);
                        if (string.IsNullOrWhiteSpace(text) || text.Length > SettingsWords.MaxHelpLength) {
                            return StepResult.Fail("a \"?\" bubble's words are missing or too long");
                        }
                    }

                    if (setting != SettingHelp.AdvancedMode && SelfTestWords.JargonIn(help.Plain).Count > 0) {
                        return StepResult.Fail("a \"?\" bubble has a technical word in simple mode");
                    }
                }

                return StepResult.Expect(SettingsWords.Help(SettingHelp.ChatHistory, "Windows DPAPI").Technical.Contains("Windows DPAPI", StringComparison.Ordinal),
                    "advanced mode's chat history \"?\" doesn't say how it's kept");
            }),
            Ask("Sections and labels", "1-2", new GuidedPrompt(
                "In Settings, are the sections, in order: Server, Chat, Local chat, Chat history, Your identity, Blocked users, with no dimmed paragraphs? Are the labels as the " +
                "checklist's step 2 lists them (Server address, Connect automatically with Disconnect beside it, Show messages in, Colour the whole line, ...)?", PromptKind.YesNo) {
                Short = "labels",
            }),
            Ask("Each setting still works", "3", new GuidedPrompt(
                "Optional (Skip if you did it before): tick and untick each box, pick another chat channel and a local chat colour. Does each take effect as before? " +
                "(The self-test puts its own settings back; your changes here stay.)", PromptKind.YesNo) { Short = "each setting" }),
            Ask("The \"?\"s", "4-5", new GuidedPrompt(
                "Is there a small round ? after exactly these: Server address, Show messages in, Show LookingGlass only in windows, local chat's Colour, Privacy notice accepted, " +
                "Keep chat history on this computer, Advanced mode, Computers signed in and Reset my identity...? Does each open a small bubble beside it on a click (not on hover), " +
                "closing on a click elsewhere, on its ? again, and on Escape (Escape leaving Settings open)?", PromptKind.YesNo) { Short = "the ?s" }),
            Ask("Advanced mode's history \"?\"", "6", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, true).ConfigureAwait(false);
                return new GuidedPrompt("Advanced mode is on now. Does Keep chat history on this computer's ? say it's stored encrypted and name how (such as Windows DPAPI), " +
                                        "while the others say the same as before?", PromptKind.YesNo) { Short = "advanced ?" };
            }),
            Ask("Near the bottom right", "7", async context => {
                await context.ChangeSettingAsync(SelfTestSetting.AdvancedMode, false).ConfigureAwait(false);
                return new GuidedPrompt("Move Settings to the bottom right corner of the screen and open each ? near the bottom: does every bubble stay on the screen?", PromptKind.YesNo) {
                    Short = "bottom right",
                };
            }),
            Ask("A greyed-out button's \"?\"", "8", new GuidedPrompt(
                "Optional: while Reset my identity... is greyed out (something in progress), does its ? still open? Skip if you can't make it greyed out.", PromptKind.YesNo) {
                Short = "greyed button",
            }),
            Ask("A narrow window", "9", new GuidedPrompt(
                "Make Settings as narrow as it goes: do Disconnect (or Connect now), Read it and Withdraw move under their lines, with nothing past the right edge? " +
                "Optional: the same with a bigger Global Font Scale in Dalamud's settings.", PromptKind.YesNo) { Short = "narrow" }),
        ],
    };

    private static SelfTestCheck Computers() => new() {
        Id = "device-notices-1", Title = "The list in Settings (computers signed in)", Steps = "device-notices 1", Mode = CheckMode.Guided, Needs = SelfTestNeeds.Connection,
        StepList = [
            Ask("Computers signed in", "1", async context => {
                await context.Host.ShowSettingsAsync(true).ConfigureAwait(false);
                return new GuidedPrompt("In Settings, under Your identity: is there Computers signed in with a ?, a line \"This computer: added ..., used just now\", and a " +
                                        "Sign out everywhere else... button (don't press it)? Does the ? say a sentence or two in everyday words? No warning line in chat?",
                    PromptKind.YesNo) { Short = "the list" };
            }),
        ],
    };
}
