using WonderlandChat.Core.Client;
using WonderlandChat.Protocol;
using static WonderlandChat.Tests.Harness;

namespace WonderlandChat.Tests;

/// <summary>
/// Authenticated membership (design doc, "Authenticated membership (v0.2, revised)"): the
/// server plays a malicious part, and clients must work out the members from the signed log.
/// </summary>
public sealed class MembershipLogTests : IAsyncLifetime {
    private Harness _server = null!;

    public ValueTask InitializeAsync() {
        this._server = new Harness();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await this._server.DisposeAsync();
        DeleteDirectory(this._server.DataDirectory);
    }

    /// <summary>Acceptance test 1.</summary>
    [Fact]
    public async Task ServerInsertedGhostNeverReceivesAKey() {
        var alice = await this._server.RegisterAsync("Alice Ghostly");
        var bob = await this._server.RegisterAsync("Bob Ghostly");
        var ghost = await this._server.RegisterAsync("Ghost Inserted");
        var channelId = await alice.Session.CreateChannelAsync("Haunted", Ct);
        await AddMemberAsync(alice, channelId, bob);

        // The server adds the ghost to its member list, with no invite or accept from anyone.
        this._server.ExecuteSql("INSERT INTO members (channel_id, user_id, rank, joined_at) VALUES ($channel, $ghost, 2, 0);",
            ("$channel", channelId), ("$ghost", ghost.UserId));
        await alice.Session.RefreshAsync(Ct);

        // Alice rekeys. A server that insists on the ghost may refuse her rekey, but it never gets the ghost a key.
        try {
            await alice.Session.RekeyAsync(channelId, Ct, force: true);
        } catch (Exception ex) when (ex is InvalidOperationException or ServerErrorException) {
            // Refused: the channel stalls, which a malicious server can always do.
        }

        Assert.Empty(this._server.Database.GetEpochKeys(channelId, ghost.UserId, 0));
        Assert.False(ghost.Store.Load().EpochKeys.ContainsKey(channelId));
        Assert.DoesNotContain(alice.Session.Snapshot.FindChannel(channelId)!.Members, m => m.User.UserId == ghost.UserId);
    }
}
