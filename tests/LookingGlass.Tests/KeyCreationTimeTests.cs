using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The time every new epoch key states it was made (SealedEpochKey.created_unix_ms): its signed payload, how the server
/// checks it (the author's, the same in every copy, near its clock), that clients keep it from keys however they come, and
/// how message catch-up uses it (a slow clock within the allowance loses nothing; one far out is refused and told).
/// </summary>
public sealed class KeyCreationTimeTests : IAsyncLifetime {
    private const string ChannelId = "0123456789abcdef0123456789abcdef";
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
    }

    /// <summary>A field as the signed payloads write it: its length (4 bytes, big-endian), then its bytes.</summary>
    private static IEnumerable<byte> Field(byte[] value) {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        return length.Concat(value);
    }

    private static byte[] Long(long value) {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>The payload is fixed by the protocol: its domain, then channel, epoch, author, commitment, position and time.</summary>
    [Fact]
    public void TheCreationTimesSignedPayloadIsFixed() {
        Assert.Equal("lookingglass/epoch-key-created/v1", Domains.EpochKeyCreated);
        var commitment = Enumerable.Range(0, 32).Select(i => (byte) i).ToArray();
        var position = new LogPosition { Seq = 9, Hash = ByteString.CopyFrom(Enumerable.Repeat((byte) 0xAB, 32).ToArray()) };

        var expected = Field(Encoding.UTF8.GetBytes("lookingglass/epoch-key-created/v1"))
            .Concat(Field(Encoding.UTF8.GetBytes(ChannelId)))
            .Concat(Field(Long(3)))
            .Concat(Field(Long(-42)))
            .Concat(Field(commitment))
            .Concat(Field(Long(1))).Concat(Field(Long(9))).Concat(Field(position.Hash.ToByteArray()))
            .Concat(Field(Long(1_790_000_000_123)))
            .ToArray();
        Assert.Equal(expected, ChannelCrypto.KeyCreatedPayload(ChannelId, 3, -42, commitment, position, 1_790_000_000_123));

        using var author = IdentityKeys.Generate();
        var key = new SealedEpochKey {
            KeyCommitment = ByteString.CopyFrom(commitment), LogPosition = position, CreatedUnixMs = 1_790_000_000_123,
            CreatedSignature = ByteString.CopyFrom(ChannelCrypto.SignKeyCreated(ChannelId, 3, commitment, position, author, -42, 1_790_000_000_123)),
        };
        Assert.True(IdentityKeys.Verify(author.SigningPublicKey, expected, key.CreatedSignature.Span));
        Assert.Equal(1_790_000_000_123, ChannelCrypto.KeyCreatedAt(key, ChannelId, 3, -42, author.SigningPublicKey));
        Assert.Null(ChannelCrypto.KeyCreatedAt(key, ChannelId, 4, -42, author.SigningPublicKey));
        Assert.Null(ChannelCrypto.KeyCreatedAt(new SealedEpochKey(), ChannelId, 3, -42, author.SigningPublicKey));
    }

    /// <summary>A key received live keeps the time it says it was made, and who made it.</summary>
    [Fact]
    public async Task AKeyReceivedLiveKeepsItsCreationTime() {
        var alice = await this._server.RegisterAsync("Alice Live Key");
        var bob = await this._server.RegisterAsync("Bob Live Key");
        var channelId = await alice.Session.CreateChannelAsync("Live Key Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = bob.Session.Snapshot.FindChannel(channelId)!.Epoch;

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await alice.Session.RekeyAsync(channelId, Ct, force: true);
        await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { Epoch: var e } c && e == epoch + 1 ? c : null);
        await bob.Session.DisposeAsync();
        var held = bob.Store.Load().EpochKeyPositions[channelId][epoch + 1];
        Assert.InRange(held.CreatedMs, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(alice.UserId, held.CreatedBy);
    }

    /// <summary>
    /// A rekey whose copies state different times, each signed by the author, is refused: every copy must say the same.
    /// </summary>
    [Fact]
    public async Task EveryCopyOfAKeyMustStateTheSameTime() {
        var (alice, channelId, epoch, rekey) = await this.SetUpRekeyAsync("Same Time");
        var differing = rekey(copies => {
            var other = copies[1];
            other.CreatedUnixMs -= 1000;
            other.CreatedSignature = ByteString.CopyFrom(ChannelCrypto.SignKeyCreated(channelId, epoch + 1, other.KeyCommitment.Span, other.LogPosition, alice.Keys, alice.Client.UserId,
                other.CreatedUnixMs));
        });
        Assert.All(differing.Keys, copy => Assert.NotNull(ChannelCrypto.KeyCreatedAt(copy, channelId, epoch + 1, alice.Client.UserId, alice.Keys.SigningPublicKey)));
        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Client.Session.SendRawAsync(new ClientFrame { SubmitRekey = differing }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Equal(epoch, this._server.Database.GetChannel(channelId)!.Epoch);
    }

    /// <summary>A key stating a time more than 10 minutes from the server's clock is refused, with words that tell its maker why.</summary>
    [Fact]
    public async Task AKeyStatingATimeFarFromTheServersClockIsRefused() {
        var (alice, channelId, epoch, rekey) = await this.SetUpRekeyAsync("Far Clock");
        foreach (var off in new[] { TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(-11) }) {
            var skewed = rekey(copies => {
                var at = DateTimeOffset.UtcNow.Add(off).ToUnixTimeMilliseconds();
                var signature = ByteString.CopyFrom(ChannelCrypto.SignKeyCreated(channelId, epoch + 1, copies[0].KeyCommitment.Span, copies[0].LogPosition, alice.Keys,
                    alice.Client.UserId, at));
                copies.ForEach(copy => (copy.CreatedUnixMs, copy.CreatedSignature) = (at, signature));
            });
            var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Client.Session.SendRawAsync(new ClientFrame { SubmitRekey = skewed }, Ct));
            Assert.Equal(ErrorCode.InvalidRequest, error.Code);
            Assert.Contains("clock is more than 10 minutes off", error.ServerMessage);
        }

        // Within the allowance it is taken.
        var near = rekey(copies => {
            var at = DateTimeOffset.UtcNow.AddMinutes(-9).ToUnixTimeMilliseconds();
            var signature = ByteString.CopyFrom(ChannelCrypto.SignKeyCreated(channelId, epoch + 1, copies[0].KeyCommitment.Span, copies[0].LogPosition, alice.Keys,
                alice.Client.UserId, at));
            copies.ForEach(copy => (copy.CreatedUnixMs, copy.CreatedSignature) = (at, signature));
        });
        Assert.NotNull((await alice.Client.Session.SendRawAsync(new ClientFrame { SubmitRekey = near }, Ct)).Ack);
    }

    /// <summary>A new channel's first key must state its time signed by its creator, if it states one.</summary>
    [Fact]
    public async Task ANewChannelsKeyMustBeDatedByItsCreator() {
        var alice = await this._server.RegisterAsync("Alice New Channel Key");
        using var keys = alice.LoadIdentity();
        CreateChannel Request(Action<SealedEpochKey> tamper) {
            var channelId = Guid.NewGuid().ToString("N");
            var genesis = Core.Membership.SignedLogMembershipProvider.Instance.CreateGenesis(channelId, keys, alice.UserId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var membership = Core.Membership.SignedLogMembershipProvider.Instance.Empty(channelId).Apply(genesis);
            var key = ChannelCrypto.NewEpochKey();
            var creatorKey = SealedEpochKeyProvider.Instance.SealToMembers(key, channelId, 0, membership.Head!, membership.Members, keys, alice.UserId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Keys.Single();
            tamper(creatorKey);
            return new CreateChannel {
                ChannelId = channelId, Genesis = genesis, CreatorKey = creatorKey,
                Name = ChannelCrypto.EncryptName("Dated", key, channelId, 0, membership.Head!, keys, alice.UserId),
            };
        }

        var error = await Assert.ThrowsAsync<ServerErrorException>(() => alice.Session.SendRawAsync(new ClientFrame { CreateChannel = Request(key => key.CreatedUnixMs += 1) }, Ct));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.NotNull((await alice.Session.SendRawAsync(new ClientFrame { CreateChannel = Request(_ => { }) }, Ct)).Channel);
    }

    /// <summary>
    /// A member whose clock is 5 minutes slow makes the key after a join: a message sent just before the join still reaches
    /// someone away throughout (the key's time counts give or take as much as a live message's).
    /// </summary>
    [Fact]
    public async Task ASlowClockMakingTheNextKeyLosesNothing() {
        var alice = await this._server.RegisterAsync("Alice Slow Clock");
        var bob = await this._server.RegisterAsync("Bob Away Slow");
        var carol = await this._server.RegisterAsync("Carol Talks Slow");
        var dave = await this._server.RegisterAsync("Dave Joins Slow");
        var channelId = await alice.Session.CreateChannelAsync("Slow Clock Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        await AddMemberAsync(alice, channelId, carol);
        await bob.Session.DisposeAsync();
        await alice.Session.DisposeAsync();
        var slow = this._server.StartClient(alice.Name, alice.Store, this._server.Options(time: new ManualClock { Offset = TimeSpan.FromMinutes(-5) }));
        await WaitFor(() => slow.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } ? new object() : null);

        await carol.Session.SendTextAsync(channelId, "just before dave joins", Ct);
        await carol.Session.DisposeAsync();
        await AddMemberAsync(slow, channelId, dave);

        var back = this._server.StartClient(bob.Name, bob.Store);
        await WaitFor(() => back.Session.CatchUpsDone > 0 ? new object() : null);
        await this._server.SendAndSettleAsync(back);
        Assert.Equal(["just before dave joins"], back.Messages.Select(m => m.Text));
    }

    private sealed record Rekeyer(TestClient Client, IdentityKeys Keys);

    /// <summary>A channel of Alice's with Bob, and a way to make (and tamper with) a rekey of it, as Alice could.</summary>
    private async Task<(Rekeyer Alice, string ChannelId, ulong Epoch, Func<Action<List<SealedEpochKey>>, SubmitRekey> Rekey)> SetUpRekeyAsync(string name) {
        var alice = await this._server.RegisterAsync($"Alice {name}");
        var bob = await this._server.RegisterAsync($"Bob {name}");
        var channelId = await alice.Session.CreateChannelAsync($"{name} Channel", Ct);
        await AddMemberAsync(alice, channelId, bob);
        var epoch = this._server.Database.GetChannel(channelId)!.Epoch;
        var keys = alice.LoadIdentity();
        var membership = alice.Session.MembershipForTests(channelId);
        SubmitRekey Rekey(Action<List<SealedEpochKey>> tamper) {
            var key = ChannelCrypto.NewEpochKey();
            var sealedKeys = SealedEpochKeyProvider.Instance.SealToMembers(key, channelId, epoch + 1, membership.Head!, membership.Members, keys, alice.UserId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var copies = sealedKeys.Keys.Select(copy => copy.Clone()).ToList();
            tamper(copies);
            var request = new SubmitRekey {
                ChannelId = channelId, NewEpoch = epoch + 1, KeyCommitment = sealedKeys.KeyCommitment, LogPosition = membership.Head!.Clone(),
                Name = ChannelCrypto.EncryptName($"{name} Channel", key, channelId, epoch + 1, membership.Head!, keys, alice.UserId),
            };
            request.Keys.AddRange(copies);
            return request;
        }

        return (new Rekeyer(alice, keys), channelId, epoch, Rekey);
    }
}
