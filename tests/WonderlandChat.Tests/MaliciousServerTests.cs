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
    public async Task OldOrHugeEpochsCannotWedgeTheChannel() {
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

        await alice.Session.RenameAsync(channelId, "New Name", Ct);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId)?.Name == "New Name" ? new object() : null);

        // Replay the epoch-0 name, which is validly signed by Alice but from an older epoch.
        using var aliceKeys = alice.LoadIdentity();
        var oldName = ChannelCrypto.EncryptName("Original Name", alice.LoadEpochKey(channelId, 0), channelId, 0, aliceKeys, alice.UserId);
        this._server.Registry.Send(bob.UserId, new Event { ChannelRenamed = new ChannelRenamed { ChannelId = channelId, Name = oldName } });

        await Task.Delay(300, Ct);
        Assert.Equal("New Name", bob.Session.Snapshot.FindChannel(channelId)!.Name);
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
