using Google.Protobuf;
using WonderlandChat.Core.Client;
using WonderlandChat.Core.Crypto;
using WonderlandChat.Core.Membership;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>
/// The test plays a malicious server: it pushes forged or replayed events
/// straight to a client through the server's connection registry, and checks
/// the client refuses them. Attacks on membership itself (ghost members, forged
/// or hidden changes, forks) are in <see cref="MembershipLogTests"/>.
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
                MyKey = ChannelCrypto.SealEpochKey(forged, channelId, before + 1, PositionOf(alice, channelId), ghostKeys, ghost.UserId, alice.UserId, alice.LoadIdentity().AgreementPublicKey),
            },
        });

        await WaitFor(() => alice.Notices.FirstOrDefault(n => n.Text.StartsWith("Rejected a new key")));
        Assert.Equal(before, alice.Session.Snapshot.FindChannel(channelId)!.Epoch);
        Assert.DoesNotContain(before + 1, alice.Store.Load().EpochKeys[channelId].Keys);
    }

    [Fact]
    public async Task ForgedMessagesAreDroppedWithoutRepeatedIdentityLookups() {
        var clock = new ManualClock();
        var alice = await this._server.RegisterAsync("Alice Lookups");
        var bob = await this._server.RegisterAsync("Bob Lookups");
        var carol = await this._server.RegisterAsync("Carol Lookups", options: this._server.Options(time: clock));
        var channelId = await alice.Session.CreateChannelAsync("Lookups", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        var epoch = carol.Session.Snapshot.FindChannel(channelId)!.Epoch;
        int Lookups() => carol.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" GetIdentities"));

        // Messages "from Bob" with bad signatures: his key hasn't changed, so each is dropped,
        // and only the first makes Carol check his identity again.
        async Task SendForgedAsync(int count, string barrier) {
            for (var i = 0; i < count; i++) {
                var forged = bob.ForgeMessage(channelId, epoch, $"forged {barrier} {i}", clock.GetUtcNow());
                forged.Signature = ByteString.CopyFrom(new byte[64]);
                this._server.Registry.Send(carol.UserId, new Event { ChatMessage = forged });
            }

            // Messages are handled in order, so once this arrives the forgeries have been too.
            await alice.Session.SendTextAsync(channelId, barrier, Ct);
            await WaitFor(() => carol.Messages.FirstOrDefault(m => m.Text == barrier));
        }

        var before = Lookups();
        await SendForgedAsync(5, "first barrier");
        Assert.Equal(before + 1, Lookups());
        Assert.Equal(5, carol.Notices.Count(n => n.Text.Contains("failed signature")));

        // A minute later, one more check is allowed.
        clock.Offset = TimeSpan.FromMinutes(2);
        await SendForgedAsync(3, "second barrier");
        Assert.Equal(before + 2, Lookups());
        Assert.Equal(8, carol.Notices.Count(n => n.Text.Contains("failed signature")));
        Assert.DoesNotContain(carol.Messages, m => m.Text?.StartsWith("forged") == true);
        Assert.DoesNotContain(carol.Notices, n => n.Text.Contains("identity key changed"));
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
                MyKey = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, current - 1, PositionOf(alice, channelId), bobKeys, bob.UserId, alice.UserId, aliceAgreement),
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
    public async Task JunkMessagesCannotFlushTheSeenSet() {
        var alice = await this._server.RegisterAsync("Alice Flush");
        var bob = await this._server.RegisterAsync("Bob Flush");
        var channelId = await alice.Session.CreateChannelAsync("Flush", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        var genuine = bob.ForgeMessage(channelId, epoch, "only once", DateTimeOffset.UtcNow);
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = genuine });
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "only once"));

        // More junk than the seen-set holds, each with a fresh ID. In batches, so the server's
        // per-connection send queue never overflows.
        const string failed = "failed signature or decryption";
        for (var sent = 0; sent < 2100;) {
            for (var i = 0; i < 200 && sent < 2100; i++, sent++) {
                this._server.Registry.Send(alice.UserId, new Event {
                    ChatMessage = new ChatMessage {
                        ChannelId = channelId, Epoch = epoch, SenderId = bob.UserId,
                        MessageId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), TimestampUnixMs = genuine.TimestampUnixMs,
                        Ciphertext = ByteString.CopyFrom(new byte[64]), Signature = ByteString.CopyFrom(new byte[64]),
                    },
                });
            }

            var expected = sent;
            await WaitFor(() => alice.Notices.Count(n => n.Text.Contains(failed)) >= expected ? new object() : null);
        }

        // The genuine message is replayed; a fresh one after it shows the replay was handled.
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = genuine });
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = bob.ForgeMessage(channelId, epoch, "barrier", DateTimeOffset.UtcNow) });
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "barrier"));
        Assert.Single(alice.Messages, m => m.Text == "only once");
    }

    [Fact]
    public async Task ReplayOlderThanNewestAcceptedIsDroppedAfterRestart() {
        var alice = await this._server.RegisterAsync("Alice Restart Replay");
        var bob = await this._server.RegisterAsync("Bob Restart Replay");
        var channelId = await alice.Session.CreateChannelAsync("Replay Restart", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // A message Bob wrote five minutes ago, never delivered, is captured by the server.
        var captured = bob.ForgeMessage(channelId, epoch, "written earlier", DateTimeOffset.UtcNow.AddMinutes(-5));
        await bob.Session.SendTextAsync(channelId, "latest", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "latest"));

        // After a restart the seen-set is empty, but the newest timestamp from Bob was saved.
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RestartAsync(alice);
        this._server.Registry.Send(aliceAgain.UserId, new Event { ChatMessage = captured });

        await WaitFor(() => aliceAgain.Notices.FirstOrDefault(n => n.Text.Contains("older than messages already received")));
        Assert.DoesNotContain(aliceAgain.Messages, m => m.Text == "written earlier");

        // Slightly out-of-order messages (within two minutes) are still fine.
        this._server.Registry.Send(aliceAgain.UserId, new Event { ChatMessage = bob.ForgeMessage(channelId, epoch, "a bit late", DateTimeOffset.UtcNow.AddSeconds(-30)) });
        await WaitFor(() => aliceAgain.Messages.FirstOrDefault(m => m.Text == "a bit late"));
    }

    [Fact]
    public async Task ReplayStateIsNotSavedForEveryMessage() {
        var store = new CountingSecretStore();
        var alice = await this._server.RegisterAsync("Alice Saves", store);
        var bob = await this._server.RegisterAsync("Bob Saves");
        var channelId = await alice.Session.CreateChannelAsync("Saves", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        // The first message may flush; after that, ten messages must not mean ten writes.
        this._server.Registry.Send(alice.UserId, new Event { ChatMessage = bob.ForgeMessage(channelId, epoch, "first", DateTimeOffset.UtcNow) });
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "first"));
        var before = store.Saves;
        for (var i = 0; i < 10; i++) {
            this._server.Registry.Send(alice.UserId, new Event { ChatMessage = bob.ForgeMessage(channelId, epoch, $"message {i}", DateTimeOffset.UtcNow.AddMilliseconds(i)) });
        }

        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == "message 9"));
        // At most one unrelated save (say, a background identity fetch) may slip in.
        Assert.True(store.Saves - before <= 1, $"{store.Saves - before} saves for 10 messages");

        // It is saved with the next save (here, on shutdown).
        await alice.Session.DisposeAsync();
        Assert.Contains(bob.UserId, store.Load().NewestMessageTimes[channelId].Keys);
    }

    [Fact]
    public async Task OlderEpochIsOnlyAcceptedBrieflyAfterARekey() {
        var clock = new ManualClock();
        var alice = await this._server.RegisterAsync("Alice Grace");
        var bob = await this._server.RegisterAsync("Bob Grace", options: this._server.Options(time: clock));
        var channelId = await alice.Session.CreateChannelAsync("Grace", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var oldEpoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: var e } c && e > oldEpoch ? c : null);

        // Sent just before the rekey and delivered just after: fine.
        this._server.Registry.Send(bob.UserId, new Event { ChatMessage = alice.ForgeMessage(channelId, oldEpoch, "in flight", clock.GetUtcNow()) });
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "in flight"));

        // Three minutes later the old key is still held, but no longer good for new messages.
        clock.Offset = TimeSpan.FromMinutes(3);
        this._server.Registry.Send(bob.UserId, new Event { ChatMessage = alice.ForgeMessage(channelId, oldEpoch, "much later", clock.GetUtcNow()) });
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains("older key that was replaced")));
        Assert.DoesNotContain(bob.Messages, m => m.Text == "much later");
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
        var olderEpoch = ChannelCrypto.EncryptName("Original Name", alice.LoadEpochKey(channelId, 0), channelId, 0, PositionOf(alice, channelId), aliceKeys, alice.UserId);
        var olderRevision = ChannelCrypto.EncryptName("Second Name", alice.LoadEpochKey(channelId, epoch), channelId, epoch, PositionOf(alice, channelId), aliceKeys, alice.UserId, revision: 1);
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
        var olderRevision = ChannelCrypto.EncryptName("Second Name", alice.LoadEpochKey(channelId, epoch), channelId, epoch, PositionOf(alice, channelId), aliceKeys, alice.UserId, revision: 1);
        this.StoreName(channelId, olderRevision);

        // ...and Bob restarts, so only what he persisted protects him.
        await bob.Session.DisposeAsync();
        var bobAgain = await this._server.RestartAsync(bob);
        Assert.NotEqual("Second Name", bobAgain.Session.Snapshot.FindChannel(channelId)!.Name);

        // A name from an older epoch, listed to a client that is still running, is refused too.
        this.StoreName(channelId, ChannelCrypto.EncryptName("Original Name", alice.LoadEpochKey(channelId, 0), channelId, 0, PositionOf(alice, channelId), aliceKeys, alice.UserId));
        await alice.Session.RefreshAsync(Ct);
        Assert.Equal("Third Name", alice.Session.Snapshot.FindChannel(channelId)!.Name);
    }

    [Fact]
    public async Task FakeRemovalDoesNotResetReplayProtection() {
        var alice = await this._server.RegisterAsync("Alice Removal");
        var bob = await this._server.RegisterAsync("Bob Removal");
        var channelId = await alice.Session.CreateChannelAsync("Original Name", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await alice.Session.RenameAsync(channelId, "Second", Ct);
        await alice.Session.RenameAsync(channelId, "Third", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Third" ? new object() : null);

        // A message Alice wrote earlier is captured; Bob has already seen a newer one from her.
        var captured = alice.ForgeMessage(channelId, epoch, "written earlier", DateTimeOffset.UtcNow.AddMinutes(-5));
        await alice.Session.SendTextAsync(channelId, "latest", Ct);
        await WaitFor(() => bob.Messages.FirstOrDefault(m => m.Text == "latest"));

        // The server tells Bob he was removed, rolls its stored name back, and lists the channel again.
        await this._server.SendAndSettleAsync(bob, new Event { ChannelRemoved = new ChannelRemoved { ChannelId = channelId, Reason = RemovalReason.Kicked } });
        Assert.Null(bob.Session.Snapshot.FindChannel(channelId));
        using var aliceKeys = alice.LoadIdentity();
        var second = ChannelCrypto.EncryptName("Second", alice.LoadEpochKey(channelId, epoch), channelId, epoch, PositionOf(alice, channelId), aliceKeys, alice.UserId, revision: 1);
        this.StoreName(channelId, second);
        await bob.Session.RefreshAsync(Ct);
        await this._server.SendAndSettleAsync(bob, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = second } });

        var relisted = bob.Session.Snapshot.FindChannel(channelId)!;
        Assert.True(relisted.HasKey);
        Assert.NotEqual("Second", relisted.Name);

        this._server.Registry.Send(bob.UserId, new Event { ChatMessage = captured });
        await WaitFor(() => bob.Notices.FirstOrDefault(n => n.Text.Contains("older than messages already received")));
        Assert.DoesNotContain(bob.Messages, m => m.Text == "written earlier");

        // A genuinely newer rename still gets through.
        await alice.Session.RenameAsync(channelId, "Fourth", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "Fourth" ? new object() : null);
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
                MyKey = ChannelCrypto.SealEpochKey(ChannelCrypto.NewEpochKey(), channelId, 1, PositionOf(alice, channelId), aliceKeys, alice.UserId, carol.UserId, carolAgreement),
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

    /// <summary>
    /// Replaces "UnusableAgreementKeyFromTheServerIsRejectedAndNamedInTheRekeyError". In 0.1 a rekey sealed
    /// to whatever agreement key the server returned for a member, so a server handing out an all-zero
    /// key could stall the channel, and the error had to name the member. Now a rekey seals to the
    /// keys in the signed log, which the server can't change, and an invite naming an unusable key
    /// can't enter the log at all. The identity is still rejected, and the rekey still names any
    /// member whose key can't be sealed to (unreachable through the log's rules).
    /// </summary>
    [Fact]
    public async Task UnusableAgreementKeyFromTheServerIsRejectedAndCannotReachARekey() {
        var alice = await this._server.RegisterAsync("Alice Zero");
        var bob = await this._server.RegisterAsync("Bob Zero");
        var carol = await this._server.RegisterAsync("Carol Zero");
        var channelId = await alice.Session.CreateChannelAsync("Zero", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var bobsFingerprint = bob.Session.Snapshot.MyFingerprint;

        // The server hands out all-zero agreement keys for Bob and Carol, validly bound to their signing keys.
        foreach (var user in new[] { bob, carol }) {
            using var keys = user.LoadIdentity();
            var bundle = CryptoTests.BundleWithAgreementKey(keys, new byte[32]);
            this._server.ExecuteSql("UPDATE users SET agreement_key = $key, binding_signature = $signature WHERE user_id = $id;",
                ("$key", bundle.AgreementPublicKey.ToByteArray()), ("$signature", bundle.BindingSignature.ToByteArray()), ("$id", user.UserId));
        }

        // Alice restarts, so she only has what GetIdentities returns now.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await alice.Session.DisposeAsync();
        var aliceAgain = await this._server.RestartAsync(alice, this._server.Options(log: (_, text) => log.Enqueue(text)));
        Assert.Contains(log, line => line.Contains($"Rejected an invalid identity for user {bob.UserId}"));
        Assert.Equal(bobsFingerprint, aliceAgain.Session.Snapshot.FindChannel(channelId)!.Members.Single(m => m.User.UserId == bob.UserId).Fingerprint);

        // Rekeying still seals to Bob's key from the log, so he still gets the new key.
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;
        await aliceAgain.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Epoch: var e } c && e > epoch ? c : null);

        // Carol can't be invited under the unusable key, by Alice's client or by a forged entry.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => aliceAgain.Session.InviteAsync(channelId, carol.Name, ProtocolInfo.DebugWorldName, Ct));
        Assert.Contains("invalid identity key", refused.Message);
        using var carolKeys = carol.LoadIdentity();
        var forged = this._server.ForgeEntry(channelId, aliceAgain, MembershipEntryKind.Invite, carol.UserId, new MemberKeys(carolKeys.SigningPublicKey, new byte[32]));
        using var aliceKeys = aliceAgain.LoadIdentity();
        var (sealedName, signature) = ChannelCrypto.SealInvite("Zero", channelId, carol.UserId, carolKeys.AgreementPublicKey, aliceKeys, aliceAgain.UserId);
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => aliceAgain.Session.SendRawAsync(new ClientFrame {
            InviteMember = new InviteMember { ChannelId = channelId, Entry = forged, SealedName = sealedName, Signature = ByteString.CopyFrom(signature) },
        }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Null(this._server.Database.GetInvite(channelId, carol.UserId));
    }

    private void StoreName(string channelId, EncryptedName name) {
        this._server.ExecuteSql("""
            UPDATE channels SET name_epoch = $epoch, name_revision = $revision, name_author = $author,
                name_ciphertext = $ciphertext, name_signature = $signature, name_log_seq = $logSeq, name_log_hash = $logHash
            WHERE channel_id = $id;
            """,
            ("$id", channelId), ("$epoch", (long) name.Epoch), ("$revision", (long) name.Revision), ("$author", name.AuthorId),
            ("$ciphertext", name.Ciphertext.ToByteArray()), ("$signature", name.Signature.ToByteArray()),
            ("$logSeq", (long) name.LogPosition.Seq), ("$logHash", name.LogPosition.Hash.ToByteArray()));
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
