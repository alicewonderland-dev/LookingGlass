using Google.Protobuf;
using LookingGlass.Core.Client;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;
using LookingGlass.Server.Realtime;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// An identity key belongs to whoever holds its private key. Anyone sharing a channel with a user can fetch their public
/// identity bundle, binding signature and all, so registering a key must prove holding it: otherwise someone could
/// register another user's key as theirs, then register again with new keys and so have the server retire the key the
/// other user still uses.
/// </summary>
public sealed class KeyPossessionTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    private string Url => this._server.ServerUri.AbsoluteUri;

    // ================================================================ proof of possession

    /// <summary>
    /// The reviewer's exploit: Mallory registers her own character with Alice's public identity bundle, then registers
    /// it again with new keys, which retires the key it replaced. Registering Alice's key is refused, so nothing of
    /// Alice's changes: her login, key login and registering again with her key all keep working.
    /// </summary>
    [Fact]
    public async Task RegisteringSomeoneElsesPublicKeyIsRefused() {
        var alice = await this._server.RegisterAsync("Alice Targeted");
        var token = alice.Store.Load().DeviceToken!;
        using var aliceKeys = alice.LoadIdentity();
        // What Mallory can fetch once she shares a channel with Alice (GetIdentities).
        var published = this._server.Database.GetUser(alice.UserId)!.ToIdentity().Identity;
        const string mallory = "Mallory Copycat";

        await using var raw = await this._server.ConnectRawAsync();
        var start = await raw.SendAsync(Start(mallory, published));
        if (start.RegistrationChallenge is { } challenge) {
            // She can't sign with Alice's key: she can only leave the signature out, or sign with her own.
            using var malloryKeys = IdentityKeys.Generate();
            foreach (var signature in new[] { [], RegistrationProof.Sign(malloryKeys, challenge.Nonce.Span, challenge.LodestoneId, this.Url) }) {
                var complete = await raw.SendAsync(Complete(this.Url, signature));
                Assert.Equal(ErrorCode.RegistrationFailed, complete.Error?.Code);
            }
        } else {
            Assert.Equal(ErrorCode.RegistrationFailed, start.Error?.Code);
        }

        Assert.Null(this._server.Database.GetUser(RequestHandler.DebugUserId(mallory)));

        // The exploit's second step: Mallory registers her character with keys of her own. That changes nothing of Alice's.
        await using var again = await this._server.ConnectRawAsync();
        using var ownKeys = IdentityKeys.Generate();
        Assert.NotNull((await this.RegisterRawAsync(again, mallory, ownKeys)).RegistrationComplete);

        await using var check = await this._server.ConnectRawAsync();
        Assert.NotNull((await check.SendAsync(new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } })).AuthenticateOk);
        await using var keyLogin = await this._server.ConnectRawAsync();
        Assert.NotNull((await KeyLoginAsync(keyLogin, aliceKeys, alice.UserId, this.Url)).KeyLoginComplete);
        await using var register = await this._server.ConnectRawAsync();
        Assert.NotNull((await this.RegisterRawAsync(register, alice.Name, aliceKeys)).RegistrationComplete);
    }

    /// <summary>
    /// Completing a registration takes a signature by the key being registered, over this connection's challenge, the
    /// account and the address the client connected to. Anything else is refused and registers nothing; an older plugin,
    /// which sends no signature, is asked to update.
    /// </summary>
    [Fact]
    public async Task RegisteringNeedsASignatureByTheKeyBeingRegistered() {
        using var keys = IdentityKeys.Generate();
        using var otherKeys = IdentityKeys.Generate();
        await using var raw = await this._server.ConnectRawAsync();
        var challenge = (await raw.SendAsync(Start("Proof Needed", keys.ToBundle()))).RegistrationChallenge!;
        Assert.Equal(RegistrationProof.NonceSize, challenge.Nonce.Length);
        var nonce = challenge.Nonce.ToByteArray();
        var id = challenge.LodestoneId;

        // Another connection's challenge, for the same key and account.
        await using var elsewhere = await this._server.ConnectRawAsync();
        var otherNonce = (await elsewhere.SendAsync(Start("Proof Needed", keys.ToBundle()))).RegistrationChallenge!.Nonce.ToByteArray();
        Assert.NotEqual(nonce, otherNonce);

        var old = await raw.SendAsync(new ClientFrame { CompleteRegistration = new CompleteRegistration() });
        Assert.Equal(ErrorCode.RegistrationFailed, old.Error?.Code);
        Assert.Contains("update the plugin", old.Error!.Message);

        foreach (var signature in new[] {
                     RegistrationProof.Sign(otherKeys, nonce, id, this.Url),
                     RegistrationProof.Sign(keys, otherNonce, id, this.Url),
                     RegistrationProof.Sign(keys, nonce, id + 1, this.Url),
                     // Signed for another address, sent with this one: the signature doesn't cover it.
                     RegistrationProof.Sign(keys, nonce, id, "wss://evil.example/ws"),
                     keys.Sign(KeyLoginProof.Payload(nonce, id, this.Url)),
                     new byte[64],
                 }) {
            var refused = await raw.SendAsync(Complete(this.Url, signature));
            Assert.Equal(ErrorCode.RegistrationFailed, refused.Error?.Code);
        }

        Assert.Null(this._server.Database.GetUser(id));

        // Signed as an honest client signs: registered, with that key.
        var done = await raw.SendAsync(Complete(this.Url, RegistrationProof.Sign(keys, nonce, id, this.Url)));
        Assert.NotNull(done.RegistrationComplete);
        Assert.Equal(keys.SigningPublicKey, this._server.Database.GetUser(id)!.SigningKey);
    }

    [Fact]
    public void TheRegistrationSignatureCoversTheChallengeUserAndAddress() {
        using var keys = IdentityKeys.Generate();
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(RegistrationProof.NonceSize);
        const string url = "wss://chat.example.com/ws";
        var signature = RegistrationProof.Sign(keys, nonce, 1234, url);

        Assert.True(RegistrationProof.Verify(keys.SigningPublicKey, nonce, 1234, url, signature));
        Assert.False(RegistrationProof.Verify(keys.SigningPublicKey, System.Security.Cryptography.RandomNumberGenerator.GetBytes(RegistrationProof.NonceSize), 1234, url, signature));
        Assert.False(RegistrationProof.Verify(keys.SigningPublicKey, nonce, 1235, url, signature));
        Assert.False(RegistrationProof.Verify(keys.SigningPublicKey, nonce, 1234, "wss://chat.example.org/ws", signature));
        Assert.False(RegistrationProof.Verify(keys.SigningPublicKey, nonce[..16], 1234, url, keys.Sign(RegistrationProof.Payload(nonce[..16], 1234, url))));
        using var other = IdentityKeys.Generate();
        Assert.False(RegistrationProof.Verify(other.SigningPublicKey, nonce, 1234, url, signature));
        // Its own domain: a key login signature over the same fields doesn't count.
        Assert.False(RegistrationProof.Verify(keys.SigningPublicKey, nonce, 1234, url, KeyLoginProof.Sign(keys, nonce, 1234, url)));
    }

    // ================================================================ helpers

    private static ClientFrame Start(string name, IdentityBundle identity) => new() {
        StartRegistration = new StartRegistration { Character = new Character { Name = name, WorldName = ProtocolInfo.DebugWorldName }, Identity = identity },
    };

    private static ClientFrame Complete(string url, byte[] signature) => new() {
        CompleteRegistration = new CompleteRegistration { ServerUrl = url, Signature = ByteString.CopyFrom(signature) },
    };

    /// <summary>Registers a debug account with <paramref name="keys"/> as an honest client does.</summary>
    private async Task<Response> RegisterRawAsync(RawConnection raw, string name, IdentityKeys keys) {
        var start = await raw.SendAsync(Start(name, keys.ToBundle()));
        if (start.RegistrationChallenge is not { } challenge) {
            return start;
        }

        return await raw.SendAsync(Complete(this.Url, RegistrationProof.Sign(keys, challenge.Nonce.Span, challenge.LodestoneId, this.Url)));
    }

    private static async Task<Response> KeyLoginAsync(RawConnection raw, IdentityKeys keys, long userId, string url) {
        var challenge = (await raw.SendAsync(new ClientFrame { StartKeyLogin = new StartKeyLogin { UserId = userId } })).KeyLoginChallenge!.Challenge.ToByteArray();
        return await raw.SendAsync(new ClientFrame {
            CompleteKeyLogin = new CompleteKeyLogin {
                Challenge = ByteString.CopyFrom(challenge),
                ServerUrl = url,
                Signature = ByteString.CopyFrom(KeyLoginProof.Sign(keys, challenge, userId, url)),
            },
        });
    }
}
