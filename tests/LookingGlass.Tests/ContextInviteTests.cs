using System.Collections.Immutable;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Right-click invites (see "Context menu invites" in docs/design.md): which channels "Invite to LookingGlass" offers for a
/// player, how they are labelled, and what is said when the invite is sent or fails, in both modes.
/// </summary>
public sealed class ContextInviteTests {
    private static readonly User Me = new() { UserId = 1, Name = "Alice Liddell", WorldName = "Lich" };
    private static readonly User Bob = new() { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
    private static readonly InviteTarget BobTarget = new("Bob Hatter", "Lich");

    private static readonly Dictionary<string, int> NoSlots = new();
    private static readonly Dictionary<string, string> NoNicknames = new();
    private static readonly Dictionary<string, ushort> NoColours = new();

    private static ChannelView Channel(string id, Rank myRank, string? name = "Tea party", bool oldKey = false, params MemberView[] others) {
        var members = ImmutableArray.Create(new MemberView(Me, myRank, null, false)).AddRange(others);
        return new ChannelView(id, name, 1, 1, true, false, oldKey ? Rank.Unspecified : myRank, members, OldKeyMembership: oldKey);
    }

    private static SessionSnapshot Snapshot(params ChannelView[] channels) => SessionSnapshot.Empty with {
        State = ConnectionState.Ready,
        Me = Me,
        Channels = [.. channels],
        ChannelsLoaded = true,
    };

    private static IReadOnlyList<InviteOffer> Offers(SessionSnapshot snapshot, InviteTarget? target = null, Dictionary<string, int>? slots = null,
        Dictionary<string, string>? nicknames = null, Dictionary<string, ushort>? colours = null, bool advanced = false) =>
        ContextInvites.Offers(snapshot, target ?? BobTarget, slots ?? NoSlots, nicknames ?? NoNicknames, colours ?? NoColours, nicknameTags: true, advanced);

    // ================================================================ which channels

    [Fact]
    public void ModeratorsAndAdminsCanInviteMembersCannot() {
        var snapshot = Snapshot(
            Channel("admin", Rank.Admin),
            Channel("moderator", Rank.Moderator),
            Channel("member", Rank.Member),
            Channel("invited", Rank.Invited));

        Assert.Equal(["admin", "moderator"], Offers(snapshot).Select(offer => offer.ChannelId).Order());
        Assert.All(Offers(snapshot), offer => Assert.True(offer.Available));
    }

    [Fact]
    public void AnOldKeysPlaceIsNeverOffered() {
        // The log has the user as admin, but under keys they no longer have: that place has no say.
        var snapshot = Snapshot(Channel("old", Rank.Admin, oldKey: true), Channel("current", Rank.Admin));
        Assert.Equal(["current"], Offers(snapshot).Select(offer => offer.ChannelId));

        Assert.Empty(Offers(Snapshot(Channel("old", Rank.Admin, oldKey: true))));
    }

    [Fact]
    public void AnOldKeysPlaceIsNeverOfferedWhateverRankItShows() {
        // Pins the old-key check itself: even a view that showed the old place's admin rank offers nothing.
        var members = ImmutableArray.Create(new MemberView(Me, Rank.Admin, null, false));
        var oldAdmin = new ChannelView("old", "Tea party", 1, 1, true, false, Rank.Admin, members, OldKeyMembership: true);
        Assert.Empty(Offers(Snapshot(oldAdmin)));
        Assert.Equal(["current"], Offers(Snapshot(oldAdmin, Channel("current", Rank.Moderator))).Select(offer => offer.ChannelId));
    }

    [Fact]
    public void NothingIsOfferedWhereNoChannelCanBeInvitedTo() {
        Assert.Empty(Offers(Snapshot()));
        Assert.Empty(Offers(Snapshot(Channel("member", Rank.Member))));
    }

    [Fact]
    public void NothingIsOfferedUnlessConnectedAndRegistered() {
        var ready = Snapshot(Channel("sky", Rank.Admin));
        Assert.Single(Offers(ready));

        foreach (var state in Enum.GetValues<ConnectionState>().Where(state => state != ConnectionState.Ready)) {
            Assert.Empty(Offers(ready with { State = state }));
        }

        Assert.Empty(Offers(ready with { Me = null }));
    }

    [Fact]
    public void NothingIsOfferedForYourself() {
        var snapshot = Snapshot(Channel("sky", Rank.Admin));
        Assert.Empty(Offers(snapshot, new InviteTarget("Alice Liddell", "Lich")));
        Assert.Empty(Offers(snapshot, new InviteTarget("alice liddell", "LICH")));

        // The same name on another world is someone else.
        Assert.Single(Offers(snapshot, new InviteTarget("Alice Liddell", "Odin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chocobo")]
    [InlineData("Wind-up Cursor Mk II")]
    [InlineData("Bob  Hatter")]
    public void NothingIsOfferedForWhatIsntAPlayer(string name) {
        Assert.Empty(Offers(Snapshot(Channel("sky", Rank.Admin)), new InviteTarget(name, "Lich")));
    }

    [Fact]
    public void NothingIsOfferedWithoutAHomeWorld() {
        Assert.Empty(Offers(Snapshot(Channel("sky", Rank.Admin)), new InviteTarget("Bob Hatter", "")));
        Assert.Empty(Offers(Snapshot(Channel("sky", Rank.Admin)), new InviteTarget("Bob Hatter", " ")));
    }

    [Fact]
    public void AChannelTheyreInOrInvitedToIsShownGreyedOutWithWhy() {
        var snapshot = Snapshot(
            Channel("in", Rank.Admin, "In it", false, new MemberView(Bob, Rank.Member, null, false)),
            Channel("invited", Rank.Moderator, "Asked", false, new MemberView(Bob, Rank.Invited, null, false)),
            Channel("free", Rank.Admin, "Free"),
            // Someone else of the same name, on another world.
            Channel("namesake", Rank.Admin, "Namesake", false,
                new MemberView(new User { UserId = 3, Name = "Bob Hatter", WorldName = "Odin" }, Rank.Member, null, false)));

        var offers = Offers(snapshot).ToDictionary(offer => offer.ChannelId);
        Assert.Equal("already a member", offers["in"].Unavailable);
        Assert.Equal("already invited", offers["invited"].Unavailable);
        Assert.True(offers["free"].Available);
        Assert.True(offers["namesake"].Available);
        Assert.False(offers["in"].Available);

        // The same, whatever the case of the name the menu gave.
        Assert.False(Offers(snapshot, new InviteTarget("bob hatter", "lich")).Single(offer => offer.ChannelId == "in").Available);
    }

    [Fact]
    public void SomeoneYouBlockedIsShownGreyedOut() {
        var snapshot = Snapshot(Channel("sky", Rank.Admin)) with { BlockedUsers = [Bob] };
        var offer = Assert.Single(Offers(snapshot));
        Assert.False(offer.Available);
        Assert.Equal("you blocked them", offer.Unavailable);
    }

    [Fact]
    public void AChannelWhoseNameIsntKnownYetIsGreyedOut() {
        // An invite carries the channel's name, so there is nothing to invite with yet.
        var offer = Assert.Single(Offers(Snapshot(Channel("abcdef0123456789", Rank.Admin, name: null))));
        Assert.Equal("not ready yet", offer.Unavailable);
        Assert.Equal("[LGC] (channel abcdef01) (not ready yet)", offer.Label);
        Assert.Equal("(encrypted channel abcdef01)", Assert.Single(Offers(Snapshot(Channel("abcdef0123456789", Rank.Admin, name: null)), advanced: true)).Name);
    }

    [Fact]
    public void ChannelsAreInNumberOrderAndAtMostAMenuful() {
        var channels = Enumerable.Range(1, 40).Select(i => Channel($"c{i:00}", Rank.Admin, $"Channel {i:00}")).ToArray();
        // Numbered backwards, and the last few without a number.
        var slots = Enumerable.Range(1, 30).ToDictionary(i => $"c{i:00}", i => 31 - i);

        var offers = Offers(Snapshot(channels), slots: slots);
        Assert.Equal(ContextInvites.MaxOffers, offers.Count);
        Assert.Equal("[LGC1]", offers[0].Tag);
        Assert.Equal("c30", offers[0].ChannelId);
        Assert.Equal(Enumerable.Range(1, ContextInvites.MaxOffers).Select(slot => $"[LGC{slot}]"), offers.Select(offer => offer.Tag));

        // Without numbers, by name.
        var unnumbered = Offers(Snapshot(Channel("b", Rank.Admin, "beta"), Channel("a", Rank.Admin, "Alpha")));
        Assert.Equal(["a", "b"], unnumbered.Select(offer => offer.ChannelId));
    }

    // ================================================================ labels

    [Fact]
    public void EachChannelShowsItsTagInItsColourAndItsName() {
        var snapshot = Snapshot(Channel("sky", Rank.Admin, "Sky pirates"), Channel("fc", Rank.Admin, "Free company"), Channel("odd", Rank.Admin, "Odd one"));
        var offers = Offers(snapshot,
            slots: new Dictionary<string, int> { ["sky"] = 3, ["fc"] = 4 },
            nicknames: new Dictionary<string, string> { ["sky"] = "sky" },
            colours: new Dictionary<string, ushort> { ["sky"] = 45 }).ToDictionary(offer => offer.ChannelId);

        Assert.Equal("[sky]", offers["sky"].Tag);
        Assert.Equal((ushort) 45, offers["sky"].Colour);
        Assert.Equal("[sky] Sky pirates", offers["sky"].Label);
        Assert.Equal(" Sky pirates", offers["sky"].Rest);

        Assert.Equal("[LGC4] Free company", offers["fc"].Label);
        Assert.Null(offers["fc"].Colour);
        Assert.Equal("[LGC] Odd one", offers["odd"].Label);

        // With nickname tags off, the number.
        var numbered = ContextInvites.Offers(snapshot, BobTarget, new Dictionary<string, int> { ["sky"] = 3 }, new Dictionary<string, string> { ["sky"] = "sky" },
            NoColours, nicknameTags: false, advanced: false);
        Assert.Equal("[LGC3]", numbered.Single(offer => offer.ChannelId == "sky").Tag);
    }

    [Fact]
    public void LongOrOddChannelNamesAreShortenedAndCleaned() {
        var offers = Offers(Snapshot(
            Channel("long", Rank.Admin, new string('a', 60)),
            Channel("odd", Rank.Admin, "Tea\u0002\u0010 party\n")));

        var shortened = offers.Single(offer => offer.ChannelId == "long").Name;
        Assert.Equal(ContextInvites.MaxNameLength, shortened.Length);
        Assert.EndsWith("…", shortened);

        // Another member chose the name: no control bytes reach the game's menu.
        Assert.DoesNotContain(offers.Single(offer => offer.ChannelId == "odd").Name, c => char.IsControl(c));
    }

    [Theory]
    [InlineData("Bob Hatter", true)]
    [InlineData("Y'shtola Rhul", true)]
    [InlineData("Alisaie Leveilleur", true)]
    [InlineData("G'raha Tia", true)]
    [InlineData("Jean-luc Picard", true)]
    [InlineData("Echo Bot", true)]
    [InlineData("Bob", false)]
    [InlineData("Bob Hatter Jr", false)]
    [InlineData(" Bob Hatter", false)]
    [InlineData("Bob Hatter ", false)]
    [InlineData("B Hatter", false)]
    [InlineData("Bob H4tter", false)]
    [InlineData("'Bob Hatter", false)]
    [InlineData("Bobbobbobbobbob Hatterhatterhat", false)]
    [InlineData("Bob\u0002 Hatter", false)]
    [InlineData(null, false)]
    public void PlayerNamesAreAForenameAndASurname(string? name, bool player) {
        Assert.Equal(player, ContextInvites.IsPlayerName(name));
    }

    [Fact]
    public void OnlyMenusAboutPlayersGetTheItem() {
        Assert.True(ContextInvites.IsPlayerMenu(null));
        Assert.True(ContextInvites.IsPlayerMenu("ChatLog"));
        Assert.True(ContextInvites.IsPlayerMenu("_PartyList"));
        Assert.True(ContextInvites.IsPlayerMenu("FriendList"));
        Assert.True(ContextInvites.IsPlayerMenu("_TargetInfoMainTarget"));
        Assert.False(ContextInvites.IsPlayerMenu("Inventory"));
        Assert.False(ContextInvites.IsPlayerMenu("RetainerList"));
        Assert.False(ContextInvites.IsPlayerMenu("BlackList"));
    }

    // ================================================================ what is said

    private static InviteOffer Sky => new("sky", "[sky]", 45, "Sky pirates", null);

    [Fact]
    public void SuccessNamesThePlayerAndTheChannelsTag() {
        Assert.Equal("Invited Bob Hatter@Lich to [sky].", ContextInvites.Invited(BobTarget, Sky));
        Assert.Equal("[sky]", ContextInvites.TagIn(Sky));

        // A channel with neither a number nor a nickname is named instead: "[LGC]" says nothing.
        var unnamed = Sky with { Tag = ChannelTag.Fallback };
        Assert.Equal("Invited Bob Hatter@Lich to \"Sky pirates\".", ContextInvites.Invited(BobTarget, unnamed));
        Assert.Null(ContextInvites.TagIn(unnamed));
    }

    [Fact]
    public void AFailureSaysWhyInTheModesWords() {
        var server = new ServerErrorException(ErrorCode.Conflict, "Bob Hatter is already a member or invited.");
        Assert.Equal("Couldn't invite Bob Hatter@Lich to [sky]: Bob Hatter is already a member or invited.",
            ContextInvites.NotInvited(BobTarget, Sky, server, advanced: false));
        Assert.Equal("Couldn't invite Bob Hatter@Lich to [sky]: Bob Hatter is already a member or invited. (Conflict)",
            ContextInvites.NotInvited(BobTarget, Sky, server, advanced: true));

        var notHere = PlainMessages.Failure(PlainMessages.NotRegisteredHere(BobTarget.Who));
        Assert.Equal("Couldn't invite Bob Hatter@Lich to [sky]: Bob Hatter@Lich isn't registered with LookingGlass on this server.",
            ContextInvites.NotInvited(BobTarget, Sky, notHere, advanced: true));
        Assert.StartsWith("Couldn't invite Bob Hatter@Lich to [sky]: Bob Hatter@Lich isn't registered with LookingGlass on this server: they need to",
            ContextInvites.NotInvited(BobTarget, Sky, notHere, advanced: false));

        var limit = new ServerErrorException(ErrorCode.RateLimited, "You've sent a lot of invites to Bob Hatter@Lich recently; try again in about a minute.");
        Assert.Equal("Couldn't invite Bob Hatter@Lich to [sky]: You've sent a lot of invites to Bob Hatter@Lich recently; try again in about a minute.",
            ContextInvites.NotInvited(BobTarget, Sky, limit, advanced: false));
        Assert.EndsWith("try again in about a minute. (RateLimited)", ContextInvites.NotInvited(BobTarget, Sky, limit, advanced: true));

        var offline = new SessionDisconnectedException("Not connected to the server.");
        Assert.Equal("Couldn't invite Bob Hatter@Lich to [sky]: Not connected to the server.", ContextInvites.NotInvited(BobTarget, Sky, offline, advanced: false));
    }

    [Fact]
    public void WhatIsSaidIsInPlainWords() {
        PlainLanguage.AssertPlain(ContextInvites.MenuLabel);
        var snapshot = Snapshot(
            Channel("in", Rank.Admin, "In it", false, new MemberView(Bob, Rank.Member, null, false)),
            Channel("invited", Rank.Admin, "Asked", false, new MemberView(Bob, Rank.Invited, null, false)),
            Channel("unnamed", Rank.Admin, null));
        foreach (var offer in Offers(snapshot).Concat(Offers(snapshot with { BlockedUsers = [Bob] }))) {
            PlainLanguage.AssertPlain(offer.Label);
        }

        PlainLanguage.AssertPlain(ContextInvites.Invited(BobTarget, Sky));
        PlainLanguage.AssertPlain(ContextInvites.NotInvited(BobTarget, Sky, PlainMessages.Failure(PlainMessages.NotRegisteredHere(BobTarget.Who)), advanced: false));
        PlainLanguage.AssertPlain(ContextInvites.NotInvited(BobTarget, Sky, PlainMessages.Failure(PlainMessages.InvalidKeys(BobTarget.Who)), advanced: false));
    }

    // ================================================================ against a server, through the same invite as the Invite button

    [Fact]
    public async Task InvitingFromTheMenuIsTheChannelsOwnInvite() {
        await using var server = new Harness();
        var alice = await server.RegisterAsync("Alice Menu");
        var bob = await server.RegisterAsync("Bob Menu");
        var channelId = await alice.Session.CreateChannelAsync("Tea party", Ct);
        var target = new InviteTarget("Bob Menu", ProtocolInfo.DebugWorldName);
        var slots = new Dictionary<string, int> { [channelId] = 1 };

        var offer = Assert.Single(Offers(alice.Session.Snapshot, target, slots));
        Assert.True(offer.Available);
        Assert.Equal("[LGC1] Tea party", offer.Label);

        await alice.Session.InviteAsync(offer.ChannelId, target.Name, target.WorldName, Ct);
        Assert.Equal($"Invited Bob Menu@{ProtocolInfo.DebugWorldName} to [LGC1].", ContextInvites.Invited(target, offer));
        await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(invite => invite.ChannelId == channelId));

        // Bob is invited now: the menu says so rather than offering it again.
        var again = await WaitFor(() => Offers(alice.Session.Snapshot, target, slots).SingleOrDefault(o => o.Unavailable != null));
        Assert.Equal("already invited", again.Unavailable);

        // And Bob's own menu on Alice offers nothing: Bob can't invite anywhere.
        Assert.Empty(Offers(bob.Session.Snapshot, new InviteTarget("Alice Menu", ProtocolInfo.DebugWorldName), new Dictionary<string, int>()));
    }

    [Fact]
    public async Task SomeoneNotRegisteredOnThisServerIsSaidPlainly() {
        await using var server = new Harness();
        var alice = await server.RegisterAsync("Alice Nobody");
        var channelId = await alice.Session.CreateChannelAsync("Tea party", Ct);
        var target = new InviteTarget("Nobody Here", ProtocolInfo.DebugWorldName);
        var offer = Assert.Single(Offers(alice.Session.Snapshot, target, new Dictionary<string, int> { [channelId] = 2 }));

        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => alice.Session.InviteAsync(offer.ChannelId, target.Name, target.WorldName, Ct));
        Assert.Equal($"Couldn't invite {target.Who} to [LGC2]: {target.Who} isn't registered with LookingGlass on this server.",
            ContextInvites.NotInvited(target, offer, failure, advanced: true));
        var plain = ContextInvites.NotInvited(target, offer, failure, advanced: false);
        Assert.Contains("isn't registered with LookingGlass on this server: they need to install it", plain);
        Assert.DoesNotContain("NotFound", plain);
        PlainLanguage.AssertPlain(plain);
    }
}
