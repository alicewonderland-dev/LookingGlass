using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// The colours of LookingGlass's own lines in the chat log: LookingGlass blue for information and status, light red for
/// warnings, dark red for critical warnings (the owner's scheme), chosen in one place.
/// </summary>
public sealed class NoticeColourTests {
    [Fact]
    public void EachToneHasItsOwnColour() {
        Assert.Equal(NoticeColours.Blue, NoticeColours.Of(NoticeTone.Info));
        Assert.Equal(NoticeColours.LightRed, NoticeColours.Of(NoticeTone.Warning));
        Assert.Equal(NoticeColours.DarkRed, NoticeColours.Of(NoticeTone.Critical));
        Assert.Equal(3, Enum.GetValues<NoticeTone>().Select(NoticeColours.Of).Distinct().Count());
        // Blue is the default [LGC] tag's colour, row 37 (0x0099FF).
        Assert.Equal((ushort) 37, NoticeColours.Blue);
    }

    [Fact]
    public void InformationIsBlueWarningsAndErrorsLightRed() {
        Assert.Equal(NoticeTone.Info, NoticeColours.ToneOf(NoticeLevel.Info));
        Assert.Equal(NoticeTone.Info, NoticeColours.ToneOf(NoticeLevel.Info, NoticeKind.Joined));
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(NoticeLevel.Warning));
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(NoticeLevel.Error));
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(NoticeLevel.Warning, NoticeKind.KeyChanged));
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(NoticeLevel.Warning, NoticeKind.MembershipHidden));
        Assert.Equal(NoticeTone.Warning, NoticeColours.ToneOf(NoticeLevel.Warning, NoticeKind.StaleKeyOffered));
    }

    [Theory]
    [InlineData(NoticeKind.MembershipForked)]
    [InlineData(NoticeKind.MembersShownDifferently)]
    [InlineData(NoticeKind.RemovalNotInEffect)]
    [InlineData(NoticeKind.ServerRefusesKey)]
    [InlineData(NoticeKind.RelayedRegistrationCode)]
    public void ASignTheServerLiesOrMessagesMayReachSomeoneTheyShouldntIsCritical(NoticeKind kind) {
        foreach (var level in Enum.GetValues<NoticeLevel>()) {
            Assert.Equal(NoticeTone.Critical, NoticeColours.ToneOf(level, kind));
        }
    }

    [Fact]
    public void OnlyThoseAreCritical() {
        var critical = Enum.GetValues<NoticeKind>().Where(kind => NoticeColours.ToneOf(NoticeLevel.Warning, kind) == NoticeTone.Critical).ToHashSet();
        Assert.True(critical.SetEquals(NoticeColours.CriticalKinds));
        Assert.Equal(5, critical.Count);
    }
}
