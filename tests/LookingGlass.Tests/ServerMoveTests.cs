using System.Collections.Concurrent;
using System.Net.WebSockets;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// Identities are kept per server address, so a server reached at a new address (another name, or TLS added) would
/// otherwise mean registering again with new keys and losing every channel. When the server at the old, trusted address
/// lists the new one among its own, the identity is copied to the new address; a new address's own claims never count.
/// </summary>
public sealed class ServerMoveTests : IDisposable {
    private const ulong ContentId = 0x0040_0000_0000_0042;
    // In-process clients reach a test server at http://localhost/ws whatever they're given; this is its old address.
    private const string OldUrl = "ws://localhost/ws";
    private const string NewUrl = "wss://chat-new.example/ws";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lgt-move-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => DeleteDirectory(this._directory);

    private ServerBoundSecretStore Store(string url) => ServerSecretFiles.Open(this._directory, ContentId, url, path => new FileSecretStore(path));

    private static Harness Server(params string[] publicUrls) {
        return new Harness(settings: publicUrls.Select((url, i) => ($"LookingGlass:PublicUrls:{i}", url)).ToArray());
    }

    [Fact]
    public async Task TheServerListsItsPublicUrlsInWelcome() {
        await using var listed = Server(OldUrl, NewUrl);
        await using var unlisted = new Harness();
        try {
            Assert.Equal([OldUrl, NewUrl], (await ServerMove.CheckAsync(OldUrl, NewUrl, listed.ConnectAsync, Ct)).ListedUrls);
            // A Development server without PublicUrls lists nothing (its Host-header fallback isn't an address it vouches for).
            Assert.Empty((await ServerMove.CheckAsync(OldUrl, NewUrl, unlisted.ConnectAsync, Ct)).ListedUrls);
        } finally {
            DeleteDirectory(listed.DataDirectory);
            DeleteDirectory(unlisted.DataDirectory);
        }
    }

    /// <summary>
    /// The old server vouches for the new address, the identity is copied, and the client is straight back in on the new
    /// address: same account and key, its channels and their keys, no registering. Switching back works too.
    /// </summary>
    [Fact]
    public async Task AVouchedMoveKeepsTheIdentityAndChannels() {
        await using var server = Server(OldUrl, NewUrl);
        try {
            var oldStore = this.Store(OldUrl);
            var alice = await server.RegisterAsync("Alice Moving", oldStore, server.Options(serverUri: new Uri(OldUrl)));
            var bob = await server.RegisterAsync("Bob Staying");
            var channelId = await alice.Session.CreateChannelAsync("Moves Along", Ct);
            await AddMemberAsync(alice, channelId, bob);
            var fingerprint = alice.Session.Snapshot.MyFingerprint;
            await alice.Session.DisposeAsync();

            var check = await ServerMove.CheckAsync(OldUrl, NewUrl, server.ConnectAsync, Ct);
            Assert.Equal(ServerMoveVerdict.SameServer, check.Verdict);
            var newStore = this.Store(NewUrl);
            ServerMove.CopyIdentity(check, oldStore, newStore);
            Assert.Equal(ServerSecretFiles.NormaliseUrl(NewUrl), new FileSecretStore(Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, NewUrl))).Load().ServerUrl);

            var moved = server.StartClient(alice.Name, newStore, server.Options(serverUri: new Uri(NewUrl)));
            var snapshot = await WaitFor(() => moved.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } s ? s : null);
            Assert.Equal(alice.UserId, snapshot.Me!.UserId);
            Assert.Equal(fingerprint, snapshot.MyFingerprint);
            await WaitFor(() => moved.Session.Snapshot.FindChannel(channelId) is { HasKey: true, Name: "Moves Along", MembershipWarning: null } c ? c : null);
            Assert.DoesNotContain(moved.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartRegistration"));
            await moved.Session.DisposeAsync();

            // The old address's identity is kept: switching back works.
            var back = server.StartClient(alice.Name, oldStore, server.Options(serverUri: new Uri(OldUrl)));
            await WaitFor(() => back.Session.Snapshot.FindChannel(channelId) is { HasKey: true } c ? c : null);
            Assert.Equal(alice.UserId, back.Session.Snapshot.Me!.UserId);
            await back.Session.DisposeAsync();

            // A key login on the new address names it, and the server takes it, since it lists that address.
            server.ExecuteSql("DELETE FROM devices WHERE user_id = $id;", ("$id", alice.UserId));
            var keyed = server.StartClient(alice.Name, newStore, server.Options(serverUri: new Uri(NewUrl)));
            await WaitFor(() => keyed.Session.Snapshot.State == ConnectionState.Ready ? new object() : null);
            Assert.Contains(keyed.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" CompleteKeyLogin"));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>
    /// A new server that claims (in its own Welcome) to be the old one gets nothing: only the old server's word counts,
    /// asked over the old address. The new server isn't even contacted, and a client pointed at it starts from nothing.
    /// </summary>
    [Fact]
    public async Task ANewServerClaimingToBeTheOldOneGetsNothing() {
        const string evilUrl = "wss://evil.example/ws";
        await using var honest = Server(OldUrl);
        await using var evil = Server(OldUrl, evilUrl);
        try {
            var oldStore = this.Store(OldUrl);
            var alice = await honest.RegisterAsync("Alice Wary", oldStore, honest.Options(serverUri: new Uri(OldUrl)));
            await alice.Session.DisposeAsync();

            var dialled = new ConcurrentQueue<Uri>();
            var check = await ServerMove.CheckAsync(OldUrl, evilUrl, (uri, ct) => {
                dialled.Enqueue(uri);
                return honest.ConnectAsync(uri, ct);
            }, Ct);
            Assert.Equal(ServerMoveVerdict.NotListed, check.Verdict);
            Assert.Equal([new Uri(OldUrl)], dialled.ToArray());
            Assert.Contains("evil.example", check.Message);

            var newStore = this.Store(evilUrl);
            Assert.Throws<InvalidOperationException>(() => ServerMove.CopyIdentity(check, oldStore, newStore));
            Assert.False(File.Exists(Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, evilUrl))));

            // Pointed at it anyway (as a different server), the client has no login to give it, and other keys.
            var fresh = evil.StartClient(alice.Name, newStore, evil.Options(serverUri: new Uri(evilUrl)));
            await WaitFor(() => fresh.Session.Snapshot.State == ConnectionState.Unregistered ? new object() : null);
            Assert.DoesNotContain(fresh.Session.GetTrace(), entry => entry.Outgoing && (entry.Summary.EndsWith(" Authenticate") || entry.Summary.EndsWith(" StartKeyLogin")));
            await fresh.Session.StartRegistrationAsync(new Character { Name = "Alice Wary", WorldName = ProtocolInfo.DebugWorldName }, Ct);
            Assert.NotEqual(alice.Keys(), fresh.Keys());
        } finally {
            DeleteDirectory(honest.DataDirectory);
            DeleteDirectory(evil.DataDirectory);
        }
    }

    /// <summary>
    /// The old server's word alone isn't enough either: a malicious (or compromised) old server could list an honest
    /// server's address to have its users carry their identity key there, then relay that server's key login challenges
    /// to them (stopped by PublicUrls, but not on a Development server going by the Host header) and link the two
    /// identities. The server at the new address must list the old address too: both sides agree, or nothing is copied.
    /// </summary>
    [Fact]
    public async Task AnOldServerCannotPushTheIdentityOntoAServerThatDoesNotListIt() {
        const string honestUrl = "wss://honest.example/ws";
        await using var malicious = Server(OldUrl, honestUrl);
        await using var honest = Server(honestUrl);
        try {
            var oldStore = this.Store(OldUrl);
            var alice = await malicious.RegisterAsync("Alice Pushed", oldStore, malicious.Options(serverUri: new Uri(OldUrl)));
            await alice.Session.DisposeAsync();

            var dialled = new ConcurrentQueue<Uri>();
            var check = await ServerMove.CheckAsync(OldUrl, honestUrl, (uri, ct) => {
                dialled.Enqueue(uri);
                return uri.Host == "localhost" ? malicious.ConnectAsync(uri, ct) : honest.ConnectAsync(uri, ct);
            }, Ct);
            Assert.Equal("NotConfirmed", check.Verdict.ToString());
            Assert.Equal([new Uri(OldUrl), new Uri(honestUrl)], dialled.ToArray());
            Assert.Contains("honest.example", check.Message);

            var newStore = this.Store(honestUrl);
            Assert.Throws<InvalidOperationException>(() => ServerMove.CopyIdentity(check, oldStore, newStore));
            Assert.False(File.Exists(Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, honestUrl))));
        } finally {
            DeleteDirectory(malicious.DataDirectory);
            DeleteDirectory(honest.DataDirectory);
        }
    }

    [Fact]
    public async Task AnUnreachableNewAddressMeansNoCopy() {
        await using var server = Server(OldUrl, NewUrl);
        try {
            var check = await ServerMove.CheckAsync(OldUrl, NewUrl, (uri, ct) => uri.Host == "localhost"
                ? server.ConnectAsync(uri, ct)
                : Task.FromException<WebSocket>(new WebSocketException("No such host is known")), Ct);
            Assert.Equal(ServerMoveVerdict.Unreachable, check.Verdict);
            Assert.Contains(NewUrl, check.Message);
            Assert.Contains("No such host", check.Message);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    [Fact]
    public async Task AnUnreachableOldServerMeansNoCopy() {
        var oldStore = this.Store(OldUrl);
        await using (var server = Server(OldUrl, NewUrl)) {
            var alice = await server.RegisterAsync("Alice Stranded", oldStore, server.Options(serverUri: new Uri(OldUrl)));
            await alice.Session.DisposeAsync();
            DeleteDirectory(server.DataDirectory);
        }

        var check = await ServerMove.CheckAsync(OldUrl, NewUrl,
            (_, _) => Task.FromException<WebSocket>(new WebSocketException("Unable to connect to the remote server")), Ct);
        Assert.Equal(ServerMoveVerdict.Unreachable, check.Verdict);
        Assert.Contains(OldUrl, check.Message);
        Assert.Contains("Unable to connect", check.Message);

        var newStore = this.Store(NewUrl);
        Assert.Throws<InvalidOperationException>(() => ServerMove.CopyIdentity(check, oldStore, newStore));
        Assert.False(File.Exists(Path.Combine(this._directory, ServerSecretFiles.FileName(ContentId, NewUrl))));
    }

    [Fact]
    public async Task AServerThatListsNoAddressesCannotVouch() {
        await using var server = new Harness();
        try {
            var check = await ServerMove.CheckAsync(OldUrl, NewUrl, server.ConnectAsync, Ct);
            Assert.Equal(ServerMoveVerdict.NothingListed, check.Verdict);
            Assert.Contains("PublicUrls", check.Message);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>Addresses are compared by origin: another path on a listed origin is the same server; another port isn't.</summary>
    [Theory]
    [InlineData("wss://CHAT-NEW.example:443/other", ServerMoveVerdict.SameServer)]
    [InlineData("wss://chat-new.example:8443/ws", ServerMoveVerdict.NotListed)]
    [InlineData("ws://chat-new.example/ws", ServerMoveVerdict.NotListed)]
    [InlineData("wss://chat-new.example.evil.example/ws", ServerMoveVerdict.NotListed)]
    [InlineData("https://chat-new.example/ws", ServerMoveVerdict.InvalidAddress)]
    [InlineData("chat-new.example", ServerMoveVerdict.InvalidAddress)]
    public async Task AddressesAreComparedByOrigin(string newUrl, ServerMoveVerdict verdict) {
        await using var server = Server(OldUrl, NewUrl);
        try {
            Assert.Equal(verdict, (await ServerMove.CheckAsync(OldUrl, newUrl, server.ConnectAsync, Ct)).Verdict);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    /// <summary>A copy never replaces an identity the new address already has, and a check only covers its own move.</summary>
    [Fact]
    public async Task ACopyNeverOverwritesAndOnlyCoversItsOwnMove() {
        await using var server = Server(OldUrl, NewUrl, "wss://third.example/ws");
        try {
            var oldStore = this.Store(OldUrl);
            var alice = await server.RegisterAsync("Alice Careful", oldStore, server.Options(serverUri: new Uri(OldUrl)));
            await alice.Session.DisposeAsync();
            var check = await ServerMove.CheckAsync(OldUrl, NewUrl, server.ConnectAsync, Ct);

            // Vouched for NewUrl, not for the third address.
            Assert.Throws<InvalidOperationException>(() => ServerMove.CopyIdentity(check, oldStore, this.Store("wss://third.example/ws")));

            // The new address has an identity of its own already: kept.
            var newStore = this.Store(NewUrl);
            var existing = new ClientSecrets { DeviceToken = "theirs" };
            newStore.Save(existing);
            Assert.Throws<InvalidOperationException>(() => ServerMove.CopyIdentity(check, oldStore, newStore));
            Assert.Equal("theirs", newStore.Load().DeviceToken);
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }
}
