using System.Collections.Immutable;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;
using LookingGlass.Server.Services;
using OperatorView = LookingGlass.Protocol.OperatorView;

namespace LookingGlass.Tests;

/// <summary>
/// The operator panel's words (<see cref="OperatorWords"/>), tested as the Settings words are, and its rules that need no game
/// (<see cref="OperatorPanel"/>): what the menus offer, what "Ban someone" and the dialog accept; and what an operator signs
/// (<see cref="OperatorActionProof"/>).
/// </summary>
public sealed class OperatorWordsTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    // ================================================================ words

    /// <summary>Both "?"s have words in both modes, within the Settings limits, plain in simple mode (they are among <see cref="SettingHelp"/>, so the Settings tests check them too).</summary>
    [Theory]
    [InlineData(SettingHelp.Operator)]
    [InlineData(SettingHelp.OperatorKeys)]
    public void TheQuestionMarksAreShortAndPlain(SettingHelp setting) {
        var help = SettingsWords.Help(setting, "x");
        foreach (var advanced in new[] { false, true }) {
            var text = help.For(advanced);
            Assert.True(text.Length <= SettingsWords.MaxHelpLength, text);
            Assert.True(text.Split(". ").Length <= 3, text);
            Assert.EndsWith(".", text);
        }

        PlainLanguage.AssertPlain(help.Plain);
        Assert.NotEqual(help.Technical, help.Plain);
    }

    /// <summary>The words the owner approved in the plan are kept.</summary>
    [Fact]
    public void ThePlansWordsAreKept() {
        Assert.Equal("You see this because the server lists this character, with its keys, as an operator. Bans you make here are on the character, and are posted to the alerts channel.",
            OperatorWords.Help.Technical);
        Assert.Equal("Operators are tied to their keys, so someone who registers your character again elsewhere doesn't become one. Only the web page can restore it.",
            OperatorWords.KeysHelp.Technical);
        Assert.Equal("You set up LookingGlass again on this character, so this server no longer treats you as an operator. Restore it on the server's operator web page.",
            OperatorWords.NotOperatorNotice(OperatorStanding.KeysChanged, "1", "2").Plain);
        Assert.Equal("Your identity keys changed (now 63390 21187 50462 99805 41733; the server's operator pin is 11873 40229 95310 72646 08154), so this server no longer " +
                     "treats you as an operator. Compare the fingerprint, then restore operator status on the web page.",
            OperatorWords.NotOperatorNotice(OperatorStanding.KeysChanged, "63390 21187 50462 99805 41733", "11873 40229 95310 72646 08154").Technical);
        Assert.Equal("Banned Bob Hatter@Lich for 7 days.", OperatorWords.Banned("Bob Hatter@Lich", 7));
        Assert.Equal("Banned Bob Hatter@Lich for 1 day.", OperatorWords.Banned("Bob Hatter@Lich", 1));
        Assert.Equal("Banned Bob Hatter@Lich until lifted.", OperatorWords.Banned("Bob Hatter@Lich", 0));
        Assert.Equal("Lifted the ban on Carol Queen@Odin.", OperatorWords.Lifted("Carol Queen@Odin"));
        Assert.Equal("Bob Hatter@Lich isn't registered on this server, so there's nothing to ban here. To stop the character registering, ban its Lodestone ID on the web page.",
            OperatorWords.NotRegistered("Bob Hatter@Lich"));
        Assert.Equal("and 4 more (see the web page)", OperatorWords.More(4));
        Assert.Equal("16 / 300", OperatorWords.Counter(16));
        Assert.Equal("Ban from server...", OperatorWords.MenuLabel);
    }

    /// <summary>Every notice and paragraph about not being an operator now says it plainly in simple mode, without the fingerprints.</summary>
    [Fact]
    public void TheNoticeIsPlainInSimpleMode() {
        foreach (var wording in OperatorWords.Examples()) {
            Assert.False(string.IsNullOrWhiteSpace(wording.Technical));
            PlainLanguage.AssertPlain(wording.Plain);
            Assert.DoesNotContain("63390", wording.Plain);
        }

        // In simple mode the fingerprint is shown in the section anyway, under a plain label: the owner compares it with the web page's.
        PlainLanguage.AssertPlain(OperatorWords.YourFingerprint(advanced: false));
        Assert.Equal(NoticeKind.NotOperatorNow, OperatorWords.NotOperatorNotice(OperatorStanding.NotPinned, "1", null).Kind);
    }

    [Fact]
    public void LabelsAreShortAndPlain() {
        var labels = OperatorWords.Labels();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        foreach (var label in labels) {
            PlainLanguage.AssertPlain(label);
            Assert.True(label.Length <= SettingsWords.MaxLabelLength, label);
            Assert.False(label.EndsWith('.'), label);
        }

        foreach (var text in new[] { OperatorWords.AddressesShortened, OperatorWords.WhatABanDoes, OperatorWords.NotYourself, OperatorWords.NotATarget,
                     OperatorWords.DaysProblem, OperatorWords.ReasonProblem, OperatorWords.IsAnOperator("Bob Hatter@Lich"), OperatorWords.NobodyFlagged }) {
            PlainLanguage.AssertPlain(text);
        }
    }

    /// <summary>
    /// A failed ban or lift says why in the plugin's own words, by the reason the server gave, in both modes: never the server's
    /// text in simple mode (advanced mode adds it); a refusal with no reason is said generally.
    /// </summary>
    [Fact]
    public void AFailedActionSaysWhyInTheModesWords() {
        var refused = new ServerErrorException(ErrorCode.LimitReached, "Server words: LookingGlass:Operators:Characters and the like.") {
            OperatorRefusal = OperatorRefusalReason.HourLimit,
        };
        Assert.Equal("Couldn't ban Bob Hatter@Lich: You've made 10 bans in the last hour, the most the plugin allows. Use the web page for more.",
            OperatorWords.NotBanned("Bob Hatter@Lich", refused, advanced: false));
        Assert.Contains("The server says: Server words", OperatorWords.NotBanned("Bob Hatter@Lich", refused, advanced: true));
        Assert.Contains("(LimitReached)", OperatorWords.NotBanned("Bob Hatter@Lich", refused, advanced: true));
        var unexplained = new ServerErrorException(ErrorCode.Internal, "Internal server error.");
        Assert.Equal("Couldn't ban Bob Hatter@Lich: The server couldn't do it. Try again later.", OperatorWords.NotBanned("Bob Hatter@Lich", unexplained, advanced: false));
        foreach (var reason in Enum.GetValues<OperatorRefusalReason>()) {
            var text = OperatorWords.WhyNot(new ServerErrorException(ErrorCode.Forbidden, "Server words.") { OperatorRefusal = reason }, advanced: false);
            Assert.DoesNotContain("Server words", text);
            PlainLanguage.AssertPlain(text);
        }

        // Not an answer from the server (not connected, no answer): as the plugin says it elsewhere.
        Assert.Contains("Not connected", OperatorWords.WhyNot(new SessionDisconnectedException("Not connected to the server."), advanced: false));
        Assert.StartsWith("Couldn't lift the ban on Bob Hatter@Lich: ", OperatorWords.NotLifted("Bob Hatter@Lich", refused, advanced: false));
    }

    /// <summary>The lists' lines: whom, how long, who made it ("you" for this character), a flag's age, an automatic block's time left.</summary>
    [Fact]
    public void TheListsLinesSayWhatTheMockupsSay() {
        var me = "Alice Wonderland@Lich";
        var bob = new User { UserId = 4242, Name = "Carol Queen", WorldName = "Odin" };
        var ban = new PanelBan(4242, bob, null, "Spamming invites", Now.AddHours(-1), Now.AddHours(-1).AddDays(7), false, "plugin", me, null, "", "", false);
        Assert.Equal("Carol Queen@Odin", ban.Who);
        Assert.StartsWith("until ", OperatorWords.BanDetail(ban, Now));
        Assert.EndsWith("(7 days) · \"Spamming invites\"", OperatorWords.BanDetail(ban, Now));
        Assert.Equal("made in the plugin by you", OperatorWords.MadeWords(ban, me));
        Assert.Equal("made in the plugin by Alice Wonderland@Lich", OperatorWords.MadeWords(ban, "Alice Rabbit@Odin"));
        Assert.Equal("made on the web page", OperatorWords.MadeWords(ban with { MadeVia = "web page", MadeBy = "" }, me));
        Assert.Equal("", OperatorWords.MadeWords(ban with { MadeVia = "" }, me));
        Assert.Equal("until lifted", OperatorWords.Until(ban with { Until = null }, Now));
        Assert.Equal("user 4242", (ban with { User = null }).Who);
        Assert.StartsWith("Banned until ", OperatorWords.AlreadyBanned(ban with { MadeVia = "web page" }, me, Now));
        Assert.EndsWith(", made on the web page. Reason: Spamming invites", OperatorWords.AlreadyBanned(ban with { MadeVia = "web page" }, me, Now));
        Assert.EndsWith("No reason given.", OperatorWords.AlreadyBanned(ban with { Reason = "" }, me, Now));

        var block = new PanelBan(0, null, "198.51.100.x", "", Now.AddMinutes(-6), Now.AddMinutes(9), true, "automatic", "", null, "", "", false);
        Assert.Equal("address 198.51.100.x", block.Who);
        Assert.Equal("blocked automatically, 9 minutes left", OperatorWords.BanDetail(block, Now));
        Assert.Equal("", OperatorWords.MadeWords(block, me));
        Assert.False(block.OnCharacter);

        var lifted = ban with { Lifted = Now.AddMinutes(-5), LiftedVia = "plugin", LiftedBy = me };
        Assert.Contains("lifted in the plugin by you", OperatorWords.HistoryDetail(lifted, me, Now));
        Assert.Contains(", ended", OperatorWords.HistoryDetail(ban with { Until = Now.AddMinutes(-1) }, me, Now));
        Assert.Contains("replaced by a new ban", OperatorWords.HistoryDetail(lifted with { Replaced = true }, me, Now));

        var flag = new PanelFlag(31337, new User { Name = "Bob Hatter", WorldName = "Lich" }, null, "refused by limits in 30 of the last 60 minutes",
            "InviteBurstPerPair, LookupBurst", Now.AddHours(-3), Now.AddHours(-2), false);
        Assert.Equal("Flagged 2 hours ago: refused by limits in 30 of the last 60 minutes (InviteBurstPerPair, LookupBurst).", OperatorWords.FlaggedLine(flag, Now));
        Assert.Equal("just now", OperatorWords.Ago(Now.AddSeconds(-20), Now));
        Assert.Equal("20 min ago", OperatorWords.Ago(Now.AddMinutes(-20), Now));
        Assert.Equal("1 hour ago", OperatorWords.Ago(Now.AddMinutes(-61), Now));
        Assert.StartsWith("on ", OperatorWords.Ago(Now.AddDays(-3), Now));

        var lists = Lists(flags: [flag], inForce: [ban, block], flagsTotal: 2, inForceTotal: 3, automatic: 1);
        // As the mockup: the block is among the bans in force, and counted again after them.
        Assert.Equal("2 flagged · 3 bans in force · 1 blocked automatically", OperatorWords.Counts(lists));
        Assert.Equal((1, 1), (lists.MoreFlags, lists.MoreInForce));
        Assert.Equal("1 flagged · 1 ban in force · 0 blocked automatically", OperatorWords.Counts(Lists(flagsTotal: 1, inForceTotal: 1)));
    }

    // ================================================================ rules

    /// <summary>The panel's limits are the server's.</summary>
    [Fact]
    public void ThePanelsLimitsAreTheServers() {
        Assert.Equal(OperatorActions.MaxReasonLength, OperatorPanel.MaxReasonLength);
        Assert.Equal(OperatorActions.MaxDays, OperatorPanel.MaxDays);
        Assert.Equal(OperatorPolicy.PluginBansPerHour, OperatorPanel.BansPerHour);
        Assert.Equal(RequestHandler.OperatorListLines, OperatorPanel.ListLines);
    }

    /// <summary>
    /// "Ban from server..." is in the menus only for an operator, on another player with a home world (the invite item's rules),
    /// and greyed out, saying why, on a character listed as an operator.
    /// </summary>
    [Fact]
    public void TheMenuItemFollowsTheInviteItemsRulesAndGreysOutOperators() {
        var me = new User { UserId = 31337, Name = "Alice Wonderland", WorldName = "Lich" };
        var bob = new InviteTarget("Bob Hatter", "Lich");
        var snapshot = SessionSnapshot.Empty with { State = ConnectionState.Ready, Me = me };

        Assert.Null(OperatorPanel.MenuItem(snapshot, bob));
        Assert.Null(OperatorPanel.MenuItem(snapshot with { Operator = OperatorStanding.KeysChanged }, bob));
        var operating = snapshot with { Operator = OperatorStanding.Operator };
        Assert.Equal((OperatorWords.MenuLabel, true), OperatorPanel.MenuItem(operating, bob));
        Assert.Null(OperatorPanel.MenuItem(operating, new InviteTarget("alice wonderland", "lich")));
        Assert.Null(OperatorPanel.MenuItem(operating, new InviteTarget("Minion", "Lich")));
        Assert.Null(OperatorPanel.MenuItem(operating, new InviteTarget("Bob Hatter", "")));

        var listed = operating with { OperatorLists = Lists(operators: [new User { UserId = 55555, Name = "Bob Hatter", WorldName = "Lich" }]) };
        Assert.Equal(("Ban from server... (an operator)", false), OperatorPanel.MenuItem(listed, new InviteTarget("bob hatter", "LICH")));
        Assert.Equal((OperatorWords.MenuLabel, true), OperatorPanel.MenuItem(listed, new InviteTarget("Carol Queen", "Odin")));
    }

    /// <summary>
    /// What the dialog offers, by how the character stands: nothing for nobody registered, the player's own character, or a
    /// listed operator (registered or not); only Lift for a ban on a character not registered (no dead end); a ban, or Replace and
    /// Lift, otherwise.
    /// </summary>
    [Fact]
    public void TheDialogOffersWhatTheServerAllows() {
        var bob = new User { UserId = 4242, Name = "Bob Hatter", WorldName = "Lich" };
        var ban = new PanelBan(4242, null, null, "", Now.AddHours(-1), null, false, "web page", "", null, "", "", false);
        Assert.Equal(OperatorDialog.NotRegistered, OperatorPanel.DialogFor(null, 31337));
        Assert.Equal(OperatorDialog.NotRegistered, OperatorPanel.DialogFor(new PanelSubject(4242, null, false, null, null), 31337));
        Assert.Equal(OperatorDialog.LiftOnly, OperatorPanel.DialogFor(new PanelSubject(4242, null, false, ban, null), 31337));
        Assert.Equal(OperatorDialog.OperatorCharacter, OperatorPanel.DialogFor(new PanelSubject(55555, null, true, null, null), 31337));
        Assert.Equal(OperatorDialog.OperatorCharacter, OperatorPanel.DialogFor(new PanelSubject(55555, null, true, ban with { UserId = 55555 }, null), 31337));
        Assert.Equal(OperatorDialog.OperatorCharacter, OperatorPanel.DialogFor(new PanelSubject(4242, bob, true, null, null), 31337));
        Assert.Equal(OperatorDialog.Yourself, OperatorPanel.DialogFor(new PanelSubject(31337, bob, true, null, null), 31337));
        Assert.Equal(OperatorDialog.Ban, OperatorPanel.DialogFor(new PanelSubject(4242, bob, false, null, null), 31337));
        Assert.Equal(OperatorDialog.AlreadyBanned, OperatorPanel.DialogFor(new PanelSubject(4242, bob, false, ban, null), 31337));
        Assert.EndsWith("you can lift this ban here.", OperatorWords.LiftOnly(ban, null, Now));
        PlainLanguage.AssertPlain(OperatorWords.LiftOnly(ban, null, Now));
    }

    /// <summary>For a character not pinned yet, nothing says "restore": it was never an operator; it is approved on the web page.</summary>
    [Fact]
    public void NotPinnedYetSaysApproveNotRestore() {
        foreach (var advanced in new[] { false, true }) {
            foreach (var text in new[] {
                         SettingsWords.Help(SettingHelp.OperatorNotPinned, "x").For(advanced), OperatorWords.NotOperatorNotice(OperatorStanding.NotPinned, "1", null).For(advanced),
                         OperatorWords.NotOperatorSection(OperatorStanding.NotPinned).For(advanced), OperatorWords.NotOperatorAdvice(OperatorStanding.NotPinned).For(advanced),
                     }) {
                Assert.DoesNotContain("restore", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("approve", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Contains("restore", OperatorWords.KeysHelp.Plain);
    }

    [Fact]
    public void BanSomeoneAcceptsANameAtWorldOrALodestoneId() {
        Assert.Equal(new OperatorTarget("Bob Hatter", "Lich", 0), OperatorPanel.ParseTarget("  Bob  Hatter@Lich "));
        Assert.Equal(new OperatorTarget(null, null, 31337), OperatorPanel.ParseTarget("31337"));
        Assert.Equal("user 31337", OperatorPanel.ParseTarget("31337")!.Who);
        Assert.Equal("Bob Hatter@Lich", OperatorPanel.ParseTarget("Bob Hatter@Lich")!.Who);
        foreach (var wrong in new[] { "", "0", "-5", "Bob@Lich", "Bob Hatter@", "@Lich", "Bob Hatter", "203.0.113.9", "Bob Hatter@Li ch", "Bob Hatter@Lich1", null }) {
            Assert.Null(OperatorPanel.ParseTarget(wrong));
        }
    }

    [Fact]
    public void TheDialogTakesDaysAndAReasonTheServerWould() {
        Assert.Equal(1, OperatorPanel.DaysOf(OperatorDuration.OneDay, null));
        Assert.Equal(7, OperatorPanel.DaysOf(OperatorDuration.SevenDays, "x"));
        Assert.Equal(30, OperatorPanel.DaysOf(OperatorDuration.ThirtyDays, null));
        Assert.Equal(0, OperatorPanel.DaysOf(OperatorDuration.UntilLifted, null));
        Assert.Equal(14, OperatorPanel.DaysOf(OperatorDuration.Custom, " 14 "));
        Assert.Equal(OperatorPanel.MaxDays, OperatorPanel.DaysOf(OperatorDuration.Custom, "36500"));
        foreach (var wrong in new[] { "", "0", "36501", "-1", "7.5", "seven", null }) {
            Assert.Null(OperatorPanel.DaysOf(OperatorDuration.Custom, wrong));
        }

        Assert.True(OperatorPanel.ReasonFits(""));
        Assert.True(OperatorPanel.ReasonFits("Spamming invites"));
        Assert.True(OperatorPanel.ReasonFits(new string('x', 300)));
        Assert.False(OperatorPanel.ReasonFits(new string('x', 301)));
        Assert.False(OperatorPanel.ReasonFits("two\nlines"));
        Assert.False(OperatorPanel.ReasonFits("hidden‮text"));
    }

    /// <summary>What a server sends is kept to what the panel shows: 10 lines a list, an https page address only, names and reasons cleaned.</summary>
    [Fact]
    public void TheListsAreKeptToWhatThePanelShows() {
        var view = new OperatorView { InForceTotal = 30, PageUrl = "javascript:alert(1)", BansLeftThisHour = 99 };
        for (var i = 0; i < 30; i++) {
            view.InForce.Add(new OperatorBanLine { UserId = i + 1, Reason = "line\u001Bbreak", CreatedUnix = 1_760_000_000 });
        }

        var lists = OperatorLists.From(view, Now);
        Assert.Equal(OperatorPanel.ListLines, lists.InForce.Length);
        Assert.Equal(20, lists.MoreInForce);
        Assert.Null(lists.PageUrl);
        Assert.Equal(OperatorPanel.BansPerHour, lists.BansLeftThisHour);
        Assert.DoesNotContain('\u001B', lists.InForce[0].Reason);
        Assert.Equal("https://box.tailnet.ts.net:8444/", OperatorLists.From(new OperatorView { PageUrl = "https://box.tailnet.ts.net:8444/" }, Now).PageUrl);
    }

    // ================================================================ the signature

    /// <summary>An operator's signature covers every field it names, under its own context: change any one, and it no longer verifies.</summary>
    [Fact]
    public void AnOperatorsSignatureCoversEverything() {
        using var keys = IdentityKeys.Generate();
        var nonce = new byte[OperatorActionProof.NonceSize];
        nonce[0] = 7;
        var tokenHash = RetireIdentityProof.TokenHash("lgt_token");
        var signature = OperatorActionProof.Sign(keys, "ban", 31337, 4242, 7, "Spam", "lgt_token", "wss://chat.example.com/ws", nonce);
        Assert.True(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 7, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));

        var otherNonce = (byte[]) nonce.Clone();
        otherNonce[0] = 8;
        using var other = IdentityKeys.Generate();
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "lift", 31337, 4242, 7, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31338, 4242, 7, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4243, 7, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 8, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 7, "Spam!", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 7, "Spam", RetireIdentityProof.TokenHash("lgt_other"), "wss://chat.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 7, "Spam", tokenHash, "wss://elsewhere.example.com/ws", nonce, signature));
        Assert.False(OperatorActionProof.Verify(keys.SigningPublicKey, "ban", 31337, 4242, 7, "Spam", tokenHash, "wss://chat.example.com/ws", otherNonce, signature));
        Assert.False(OperatorActionProof.Verify(other.SigningPublicKey, "ban", 31337, 4242, 7, "Spam", tokenHash, "wss://chat.example.com/ws", nonce, signature));
        // Its own context: no other signature's payload is the same bytes.
        Assert.NotEqual(Domains.SignOutOthers, Domains.OperatorAction);
        Assert.DoesNotContain(Domains.OperatorAction, typeof(Domains).GetFields().Where(field => field.Name != nameof(Domains.OperatorAction)).Select(field => (string) field.GetValue(null)!));
    }

    private static OperatorLists Lists(PanelFlag[]? flags = null, PanelBan[]? inForce = null, int flagsTotal = 0, int inForceTotal = 0, int automatic = 0,
        User[]? operators = null) =>
        new([.. flags ?? []], [.. inForce ?? []], ImmutableArray<PanelBan>.Empty, flagsTotal, inForceTotal, 0, automatic, null, 10, [.. operators ?? []], Now);
}
