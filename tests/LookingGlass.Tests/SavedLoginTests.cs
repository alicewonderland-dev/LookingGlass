using LookingGlass.Core.Client;
using LookingGlass.Core.Debug;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// A saved login the server doesn't recognise (the wrong server, or one reset or restored from a backup) is kept
/// and tried again, so it works once the right server is back; only registering again, or "Forget account", replaces it.
/// </summary>
public sealed class SavedLoginTests : IAsyncLifetime {
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
    public async Task ALoginTheServerDoesNotRecogniseIsKept() {
        var alice = await this._server.RegisterAsync("Alice Kept");
        var token = TokenOf(alice);
        Assert.NotNull(token);

        this.HideDevices(alice.UserId);
        alice.Session.Reconnect();

        var snapshot = await WaitFor(() => alice.Session.Snapshot is { State: ConnectionState.LoginNotRecognized } s ? s : null);
        Assert.True(snapshot.LoginRejected);
        Assert.Contains("This server doesn't recognise your login", snapshot.StatusText);
        Assert.Contains("register again", snapshot.StatusText);
        Assert.Equal(token, TokenOf(alice));

        // It keeps trying, and keeping the login, while the server still refuses it.
        var tries = AuthenticateCount(alice);
        await WaitFor(() => AuthenticateCount(alice) >= tries + 2 ? new object() : null);
        Assert.Equal(ConnectionState.LoginNotRecognized, alice.Session.Snapshot.State);
        Assert.Equal(token, TokenOf(alice));
    }

    [Fact]
    public async Task ASavedLoginWorksAgainOnceTheServerKnowsItAgain() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Restored", store);
        var userId = alice.UserId;
        var channelId = await alice.Session.CreateChannelAsync("Still Mine", Ct);
        var token = TokenOf(alice);
        await alice.Session.DisposeAsync();

        // The server loses the login (say, it runs on the wrong database for a while), and the plugin starts.
        this.HideDevices(userId);
        var restarted = this._server.StartClient(alice.Name, store);
        await WaitFor(() => restarted.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);
        Assert.Equal(token, TokenOf(restarted));

        // The server gets it back: the saved login works again, without registering again.
        this.RestoreDevices();
        var snapshot = await WaitFor(() => restarted.Session.Snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true } s ? s : null);
        Assert.Equal(userId, snapshot.Me!.UserId);
        Assert.False(snapshot.LoginRejected);
        Assert.Equal("Still Mine", snapshot.FindChannel(channelId)?.Name);
        Assert.Equal(token, TokenOf(restarted));
        Assert.DoesNotContain(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" StartRegistration"));
        // Tried again on the same connection.
        Assert.Single(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));
    }

    [Fact]
    public async Task RetryNowTriesTheSavedLoginAtOnce() {
        var store = new InMemorySecretStore();
        var alice = await this._server.RegisterAsync("Alice Retry", store);
        var userId = alice.UserId;
        await alice.Session.DisposeAsync();

        this.HideDevices(userId);
        // Not tried again by itself during the test.
        var restarted = this._server.StartClient(alice.Name, store, this._server.Options(loginRetryDelay: TimeSpan.FromHours(1)));
        await WaitFor(() => restarted.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Session.RetryLoginAsync(Ct));
        Assert.Contains("doesn't recognise your login", refused.Message);
        Assert.Equal(ConnectionState.LoginNotRecognized, restarted.Session.Snapshot.State);

        this.RestoreDevices();
        await restarted.Session.RetryLoginAsync(Ct);
        Assert.Equal(ConnectionState.Ready, restarted.Session.Snapshot.State);
        Assert.Equal(userId, restarted.Session.Snapshot.Me!.UserId);
        Assert.Single(restarted.Session.GetTrace(), entry => entry.Outgoing && entry.Summary.EndsWith(" Hello"));
    }

    [Fact]
    public async Task RegisteringAgainReplacesALoginTheServerDoesNotRecognise() {
        var alice = await this._server.RegisterAsync("Alice Again");
        var old = TokenOf(alice);
        this.HideDevices(alice.UserId);
        alice.Session.Reconnect();
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);

        var challenge = await alice.Session.StartRegistrationAsync(new Character { Name = alice.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        Assert.True(challenge.VerificationSkipped);
        Assert.True(alice.Session.Snapshot is { State: ConnectionState.Registering, LoginRejected: true });

        // The old login isn't tried while the registration is under way.
        var tries = AuthenticateCount(alice);
        await Task.Delay(1000, Ct);
        Assert.Equal(tries, AuthenticateCount(alice));

        await alice.Session.CompleteRegistrationAsync(Ct);
        Assert.True(alice.Session.Snapshot is { State: ConnectionState.Ready, LoginRejected: false });
        var token = TokenOf(alice);
        Assert.NotNull(token);
        Assert.NotEqual(old, token);

        // Nothing tries the old login afterwards, or undoes the new one.
        var after = AuthenticateCount(alice);
        await Task.Delay(1000, Ct);
        Assert.Equal(after, AuthenticateCount(alice));
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        Assert.Equal(token, TokenOf(alice));

        // And the new login is what the next session uses.
        await alice.Session.DisposeAsync();
        var restarted = await this._server.RestartAsync(alice);
        Assert.Equal(alice.Session.Snapshot.Me!.UserId, restarted.UserId);
    }

    [Fact]
    public async Task RegisteringAgainWaitsForARetryUnderWay() {
        var store = new InMemorySecretStore();
        var first = await this._server.RegisterAsync("Alice Race", store);
        var userId = first.UserId;
        var old = TokenOf(first);
        await first.Session.DisposeAsync();

        this.HideDevices(userId);
        HoldingWebSocket? socket = null;
        var alice = this._server.StartClient(first.Name, store, this._server.Options(wrap: inner => {
            var holding = new HoldingWebSocket(inner);
            Volatile.Write(ref socket, holding);
            return holding;
        }));
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);

        // A retry of the old login is on its way when the user starts registering again.
        var held = Volatile.Read(ref socket)!;
        held.HoldNext(ClientFrame.BodyOneofCase.Authenticate);
        await held.Held.WaitAsync(Harness.Timeout, Ct);
        var registering = alice.Session.StartRegistrationAsync(new Character { Name = first.Name, WorldName = ProtocolInfo.DebugWorldName }, Ct);
        await Task.Delay(300, Ct);
        Assert.False(registering.IsCompleted);

        // It is answered first; then the registration goes ahead, and its login is the one kept.
        held.Release();
        await registering.WaitAsync(Harness.Timeout, Ct);
        await alice.Session.CompleteRegistrationAsync(Ct);
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        var token = TokenOf(alice);
        Assert.NotEqual(old, token);
        await Task.Delay(500, Ct);
        Assert.Equal(ConnectionState.Ready, alice.Session.Snapshot.State);
        Assert.Equal(token, TokenOf(alice));
    }

    [Fact]
    public async Task ForgettingTheAccountClearsTheSavedLogin() {
        var alice = await this._server.RegisterAsync("Alice Forgets");
        alice.Session.ForgetAccount();

        var snapshot = await WaitFor(() => alice.Session.Snapshot is { State: ConnectionState.Unregistered } s ? s : null);
        Assert.False(snapshot.LoginRejected);
        Assert.Null(TokenOf(alice));
        Assert.Null(alice.Store.Load().UserId);
    }

    [Fact]
    public async Task ForgettingTheAccountClearsALoginTheServerDoesNotRecognise() {
        var alice = await this._server.RegisterAsync("Alice Forgets Again");
        this.HideDevices(alice.UserId);
        alice.Session.Reconnect();
        await WaitFor(() => alice.Session.Snapshot.State == ConnectionState.LoginNotRecognized ? new object() : null);

        alice.Session.ForgetAccount();
        var snapshot = await WaitFor(() => alice.Session.Snapshot is { State: ConnectionState.Unregistered } s ? s : null);
        Assert.False(snapshot.LoginRejected);
        Assert.Null(TokenOf(alice));

        // Nothing brings it back.
        await Task.Delay(500, Ct);
        Assert.Equal(ConnectionState.Unregistered, alice.Session.Snapshot.State);
        Assert.Null(TokenOf(alice));
    }

    [Fact]
    public async Task EchoBotRegistersAgainOnAServerThatDoesNotKnowIt() {
        var store = new InMemorySecretStore();
        var bot = new EchoBot(this._server.Options(), store, "Echo Forgotten");
        this._server.Track(bot);
        bot.Start();
        await bot.WaitUntilReadyAsync(Harness.Timeout);
        var old = store.Load().DeviceToken;

        this.HideDevices(bot.Session.Snapshot.Me!.UserId);
        bot.Session.Reconnect();
        await WaitFor(() => store.Load().DeviceToken is { } token && token != old && bot.Session.Snapshot.State == ConnectionState.Ready ? token : null);

        // And it answers as before.
        var alice = await this._server.RegisterAsync("Alice Echo Again");
        var channelId = await alice.Session.CreateChannelAsync("Echo Again", Ct);
        await alice.Session.InviteAsync(channelId, "Echo Forgotten", ProtocolInfo.DebugWorldName, Ct);
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId)?.Members.FirstOrDefault(m => m.User.Name == "Echo Forgotten" && m.Rank == Rank.Member));
        await WaitFor(() => alice.Session.Snapshot.FindChannel(channelId) is { RekeyPending: false } c ? c : null);
        await alice.Session.SendTextAsync(channelId, "still there?", Ct);
        await WaitFor(() => alice.Messages.FirstOrDefault(m => m.Text == EchoBot.EchoPrefix + "still there?"));
    }

    private static string? TokenOf(TestClient client) => client.Store.Load().DeviceToken;

    private static int AuthenticateCount(TestClient client) =>
        client.Session.GetTrace().Count(entry => entry.Outgoing && entry.Summary.EndsWith(" Authenticate"));

    /// <summary>
    /// The server forgets a user's logins, and doesn't know their identity key either, so signing in with the key
    /// fails too (as on the wrong database), keeping a copy for <see cref="RestoreDevices"/>. A server that only lost
    /// the logins is signed back into with the key; see <see cref="KeyLoginTests"/>.
    /// </summary>
    private void HideDevices(long userId) {
        this._server.ExecuteSql("""
            CREATE TABLE IF NOT EXISTS hidden_devices AS SELECT * FROM devices WHERE 0;
            INSERT INTO hidden_devices SELECT * FROM devices WHERE user_id = $id;
            DELETE FROM devices WHERE user_id = $id;
            CREATE TABLE IF NOT EXISTS hidden_keys AS SELECT user_id, signing_key FROM users WHERE 0;
            INSERT INTO hidden_keys SELECT user_id, signing_key FROM users WHERE user_id = $id;
            UPDATE users SET signing_key = randomblob(32) WHERE user_id = $id;
            """, ("$id", userId));
    }

    /// <summary>The server knows the hidden logins and keys again (as when the right database is back).</summary>
    private void RestoreDevices() {
        this._server.ExecuteSql("""
            INSERT INTO devices SELECT * FROM hidden_devices;
            DELETE FROM hidden_devices;
            UPDATE users SET signing_key = (SELECT signing_key FROM hidden_keys WHERE hidden_keys.user_id = users.user_id)
            WHERE user_id IN (SELECT user_id FROM hidden_keys);
            DELETE FROM hidden_keys;
            """);
    }
}
