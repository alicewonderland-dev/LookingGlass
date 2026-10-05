using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Re-verifying a character through the Lodestone with new identity keys (a new computer, a lost secrets file, "Reset my
/// identity") restores everything: the account's places in its channels and its invites move to the new keys, ranks
/// included, through a key recovered entry in each channel's log. The old key and its logins are shut out, the channel is
/// rekeyed so the old key reads nothing new, and the other members are told in plain words.
/// </summary>
public sealed class KeyRecoveryTests : IAsyncLifetime {
    private const string ReVerified = "re-verified their character and has a new key";

    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>
    /// The character registers on a "new computer": no secrets file, so new keys, and the Lodestone (here a debug account)
    /// as proof. The old computer is gone.
    /// </summary>
    internal static async Task<TestClient> NewComputerAsync(Harness server, TestClient client) {
        await client.Session.DisposeAsync();
        var again = server.StartClient(client.Name, new InMemorySecretStore());
        await WaitFor(() => again.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        await again.Session.StartRegistrationAsync(new Character { Name = client.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await again.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => again.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        return again;
    }

    /// <summary>"Reset my identity" as the plugin does it, on the same computer: retire the old key, new keys, register them.</summary>
    internal static async Task<TestClient> ResetAsync(Harness server, TestClient client) {
        await client.Session.RetireIdentityAsync(Ct);
        await client.Session.DisposeAsync();
        var secrets = client.Store.Load();
        secrets.ResetIdentity();
        client.Store.Save(secrets);

        var reset = server.StartClient(client.Name, client.Store);
        await WaitFor(() => reset.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
        await reset.Session.StartRegistrationAsync(new Character { Name = client.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await reset.Session.CompleteRegistrationAsync(Ct);
        await WaitFor(() => reset.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);
        return reset;
    }

    [Fact]
    public async Task OnANewComputerTheChannelsAndTheAdminRoleComeBack() {
        var alice = await this._server.RegisterAsync("Alice New Computer");
        var bob = await this._server.RegisterAsync("Bob Moderates On");
        var carol = await this._server.RegisterAsync("Carol Gets Removed");
        var channelId = await alice.Session.CreateChannelAsync("Came Back", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        await alice.Session.SetRankAsync(channelId, bob.UserId, Rank.Moderator, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Moderator } c ? c : null);
        var userId = alice.UserId;
        var oldToken = alice.Store.Load().DeviceToken!;
        using var oldKeys = alice.LoadIdentity();
        var oldEpoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        var again = await NewComputerAsync(this._server, alice);

        // Her place is back at once, as admin, under the new key; she's told in plain words.
        var place = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Admin } c ? c : null);
        Assert.False(place.OldKeyMembership);
        Assert.Contains(again.Notices, n => n.Text.Contains("restored") && n.Text.Contains("new key"));
        var newKeys = again.Keys();
        Assert.NotEqual(MemberKeys.Of(oldKeys), newKeys);
        Assert.Equal(newKeys, this._server.ServerMembership(channelId).FindMember(userId)!.Keys);
        var recovered = this._server.Database.GetLogEntries(channelId, 0, 100).Last();
        Assert.Equal(MembershipEntryKind.KeyRecovered, recovered.Kind);
        Assert.Equal(MemberKeys.Of(oldKeys), MemberKeys.FromProto(recovered.Subject));
        Assert.Equal(newKeys, MemberKeys.FromProto(recovered.NewKeys));

        // The others check the entry, see her new key as expected (not a "key changed" alarm, but not compared yet), and are told.
        foreach (var other in new[] { bob, carol }) {
            var notice = await WaitFor(() => other.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
            Assert.Equal(NoticeLevel.Info, notice.Level);
            Assert.Equal(channelId, notice.ChannelId);
            Assert.StartsWith("Alice New Computer", notice.Text);
            var seen = await WaitFor(() => other.Session.Snapshot.FindChannel(channelId)!.Members.FirstOrDefault(m => m.User.UserId == userId && m.KeyRecovered));
            Assert.Equal(Rank.Admin, seen.Rank);
            Assert.Equal(newKeys.Fingerprint, seen.Fingerprint);
            Assert.False(seen.KeyChanged);
            Assert.False(seen.KeyReplaced);
            Assert.False(seen.FingerprintCompared);
            Assert.DoesNotContain(other.Notices, n => n.Text.Contains("identity key changed"));
        }

        // A member who is online makes the channel a new key, so the old key reads nothing new and the new one gets it.
        var keyed = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Came Back" } c ? c : null);
        Assert.True(keyed.Epoch > oldEpoch);
        var newEpoch = this._server.Database.GetChannel(channelId)!.Epoch;
        Assert.DoesNotContain(this._server.Database.GetEpochKeys(channelId, userId, newEpoch), key => key.Key.Box == null);
        Assert.Single(this._server.Database.GetEpochKeys(channelId, userId, newEpoch));

        await again.Session.SendTextAsync(channelId, "back again", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "back again"));
        await bob.Session.SendTextAsync(channelId, "welcome back", Ct);
        await WaitFor(() => again.Messages.FirstOrDefault(m => m.Text == "welcome back"));

        // Still the admin: she renames, removes, hands nothing on.
        await again.Session.RenameAsync(channelId, "Came Back Renamed", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Name: "Came Back Renamed" } c ? c : null);
        await again.Session.KickAsync(channelId, carol.UserId, Ct);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
        await again.Session.SetRankAsync(channelId, bob.UserId, Rank.Member, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Member } c ? c : null);

        // The old key and its login are shut out.
        await using var raw = await this._server.ConnectRawAsync();
        Assert.Equal(ErrorCode.NotAuthenticated, (await raw.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = oldToken } })).Error?.Code);
        Assert.Equal(ErrorCode.NotAuthenticated, (await IdentityResetTests.KeyLoginAsync(raw, oldKeys, userId, this._server.ServerUri.AbsoluteUri)).Error?.Code);
        Assert.True(this._server.Database.IsKeyRetired(userId, oldKeys.SigningPublicKey));

        // And she can end it.
        await again.Session.DisbandAsync(channelId, Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
    }

    /// <summary>
    /// Nobody else is online when she recovers: she can't make the new key herself (without the channel's key she doesn't
    /// know its name), so she waits, and isn't the one asked. A member who comes back is told, and shares it.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhileTheOthersAreOfflineReachesThemWhenTheyComeBack() {
        var alice = await this._server.RegisterAsync("Alice Recovers Alone");
        var bob = await this._server.RegisterAsync("Bob Was Away");
        var channelId = await alice.Session.CreateChannelAsync("Quiet Hours", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var bobId = bob.UserId;
        await bob.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(bobId) ? null : new object());

        var again = await NewComputerAsync(this._server, alice);
        var waiting = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Admin } c ? c : null);
        Assert.False(waiting.HasKey);
        Assert.True(this._server.Database.GetChannel(channelId)!.RekeyPending);

        var back = await this._server.RestartAsync(bob);
        await WaitFor(() => back.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: "Quiet Hours" } c ? c : null);
        await again.Session.SendTextAsync(channelId, "morning", Ct);
        await WaitFor(() => back.Messages.FirstOrDefault(m => m.Text == "morning"));
        await back.Session.SendTextAsync(channelId, "you're back", Ct);
        await WaitFor(() => again.Messages.FirstOrDefault(m => m.Text == "you're back"));
    }

    /// <summary>
    /// A member who saw the recovery, and restarted before making the new key, still knows the channel's name to carry into
    /// it: the name in use was signed with her old key, which the log says she had where it was made.
    /// </summary>
    [Fact]
    public async Task AMemberWhoRestartedAfterSeeingTheRecoveryStillMakesTheNewKey() {
        var alice = await this._server.RegisterAsync("Alice Named It");
        var bob = await this._server.RegisterAsync("Bob Slow To Rekey", options: this._server.Options(autoRekey: false));
        var channelId = await alice.Session.CreateChannelAsync("Named Before", Ct);
        await AddMemberAsync(alice, channelId, bob);

        var again = await NewComputerAsync(this._server, alice);
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        Assert.Equal(again.Keys(), bob.Session.MembershipForTests(channelId).FindMember(again.UserId)!.Keys);
        await bob.Session.DisposeAsync();

        var back = await this._server.RestartAsync(bob, this._server.Options());
        Assert.Equal("Named Before", back.Session.Snapshot.FindChannel(channelId)!.Name);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Named Before" } c ? c : null);
    }

    /// <summary>
    /// Back with her new key but no channel key yet, the admin can already remove someone: the new key that leaves them out
    /// is made by a member who can, not by her (she doesn't know the channel's name yet).
    /// </summary>
    [Fact]
    public async Task BackWithoutTheChannelKeyTheAdminCanStillRemoveSomeone() {
        var alice = await this._server.RegisterAsync("Alice Removes Early");
        var bob = await this._server.RegisterAsync("Bob Removed Early", options: this._server.Options(autoRekey: false));
        var carol = await this._server.RegisterAsync("Carol Rekeys Later", options: this._server.Options(autoRekey: false));
        var channelId = await alice.Session.CreateChannelAsync("Early Removal", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);

        var again = await NewComputerAsync(this._server, alice);
        Assert.False(again.Session.Snapshot.FindChannel(channelId)!.HasKey);
        await again.Session.KickAsync(channelId, bob.UserId, Ct);

        Assert.Null(this._server.ServerMembership(channelId).FindMember(bob.UserId));
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) == null ? new object() : null);
        await carol.Session.RekeyAsync(channelId, Ct);
        await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Early Removal" } c ? c : null);
        Assert.DoesNotContain(again.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == bob.UserId);
    }

    /// <summary>
    /// An invite moves with her: the new key answers it. The name in it was sealed to the old key, so it shows once she has
    /// joined and a member shares the channel's key.
    /// </summary>
    [Fact]
    public async Task AnInviteMovesToTheNewKeyAndCanBeAccepted() {
        var bob = await this._server.RegisterAsync("Bob Invites Before");
        var alice = await this._server.RegisterAsync("Alice Invited Before");
        var channelId = await bob.Session.CreateChannelAsync("Invited Before", Ct);
        await bob.Session.InviteAsync(channelId, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));

        var again = await NewComputerAsync(this._server, alice);

        var invite = await WaitFor(() => again.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.Verified));
        Assert.Null(invite.ChannelName);
        Assert.Equal(again.Keys(), this._server.ServerMembership(channelId).FindInvitee(again.UserId)!.Keys);

        await again.Session.RespondToInviteAsync(channelId, true, Ct);
        var joined = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Invited Before" } c ? c : null);
        Assert.Equal(Rank.Member, joined.MyRank);
        await again.Session.SendTextAsync(channelId, "joined with the new key", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "joined with the new key"));
    }

    /// <summary>"Reset my identity" no longer leaves anything behind: the channels, the admin role and the invites stay hers.</summary>
    [Fact]
    public async Task ResettingTheIdentityKeepsTheChannels() {
        var alice = await this._server.RegisterAsync("Alice Resets Keeps");
        var bob = await this._server.RegisterAsync("Bob Keeps Her");
        var mine = await alice.Session.CreateChannelAsync("Hers Still", Ct);
        await AddMemberAsync(alice, mine, bob);
        var invited = await bob.Session.CreateChannelAsync("Invited Still", Ct);
        await bob.Session.InviteAsync(invited, alice.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == invited && i.ChannelName != null));
        using var oldKeys = alice.LoadIdentity();

        var reset = await ResetAsync(this._server, alice);

        await WaitFor(() => reset.Session.Snapshot.FindChannel(mine) is { MyRank: Rank.Admin, HasKey: true, Name: "Hers Still" } c ? c : null);
        await WaitFor(() => reset.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == invited && i.Verified));
        Assert.DoesNotContain(this._server.Database.GetLogEntries(mine, 0, 100), entry => entry.Kind == MembershipEntryKind.Leave);
        Assert.True(this._server.Database.IsKeyRetired(reset.UserId, oldKeys.SigningPublicKey));
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified) && n.ChannelId == mine));
        await reset.Session.SendTextAsync(mine, "same me, new key", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "same me, new key"));

        // Her own move isn't news to her: not as someone else's new key, nor as her key going elsewhere.
        Assert.DoesNotContain(reset.Notices, n => n.Text.Contains(ReVerified) || n.Text == PlainMessages.ReVerifiedElsewhere);
    }

    /// <summary>
    /// A place an old key still holds (registered again before recovery existed) is the same account's: the next recovery
    /// moves it too. One removed from the list stays removed, under the key it had.
    /// </summary>
    [Fact]
    public async Task AnOldKeysPlaceComesBackButARemovedOneStaysRemoved() {
        var bob = await this._server.RegisterAsync("Bob Two Channels");
        var alice = await this._server.RegisterAsync("Alice Old Places");
        var kept = await bob.Session.CreateChannelAsync("Kept Place", Ct);
        var removed = await bob.Session.CreateChannelAsync("Removed Place", Ct);
        await AddMemberAsync(bob, kept, alice);
        await AddMemberAsync(bob, removed, alice);
        var firstKeys = alice.Keys();
        var stale = await ForgetChannelTests.RegisterOnAnOldServerAsync(this._server, alice);
        await WaitFor(() => stale.Session.Snapshot.FindChannel(kept) is { OldKeyMembership: true } c ? c : null);
        await stale.Session.ForgetChannelAsync(removed, Ct);

        var again = await NewComputerAsync(this._server, stale);

        await WaitFor(() => again.Session.Snapshot.FindChannel(kept) is { MyRank: Rank.Member, OldKeyMembership: false } c ? c : null);
        Assert.Equal(again.Keys(), this._server.ServerMembership(kept).FindMember(again.UserId)!.Keys);
        Assert.Equal(MemberKeys.FromProto(this._server.Database.GetLogEntries(kept, 0, 100).Last().Subject), firstKeys);
        Assert.Null(again.Session.Snapshot.FindChannel(removed));
        Assert.Equal(firstKeys, this._server.ServerMembership(removed).FindMember(again.UserId)!.Keys);
        Assert.True(Assert.Single(this._server.Database.GetMembers(removed), m => m.User.UserId == again.UserId).Forgotten);
    }

    /// <summary>
    /// Alone in her channel, she is the only one who could make its new key, and nobody can tell her its name: she makes
    /// one under a name of its own, which she can change.
    /// </summary>
    [Fact]
    public async Task AloneInAChannelTheRecoveredMemberMakesItsNewKey() {
        var alice = await this._server.RegisterAsync("Alice All Alone");
        var channelId = await alice.Session.CreateChannelAsync("Just Me", Ct);

        var again = await NewComputerAsync(this._server, alice);

        var channel = await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: not null } c ? c : null);
        Assert.Equal(PlainMessages.RestoredChannelName, channel.Name);
        await again.Session.RenameAsync(channelId, "Just Me Again", Ct);
        Assert.Equal("Just Me Again", again.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    /// <summary>
    /// Everyone who held the channel's key re-verified with new keys before any of their old computers came back (here, Bob
    /// while Alice already waits for him): nobody can share the key or tell its name, so one of them makes a new key under a
    /// name of its own rather than both waiting for ever.
    /// </summary>
    [Fact]
    public async Task WhenEveryoneWhoHeldTheKeyRecoveredOneOfThemMakesANewOne() {
        var alice = await this._server.RegisterAsync("Alice Both Recover");
        var bob = await this._server.RegisterAsync("Bob Both Recover");
        var channelId = await alice.Session.CreateChannelAsync("Both Came Back", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var bobId = bob.UserId;
        await bob.Session.DisposeAsync();
        await WaitFor(() => this._server.Registry.IsOnline(bobId) ? null : new object());

        // Bob, who holds the key, may come back: she waits for him.
        var alice2 = await NewComputerAsync(this._server, alice);
        Assert.False((await WaitFor(() => alice2.Session.Snapshot.FindChannel(channelId) is { MyRank: Rank.Admin } c ? c : null)).HasKey);

        // He comes back on a new computer too: nobody holds it any more.
        var bob2 = await NewComputerAsync(this._server, bob);

        foreach (var client in new[] { alice2, bob2 }) {
            await WaitFor(() => client.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: PlainMessages.RestoredChannelName } c ? c : null);
        }

        Assert.False(this._server.Database.GetChannel(channelId)!.RekeyPending);
        Assert.DoesNotContain(this._server.Database.GetMembers(channelId), member => member.AwaitingKey);
        Assert.Equal(Rank.Admin, alice2.Session.Snapshot.FindChannel(channelId)!.MyRank);
        await alice2.Session.SendTextAsync(channelId, "both new", Ct);
        await WaitFor(() => bob2.Messages.FirstOrDefault(m => m.Text == "both new"));
        await alice2.Session.RenameAsync(channelId, "Both Came Back Again", Ct);
        await WaitFor(() => bob2.Session.Snapshot.FindChannel(channelId) is { Name: "Both Came Back Again" } c ? c : null);
    }

    /// <summary>
    /// The only other member is a place under keys its owner no longer has (registered again on a server from before
    /// recovery; in the second channel also removed from their list): it can't make a key, and nobody else holds one, so
    /// the recovered member does, under a name of its own, although they aren't alone in the log.
    /// </summary>
    [Fact]
    public async Task WhenTheOnlyOtherPlaceIsAnOldKeysTheRecoveredMemberMakesTheKey() {
        var alice = await this._server.RegisterAsync("Alice Beside Old Keys");
        var bob = await this._server.RegisterAsync("Bob Left Old Keys");
        var oldKey = await alice.Session.CreateChannelAsync("Old Key Company", Ct);
        var forgotten = await alice.Session.CreateChannelAsync("Forgotten Company", Ct);
        await AddMemberAsync(alice, oldKey, bob);
        await AddMemberAsync(alice, forgotten, bob);
        var stale = await ForgetChannelTests.RegisterOnAnOldServerAsync(this._server, bob);
        await WaitFor(() => stale.Session.Snapshot.FindChannel(forgotten) is { OldKeyMembership: true } c ? c : null);
        await stale.Session.ForgetChannelAsync(forgotten, Ct);

        var again = await NewComputerAsync(this._server, alice);

        foreach (var channelId in new[] { oldKey, forgotten }) {
            await WaitFor(() => again.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false, Name: PlainMessages.RestoredChannelName } c ? c : null);
            Assert.Equal(2, again.Session.Snapshot.FindChannel(channelId)!.Members.Length);
            Assert.False(this._server.Database.GetChannel(channelId)!.RekeyPending);
        }
    }

    /// <summary>
    /// The new keys' consent goes with the registration: without it, a registration that would move places is refused
    /// (asking to update), and one signed by any other key, or for another account, registers nothing.
    /// </summary>
    [Fact]
    public async Task RegisteringNewKeysNeedsTheirConsentToMoveThePlaces() {
        var alice = await this._server.RegisterAsync("Alice Consents");
        var channelId = await alice.Session.CreateChannelAsync("Consent", Ct);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();
        using var newKeys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        var url = this._server.ServerUri.AbsoluteUri;

        foreach (var recovery in new[] {
                     ByteString.Empty,
                     ByteString.CopyFrom(KeyRecoveryProof.Sign(otherKeys, userId)),
                     ByteString.CopyFrom(KeyRecoveryProof.Sign(newKeys, userId + 1)),
                     ByteString.CopyFrom(new byte[64]),
                 }) {
            await using var raw = await this._server.ConnectRawAsync();
            var challenge = (await raw.SendAsync(new ClientFrame {
                StartRegistration = new StartRegistration {
                    Character = new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Identity = newKeys.ToBundle(), ServerUrl = url, ClientNonce = NewClientNonce(),
                },
            })).RegistrationChallenge!;
            var response = await raw.SendAsync(new ClientFrame {
                CompleteRegistration = new CompleteRegistration {
                    ServerUrl = url,
                    Signature = ByteString.CopyFrom(RegistrationProof.Sign(newKeys, challenge.Nonce.Span, challenge.LodestoneId, url)),
                    RecoverySignature = recovery,
                },
            });

            Assert.Equal(ErrorCode.RegistrationFailed, response.Error?.Code);
            Assert.Contains(recovery.IsEmpty ? "update the plugin" : "isn't signed", response.Error!.Message);
            Assert.Equal(alice.Keys(), MemberKeys.Of(this._server.Database.GetUser(userId)!.ToIdentity().Identity));
            Assert.Equal(alice.Keys(), this._server.ServerMembership(channelId).FindMember(userId)!.Keys);
        }
    }

    /// <summary>
    /// Plays a malicious server: key recovered entries that don't check out are refused by every client, and the member list
    /// stays as verified. One for someone not in the channel, naming keys their place isn't under, moving them to keys they
    /// have or another member has, or not signed by the new keys.
    /// </summary>
    [Fact]
    public async Task ForgedKeyRecoveriesAreRefused() {
        var alice = await this._server.RegisterAsync("Alice Checks Recoveries");
        var bob = await this._server.RegisterAsync("Bob Is Recovered");
        var carol = await this._server.RegisterAsync("Carol Never Joined");
        var channelId = await alice.Session.CreateChannelAsync("Forged Recoveries", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var head = this._server.Database.GetChannel(channelId)!.LogHead;
        using var serverKeys = IdentityKeys.Generate();
        using var bobKeys = bob.LoadIdentity();
        using var aliceKeys = alice.LoadIdentity();

        MembershipEntry Recovery(long userId, MemberKeys subject, MemberKeys newKeys, byte[] signature) => new() {
            ChannelId = channelId,
            Seq = head.Seq + 1,
            PreviousHash = head.Hash,
            Kind = MembershipEntryKind.KeyRecovered,
            ActorId = userId,
            ActorKeyHash = ByteString.CopyFrom(newKeys.Hash),
            Subject = subject.ToProto(userId),
            NewKeys = newKeys.ToProto(userId),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Signature = ByteString.CopyFrom(signature),
        };

        var server = MemberKeys.Of(serverKeys);
        var forgeries = new[] {
            // Carol isn't in the channel.
            Recovery(carol.UserId, carol.Keys(), server, KeyRecoveryProof.Sign(serverKeys, carol.UserId)),
            // Bob's place isn't under those keys.
            Recovery(bob.UserId, carol.Keys(), server, KeyRecoveryProof.Sign(serverKeys, bob.UserId)),
            // To the keys he has, or Alice's.
            Recovery(bob.UserId, bob.Keys(), bob.Keys(), KeyRecoveryProof.Sign(bobKeys, bob.UserId)),
            Recovery(bob.UserId, bob.Keys(), alice.Keys(), KeyRecoveryProof.Sign(aliceKeys, bob.UserId)),
            // Not signed by the new keys: by his old ones, by none, or for someone else.
            Recovery(bob.UserId, bob.Keys(), server, KeyRecoveryProof.Sign(bobKeys, bob.UserId)),
            Recovery(bob.UserId, bob.Keys(), server, new byte[64]),
            Recovery(bob.UserId, bob.Keys(), server, KeyRecoveryProof.Sign(serverKeys, carol.UserId)),
        };

        foreach (var forged in forgeries) {
            // Handled once the announcement after it arrives (events are handled in order).
            await this._server.SendAndSettleAsync(alice, new Event { LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = forged } });
            Assert.Equal(bob.Keys(), alice.Session.MembershipForTests(channelId).FindMember(bob.UserId)!.Keys);
            Assert.Null(alice.Session.MembershipForTests(channelId).FindMember(carol.UserId));
            Assert.True(MembershipEntries.SamePosition(head, alice.Session.MembershipForTests(channelId).Head));
        }

        // Each told (once per reason: the same one twice is said once).
        Assert.True(alice.Notices.Count(n => n.Level == NoticeLevel.Warning && n.Text.Contains("doesn't check out")) >= 5);
        Assert.DoesNotContain(alice.Notices, n => n.Text.Contains(ReVerified));
        Assert.DoesNotContain(alice.Session.Snapshot.FindChannel(channelId)!.Members, m => m.KeyRecovered || m.User.UserId == carol.UserId);
    }

    /// <summary>
    /// The trust model, played: the server can swap a member's keys for its own, since only it saw the Lodestone. It can't
    /// do it quietly: everyone is told, the member shows a new key not compared yet, and the member's own client sees its
    /// place now belongs to keys it doesn't have.
    /// </summary>
    [Fact]
    public async Task AServerCanSwapAKeyButNotQuietly() {
        var alice = await this._server.RegisterAsync("Alice Watches Swap");
        var bob = await this._server.RegisterAsync("Bob Gets Swapped");
        var channelId = await alice.Session.CreateChannelAsync("Swapped", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.AcknowledgeKeyChangeAsyncFor(bob);
        using var serverKeys = IdentityKeys.Generate();
        var swapped = this._server.ServerMembership(channelId).CreateKeyRecovered(bob.UserId, MemberKeys.Of(serverKeys), KeyRecoveryProof.Sign(serverKeys, bob.UserId),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(this._server.Database.AppendEntry(channelId, swapped));
        // And it says the same of Bob wherever it is asked (a server that didn't would show a "key changed" warning too).
        var bundle = serverKeys.ToBundle();
        this._server.ExecuteSql("UPDATE users SET signing_key = $s, agreement_key = $a, binding_signature = $b, key_version = key_version + 1 WHERE user_id = $u;",
            ("$s", bundle.SigningPublicKey.ToByteArray()), ("$a", bundle.AgreementPublicKey.ToByteArray()), ("$b", bundle.BindingSignature.ToByteArray()), ("$u", bob.UserId));

        foreach (var client in new[] { alice, bob }) {
            await this._server.SendAndSettleAsync(client, new Event {
                LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = swapped, Subject = new User { UserId = bob.UserId, Name = bob.Name, WorldName = ProtocolInfo.DebugWorldName } },
            });
        }

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        var seen = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId);
        Assert.True(seen.KeyRecovered);
        Assert.False(seen.FingerprintCompared);
        Assert.Equal(MemberKeys.Of(serverKeys).Fingerprint, seen.Fingerprint);
        var place = await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { OldKeyMembership: true } c ? c : null);

        // Bob himself is told, in plain words: someone re-verified his character with another key, and what to do if it wasn't him.
        var told = await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text == PlainMessages.ReVerifiedElsewhere));
        Assert.Equal(NoticeLevel.Warning, told.Level);
        Assert.Contains("Reset my identity", told.Text);
        Assert.True(place.KeyMovedAway);
        Assert.Equal(PlainMessages.KeyMovedAwayChannel, place.MembershipWarning);
        // Not as someone else who has a new key.
        Assert.DoesNotContain(bob.Notices, n => n.Text.Contains(ReVerified));
    }

    /// <summary>
    /// A log read from its start (an invite's) can still change a key pinned before: Alice compared Bob's key in a channel
    /// they shared, which he has left since. When his place in another channel moved to a new key, she wasn't there; reading
    /// that channel's history when she's invited, she is told, and he no longer shows as compared. Dave, who never saw Bob
    /// before, reads the same history as history, and isn't told.
    /// </summary>
    [Fact]
    public async Task APinnedKeyChangesOutLoudEvenInALogReadFromItsStart() {
        var alice = await this._server.RegisterAsync("Alice Compared Bob");
        var bob = await this._server.RegisterAsync("Bob Compared Once");
        var carol = await this._server.RegisterAsync("Carol Invites Later");
        var dave = await this._server.RegisterAsync("Dave Never Met Bob");
        var shared = await alice.Session.CreateChannelAsync("Compared Here", Ct);
        await AddMemberAsync(alice, shared, bob);
        await alice.Session.AcknowledgeKeyChangeAsyncFor(bob);
        await bob.Session.LeaveAsync(shared, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(shared)!.Members.Length == 1 ? new object() : null);
        var channelId = await carol.Session.CreateChannelAsync("Bob Moved Here", Ct);
        await AddMemberAsync(carol, channelId, bob);

        var bob2 = await NewComputerAsync(this._server, bob);
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId)!.Members.FirstOrDefault(m => m.User.UserId == bob2.UserId && m.KeyRecovered));
        await WaitFor(() => carol.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);

        foreach (var invited in new[] { alice, dave }) {
            await carol.Session.InviteAsync(channelId, invited.Name, ProtocolInfo.DebugWorldName, Ct);
            await WaitFor(() => invited.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.Verified));
            await invited.Session.RespondToInviteAsync(channelId, true, Ct);
            await WaitFor(() => invited.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);
        }

        var notice = await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        Assert.Contains(bob.Name, notice.Text);
        Assert.Contains("compared", notice.Text);
        var seen = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob2.UserId);
        Assert.Equal(bob2.Keys().Fingerprint, seen.Fingerprint);
        Assert.True(seen.KeyRecovered);
        Assert.False(seen.FingerprintCompared);

        Assert.DoesNotContain(dave.Notices, n => n.Text.Contains(ReVerified));
        Assert.False(dave.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob2.UserId).KeyRecovered);
    }

    /// <summary>
    /// The server's identities show Bob's new key first, as an unexplained change ("key changed"); the log then says why. The
    /// warning becomes "New key", and Alice is told he re-verified his character.
    /// </summary>
    [Fact]
    public async Task AKeyChangeWarningBecomesANewKeyOnceTheLogExplainsIt() {
        var alice = await this._server.RegisterAsync("Alice Warned First");
        var bob = await this._server.RegisterAsync("Bob Explained Later");
        var channelId = await alice.Session.CreateChannelAsync("Explained", Ct);
        await AddMemberAsync(alice, channelId, bob);
        using var newKeys = IdentityKeys.Generate();
        var bundle = newKeys.ToBundle();
        this._server.ExecuteSql("UPDATE users SET signing_key = $s, agreement_key = $a, binding_signature = $b, key_version = key_version + 1 WHERE user_id = $u;",
            ("$s", bundle.SigningPublicKey.ToByteArray()), ("$a", bundle.AgreementPublicKey.ToByteArray()), ("$b", bundle.BindingSignature.ToByteArray()), ("$u", bob.UserId));
        await alice.Session.RefreshAsync(Ct);
        Assert.Contains(alice.Notices, n => n.Level == NoticeLevel.Warning && n.Text.Contains("identity key changed"));
        Assert.True(alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId).KeyChanged);

        var moved = this._server.ServerMembership(channelId).CreateKeyRecovered(bob.UserId, MemberKeys.Of(newKeys), KeyRecoveryProof.Sign(newKeys, bob.UserId),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.True(this._server.Database.AppendEntry(channelId, moved));
        await this._server.SendAndSettleAsync(alice, new Event { LogEntryAdded = new LogEntryAdded { ChannelId = channelId, Entry = moved } });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        var seen = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId);
        Assert.Equal(MemberKeys.Of(newKeys).Fingerprint, seen.Fingerprint);
        Assert.False(seen.KeyChanged);
        Assert.True(seen.KeyRecovered);
        Assert.False(seen.FingerprintCompared);
    }

    /// <summary>A member whose place moved twice while Alice wasn't looking is announced once, under the keys they have now.</summary>
    [Fact]
    public async Task AMemberMovedTwiceAtOnceIsAnnouncedOnce() {
        var alice = await this._server.RegisterAsync("Alice Catches Up");
        var bob = await this._server.RegisterAsync("Bob Moved Twice");
        var channelId = await alice.Session.CreateChannelAsync("Moved Twice", Ct);
        await AddMemberAsync(alice, channelId, bob);
        using var first = IdentityKeys.Generate();
        using var second = IdentityKeys.Generate();
        foreach (var keys in new[] { first, second }) {
            var moved = this._server.ServerMembership(channelId).CreateKeyRecovered(bob.UserId, MemberKeys.Of(keys), KeyRecoveryProof.Sign(keys, bob.UserId),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.True(this._server.Database.AppendEntry(channelId, moved));
        }

        // As registering them would have left the server's identity for him.
        var bundle = second.ToBundle();
        this._server.ExecuteSql("UPDATE users SET signing_key = $s, agreement_key = $a, binding_signature = $b, key_version = key_version + 2 WHERE user_id = $u;",
            ("$s", bundle.SigningPublicKey.ToByteArray()), ("$a", bundle.AgreementPublicKey.ToByteArray()), ("$b", bundle.BindingSignature.ToByteArray()), ("$u", bob.UserId));
        await alice.Session.RefreshAsync(Ct);

        Assert.Equal(MemberKeys.Of(second), alice.Session.MembershipForTests(channelId).FindMember(bob.UserId)!.Keys);
        Assert.Single(alice.Notices, n => n.Text.Contains(ReVerified));
        var seen = alice.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId);
        Assert.Equal(MemberKeys.Of(second).Fingerprint, seen.Fingerprint);
        Assert.True(seen.KeyRecovered);
    }

    /// <summary>
    /// The identity a member holds for someone whose place just moved is the old one: it is fetched again (here, the new
    /// one, as the server registered it), not left out.
    /// </summary>
    [Fact]
    public async Task AMovedMembersNewIdentityIsFetched() {
        var asked = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var recording = false;
        var alice = await this._server.RegisterAsync("Alice Fetches Again", options: this._server.Options(wrap: socket => new RewritingWebSocket(socket, frame => frame, sent: frame => {
            if (Volatile.Read(ref recording) && frame.GetIdentities is { } request) {
                foreach (var id in request.UserIds) {
                    asked.Enqueue(id);
                }
            }
        })));
        var bob = await this._server.RegisterAsync("Bob Fetched Again");
        var channelId = await alice.Session.CreateChannelAsync("Fetched Again", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await alice.Session.RefreshAsync(Ct);
        Volatile.Write(ref recording, true);

        var bob2 = await NewComputerAsync(this._server, bob);

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains(ReVerified)));
        await WaitFor(() => asked.Contains(bob2.UserId) ? new object() : null);
    }
}

internal static class KeyRecoveryTestExtensions {
    /// <summary>Marks another client's current keys verified, as after comparing fingerprints over /tell.</summary>
    public static Task AcknowledgeKeyChangeAsyncFor(this ClientSession session, TestClient other) {
        session.AcknowledgeKeyChange(other.UserId, other.Keys().Fingerprint);
        return Task.CompletedTask;
    }
}
