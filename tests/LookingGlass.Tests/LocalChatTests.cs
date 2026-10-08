using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// Local chat's rules, without the game: whom a message goes to (friends near the sender, closest first, at most so many),
/// whether one received is shown (the sender near and a friend), what is said when there is nobody to send to (and when
/// the friends list may not be loaded yet), and its words in both modes. See "Local chat (friends only)" in docs/design.md.
/// </summary>
public sealed class LocalChatTests {
    private static NearbyPlayer Player(string name, float distance, bool friend, string world = "Lich") => new(name, world, distance, friend);

    private static LocalSurroundings Around(bool friendsListLoaded, params NearbyPlayer[] players) => new(players, friendsListLoaded);

    // ================================================================ whom a message goes to

    [Fact]
    public void ItGoesToFriendsWithinSayRangeClosestFirst() {
        var around = Around(true,
            Player("Far Friend", 25, true),
            Player("Near Stranger", 3, false),
            Player("Near Friend", 5, true),
            Player("Nearest Friend", 1, true),
            Player("Edge Friend", LocalChat.SayRange, true));

        var to = LocalChat.Recipients(around, LocalChat.MaxRecipients);

        Assert.Equal(["Nearest Friend", "Near Friend", "Edge Friend"], to.Select(player => player.Name));
    }

    [Fact]
    public void ItGoesToAtMostTheCapTheClosestOnes() {
        var players = Enumerable.Range(0, 80).Select(i => Player($"Friend {i:00}", 19 - i * 0.2f, true)).ToArray();

        var to = LocalChat.Recipients(Around(true, players), 50);

        Assert.Equal(50, to.Count);
        Assert.Equal("Friend 79", to[0].Name);
        Assert.DoesNotContain(to, player => player.Name == "Friend 00");
    }

    [Fact]
    public void TheSamePlayerTwiceGetsOneCopy() {
        var to = LocalChat.Recipients(Around(true, Player("Bob Hatter", 4, true), Player("bob hatter", 2, true), Player("Bob Hatter", 3, true, "Odin")), 50);

        Assert.Equal([("bob hatter", "Lich"), ("Bob Hatter", "Odin")], to.Select(player => (player.Name, player.WorldName)));
    }

    [Fact]
    public void SayRangeIsAboutTwentyYalmsAndAReceiverAllowsForMovement() {
        Assert.Equal(20f, LocalChat.SayRange);
        // Someone running away while it travels is still near enough on arrival, but not someone across the zone.
        Assert.InRange(LocalChat.ReceiveRange, LocalChat.SayRange + 5, LocalChat.SayRange * 2);
        Assert.Equal(50, LocalChat.MaxRecipients);
    }

    // ================================================================ whether a message received is shown

    [Fact]
    public void AMessageFromAFriendNearIsShown() {
        var around = Around(true, Player("Bob Hatter", 12, true));

        Assert.Equal(LocalVerdict.Show, LocalChat.Judge(around, "Bob Hatter", "Lich"));
        // Names and worlds as the game and the server spell them: case doesn't matter.
        Assert.Equal(LocalVerdict.Show, LocalChat.Judge(around, "bob hatter", "LICH"));
    }

    [Fact]
    public void AMessageFromSomeoneNotNearIsDropped() {
        Assert.Equal(LocalVerdict.NotNear, LocalChat.Judge(Around(true), "Bob Hatter", "Lich"));
        Assert.Equal(LocalVerdict.NotNear, LocalChat.Judge(Around(true, Player("Bob Hatter", LocalChat.ReceiveRange + 1, true)), "Bob Hatter", "Lich"));
        // The same name from another world is someone else.
        Assert.Equal(LocalVerdict.NotNear, LocalChat.Judge(Around(true, Player("Bob Hatter", 3, true, "Odin")), "Bob Hatter", "Lich"));
        // A little further than say range is still near: they may have moved while it travelled.
        Assert.Equal(LocalVerdict.Show, LocalChat.Judge(Around(true, Player("Bob Hatter", LocalChat.SayRange + 4, true)), "Bob Hatter", "Lich"));
    }

    [Fact]
    public void AMessageFromSomeoneNearWhoIsntAFriendIsDropped() {
        Assert.Equal(LocalVerdict.NotFriend, LocalChat.Judge(Around(true, Player("Bob Hatter", 3, false)), "Bob Hatter", "Lich"));
    }

    [Fact]
    public void WithTheFriendsListNotLoadedSomeoneNotKnownAsAFriendIsDroppedAndTheReasonSaysSo() {
        Assert.Equal(LocalVerdict.FriendsListNotLoaded, LocalChat.Judge(Around(false, Player("Bob Hatter", 3, false)), "Bob Hatter", "Lich"));
        // The game marks a friend near the player as one whether the list is loaded or not.
        Assert.Equal(LocalVerdict.Show, LocalChat.Judge(Around(false, Player("Bob Hatter", 3, true)), "Bob Hatter", "Lich"));
        // Not near is not near, loaded or not.
        Assert.Equal(LocalVerdict.NotNear, LocalChat.Judge(Around(false), "Bob Hatter", "Lich"));
    }

    // ================================================================ nobody to send to

    [Fact]
    public void WithNobodyNearItSaysSo() {
        Assert.Equal(LocalChatWords.NobodyNear, LocalChat.NobodyToSendTo(Around(true)));
        Assert.Equal(LocalChatWords.NobodyNear, LocalChat.NobodyToSendTo(Around(true, Player("Far Friend", 30, true))));
    }

    [Fact]
    public void WithOnlyStrangersNearItSaysNoneIsAFriend() {
        Assert.Equal(LocalChatWords.NoFriendsNear, LocalChat.NobodyToSendTo(Around(true, Player("Near Stranger", 3, false))));
    }

    [Fact]
    public void WithTheFriendsListNotLoadedItSaysToOpenItOnce() {
        Assert.Equal(LocalChatWords.OpenFriendsList, LocalChat.NobodyToSendTo(Around(false, Player("Near Stranger", 3, false))));
        // Nobody near at all: the list wouldn't help.
        Assert.Equal(LocalChatWords.NobodyNear, LocalChat.NobodyToSendTo(Around(false)));
    }

    [Fact]
    public void WithAFriendNearThereIsSomeoneToSendTo() {
        Assert.Null(LocalChat.NobodyToSendTo(Around(true, Player("Near Friend", 3, true))));
    }

    // ================================================================ colour

    [Fact]
    public void ItsColourIsARowOrACustomColourOrTheDefault() {
        Assert.Null(LocalChat.ColourOf(0, null));
        Assert.Equal(ChannelColour.OfRow(45), LocalChat.ColourOf(45, null));
        // A custom colour wins over a row (a hand-edited file with both), as for channels.
        Assert.Equal(ChannelColour.Custom(0x33DDAA), LocalChat.ColourOf(45, 0x33DDAA));
        Assert.Equal(ChannelColour.Custom(0x33DDAA), LocalChat.ColourOf(0, 0xFF33DDAA));
    }

    // ================================================================ words

    public static TheoryData<string, string> Words() {
        var data = new TheoryData<string, string>();
        foreach (var wording in LocalChatWords.All()) {
            data.Add(wording.Technical, wording.Plain);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void ItsWordsArePlainInSimpleMode(string technical, string plain) {
        Assert.False(string.IsNullOrWhiteSpace(technical));
        PlainLanguage.AssertPlain(plain);
    }

    [Fact]
    public void TheFriendsListLineSaysWhereToOpenIt() {
        Assert.Contains("Friend List", LocalChatWords.OpenFriendsList.Plain);
        Assert.Contains("once", LocalChatWords.OpenFriendsList.Plain);
    }

    [Fact]
    public void WhatWasSentSaysHowManyGotItWithoutNamingAnyone() {
        Assert.Null(LocalChatWords.Sent(new LocalSendResult(3, 0, 0)));
        Assert.Equal(LocalChatWords.NobodyUsesIt, LocalChatWords.Sent(new LocalSendResult(0, 2, 0)));
        Assert.Contains("try again", LocalChatWords.Sent(new LocalSendResult(0, 0, 2))!.Plain);
        // Some got it, some couldn't be checked: said, so the player knows not everyone may have.
        Assert.Contains("1", LocalChatWords.Sent(new LocalSendResult(2, 0, 1))!.Plain);
    }

    [Fact]
    public void TheUsageNamesTheCommandAndTheRange() {
        Assert.Contains(LocalChat.Command, LocalChatWords.Usage.Plain);
        Assert.Contains("friends list", LocalChatWords.Usage.Plain);
        Assert.Contains("20 yalms", LocalChatWords.Usage.Plain);
    }
}
