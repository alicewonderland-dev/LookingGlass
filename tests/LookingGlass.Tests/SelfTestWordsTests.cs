using LookingGlass.Core.SelfTest;

namespace LookingGlass.Tests;

/// <summary>The self-test's words (docs/self-test-plan.md, "Words"): plain in simple mode, short, and the owner's names for the buttons.</summary>
public sealed class SelfTestWordsTests {
    [Fact]
    public void EveryWordIsPlain() {
        foreach (var text in SelfTestWords.All()) {
            PlainLanguage.AssertPlain(text);
        }
    }

    [Fact]
    public void ReasonsAreShortLowerCaseAndFitTheReport() {
        foreach (var reason in SelfTestWords.Reasons()) {
            Assert.True(reason.Length <= SelfTestWords.MaxReasonLength, $"Too long: {reason}");
            Assert.False(char.IsUpper(reason[0]), $"A reason follows a colon in the report: {reason}");
            Assert.False(reason.EndsWith('.'), $"A reason ends the line without a full stop: {reason}");
        }
    }

    [Fact]
    public void TheButtonsAreThePlansWords() {
        string[] buttons = [
            SelfTestWords.OpenButton, SelfTestWords.Start, SelfTestWords.StopAndCleanUp, SelfTestWords.NeedsYou, SelfTestWords.Done, SelfTestWords.DidntWork,
            SelfTestWords.SkipStep, SelfTestWords.Yes, SelfTestWords.No, SelfTestWords.CopyReport, SelfTestWords.RunFailedAgain, SelfTestWords.CheckAgain,
            SelfTestWords.TwoPlayers,
        ];
        Assert.Equal(["Self-test...", "Start", "Stop and clean up", "Needs you", "Done", "It didn't work", "Skip this step", "Yes", "No", "Copy report",
            "Run the failed ones again", "Check again", "Two players..."], buttons);
        Assert.All(buttons, button => Assert.True(button.Length <= SelfTestWords.MaxButtonLength));
    }

    [Fact]
    public void ThePlacesAreNamedNeverByTheirOwnNames() {
        Assert.Equal("an inn room", SelfTestWords.Place(PlaceKind.InnRoom));
        Assert.Equal("an apartment lobby", SelfTestWords.Place(PlaceKind.ApartmentLobby));
        Assert.All(Enum.GetValues<PlaceKind>(), kind => Assert.False(string.IsNullOrWhiteSpace(SelfTestWords.Place(kind))));
    }

    [Fact]
    public void TheJargonCheckMatchesThePlainLanguageTests() {
        foreach (var text in new[] { "Your key changed.", "The log head.", "Signed by them.", "Signed in elsewhere.", "Logged in.", "encrypted" }) {
            Assert.Equal(PlainLanguage.Jargon(text), SelfTestWords.JargonIn(text));
        }
    }

    [Fact]
    public void TheCleanUpLineSaysWhatWasDone() {
        Assert.Equal("Clean-up: test channel left (deleted); 7 settings put back; your channel windows opened again.", SelfTestWords.CleanUp(1, 0, 7, 0, true, false));
        Assert.Equal("Clean-up: a test channel couldn't be left; 1 setting put back; 1 couldn't be put back; the test's lines removed from the chat history.",
            SelfTestWords.CleanUp(0, 1, 1, 1, false, true));
    }
}
