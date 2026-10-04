using System.Collections.Concurrent;
using System.Collections.Immutable;
using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>The plugin's numbered channel commands must keep pointing at the same channel across restarts.</summary>
public sealed class CommandSlotTests {
    [Fact]
    public void SlotsSurviveARestartAndAreOnlyFreedWhenTheirChannelIsGone() {
        var slots = new Dictionary<string, int> { ["aaa"] = 1, ["bbb"] = 2, ["ccc"] = 3 };
        var saved = new Dictionary<string, int>(slots);

        // What a restart publishes before the list is in: Ready with no channels, then some of them.
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false), 8));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false, "ccc", "bbb"), 8));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Reconnecting, true, "ccc"), 8));
        Assert.Equal(saved, slots);

        // The complete list, in another order and with a new channel: only the new one is given a slot.
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "bbb", "aaa"), 8));
        Assert.Equal(new Dictionary<string, int> { ["aaa"] = 1, ["bbb"] = 2, ["ccc"] = 3, ["ddd"] = 4 }, slots);

        // Leaving a channel frees its slot and moves nothing else; the next new channel gets it.
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "aaa"), 8));
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "eee", "ddd", "ccc", "aaa"), 8));
        Assert.Equal(new Dictionary<string, int> { ["aaa"] = 1, ["eee"] = 2, ["ccc"] = 3, ["ddd"] = 4 }, slots);

        // Moving a channel by hand swaps it with whatever held that slot.
        CommandSlots.Assign(slots, "ddd", 1);
        Assert.Equal(1, slots["ddd"]);
        Assert.Equal(4, slots["aaa"]);
    }

    [Fact]
    public async Task ChannelsLoadedIsOnlySetOnceTheListIsIn() {
        await using var server = new Harness();
        try {
            var alice = await server.RegisterAsync("Alice Slots");
            var first = await alice.Session.CreateChannelAsync("First", Ct);
            var second = await alice.Session.CreateChannelAsync("Second", Ct);
            await alice.Session.DisposeAsync();

            // Every snapshot a restarted session publishes, from the first one on.
            var snapshots = new ConcurrentQueue<SessionSnapshot>();
            var session = new ClientSession(server.Options(), alice.Store);
            server.Track(session);
            session.SnapshotChanged += snapshots.Enqueue;
            session.Start();
            await WaitFor(() => session.Snapshot.ChannelsLoaded ? new object() : null);

            // The session is Ready before it has the list, but never claims a list without both channels.
            Assert.Contains(snapshots, snapshot => snapshot is { State: ConnectionState.Ready, ChannelsLoaded: false });
            Assert.All(snapshots.Where(snapshot => snapshot.ChannelsLoaded), snapshot => {
                Assert.Equal(ConnectionState.Ready, snapshot.State);
                Assert.NotNull(snapshot.FindChannel(first));
                Assert.NotNull(snapshot.FindChannel(second));
            });

            // A reconnect starts over.
            snapshots.Clear();
            session.Reconnect();
            await WaitFor(() => snapshots.Any(snapshot => snapshot.State != ConnectionState.Ready) && session.Snapshot.ChannelsLoaded ? new object() : null);
            Assert.All(snapshots.Where(snapshot => snapshot.State != ConnectionState.Ready), snapshot => Assert.False(snapshot.ChannelsLoaded));
            Assert.NotNull(session.Snapshot.FindChannel(first));
        } finally {
            DeleteDirectory(server.DataDirectory);
        }
    }

    private static SessionSnapshot Snapshot(ConnectionState state, bool loaded, params string[] channelIds) {
        return SessionSnapshot.Empty with {
            State = state,
            ChannelsLoaded = loaded,
            Channels = channelIds.Select(id => new ChannelView(id, null, 0, 0, false, false, Rank.Member, [])).ToImmutableArray(),
        };
    }
}
