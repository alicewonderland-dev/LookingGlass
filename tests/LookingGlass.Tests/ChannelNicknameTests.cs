using LookingGlass.Core.Client;
using static LookingGlass.Tests.CommandSlotTests;

namespace LookingGlass.Tests;

/// <summary>
/// Local channel nicknames, for /lgc &lt;nickname&gt; &lt;message&gt;: they belong to one character,
/// never reach the server, and like command slots stay until their channel is gone.
/// </summary>
public sealed class ChannelNicknameTests {
    [Theory]
    [InlineData("sky")]
    [InlineData("S")]
    [InlineData("x1")]
    [InlineData("1x")]
    [InlineData("fc-raid_2")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("ABCDEFGHIJKLMNOP")]
    public void ValidNicknames(string nickname) {
        Assert.Null(ChannelNicknames.Validate(nickname));
    }

    [Theory]
    [InlineData("", "A nickname needs at least one character.")]
    [InlineData("ABCDEFGHIJKLMNOPQ", "A nickname can be at most 16 characters.")]
    [InlineData("sky blue", "A nickname can only use letters, digits, - and _.")]
    [InlineData("sky!", "A nickname can only use letters, digits, - and _.")]
    [InlineData("café", "A nickname can only use letters, digits, - and _.")]
    [InlineData("sky\u0002", "A nickname can only use letters, digits, - and _.")]
    [InlineData("/lgc", "A nickname can only use letters, digits, - and _.")]
    [InlineData("3", "A nickname can't be only digits: /lgc 3 would look like /lgc3.")]
    [InlineData("0042", "A nickname can't be only digits: /lgc 0042 would look like /lgc0042.")]
    public void InvalidNicknames(string nickname, string error) {
        Assert.Equal(error, ChannelNicknames.Validate(nickname));
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData(" Local ")]
    public void LocalIsLocalChatsAndNoChannelsNickname(string nickname) {
        // Its tag would be [Local], local chat's.
        Assert.Equal("Local is used by local chat; choose another nickname.", ChannelNicknames.Validate(nickname));
        var nicknames = new Dictionary<string, string>();
        Assert.Equal("Local is used by local chat; choose another nickname.", ChannelNicknames.Set(nicknames, "aaa", nickname));
        Assert.Empty(nicknames);

        // Only the whole word.
        Assert.Null(ChannelNicknames.Validate("locals"));
        Assert.Null(ChannelNicknames.Validate("my-local"));
    }

    [Fact]
    public void NicknamesAreUniqueIgnoringCase() {
        var nicknames = new Dictionary<string, string>();

        Assert.Null(ChannelNicknames.Set(nicknames, "aaa", "  Sky "));
        Assert.Equal("Sky", nicknames["aaa"]);

        // Another channel can't take it in any case; nothing changes when refused.
        Assert.Equal("Another channel already has the nickname 'Sky'.", ChannelNicknames.Set(nicknames, "bbb", "sky"));
        Assert.Equal("Another channel already has the nickname 'Sky'.", ChannelNicknames.Set(nicknames, "bbb", "SKY"));
        Assert.Equal("Another channel already has the nickname 'Sky'.", ChannelNicknames.Check(nicknames, "bbb", "sKy"));
        Assert.False(nicknames.ContainsKey("bbb"));

        // An invalid one is refused, and keeps the old nickname.
        Assert.Equal("A nickname can be at most 16 characters.", ChannelNicknames.Set(nicknames, "aaa", "much-too-long-a-name"));
        Assert.Equal("Sky", nicknames["aaa"]);

        // Its own channel can change its case, or change it altogether, which frees the old one.
        Assert.Null(ChannelNicknames.Check(nicknames, "aaa", "SKY"));
        Assert.Null(ChannelNicknames.Set(nicknames, "aaa", "SKY"));
        Assert.Equal("SKY", nicknames["aaa"]);
        Assert.Null(ChannelNicknames.Set(nicknames, "aaa", "moon"));
        Assert.Null(ChannelNicknames.Set(nicknames, "bbb", "sky"));
        Assert.Equal(new Dictionary<string, string> { ["aaa"] = "moon", ["bbb"] = "sky" }, nicknames);

        // Clearing: empty or blank.
        Assert.Null(ChannelNicknames.Set(nicknames, "aaa", ""));
        Assert.Null(ChannelNicknames.Set(nicknames, "bbb", "   "));
        Assert.Null(ChannelNicknames.Set(nicknames, "ccc", null));
        Assert.Empty(nicknames);
    }

    [Fact]
    public void NicknamesResolveToTheirChannelIgnoringCase() {
        var nicknames = new Dictionary<string, string> { ["aaa"] = "Sky", ["bbb"] = "fc_raid" };

        Assert.Equal("aaa", ChannelNicknames.ChannelWith(nicknames, "sky"));
        Assert.Equal("aaa", ChannelNicknames.ChannelWith(nicknames, "SKY"));
        Assert.Equal("bbb", ChannelNicknames.ChannelWith(nicknames, "FC_Raid"));
        Assert.Null(ChannelNicknames.ChannelWith(nicknames, "moon"));
        Assert.Null(ChannelNicknames.ChannelWith(nicknames, "sk"));
        Assert.Null(ChannelNicknames.ChannelWith(nicknames, ""));
    }

    [Theory]
    [InlineData("sky hello there", "sky", "hello there")]
    [InlineData("  sky   hello there  ", "sky", "hello there")]
    [InlineData("sky\thello", "sky", "hello")]
    [InlineData("sky hello  spaced   out", "sky", "hello  spaced   out")]
    [InlineData("sky", "sky", "")]
    [InlineData("  sky   ", "sky", "")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    public void NicknameArgumentsAreParsed(string arguments, string nickname, string message) {
        Assert.Equal(new NicknameArguments(nickname, message), ChannelCommand.ParseNickname(arguments));
    }

    [Fact]
    public void NicknameCommandsResolveOrExplain() {
        var nicknames = new Dictionary<string, string> { ["aaa"] = "Sky" };
        const string usage = "Usage: /lgc <nickname> <message>, or /lgc1 to /lgc50 <message>. " +
                             "Leave out the message to keep talking in that channel until you switch back (for example with /s). " +
                             "Set nicknames and numbers in the main window (/lg).";

        Assert.Equal(new ChannelCommand.Send("aaa", "hello there"), ChannelCommand.ForNickname(nicknames, " sky hello there "));
        Assert.Equal(new ChannelCommand.Send("aaa", "hi"), ChannelCommand.ForNickname(nicknames, "SKY hi"));

        // /lgc alone explains itself.
        Assert.Equal(new ChannelCommand.Usage(usage), ChannelCommand.ForNickname(nicknames, ""));
        Assert.Equal(new ChannelCommand.Usage(usage), ChannelCommand.ForNickname(nicknames, "   "));

        // A nickname with no message: talk in that channel from now on.
        Assert.Equal(new ChannelCommand.TalkIn("aaa"), ChannelCommand.ForNickname(nicknames, "sky"));
        Assert.Equal(new ChannelCommand.TalkIn("aaa"), ChannelCommand.ForNickname(nicknames, "  SKY  "));

        // An unknown nickname, with or without a message.
        Assert.Equal(new ChannelCommand.NotFound("No channel has the nickname 'moon'."), ChannelCommand.ForNickname(nicknames, "moon  "));
        Assert.Equal(new ChannelCommand.NotFound("No channel has the nickname 'moon'."), ChannelCommand.ForNickname(nicknames, "moon hello"));

        // A nickname is never only digits, so "/lgc 3" is never channel number 3 (that is /lgc3), with or without a message.
        Assert.Equal(new ChannelCommand.NotFound("No channel has the nickname '3'."), ChannelCommand.ForNickname(nicknames, "3 hello"));
        Assert.Equal(new ChannelCommand.NotFound("No channel has the nickname '3'."), ChannelCommand.ForNickname(nicknames, "3"));
    }

    [Fact]
    public void NicknamesSurviveARestartAndAreOnlyRemovedWhenTheirChannelIsGone() {
        var nicknames = new Dictionary<string, string> { ["aaa"] = "sky", ["bbb"] = "Moon", ["ccc"] = "fc" };
        var saved = new Dictionary<string, string>(nicknames);

        // What a restart publishes before the list is in: Ready with no channels, then some of them.
        Assert.False(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Ready, false)));
        Assert.False(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Ready, false, "ccc")));
        Assert.False(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Reconnecting, true, "aaa")));
        Assert.False(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Stopped, true)));
        Assert.Equal(saved, nicknames);

        // The complete list, in another order and with a new channel: nothing changes (new channels get no nickname).
        Assert.False(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "bbb", "aaa")));
        Assert.Equal(saved, nicknames);
        Assert.Equal("bbb", ChannelNicknames.ChannelWith(nicknames, "moon"));

        // Leaving (or being removed from) a channel drops its nickname, which another channel can then take.
        Assert.True(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "aaa")));
        Assert.Equal(new Dictionary<string, string> { ["aaa"] = "sky", ["ccc"] = "fc" }, nicknames);
        Assert.Null(ChannelNicknames.ChannelWith(nicknames, "moon"));
        Assert.Null(ChannelNicknames.Set(nicknames, "ddd", "moon"));

        // Everything gone.
        Assert.True(ChannelNicknames.Sync(nicknames, Snapshot(ConnectionState.Ready, true)));
        Assert.Empty(nicknames);
    }
}
