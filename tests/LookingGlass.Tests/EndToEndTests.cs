using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Debug;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Full flows against an in-process server with debug accounts enabled:
/// registration, invites, automatic rekeying, messaging, kicks and the echo bot.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    [Fact]
    public async Task InviteJoinRekeyAndChat() {
        var alice = await this._server.RegisterAsync("Alice Test");
        var bob = await this._server.RegisterAsync("Bob Test");

        var channelId = await alice.Session.CreateChannelAsync("Tea Party", Ct);
        await alice.Session.InviteAsync(channelId, "Bob Test", ProtocolInfo.DebugWorldName, Ct);

        var invite = await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        Assert.Equal("Tea Party", invite.ChannelName);
        Assert.True(invite.Verified);

        await bob.Session.RespondToInviteAsync(channelId, true, Ct);

        // Alice is designated to rekey for the new member; Bob receives the new epoch key.
        var bobChannel = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        Assert.Equal(1UL, bobChannel.Epoch);
        Assert.Equal("Tea Party", bobChannel.Name);

        await alice.Session.SendTextAsync(channelId, "hello bob", Ct);
        var atBob = await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "hello bob"));
        Assert.Equal("Alice Test", atBob.Sender.Name);
        Assert.False(atBob.IsOwn);

        await bob.Session.SendTextAsync(channelId, "hi alice", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "hi alice"));

        // Senders see their own message once, flagged as theirs.
        Assert.Single(alice.Messages, m => m.Text == "hello bob" && m.IsOwn);
    }

    [Fact]
    public async Task KickedMemberIsCutOffAndChannelRekeysOnce() {
        var alice = await this._server.RegisterAsync("Alice Kick");
        var bob = await this._server.RegisterAsync("Bob Kick");
        var carol = await this._server.RegisterAsync("Carol Kick");

        var channelId = await alice.Session.CreateChannelAsync("Book Club", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);

        var epochBeforeKick = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var carolUserId = carol.UserId;
        await alice.Session.KickAsync(channelId, carolUserId, Ct);

        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
        var bobChannel = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: var e, HasKey: true } c && e > epochBeforeKick ? c : null);
        Assert.DoesNotContain(bobChannel.Members, m => m.User.UserId == carolUserId);

        await alice.Session.SendTextAsync(channelId, "after the kick", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "after the kick"));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(carol.Messages, m => m.Text == "after the kick");

        // The kick and the server's rekey request must not cause two rekeys.
        Assert.Equal(epochBeforeKick + 1, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task KickDoesNotRekeyAgainWhenTheBackgroundRekeyFinishedFirst() {
        var alice = await this._server.RegisterAsync("Alice Race");
        var bob = await this._server.RegisterAsync("Bob Race");
        var carol = await this._server.RegisterAsync("Carol Race");
        var channelId = await alice.Session.CreateChannelAsync("Race", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epochBeforeKick = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // Force the race: the rekey the server asks for completes before KickAsync carries on.
        alice.Session.AfterKickRequestForTests = () => WaitFor(() =>
            alice.Session.Snapshot.FindChannel(channelId) is { Epoch: var e, RekeyPending: false } c && e > epochBeforeKick ? c : null);
        await alice.Session.KickAsync(channelId, carol.UserId, Ct);

        Assert.Equal(epochBeforeKick + 1, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.Equal(epochBeforeKick + 1, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task NoticesWithUserContentStayOutOfTheLog() {
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var alice = await this._server.RegisterAsync("Alice Quiet", options: this._server.Options(log: (_, text) => log.Enqueue(text)));
        var bob = await this._server.RegisterAsync("Bob Quiet");
        var channelId = await alice.Session.CreateChannelAsync("Hush Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // The notice is shown to the user...
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("Bob Quiet@Debug joined Hush Channel")));
        await alice.Session.RenameAsync(channelId, "Hush Renamed", Ct);
        await bob.Session.LeaveAsync(channelId, Ct);
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("left Hush Renamed")));

        // ...but names and channel names never reach the diagnostic log.
        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, line => line.Contains("Hush") || line.Contains("Bob Quiet"));
    }

    [Fact]
    public async Task InvitesFromBlockedUsersAreDeclinedUnseen() {
        var alice = await this._server.RegisterAsync("Alice Blocked");
        var bob = await this._server.RegisterAsync("Bob Blocker");
        var first = await alice.Session.CreateChannelAsync("Pending When Blocked", Ct);
        var second = await alice.Session.CreateChannelAsync("Sent After Blocking", Ct);

        // Blocking declines an invite already pending...
        await alice.Session.InviteAsync(first, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == first && i.ChannelName != null));
        bob.Session.BlockUser(alice.UserId);
        Assert.Empty(bob.Session.Snapshot.Invites);
        await WaitFor(() => this._server.Database.GetInvite(first, bob.UserId) == null ? new object() : null);

        // ...and later invites are declined without ever being shown.
        var noticesBefore = bob.Notices.Count;
        await alice.Session.InviteAsync(second, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("declined the invite to Sent After Blocking")));
        Assert.Empty(bob.Session.Snapshot.Invites);
        Assert.Equal(noticesBefore, bob.Notices.Count);

        // The block list is shown with names, and saved.
        Assert.Equal("Alice Blocked", Assert.Single(bob.Session.Snapshot.BlockedUsers).Name);
        Assert.Contains(alice.UserId, bob.Store.Load().BlockedUsers);
    }

    [Fact]
    public async Task MessagesFromBlockedUsersAreHidden() {
        var alice = await this._server.RegisterAsync("Alice Hidden");
        var bob = await this._server.RegisterAsync("Bob Hiding");
        var carol = await this._server.RegisterAsync("Carol Hidden");
        var channelId = await alice.Session.CreateChannelAsync("Hidden", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true } c ? c : null);

        bob.Session.BlockUser(alice.UserId);
        await alice.Session.SendTextAsync(channelId, "you won't see this", Ct);
        // Messages reach Bob in order, so once Carol's arrives, Alice's has been handled.
        await carol.Session.SendTextAsync(channelId, "barrier", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "barrier"));
        Assert.DoesNotContain(bob.Messages, m => m.Text == "you won't see this");

        bob.Session.UnblockUser(alice.UserId);
        Assert.Empty(bob.Session.Snapshot.BlockedUsers);
        await alice.Session.SendTextAsync(channelId, "but you'll see this", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "but you'll see this"));
    }

    [Fact]
    public async Task InviteFromInviterWithChangedKeyNeedsVerification() {
        var alice = await this._server.RegisterAsync("Alice Changed");
        var bob = await this._server.RegisterAsync("Bob Careful");
        var first = await alice.Session.CreateChannelAsync("Before", Ct);
        await alice.Session.InviteAsync(first, bob.Name, ProtocolInfo.DebugWorldName, Ct);
        var before = await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == first && i.ChannelName != null));
        Assert.False(before.InviterKeyChanged);

        // Alice (or someone who took over her account) registers again with new keys, on a server that says nothing about
        // it in a log (one from before key recovery): an unexplained change.
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterOnAnOldServerAsync("Alice Changed");
        var second = await aliceAgain.Session.CreateChannelAsync("After", Ct);
        await aliceAgain.Session.InviteAsync(second, bob.Name, ProtocolInfo.DebugWorldName, Ct);

        // The invite is validly signed by the new key, but flagged until Bob checks the fingerprint.
        var after = await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == second && i.ChannelName != null));
        Assert.True(after.Verified);
        Assert.True(after.InviterKeyChanged);
        Assert.Equal(aliceAgain.Session.Snapshot.MyFingerprint, after.InviterFingerprint);

        bob.Session.AcknowledgeKeyChange(aliceAgain.UserId, after.InviterFingerprint!);
        Assert.False(bob.Session.Snapshot.Invites.Single(i => i.ChannelId == second).InviterKeyChanged);
    }

    [Fact]
    public async Task InvitesDisappearWhenTheirChannelIsDeleted() {
        var alice = await this._server.RegisterAsync("Alice Deletes");
        var bob = await this._server.RegisterAsync("Bob Invited");

        // Disbanded by the admin: the invitee is told.
        var disbanded = await this.InviteAsync(alice, bob, "Disbanded");
        await alice.Session.DisbandAsync(disbanded, Ct);
        await WaitFor(() => bob.Session.Snapshot.Invites.Any(i => i.ChannelId == disbanded) ? null : new object());
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text == "The admin disbanded Disbanded."));

        // Deleted because its last member left.
        var abandoned = await this.InviteAsync(alice, bob, "Abandoned");
        await alice.Session.LeaveAsync(abandoned, Ct);
        await WaitFor(() => bob.Session.Snapshot.Invites.Any(i => i.ChannelId == abandoned) ? null : new object());
        Assert.Null(this._server.Database.GetChannel(abandoned));
    }

    [Fact]
    public async Task AnsweringAnInviteThatNoLongerExistsRemovesIt() {
        var alice = await this._server.RegisterAsync("Alice Vanishes");
        var bob = await this._server.RegisterAsync("Bob Too Late");
        var accepted = await this.InviteAsync(alice, bob, "Vanished");
        var declined = await this.InviteAsync(alice, bob, "Also Vanished");

        // Gone without the invitee being told (as an older server would do).
        this._server.Database.DeleteChannel(accepted);
        this._server.Database.DeleteChannel(declined);

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.RespondToInviteAsync(accepted, true, Ct));
        Assert.Equal(ErrorCode.NotFound, error.Code);
        error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.RespondToInviteAsync(declined, false, Ct));
        Assert.Equal(ErrorCode.NotFound, error.Code);
        Assert.Empty(bob.Session.Snapshot.Invites);
    }

    [Fact]
    public async Task InviteeAloneOnlineMakesTheChannelKeyWhenJoining() {
        var alice = await this._server.RegisterAsync("Alice Away");
        var bob = await this._server.RegisterAsync("Bob Alone");
        var channelId = await this.InviteAsync(alice, bob, "Empty Room");
        await this.TakeOfflineAsync(alice);

        // Nobody else is online, so the server asks Bob to rekey, before he even knows the channel.
        await bob.Session.RespondToInviteAsync(channelId, true, Ct);
        var channel = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        Assert.Equal("Empty Room", channel.Name);
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains("Waiting for a member"));
        await bob.Session.SendTextAsync(channelId, "anyone here?", Ct);

        // Alice picks up the new key when she's back.
        var aliceAgain = await this._server.RestartAsync(alice);
        await WaitFor(() => aliceAgain.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: "Empty Room" } c ? c : null);
        await aliceAgain.Session.SendTextAsync(channelId, "welcome", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "welcome"));
    }

    [Fact]
    public async Task MemberComingBackOnlineRekeysAChannelLeftWaiting() {
        var alice = await this._server.RegisterAsync("Alice Returns");
        // Bob doesn't rekey when asked, so the channel is still waiting when Alice comes back.
        var bob = await this._server.RegisterAsync("Bob Waits", options: this._server.Options(autoRekey: false));
        var channelId = await this.InviteAsync(alice, bob, "Waiting Room");
        await this.TakeOfflineAsync(alice);
        await bob.Session.RespondToInviteAsync(channelId, true, Ct);
        Assert.True(bob.Session.Snapshot.FindChannel(channelId)!.RekeyPending);

        // Alice is asked to rekey as she reconnects, without anyone sending anything.
        var aliceAgain = await this._server.RestartAsync(alice);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: "Waiting Room" } c ? c : null);
        await WaitFor(() => aliceAgain.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        Assert.Equal(1UL, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    /// <summary>Stops a client and waits until the server has noticed.</summary>
    private async Task TakeOfflineAsync(TestClient client) {
        var userId = client.UserId;
        await client.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(userId) ? null : new object());
    }

    /// <summary>Creates a channel and invites <paramref name="invitee"/>, waiting until they can read the invite.</summary>
    private async Task<string> InviteAsync(TestClient admin, TestClient invitee, string channelName) {
        var channelId = await admin.Session.CreateChannelAsync(channelName, Ct);
        await admin.Session.InviteAsync(channelId, invitee.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => invitee.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        return channelId;
    }

    /// <summary>
    /// Adapted: the client now refuses from its own verified log before asking the server, so the
    /// server's refusal is checked with a signed removal sent anyway.
    /// </summary>
    [Fact]
    public async Task MemberCannotKickModerator() {
        var alice = await this._server.RegisterAsync("Alice Rank");
        var bob = await this._server.RegisterAsync("Bob Rank");
        var channelId = await alice.Session.CreateChannelAsync("Ranks", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var refused = await Assert.ThrowsAsync<MembershipException>(() => bob.Session.KickAsync(channelId, alice.UserId, Ct));
        Assert.Equal(MembershipVerdictKind.Forbidden, refused.Verdict.Kind);

        var removal = this._server.ForgeEntry(channelId, bob, MembershipEntryKind.Remove, alice.UserId, alice.Keys());
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.SendRawAsync(new ClientFrame {
            KickMember = new KickMember { ChannelId = channelId, Entry = removal },
        }, Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal(Rank.Admin, this._server.Database.GetRank(channelId, alice.UserId));
    }

    [Fact]
    public async Task EchoBotAnswers() {
        var alice = await this._server.RegisterAsync("Alice Echo");

        var bot = new EchoBot(this._server.Options(), new InMemorySecretStore(), "Echo Test Bot");
        this._server.Track(bot);
        bot.Start();
        await bot.WaitUntilReadyAsync(Harness.Timeout);

        var channelId = await alice.Session.CreateChannelAsync("Echo Chamber", Ct);
        await alice.Session.InviteAsync(channelId, "Echo Test Bot", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.Name == "Echo Test Bot" && m.Rank == Rank.Member));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);

        await alice.Session.SendTextAsync(channelId, "is anyone there?", Ct);
        var reply = await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "echo: is anyone there?"));
        Assert.Equal("Echo Test Bot", reply.Sender.Name);
    }

    /// <summary>
    /// A server hosting the echo bot logs what it does, but never what its channels say: not their messages, not their
    /// names, and not its notices (which can quote either).
    /// </summary>
    [Fact]
    public async Task AHostedEchoBotLogsNothingOfItsChannels() {
        var alice = await this._server.RegisterAsync("Alice Quiet Echo");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bot = new EchoBot(this._server.Options(), new InMemorySecretStore(), "Quiet Echo Bot", lines.Enqueue) { LogContent = false };
        this._server.Track(bot);
        bot.Start();
        await bot.WaitUntilReadyAsync(Harness.Timeout);

        var channelId = await alice.Session.CreateChannelAsync("Secret Bot Haunt", Ct);
        await alice.Session.InviteAsync(channelId, "Quiet Echo Bot", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c
                            && c.Members.Any(m => m.User.Name == "Quiet Echo Bot" && m.Rank == Rank.Member) ? c : null);
        await alice.Session.RenameAsync(channelId, "Renamed Secret Haunt", Ct);
        await alice.Session.SendTextAsync(channelId, "whispered words", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "echo: whispered words"));

        Assert.Contains(lines, line => line.StartsWith("Joined a channel"));
        Assert.DoesNotContain(lines, line => line.Contains("Secret") || line.Contains("whispered"));
    }

    [Fact]
    public async Task RestartedClientKeepsIdentityAndKeys() {
        var store = new InMemorySecretStore();
        var first = await this._server.RegisterAsync("Dana Restart", store);
        var channelId = await first.Session.CreateChannelAsync("Persistent", Ct);
        var fingerprint = first.Session.Snapshot.MyFingerprint;
        await first.Session.DisposeAsync();

        var second = this._server.StartClient("Dana Restart", store);
        var channel = await WaitFor(() => second.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: not null } c ? c : null);
        Assert.Equal("Persistent", channel.Name);
        Assert.Equal(fingerprint, second.Session.Snapshot.MyFingerprint);
    }

    /// <summary>
    /// Replaces "ReRegisteredMemberGetsKeySealedToNewIdentity". In 0.1 a member who registered again was
    /// sent the channel key under their new identity key straight away: the server's word that the new
    /// key was theirs was enough. Now a membership stays bound to the key it was admitted with unless the
    /// log moves it (a key recovered entry, see <see cref="KeyRecoveryTests"/>); on a server from before
    /// that, which these re-registrations play, the new key only gets in through a fresh invite and accept.
    /// </summary>
    [Fact]
    public async Task ReRegisteredMembersNewKeyIsNotAMemberUntilInvitedAgain() {
        var alice = await this._server.RegisterAsync("Alice Rereg");
        var carol = await this._server.RegisterAsync("Carol Rereg");
        var channelId = await alice.Session.CreateChannelAsync("Phoenix", Ct);
        await AddMemberAsync(alice, channelId, carol);
        await carol.Session.DisposeAsync();

        // Carol loses her config and registers again from a fresh install: new identity keys, which this (old) server doesn't move her place to.
        var carolAgain = await this._server.RegisterOnAnOldServerAsync("Carol Rereg");
        Assert.Equal(carol.Name, carolAgain.Name);
        var listed = await WaitFor(() => carolAgain.Session.Snapshot.FindChannel(channelId));
        Assert.Equal(Rank.Unspecified, listed.MyRank);
        Assert.NotNull(listed.MembershipWarning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => carolAgain.Session.SendTextAsync(channelId, "let me in", Ct));

        // Alice is warned, persistently, that Carol's key changed, and sees that the new one isn't the member.
        await alice.Session.RefreshAsync(Ct);
        var member = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.True(member.KeyChanged);
        Assert.True(member.KeyReplaced);
        Assert.NotEqual(carolAgain.Session.Snapshot.MyFingerprint, member.Fingerprint);

        // A rekey still goes to the member, Carol's old key: nothing is sealed to the new one.
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await carolAgain.Session.RefreshAsync(Ct);
        Assert.False(carolAgain.Store.Load().EpochKeys.ContainsKey(channelId));
        Assert.False(carolAgain.Session.Snapshot.FindChannel(channelId)!.HasKey);

        // Removed and invited again, the new key is a member.
        await alice.Session.KickAsync(channelId, carolAgain.UserId, Ct);
        await WaitFor(() => carolAgain.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
        await AddMemberAsync(alice, channelId, carolAgain);
        Assert.Equal("Phoenix", carolAgain.Session.Snapshot.FindChannel(channelId)!.Name);

        var rejoined = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.Equal(carolAgain.Session.Snapshot.MyFingerprint, rejoined.Fingerprint);
        Assert.False(rejoined.KeyReplaced);
        Assert.True(rejoined.KeyChanged);
        alice.Session.AcknowledgeKeyChange(carolAgain.UserId, rejoined.Fingerprint!);
        rejoined = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.False(rejoined.KeyChanged);
        Assert.True(rejoined.FingerprintCompared);

        await carolAgain.Session.SendTextAsync(channelId, "I'm back", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "I'm back"));
    }

    /// <summary>
    /// "Mark verified" vouches for the fingerprint the user was shown and compared, nothing else. A member who
    /// registered again is shown with the key the log binds them to; marking that row verified must not mark
    /// their new key, which wasn't shown, as compared, so it isn't after they are removed and invited again.
    /// </summary>
    [Fact]
    public async Task MarkingAMemberVerifiedOnlyVouchesForTheFingerprintShown() {
        var alice = await this._server.RegisterAsync("Alice Vouches");
        var carol = await this._server.RegisterAsync("Carol Vouched For");
        var channelId = await alice.Session.CreateChannelAsync("Vouching", Ct);
        await AddMemberAsync(alice, channelId, carol);
        await carol.Session.DisposeAsync();
        var carolAgain = await this._server.RegisterOnAnOldServerAsync(carol.Name);
        var newFingerprint = carolAgain.Session.Snapshot.MyFingerprint;

        await alice.Session.RefreshAsync(Ct);
        var row = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.True(row is { KeyChanged: true, KeyReplaced: true });
        Assert.NotEqual(newFingerprint, row.Fingerprint);

        // Alice compares the fingerprint shown in the row's fingerprint column (Carol's old key) and marks it verified.
        // That isn't the key held for Carol now, so nothing is marked. (Before the fix, the call took no fingerprint, as
        // the UI's button did, and marked the server's new key.)
        Assert.Throws<InvalidOperationException>(() => alice.Session.AcknowledgeKeyChange(carolAgain.UserId, row.Fingerprint!));

        // Removed and invited again, Carol's new key, whose fingerprint Alice was never shown, isn't compared.
        await alice.Session.KickAsync(channelId, carolAgain.UserId, Ct);
        await AddMemberAsync(alice, channelId, carolAgain);
        var rejoined = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.Equal(newFingerprint, rejoined.Fingerprint);
        Assert.False(rejoined.FingerprintCompared);
        Assert.True(rejoined.KeyChanged);

        // Until its own fingerprint, now shown, is compared and marked.
        alice.Session.AcknowledgeKeyChange(carolAgain.UserId, rejoined.Fingerprint!);
        rejoined = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.True(rejoined.FingerprintCompared);
        Assert.False(rejoined.KeyChanged);
    }

    /// <summary>A "registered again" row shows the new key's fingerprint, which is what its "Mark new key verified" vouches for.</summary>
    [Fact]
    public async Task ARegisteredAgainRowShowsTheNewFingerprintItMarksVerified() {
        var alice = await this._server.RegisterAsync("Alice Shown");
        var carol = await this._server.RegisterAsync("Carol Shown");
        var channelId = await alice.Session.CreateChannelAsync("Shown", Ct);
        await AddMemberAsync(alice, channelId, carol);
        await carol.Session.DisposeAsync();
        var carolAgain = await this._server.RegisterOnAnOldServerAsync(carol.Name);

        await alice.Session.RefreshAsync(Ct);
        var row = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.True(row is { KeyChanged: true, KeyReplaced: true });
        Assert.Equal(carolAgain.Session.Snapshot.MyFingerprint, row.NewFingerprint);

        alice.Session.AcknowledgeKeyChange(carolAgain.UserId, row.NewFingerprint!);
        row = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.False(row.KeyChanged);
        // The key that is the member (her old one) wasn't compared, and isn't marked.
        Assert.False(row.FingerprintCompared);

        // Invited again, the new key, compared before, shows as compared.
        await alice.Session.KickAsync(channelId, carolAgain.UserId, Ct);
        await AddMemberAsync(alice, channelId, carolAgain);
        var rejoined = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == carolAgain.UserId);
        Assert.True(rejoined is { FingerprintCompared: true, KeyChanged: false, KeyReplaced: false, NewFingerprint: null });
    }

    /// <summary>
    /// Adapted: in 0.1 Bob's new key counted as soon as a member rekeyed to it. On a server from before key
    /// recovery it counts once he is removed and invited again. Carol, who has his old key, must take the new
    /// one from the log without restarting, and still be warned that his key changed.
    /// </summary>
    [Fact]
    public async Task OtherMembersPickUpAReRegisteredMembersNewKeyWithoutRestarting() {
        var (alice, bob, carol, channelId) = await this.ThreeMembersAsync("Stale");
        await bob.Session.DisposeAsync();

        var bobAgain = await this._server.RegisterOnAnOldServerAsync(bob.Name);
        await alice.Session.KickAsync(channelId, bobAgain.UserId, Ct);
        await AddMemberAsync(alice, channelId, bobAgain);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == bobAgain.UserId && m.Rank == Rank.Member));
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);

        await bobAgain.Session.SendTextAsync(channelId, "new keys, same Bob", Ct);
        await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == "new keys, same Bob"));

        // The new key is still treated as a change to check, not silently trusted.
        Assert.Contains(carol.Notices, n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{bob.Name}@Debug's identity key changed"));
        Assert.Contains(carol.Notices, n => n.Kind == NoticeKind.KeyChanged && n.TextFor(advanced: false).StartsWith($"{bob.Name}@Debug set up LookingGlass again (new computer or reset), or someone else may be using their name"));
        var member = carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bobAgain.UserId);
        Assert.True(member.KeyChanged);
        Assert.Equal(bobAgain.Session.Snapshot.MyFingerprint, member.Fingerprint);
        Assert.DoesNotContain(carol.Notices, n => n.Text.Contains("failed signature"));
    }

    /// <summary>
    /// Adapted: Bob registering again on a server from before key recovery makes nobody rekey (his
    /// membership stays bound to his old key), so there is nothing to wait for but the registration.
    /// Reconnecting must still fetch identities again and notice his new key.
    /// </summary>
    [Fact]
    public async Task ReconnectingFetchesIdentitiesAgain() {
        var (_, bob, carol, channelId) = await this.ThreeMembersAsync("Reconnect");
        await bob.Session.DisposeAsync();
        var bobAgain = await this._server.RegisterOnAnOldServerAsync(bob.Name);
        Assert.False(carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bobAgain.UserId).KeyChanged);

        // Nothing from Bob has arrived, but reconnecting is enough to notice his new key.
        carol.Session.Reconnect();
        var member = await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == bobAgain.UserId && m.KeyChanged));
        Assert.True(member.KeyReplaced);
        var newFingerprint = bobAgain.Session.Snapshot.MyFingerprint!;
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning
                                                              && n.Text.StartsWith($"{bob.Name}@Debug's identity key changed") && n.Text.EndsWith(newFingerprint)));
    }

    /// <summary>
    /// Replaces "NameSignedWithAnAuthorsNewKeyIsShownAfterFetchingItAgain" and the two
    /// "MessageIsDelivered...ItsAuthorsNewIdentity" tests (R5-3). Those checked that a name or message
    /// signed with a member's new keys, after they registered again, was shown once the client had
    /// fetched their identity again, however those fetches raced. Now a new key the log hasn't moved their
    /// place to (on a server from before key recovery) signs nothing that counts until its owner is invited
    /// again, so both are refused, and fetching the identity again
    /// only explains why. What R5-3 protected still matters for that: a name's fetch must not leave
    /// the message with a bare "failed checks", nor cost a second lookup. (Names and messages are now
    /// handled in one queue, in order, so the old tests' way of holding one while the other ran no
    /// longer applies.)
    /// </summary>
    [Fact]
    public async Task NamesAndMessagesSignedWithAReRegisteredMembersNewKeyAreRefusedWithAReason() {
        var alice = await this._server.RegisterAsync("Alice Renamer");
        // Carol doesn't rekey when asked, which would also fetch Alice's new identity.
        var carol = await this._server.RegisterAsync("Carol Renamed", options: this._server.Options(autoRekey: false));
        var channelId = await alice.Session.CreateChannelAsync("Old Name", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var position = PositionOf(carol, channelId);
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterOnAnOldServerAsync(alice.Name);
        int Lookups() => carol.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" GetIdentities"));
        var before = Lookups();

        // A name and a message signed with Alice's new keys reach Carol, who knows only her old ones.
        using var newKeys = aliceAgain.LoadIdentity();
        var key = carol.LoadEpochKey(channelId, epoch);
        var name = ChannelCrypto.EncryptName("New Name", key, channelId, epoch, position, newKeys, aliceAgain.UserId, revision: 1);
        var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = "signed with my new keys" } }, key,
            channelId, epoch, newKeys, aliceAgain.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await this._server.SendAndSettleAsync(carol,
            new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = name } },
            new Event {
                ChatMessage = new ChatMessage {
                    ChannelId = channelId, Epoch = epoch, SenderId = aliceAgain.UserId, MessageId = sent.MessageId,
                    TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
                },
            });

        var dropped = await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Text.StartsWith($"Dropped a message from {alice.Name}")));
        Assert.Contains("registered again", dropped.Text);
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{alice.Name}@Debug's identity key changed")));
        Assert.Equal("Old Name", carol.Session.Snapshot.FindChannel(channelId)!.Name);
        Assert.DoesNotContain(carol.Messages, m => m.Text == "signed with my new keys");
        Assert.Equal(before + 1, Lookups());
    }

    /// <summary>
    /// Two checks that fail for the same user at once share one fetch of their identity, and both use what it
    /// brings. A name's follow-up starts the fetch, which is held on its way to the server; a message that fails
    /// meanwhile must wait for it, not be refused a second fetch for a minute and dropped with a bare "failed
    /// checks". (Restores the coverage of the two "MessageIsDelivered...ItsAuthorsNewIdentity" tests.)
    /// </summary>
    [Fact]
    public async Task ChecksFailingAtOnceForOneUserShareOneIdentityFetch() {
        HoldingWebSocket? socket = null;
        var alice = await this._server.RegisterAsync("Alice Shared Fetch");
        // Carol doesn't rekey when asked, which would also fetch Alice's new identity.
        var carol = await this._server.RegisterAsync("Carol Shared Fetch", options: this._server.Options(autoRekey: false, wrap: inner => socket = new HoldingWebSocket(inner)));
        var channelId = await alice.Session.CreateChannelAsync("Shared Fetch", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        var position = PositionOf(carol, channelId);
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterOnAnOldServerAsync(alice.Name);
        int Lookups() => carol.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" GetIdentities"));
        var before = Lookups();

        using var newKeys = aliceAgain.LoadIdentity();
        var key = carol.LoadEpochKey(channelId, epoch);
        var name = ChannelCrypto.EncryptName("New Name", key, channelId, epoch, position, newKeys, aliceAgain.UserId, revision: 1);
        var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = "while you were fetching" } }, key,
            channelId, epoch, newKeys, aliceAgain.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // A name signed with Alice's new keys fails its check, and its follow-up fetches her identity: held.
        socket!.HoldNext(ClientFrame.BodyOneofCase.GetIdentities);
        this._server.Registry.Send(carol.UserId, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = name } });
        await socket.Held.WaitAsync(Harness.Timeout, Ct);

        // A message signed with the same keys fails its check while that fetch is under way. It waits for it,
        // and so does everything after it in Carol's inbox.
        var sentinel = "sentinel " + Guid.NewGuid().ToString("N");
        this._server.Registry.Send(carol.UserId, new Event {
            ChatMessage = new ChatMessage {
                ChannelId = channelId, Epoch = epoch, SenderId = aliceAgain.UserId, MessageId = sent.MessageId,
                TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
            },
        });
        this._server.Registry.Send(carol.UserId, new Event { Announcement = new Announcement { Text = sentinel } });
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(carol.Notices, n => n.Text == sentinel || n.Text.StartsWith("Dropped a message"));

        // Once the one fetch answers, both use it: the message is refused for the right reason, and the key change shown.
        socket.Release();
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Text == sentinel));
        var dropped = carol.Notices.Single(n => n.Text.StartsWith("Dropped a message"));
        Assert.StartsWith($"Dropped a message from {alice.Name}: it's signed with the identity key they registered again with", dropped.Text);
        Assert.Equal(NoticeKind.MessageFromNewSetup, dropped.Kind);
        Assert.StartsWith($"Dropped a message from {alice.Name}: they set up LookingGlass again", dropped.TextFor(advanced: false));
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{alice.Name}@Debug's identity key changed")));
        Assert.Equal(before + 1, Lookups());
        Assert.Equal("Shared Fetch", carol.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    [Fact]
    public async Task RenameMissedWhileOfflineIsNotBlamedOnTheNextRekey() {
        var online = Task.CompletedTask;
        var alice = await this._server.RegisterAsync("Alice Offline");
        var bob = await this._server.RegisterAsync("Bob Offline", options: this._server.Options(beforeConnect: ct => online.WaitAsync(ct)));
        var carol = await this._server.RegisterAsync("Carol Offline");
        var channelId = await alice.Session.CreateChannelAsync("First Name", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // Bob drops off; meanwhile Alice renames and Carol rekeys, carrying the new name over.
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        online = reconnect.Task;
        bob.Session.Reconnect();
        await WaitFor(() => this._server.Registry.IsOnline(bob.UserId) ? null : new object());
        await alice.Session.RenameAsync(channelId, "Second Name", Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Name == "Second Name" ? new object() : null);
        await carol.Session.RekeyAsync(channelId, Ct, force: true);

        // Bob, back online, still holds "First Name" (epoch N, revision 0) but missed revision 1.
        Assert.Equal("First Name", bob.Session.Snapshot.FindChannel(channelId)!.Name);
        reconnect.SetResult();
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Name: "Second Name", HasKey: true } c && c.Epoch == epoch + 1 ? c : null);
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains("while rekeying"));
    }

    /// <summary>
    /// The server asks for a rekey at epoch N+1 (someone left just after Alice's rekey to N+1 was applied) before it
    /// answers that rekey, as it can when the leave lands between the two. Her rekey, for N+1, doesn't settle a request
    /// made at N+1: the channel still needs a new key.
    /// </summary>
    [Fact]
    public async Task OwnRekeyDoesNotSettleALaterRequestForANewKey() {
        var bob = await this._server.RegisterAsync("Bob Later Request");
        long aliceId = 0;
        var armed = 0;
        var pushed = 0;
        var alice = await this._server.RegisterAsync("Alice Later Request", options: this._server.Options(
            wrap: socket => new RewritingWebSocket(socket, frame => frame, sent: frame => {
                // On its way to the server: queued for her before the server's answer. Nobody is asked to make it.
                if (frame.SubmitRekey is { } rekey && Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref pushed, 1) == 0) {
                    this._server.Registry.Send(aliceId, new Event { RekeyNeeded = new RekeyNeeded { ChannelId = rekey.ChannelId, CurrentEpoch = rekey.NewEpoch } });
                }
            })));
        aliceId = alice.UserId;
        var channelId = await alice.Session.CreateChannelAsync("Later Request", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        Volatile.Write(ref armed, 1);
        await alice.Session.RekeyAsync(channelId, Ct, force: true);

        Assert.Equal(1, pushed);
        var channel = alice.Session.Snapshot.FindChannel(channelId)!;
        Assert.Equal(epoch + 1, channel.Epoch);
        Assert.True(channel.RekeyPending);
    }

    /// <summary>
    /// Likewise for a key someone else made: the server asked for a rekey at epoch N+1, so a key for N+1 (Bob's, sent
    /// before the request but arriving after it) doesn't settle it. Only a key for a later epoch does.
    /// </summary>
    [Fact]
    public async Task AKeyFromBeforeARequestForANewKeyDoesNotSettleIt() {
        var alice = await this._server.RegisterAsync("Alice Earlier Key");
        var bob = await this._server.RegisterAsync("Bob Earlier Key");
        var channelId = await alice.Session.CreateChannelAsync("Earlier Key", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        using var bobKeys = bob.LoadIdentity();
        var position = PositionOf(alice, channelId);
        var next = ChannelCrypto.NewEpochKey();
        var sealedKey = ChannelCrypto.SealEpochKey(next, channelId, epoch + 1, position, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey);
        await this._server.SendAndSettleAsync(alice,
            new Event { RekeyNeeded = new RekeyNeeded { ChannelId = channelId, CurrentEpoch = epoch + 1 } },
            new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = epoch + 1, AuthorId = bob.UserId, MyKey = sealedKey } });

        var channel = alice.Session.Snapshot.FindChannel(channelId)!;
        Assert.Equal(epoch + 1, channel.Epoch);
        Assert.True(channel.RekeyPending);

        // A key for the epoch after the one the request was made at settles it.
        var later = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, epoch + 2, position, bobKeys, bob.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey);
        await this._server.SendAndSettleAsync(alice, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = epoch + 2, AuthorId = bob.UserId, MyKey = later } });
        Assert.False(alice.Session.Snapshot.FindChannel(channelId)!.RekeyPending);
    }

    /// <summary>Alice (admin), Bob and Carol in one channel, all holding its current key.</summary>
    private async Task<(TestClient Alice, TestClient Bob, TestClient Carol, string ChannelId)> ThreeMembersAsync(string suffix) {
        var alice = await this._server.RegisterAsync("Alice " + suffix);
        var bob = await this._server.RegisterAsync("Bob " + suffix);
        var carol = await this._server.RegisterAsync("Carol " + suffix);
        var channelId = await alice.Session.CreateChannelAsync("Trio " + suffix, Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        return (alice, bob, carol, channelId);
    }
}
