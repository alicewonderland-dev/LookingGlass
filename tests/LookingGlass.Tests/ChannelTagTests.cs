using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// The tag in front of a channel's chat lines: its nickname if it has one (and that's wanted),
/// else its command number, else a plain [LGC].
/// </summary>
public sealed class ChannelTagTests {
    [Fact]
    public void ANicknameIsTheTag() {
        Assert.Equal("[sky]", ChannelTag.For(3, "sky", true));
        Assert.Equal("[Raid-Team_2]", ChannelTag.For(50, "Raid-Team_2", true));
    }

    [Fact]
    public void WithoutANicknameTheNumberIsTheTag() {
        Assert.Equal("[LGC3]", ChannelTag.For(3, null, true));
        Assert.Equal("[LGC50]", ChannelTag.For(50, "", true));
        Assert.Equal("[LGC1]", ChannelTag.For(1, "   ", true));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("local")]
    [InlineData(" LOCAL ")]
    public void ANicknameSavedAsLocalBeforeItWasReservedIsntShownAsLocalChatsTag(string nickname) {
        // [Local] is local chat's: a channel nicknamed so (saved before the name was reserved) is tagged by its number.
        Assert.Equal("[LGC3]", ChannelTag.For(3, nickname, true));
        Assert.Equal(ChannelTag.Fallback, ChannelTag.For(null, nickname, true));
    }

    [Fact]
    public void WithNicknameTagsOffTheNumberIsTheTag() {
        Assert.Equal("[LGC3]", ChannelTag.For(3, "sky", false));
        Assert.Equal("[LGC12]", ChannelTag.For(12, null, false));
    }

    [Fact]
    public void AChannelWithNeitherANumberNorANicknameIsTaggedLgc() {
        // More channels than numbers, or a message before the channel list is in.
        Assert.Equal("[LGC]", ChannelTag.For(null, null, true));
        Assert.Equal("[LGC]", ChannelTag.For(null, null, false));
        Assert.Equal("[LGC]", ChannelTag.For(null, "sky", false));

        // A nickname still names a channel without a number.
        Assert.Equal("[sky]", ChannelTag.For(null, "sky", true));
    }

    [Theory]
    [InlineData("sky]<b>")]
    [InlineData("a\u0002b")]
    [InlineData("12345")]
    [InlineData("seventeen-letters")]
    [InlineData("ｓｋｙ")]
    public void ANicknameThatIsntValidIsNeverShown(string nickname) {
        // Nicknames are checked when set, but the settings file can be edited by hand.
        Assert.Equal("[LGC4]", ChannelTag.For(4, nickname, true));
        Assert.Equal("[LGC]", ChannelTag.For(null, nickname, true));
    }

    [Fact]
    public void ANumberOutsideTheCommandsIsntShown() {
        Assert.Equal("[LGC]", ChannelTag.For(0, null, true));
        Assert.Equal("[LGC]", ChannelTag.For(CommandSlots.Count + 1, null, true));
    }
}
