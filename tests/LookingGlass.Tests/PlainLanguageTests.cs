using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// Simple mode (the default) and advanced mode: every warning has words for both, simple mode's have no jargon, and the
/// mode only changes the words, never whether something is shown. See <see cref="Wording"/> and <see cref="PlainMessages"/>.
/// (Every notice in every other test is checked too: see <see cref="Harness.DisposeAsync"/>.)
/// </summary>
public sealed class PlainLanguageTests {
    public static TheoryData<string, string> Wordings() {
        var data = new TheoryData<string, string>();
        foreach (var wording in PlainMessages.Examples()) {
            data.Add(wording.Technical, wording.Plain);
        }

        return data;
    }

    [Fact]
    public void EveryKindOfWarningHasWordsForSimpleMode() {
        var covered = PlainMessages.Examples().Select(wording => wording.Kind).ToHashSet();

        var missing = Enum.GetValues<NoticeKind>().Where(kind => kind != NoticeKind.General && !covered.Contains(kind)).ToList();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Wordings))]
    public void SimpleModeSaysItWithoutJargon(string technical, string plain) {
        Assert.False(string.IsNullOrWhiteSpace(technical));
        PlainLanguage.AssertPlain(plain);
    }

    [Fact]
    public void TheJargonCheckCatchesTechnicalWordsButNotEverydayOnes() {
        Assert.Equal(["key"], PlainLanguage.Jargon("Their identity key changed."));
        Assert.Equal(["keys"], PlainLanguage.Jargon("Their keys are unchanged."));
        Assert.Equal(["fingerprints", "epoch", "rekeying", "log", "forked", "signature", "encrypted", "pinned"],
            PlainLanguage.Jargon("Compare fingerprints. epoch 3, rekeying, the membership log forked, a bad signature, encrypted, pinned"));
        Assert.Empty(PlainLanguage.Jargon("Your login is tried again by itself, through the Lodestone, once you've logged in. Check with them over /tell."));
    }

    [Fact]
    public void AdvancedModeKeepsTodaysWords() {
        Assert.Equal(PlainMessages.ReVerifiedElsewhere, PlainMessages.ReVerifiedElsewhereWording.Technical);
        Assert.Equal(PlainMessages.KeyMovedAwayChannel, PlainMessages.KeyMovedAwayWording.Technical);
        Assert.Equal(PlainMessages.OldKeyChannel, PlainMessages.OldKeyChannelWording.Technical);
        Assert.Equal(PlainMessages.CantLeaveOldKeyChannel, PlainMessages.CantLeaveOldKeyWording.Technical);
        Assert.Equal(PlainMessages.OldKeyCantChangeMembers, PlainMessages.OldKeyCantChangeMembersWording.Technical);
        Assert.Equal(PlainMessages.LoginMaybeReplaced, PlainMessages.LoginMaybeReplacedWording.Technical);
        Assert.Equal("Bob Hatter@Lich re-verified their character and has a new key.", PlainMessages.ReVerifiedWording("Bob Hatter@Lich", false).Technical);
        Assert.Equal("Bob Hatter@Lich's identity key changed (they may have re-registered). Compare fingerprints over /tell before trusting it: 1 2",
            PlainMessages.KeyChanged("Bob Hatter@Lich", "1 2").Technical);
    }

    [Fact]
    public void SimpleModeSaysWhatHappenedAndWhatToDo() {
        Assert.Equal("Bob Hatter@Lich set up LookingGlass again (new computer or reset). If you didn't expect that, check with them over /tell.",
            PlainMessages.ReVerifiedWording("Bob Hatter@Lich", false).Plain);
        Assert.StartsWith("Bob Hatter@Lich set up LookingGlass again (new computer or reset), or someone else may be using their name.",
            PlainMessages.KeyChanged("Bob Hatter@Lich", "1 2").Plain);
        // Only a new setup changes anything, not reinstalling the plugin; and new keys are a change to the members, joining or leaving or not.
        Assert.All(PlainMessages.Examples(), wording => Assert.DoesNotContain("reinstall", wording.Plain));
        Assert.Equal("Being updated after a change to its members.", PlainMessages.NewKeyPending.Plain);

        // Wherever someone else's identity or a channel's members can't be trusted as shown: check over /tell.
        foreach (var wording in PlainMessages.Examples().Where(wording => wording.Kind is NoticeKind.KeyChanged or NoticeKind.NameNowAnotherAccount
                     or NoticeKind.ReVerified or NoticeKind.InviteFromChangedKey or NoticeKind.NameChangedWhileRekeying or NoticeKind.MembershipHidden
                     or NoticeKind.MembershipForked or NoticeKind.MembersShownDifferently or NoticeKind.StaleKeyOffered or NoticeKind.MembershipChangeRefused)) {
            Assert.Contains("/tell", wording.Plain);
        }

        // Wherever this user's own place may have been taken: what to do if it wasn't them.
        foreach (var wording in new[] { PlainMessages.ReVerifiedElsewhereWording, PlainMessages.KeyMovedAwayWording, PlainMessages.LoginNotRecognized }) {
            Assert.Contains("If that wasn't you, use \"Reset my identity\" in Settings", wording.Plain);
        }
    }

    [Fact]
    public void ANoticeIsShownInEitherMode() {
        var notice = SessionNotice.Of(NoticeLevel.Warning, PlainMessages.KeyChanged("Bob Hatter@Lich", "1 2"), "abc");

        Assert.Equal(NoticeKind.KeyChanged, notice.Kind);
        Assert.Equal(notice.Text, notice.TextFor(advanced: true));
        Assert.Equal(notice.Plain, notice.TextFor(advanced: false));
        Assert.StartsWith("Bob Hatter@Lich's identity key changed", notice.TextFor(advanced: true));
        Assert.StartsWith("Bob Hatter@Lich set up LookingGlass again", notice.TextFor(advanced: false));
        PlainLanguage.AssertShownInBothModes(notice);

        // A notice with no plain words (from elsewhere) is shown as it is in simple mode, never dropped.
        var announcement = new SessionNotice(NoticeLevel.Info, "The server restarts at noon.");
        Assert.Equal("The server restarts at noon.", announcement.TextFor(advanced: false));
        Assert.Equal(NoticeKind.General, announcement.Kind);
    }

    [Fact]
    public void SwitchingModesNeverHidesAWarning() {
        foreach (var wording in PlainMessages.Examples()) {
            var notice = SessionNotice.Of(NoticeLevel.Warning, wording);
            foreach (var advanced in new[] { false, true, false }) {
                Assert.False(string.IsNullOrWhiteSpace(notice.TextFor(advanced)));
            }
        }

        var channel = new ChannelView("abc", "Tea party", 1, 1, true, false, Rank.Member, [], MembershipWarning: "technical", PlainMembershipWarning: "plain");
        Assert.Equal("technical", channel.WarningFor(advanced: true));
        Assert.Equal("plain", channel.WarningFor(advanced: false));
        // Without plain words, simple mode shows the technical ones rather than nothing.
        Assert.Equal("technical", (channel with { PlainMembershipWarning = null }).WarningFor(advanced: false));
        Assert.Null((channel with { MembershipWarning = null, PlainMembershipWarning = null }).WarningFor(advanced: false));

        var snapshot = SessionSnapshot.Empty with { StatusText = "technical", PlainStatusText = "plain", AddressNotListed = "listed", PlainAddressNotListed = "plain listed" };
        Assert.Equal("plain", snapshot.StatusFor(advanced: false));
        Assert.Equal("technical", snapshot.StatusFor(advanced: true));
        Assert.Equal("technical", (snapshot with { PlainStatusText = null }).StatusFor(advanced: false));
        Assert.Equal("plain listed", snapshot.AddressNotListedFor(advanced: false));
        Assert.Equal("listed", snapshot.AddressNotListedFor(advanced: true));
        Assert.Equal("listed", (snapshot with { PlainAddressNotListed = null }).AddressNotListedFor(advanced: false));
    }

    [Fact]
    public void AFailureKeepsItsTypeAndMessageAndCarriesItsPlainWords() {
        var failure = PlainMessages.Failure(PlainMessages.CantLeaveOldKeyWording);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(PlainMessages.CantLeaveOldKeyChannel, failure.Message);
        Assert.Equal(PlainMessages.CantLeaveOldKeyChannel, PlainMessages.MessageOf(failure, advanced: true));
        Assert.Equal(PlainMessages.CantLeaveOldKeyWording.Plain, PlainMessages.MessageOf(failure, advanced: false));
        Assert.Equal(NoticeKind.CantLeaveOldKeyPlace, PlainMessages.KindOf(failure));

        // Any other exception says what it says, in both modes.
        var other = new InvalidOperationException("That message is too long.");
        Assert.Equal("That message is too long.", PlainMessages.MessageOf(other, advanced: false));
        Assert.Equal(NoticeKind.General, PlainMessages.KindOf(other));
    }

    [Fact]
    public void SomethingThatFailedInTheBackgroundIsToldInBothModes() {
        var sealing = PlainMessages.Failed(PlainMessages.Rekeying, PlainMessages.Failure(PlainMessages.CantSealTo("Bob Hatter@Lich", "bad point")));
        Assert.Equal(NoticeKind.CantSealTo, sealing.Kind);
        Assert.StartsWith("Rekeying a channel failed: Can't rekey: the key couldn't be sealed to Bob Hatter@Lich's identity key", sealing.Technical);
        Assert.Equal("Updating a channel failed: The channel can't be updated: Bob Hatter@Lich's LookingGlass setup is broken. A moderator needs to remove them.",
            sealing.Plain);

        var other = PlainMessages.Failed(PlainMessages.Rekeying, new TimeoutException("The server didn't answer in time."));
        Assert.Equal(NoticeKind.BackgroundFailure, other.Kind);
        Assert.Equal("Updating a channel failed: The server didn't answer in time.", other.Plain);
    }

    [Fact]
    public void WhatTheRulesSayAboutAnOldKeysPlaceIsToldInEitherModesWords() {
        const string rules = "Leaving \"Tea party\" failed: Invalid membership log entry: It isn't signed with the key the log knows its author by.";

        Assert.Equal("Leaving \"Tea party\" failed: " + PlainMessages.OldKeyCantChangeMembers, PlainMessages.Of(rules));
        Assert.Equal("Leaving \"Tea party\" failed: " + PlainMessages.OldKeyCantChangeMembersWording.Plain, PlainMessages.Of(rules, advanced: false));
        Assert.Equal("Something else.", PlainMessages.Of("Something else.", advanced: false));
    }

    [Fact]
    public void AMembershipWarningIsFormattedInBothModes() {
        var wording = PlainMessages.MembershipForked(12).Format("Tea party");

        Assert.Contains("Tea party", wording.Technical);
        Assert.Contains("entry #12", wording.Technical);
        Assert.Contains("Tea party", wording.Plain);
        PlainLanguage.AssertPlain(wording.Plain);
    }

    [Fact]
    public void TheChannelListFlagsTheSameWarningsInSimpleModesWords() {
        var me = new User { UserId = 1, Name = "Alice Liddell", WorldName = "Twintania" };
        var bob = new User { UserId = 2, Name = "Bob Hatter", WorldName = "Lich" };
        var carol = new User { UserId = 3, Name = "Carol Queen", WorldName = "Odin" };
        var warning = PlainMessages.MembershipHidden.Format("Tea party");
        var channel = new ChannelView("aaa", "Tea party", 3, 3, false, false, Rank.Admin, [
                new MemberView(me, Rank.Admin, "1", false, true),
                new MemberView(bob, Rank.Member, "2", KeyChanged: true),
                new MemberView(carol, Rank.Member, "3", KeyChanged: true, KeyReplaced: true, NewFingerprint: "4"),
            ], MembershipWarning: warning.Technical, PlainMembershipWarning: warning.Plain);

        var advanced = ChannelAttention.Of(channel, advanced: true);
        var simple = ChannelAttention.Of(channel, advanced: false);

        Assert.Equal(AttentionLevel.Warning, simple.Level);
        Assert.Equal(advanced.Level, simple.Level);
        Assert.Equal(advanced.Reasons.Length, simple.Reasons.Length);
        Assert.Equal(4, simple.Reasons.Length);
        Assert.Equal(warning.Plain, simple.Reasons[0]);
        Assert.Contains("Bob Hatter@Lich", simple.Reasons[1]);
        Assert.Contains("Carol Queen@Odin", simple.Reasons[2]);
        Assert.Equal(PlainMessages.WaitingForKey.Plain, simple.Reasons[3]);
        foreach (var reason in simple.Reasons) {
            PlainLanguage.AssertPlain(reason);
        }

        // Advanced mode is as before.
        Assert.Equal(ChannelAttention.Of(channel).Reasons, advanced.Reasons);
        Assert.Equal("Key changed: Bob Hatter@Lich. Compare fingerprints.", advanced.Reasons[1]);
    }

    [Fact]
    public void AChannelWhoseNameIsntKnownYetIsNamedPlainlyInSimpleMode() {
        var channel = new ChannelView("abcdef0123456789", null, 0, 0, false, false, Rank.Member, []);

        Assert.Equal("(encrypted channel abcdef01)", channel.DisplayNameFor(advanced: true));
        Assert.Equal(channel.DisplayName, channel.DisplayNameFor(advanced: true));
        Assert.Equal("(channel abcdef01)", channel.DisplayNameFor(advanced: false));
        Assert.Equal("Tea party", (channel with { Name = "Tea party" }).DisplayNameFor(advanced: false));
        Assert.Equal("Bob left (channel abcdef01).", ChannelView.PlainNames("Bob left (encrypted channel abcdef01)."));
    }

    [Fact]
    public void AnAddressTheServerDoesntListIsToldInBothModes() {
        var hint = ClientSession.AddressNotListedText(new Uri("ws://203.0.113.5:5180/ws"), ["wss://chat.example.com/ws"]);

        Assert.NotNull(hint);
        Assert.Equal(NoticeKind.AddressNotListed, hint.Kind);
        Assert.Contains("signing in with your identity key", hint.Technical);
        Assert.Contains("wss://chat.example.com/ws", hint.Plain);
        PlainLanguage.AssertPlain(hint.Plain);
        Assert.Null(ClientSession.AddressNotListedText(new Uri("wss://chat.example.com/ws"), ["wss://chat.example.com/ws"]));
    }
}
