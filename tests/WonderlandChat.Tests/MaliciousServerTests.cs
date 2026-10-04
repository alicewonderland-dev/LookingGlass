using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>
/// The test plays a malicious server: it pushes forged or replayed events
/// straight to a client through the server's connection registry, and checks
/// the client refuses them.
///
/// Not covered yet: a ghost member inserted into the member list. That needs
/// authenticated membership (design doc, v0.2).
/// </summary>
public sealed class MaliciousServerTests : IAsyncLifetime {
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
    public async Task EpochKeyFromNonMemberIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Forge");
        var ghost = await this._server.RegisterAsync("Ghost Forge");
        var channelId = await alice.Session.CreateChannelAsync("Secret", Ct);
        var before = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // The ghost is a real registered user, but not a member. A server pushes its key to Alice.
        using var ghostKeys = ghost.LoadIdentity();
        var forged = ChannelCrypto.NewEpochKey();
        this._server.Registry.Send(alice.UserId, new Event {
            EpochAdvanced = new EpochAdvanced {
                ChannelId = channelId,
                Epoch = before + 1,
                AuthorId = ghost.UserId,
                MyKey = ChannelCrypto.SealEpochKey(forged, channelId, before + 1, ghostKeys, ghost.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey),
            },
        });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key")));
        Assert.Equal(before, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.DoesNotContain(before + 1, alice.Store.Load().EpochKeys[channelId].Keys);
    }

    [Fact]
    public async Task ReplayedOlderEpochKeyIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Wedge");
        var bob = await this._server.RegisterAsync("Bob Wedge");
        var channelId = await alice.Session.CreateChannelAsync("Wedge", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var current = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // A replayed older epoch, validly signed by a real member, must not roll Alice back.
        using var bobKeys = bob.LoadIdentity();
        var aliceAgreement = alice.LoadIdentity().AgreementPublicKey;
        this._server.Registry.Send(alice.UserId, new Event {
            EpochAdvanced = new EpochAdvanced {
                ChannelId = channelId,
                Epoch = current - 1,
                AuthorId = bob.UserId,
                MyKey = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, current - 1, bobKeys, bob.UserId, alice.UserId, aliceAgreement),
            },
        });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("already have epoch")));
        Assert.Equal(current, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);

        // Chat still works afterwards.
        await bob.Session.SendTextAsync(channelId, "still fine", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "still fine"));
    }

    [Fact]
    public async Task ReplayedAndStaleMessagesAreDropped() {
        var alice = await this._server.RegisterAsync("Alice Replay");
        var bob = await this._server.RegisterAsync("Bob Replay");
        var channelId = await alice.Session.CreateChannelAsync("Replay", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        using var bobKeys = bob.LoadIdentity();
        var key = bob.LoadEpochKey(channelId, epoch);
        ChatMessage Forge(string text, DateTimeOffset when) {
            var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = text } }, key, channelId, epoch, bobKeys, bob.UserId, when.ToUnixTimeMilliseconds());
            return new ChatMessage {
                ChannelId = channelId, Epoch = epoch, SenderId = bob.UserId, MessageId = sent.MessageId,
                TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
            };
        }

        // A genuine message delivered twice is shown once.
        var fresh = Forge("delivered twice", DateTimeOffset.UtcNow);
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = fresh });
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = fresh });

        // A genuine but old message (as if captured and replayed later) is dropped.
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = Forge("from an hour ago", DateTimeOffset.UtcNow.AddHours(-1)) });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("too far from the current time")));
        await Task.Delay(200, Ct);
        Assert.Single(alice.Messages, m => m.Text == "delivered twice");
        Assert.DoesNotContain(alice.Messages, m => m.Text == "from an hour ago");
    }

    [Fact]
    public async Task OldChannelNameCannotBeReplayed() {
        var alice = await this._server.RegisterAsync("Alice Rename");
        var bob = await this._server.RegisterAsync("Bob Rename");
        var channelId = await alice.Session.CreateChannelAsync("Original Name", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        await alice.Session.RenameAsync(channelId, "Second Name", Ct);
        await alice.Session.RenameAsync(channelId, "Third Name", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Third Name" ? new object() : null);

        // Replay names validly signed by Alice: one from an older epoch, and an older rename from this epoch.
        using var aliceKeys = alice.LoadIdentity();
        var olderEpoch = ChannelCrypto.EncryptName("Original Name", alice.LoadEpochKey(channelId, 0), channelId, 0, aliceKeys, alice.UserId);
        var olderRevision = ChannelCrypto.EncryptName("Second Name", alice.LoadEpochKey(channelId, epoch), channelId, epoch, aliceKeys, alice.UserId, revision: 1);
        await this._server.SendAndSettleAsync(bob,
            new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = olderEpoch } },
            new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = olderRevision } });

        Assert.Equal("Third Name", bob.Session.Snapshot.FindChannel(channelId)!.Name);

        // A genuinely newer rename is still accepted.
        await alice.Session.RenameAsync(channelId, "Fourth Name", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Fourth Name" ? new object() : null);
    }

    [Fact]
    public async Task OldChannelNameFromChannelListIsRefusedEvenAfterRestart() {
        var alice = await this._server.RegisterAsync("Alice Relist");
        var bob = await this._server.RegisterAsync("Bob Relist");
        var channelId = await alice.Session.CreateChannelAsync("Original Name", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await alice.Session.RenameAsync(channelId, "Second Name", Ct);
        await alice.Session.RenameAsync(channelId, "Third Name", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Third Name" ? new object() : null);

        // The server rolls its stored name back to an older rename from the same epoch...
        using var aliceKeys = alice.LoadIdentity();
        var olderRevision = ChannelCrypto.EncryptName("Second Name", alice.LoadEpochKey(channelId, epoch), channelId, epoch, aliceKeys, alice.UserId, revision: 1);
        this.StoreName(channelId, olderRevision);

        // ...and Bob restarts, so only what he persisted protects him.
        await bob.Session.DisposeAsync();
        var bobAgain = await this._server.RestartAsync(bob);
        Assert.NotEqual("Second Name", bobAgain.Session.Snapshot.FindChannel(channelId)!.Name);

        // A name from an older epoch, listed to a client that is still running, is refused too.
        this.StoreName(channelId, ChannelCrypto.EncryptName("Original Name", alice.LoadEpochKey(channelId, 0), channelId, 0, aliceKeys, alice.UserId));
        await alice.Session.RefreshAsync(Ct);
        Assert.Equal("Third Name", alice.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    [Fact]
    public async Task ServerEpochJumpDoesNotMoveTheKeyEpoch() {
        var alice = await this._server.RegisterAsync("Alice Jump");
        var bob = await this._server.RegisterAsync("Bob Jump");
        var channelId = await alice.Session.CreateChannelAsync("Jump", Ct);
        await AddMemberAsync(alice, channelId, bob);
        Assert.Equal(1UL, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);

        // The server claims epoch 7, but nobody ever sent a key for it.
        this._server.ExecuteSql("UPDATE channels SET epoch = 7 WHERE channel_id = $id;", ("$id", channelId));
        await bob.Session.RefreshAsync(Ct);

        var view = bob.Session.Snapshot.FindChannel(channelId)!;
        Assert.Equal(7UL, view.ServerEpoch);
        Assert.Equal(1UL, view.Epoch);
        Assert.False(view.HasKey);
        Assert.True(view.RekeyPending);

        // Sending rekeys to the server's epoch + 1 with a key Bob actually holds, so chat recovers.
        await bob.Session.SendTextAsync(channelId, "after the jump", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "after the jump"));
        Assert.Equal(8UL, bob.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.Equal(8UL, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
    }

    [Fact]
    public async Task ClientWithoutKeysRefusesAnEpochKeyFarBehindTheServer() {
        var alice = await this._server.RegisterAsync("Alice Behind", options: this._server.Options(autoRekey: false));
        var carol = await this._server.RegisterAsync("Carol Behind");
        var channelId = await alice.Session.CreateChannelAsync("Behind", Ct);
        for (var i = 0; i < 3; i++) {
            await alice.Session.RekeyAsync(channelId, Ct, force: true);
        }

        await alice.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => carol.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
        await carol.Session.RespondToInviteAsync(channelId, true, Ct);
        Assert.Equal(3UL, carol.Session.Snapshot.FindChannel(channelId)!.ServerEpoch);

        // Alice (a real member) once sealed an epoch-1 key to Carol; the server hands it over now.
        using var aliceKeys = alice.LoadIdentity();
        var carolAgreement = carol.LoadIdentity().AgreementPublicKey;
        this._server.Registry.Send(carol.UserId, new Event {
            EpochAdvanced = new EpochAdvanced {
                ChannelId = channelId,
                Epoch = 1,
                AuthorId = alice.UserId,
                MyKey = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 1, aliceKeys, alice.UserId, carol.UserId, carolAgreement),
            },
        });

        await WaitFor(() => carol.Notices.FirstOrDefault(n => n.Text.Contains("already at epoch 3")));
        Assert.False(carol.Store.Load().EpochKeys.ContainsKey(channelId));
    }

    [Fact]
    public async Task InvalidChannelIdsFromTheServerAreDropped() {
        var bob = await this._server.RegisterAsync("Bob Ids");
        var alice = await this._server.RegisterAsync("Alice Ids");

        // An invite event for a non-canonical channel ID.
        await this._server.SendAndSettleAsync(bob, new Event {
            InviteReceived = new InviteReceived {
                Invite = new InviteInfo { ChannelId = "ABC", Inviter = alice.Session.Snapshot.Me, SealedName = new SealedBox(), CreatedUnix = 1 },
            },
        });
        Assert.Empty(bob.Session.Snapshot.Invites);

        // A channel row with a short ID, as if the server's database were edited.
        this._server.ExecuteSql("""
            INSERT INTO channels (channel_id, epoch, rekey_pending, name_epoch, name_author, name_ciphertext, name_signature, created_at)
            VALUES ('short', 0, 0, 0, 0, x'', x'', 0);
            INSERT INTO members (channel_id, user_id, rank, joined_at) VALUES ('short', $user, 2, 0);
            """, ("$user", bob.UserId));
        await bob.Session.RefreshAsync(Ct);
        Assert.Null(bob.Session.Snapshot.FindChannel("short"));
        Assert.Equal("(encrypted channel abc)", new ChannelView("abc", null, 0, 0, false, false, Rank.Member, []).DisplayName);
    }

    private void StoreName(string channelId, EncryptedName name) {
        this._server.ExecuteSql("""
            UPDATE channels SET name_epoch = $epoch, name_revision = $revision, name_author = $author,
                name_ciphertext = $ciphertext, name_signature = $signature
            WHERE channel_id = $id;
            """,
            ("$id", channelId), ("$epoch", (long) name.Epoch), ("$revision", (long) name.Revision), ("$author", name.AuthorId),
            ("$ciphertext", name.Ciphertext.ToByteArray()), ("$signature", name.Signature.ToByteArray()));
    }

    [Fact]
    public async Task MessageWithForgedSenderIsRejected() {
        var alice = await this._server.RegisterAsync("Alice Sender");
        var bob = await this._server.RegisterAsync("Bob Sender");
        var channelId = await alice.Session.CreateChannelAsync("Who said it", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = alice.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // The server (which doesn't have Bob's signing key) re-attributes Alice's message to Bob.
        using var aliceKeys = alice.LoadIdentity();
        var sent = ChannelCrypto.EncryptMessage(new Content { Text = new TextContent { Text = "who wrote this?" } },
            alice.LoadEpochKey(channelId, epoch), channelId, epoch, aliceKeys, alice.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        this._server.Registry.Send(alice.UserId, new Event {
            ChatMessage = new ChatMessage {
                ChannelId = channelId, Epoch = epoch, SenderId = bob.UserId, MessageId = ByteString.CopyFrom(new byte[16]),
                TimestampUnixMs = sent.TimestampUnixMs, Ciphertext = sent.Ciphertext, Signature = sent.Signature,
            },
        });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.Contains("failed signature or decryption")));
        Assert.DoesNotContain(alice.Messages, m => m.Text == "who wrote this?");
    }
}
