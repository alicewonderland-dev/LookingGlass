using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Debug;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

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

        // Alice (or someone who took over her account) registers again with new keys.
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterAsync("Alice Changed");
        var second = await aliceAgain.Session.CreateChannelAsync("After", Ct);
        await aliceAgain.Session.InviteAsync(second, bob.Name, ProtocolInfo.DebugWorldName, Ct);

        // The invite is validly signed by the new key, but flagged until Bob checks the fingerprint.
        var after = await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == second && i.ChannelName != null));
        Assert.True(after.Verified);
        Assert.True(after.InviterKeyChanged);
        Assert.Equal(aliceAgain.Session.Snapshot.MyFingerprint, after.InviterFingerprint);

        bob.Session.AcknowledgeKeyChange(aliceAgain.UserId);
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

    [Fact]
    public async Task MemberCannotKickModerator() {
        var alice = await this._server.RegisterAsync("Alice Rank");
        var bob = await this._server.RegisterAsync("Bob Rank");
        var channelId = await alice.Session.CreateChannelAsync("Ranks", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => bob.Session.KickAsync(channelId, alice.UserId, Ct));
        Assert.Equal(ErrorCode.Forbidden, error.Code);
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

    [Fact]
    public async Task ReRegisteredMemberGetsKeySealedToNewIdentity() {
        var alice = await this._server.RegisterAsync("Alice Rereg");
        var carol = await this._server.RegisterAsync("Carol Rereg");
        var channelId = await alice.Session.CreateChannelAsync("Phoenix", Ct);
        await AddMemberAsync(alice, channelId, carol);
        await carol.Session.DisposeAsync();

        // Carol loses her config and registers again from a fresh install: new identity keys.
        var carolAgain = await this._server.RegisterAsync("Carol Rereg");
        Assert.Equal(carol.Name, carolAgain.Name);

        // Alice must rekey to Carol's NEW key (0.1 sealed it to the stale cached one).
        var channel = await WaitFor(() => carolAgain.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: not null } c ? c : null);
        Assert.Equal("Phoenix", channel.Name);

        // And Alice is warned, persistently, that Carol's key changed.
        var member = await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.Name == carol.Name && m.KeyChanged));
        Assert.True(member.KeyChanged);
        alice.Session.AcknowledgeKeyChange(member.User.UserId);
        Assert.False(alice.Session.Snapshot.FindChannel(channelId)!.Members.First(m => m.User.Name == carol.Name).KeyChanged);

        await carolAgain.Session.SendTextAsync(channelId, "I'm back", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "I'm back"));
    }

    [Fact]
    public async Task OtherMembersPickUpAReRegisteredMembersNewKeyWithoutRestarting() {
        var (alice, bob, carol, channelId) = await this.ThreeMembersAsync("Stale");
        await bob.Session.DisposeAsync();

        // Bob registers again with new keys; Alice rekeys to them. Carol still has his old key cached.
        var bobAgain = await this._server.RegisterAsync(bob.Name);
        await WaitFor(() => bobAgain.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        Assert.False(carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bobAgain.UserId).KeyChanged);

        await bobAgain.Session.SendTextAsync(channelId, "new keys, same Bob", Ct);
        await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == "new keys, same Bob"));

        // The new key is still treated as a change to check, not silently trusted.
        Assert.Contains(carol.Notices, n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{bob.Name}@Debug's identity key changed"));
        var member = carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bobAgain.UserId);
        Assert.True(member.KeyChanged);
        Assert.Equal(bobAgain.Session.Snapshot.MyFingerprint, member.Fingerprint);
        Assert.DoesNotContain(carol.Notices, n => n.Text.Contains("failed signature"));
    }

    [Fact]
    public async Task ReconnectingFetchesIdentitiesAgain() {
        var (_, bob, carol, channelId) = await this.ThreeMembersAsync("Reconnect");
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await bob.Session.DisposeAsync();
        var bobAgain = await this._server.RegisterAsync(bob.Name);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) is { Epoch: var e, HasKey: true } c && e > epoch ? c : null);
        Assert.False(carol.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bobAgain.UserId).KeyChanged);

        // Nothing from Bob has arrived, but reconnecting is enough to notice his new key.
        carol.Session.Reconnect();
        var member = await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.UserId == bobAgain.UserId && m.KeyChanged));
        Assert.Equal(bobAgain.Session.Snapshot.MyFingerprint, member.Fingerprint);
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{bob.Name}@Debug's identity key changed")));
    }

    [Fact]
    public async Task NameSignedWithAnAuthorsNewKeyIsShownAfterFetchingItAgain() {
        var alice = await this._server.RegisterAsync("Alice Renamer");
        // Carol doesn't rekey when asked, which would also fetch Alice's new identity.
        var carol = await this._server.RegisterAsync("Carol Renamed", options: this._server.Options(autoRekey: false));
        var channelId = await alice.Session.CreateChannelAsync("Old Name", Ct);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterAsync(alice.Name);

        // A name signed with Alice's new keys reaches Carol, who has her old ones cached.
        using var newKeys = aliceAgain.LoadIdentity();
        var name = ChannelCrypto.EncryptName("New Name", carol.LoadEpochKey(channelId, epoch), channelId, epoch, newKeys, aliceAgain.UserId, revision: 1);
        this._server.Registry.Send(carol.UserId, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = name } });

        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Name == "New Name" ? new object() : null);
        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Warning && n.Text.StartsWith($"{alice.Name}@Debug's identity key changed")));
    }

    [Fact]
    public async Task MessageIsDeliveredWhenANameAlreadyFetchedItsAuthorsNewIdentity() {
        using var clock = new StallingClock();
        var (carol, aliceAgain, channelId, epoch) = await this.MessageHeldWithAStaleIdentityAsync("Name First", clock);

        // Meanwhile a name signed with Alice's new keys arrives, and following it up fetches her identity again.
        await this._server.SendAndSettleAsync(carol, RenameSignedBy(aliceAgain, carol, channelId, epoch, "Renamed First"));
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)?.Name == "Renamed First" ? new object() : null);

        // The message's check then fails against the identity it looked up before. It must use the
        // one just fetched, not be refused another fetch for a minute and dropped.
        clock.Resume();
        await AssertDeliveredAsync(carol, HeldMessageText);
    }

    [Fact]
    public async Task MessageIsDeliveredWhileANameIsStillFetchingItsAuthorsNewIdentity() {
        using var clock = new StallingClock();
        var (carol, aliceAgain, channelId, epoch) = await this.MessageHeldWithAStaleIdentityAsync("Name Fetching", clock);
        var lastRequest = LastRequestId(carol);

        // A name signed with Alice's new keys arrives and starts a fetch of her identity, whose answer
        // can't arrive yet: Carol stops reading from the server just after the name.
        var hold = "hold " + Guid.NewGuid().ToString("N");
        var readerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new ManualResetEventSlim();
        carol.Session.Notice += notice => {
            if (notice.Text == hold && readerHeld.TrySetResult()) {
                releaseReader.Wait(Harness.Timeout);
            }
        };

        try {
            this._server.Registry.Send(carol.UserId, RenameSignedBy(aliceAgain, carol, channelId, epoch, "Renamed Later"));
            this._server.Registry.Send(carol.UserId, new Event { Announcement = new Announcement { Text = hold } });
            await readerHeld.Task.WaitAsync(Harness.Timeout, Ct);
            await WaitFor(() => carol.Session.GetTrace().Any(entry => entry.Outgoing && entry.Summary.EndsWith(" GetIdentities") && RequestIdOf(entry) > lastRequest) ? new object() : null);

            // The message's check fails while that fetch is under way: it must wait for it. Unfixed,
            // the message is dropped at once; give that a moment to show before letting the fetch finish.
            clock.Resume();
            try {
                await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Text.Contains("failed signature or decryption checks")), TimeSpan.FromMilliseconds(500));
            } catch (TimeoutException) {
                // Still waiting, as it should be.
            }
        } finally {
            releaseReader.Set();
        }

        await AssertDeliveredAsync(carol, HeldMessageText);
        Assert.Equal("Renamed Later", carol.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    private const string HeldMessageText = "signed with my new keys";

    /// <summary>
    /// Alice (admin) and Carol share a channel; Alice registers again with new keys, which Carol hasn't
    /// fetched. A message signed with Alice's new keys reaches Carol, whose inbox is then held by
    /// <paramref name="clock"/> just after it has looked up Alice's (old) identity for it, before the
    /// message's signature is checked. <see cref="StallingClock.Resume"/> lets it carry on.
    /// </summary>
    private async Task<(TestClient Carol, TestClient AliceAgain, string ChannelId, ulong Epoch)> MessageHeldWithAStaleIdentityAsync(string suffix, StallingClock clock) {
        var alice = await this._server.RegisterAsync("Alice " + suffix);
        // Carol doesn't rekey when asked, which would also fetch Alice's new identity.
        var carol = await this._server.RegisterAsync("Carol " + suffix, options: this._server.Options(autoRekey: false, time: clock));
        var channelId = await alice.Session.CreateChannelAsync("Before " + suffix, Ct);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RegisterAsync(alice.Name);

        // Hold Carol's inbox on a harmless event (a stale key it rejects), to learn its thread...
        var inboxThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInbox = new ManualResetEventSlim();
        carol.Session.Notice += notice => {
            if (notice.Text.StartsWith("Rejected a new key for a channel: it's for epoch 0,") && inboxThread.TrySetResult(Environment.CurrentManagedThreadId)) {
                releaseInbox.Wait(Harness.Timeout);
            }
        };

        try {
            this._server.Registry.Send(carol.UserId, new Event { EpochAdvanced = new EpochAdvanced { ChannelId = channelId, Epoch = 0, AuthorId = aliceAgain.UserId } });
            var thread = await inboxThread.Task.WaitAsync(Harness.Timeout, Ct);

            // ...queue the message behind it...
            using var newKeys = aliceAgain.LoadIdentity();
            var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = HeldMessageText } }, carol.LoadEpochKey(channelId, epoch),
                channelId, epoch, newKeys, aliceAgain.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await this._server.SendAndSettleAsync(carol, new Event {
                ChatMessage = new ChatMessage {
                    ChannelId = channelId, Epoch = epoch, SenderId = aliceAgain.UserId, MessageId = sent.MessageId,
                    TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
                },
            });

            // ...and let the inbox go on to the message, until it next reads the time.
            clock.StallNextReadOn(thread);
        } finally {
            releaseInbox.Set();
        }

        await clock.Stalled.WaitAsync(Harness.Timeout, Ct);
        return (carol, aliceAgain, channelId, epoch);
    }

    private static Event RenameSignedBy(TestClient author, TestClient holder, string channelId, ulong epoch, string name) {
        using var keys = author.LoadIdentity();
        var encrypted = ChannelCrypto.EncryptName(name, holder.LoadEpochKey(channelId, epoch), channelId, epoch, keys, author.UserId, revision: 1);
        return new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = encrypted } };
    }

    private static uint LastRequestId(TestClient client) => client.Session.GetTrace().Where(entry => entry.Outgoing).Select(RequestIdOf).DefaultIfEmpty(0u).Max();

    /// <summary>Outgoing trace entries read "#id Kind".</summary>
    private static uint RequestIdOf(TraceEntry entry) => uint.Parse(entry.Summary.AsSpan(1, entry.Summary.IndexOf(' ') - 1));

    private static async Task AssertDeliveredAsync(TestClient client, string text) {
        var outcome = await WaitFor(() => (object?) client.Messages.FirstOrDefault(m => m.Text == text)
                                          ?? client.Notices.FirstOrDefault(n => n.Text.Contains("failed signature or decryption checks")));
        Assert.True(outcome is IncomingMessage, $"The message was dropped: {(outcome as SessionNotice)?.Text}");
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
