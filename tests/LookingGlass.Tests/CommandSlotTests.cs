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
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false), CommandSlots.Count));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false, "ccc", "bbb"), CommandSlots.Count));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Reconnecting, true, "ccc"), CommandSlots.Count));
        Assert.Equal(saved, slots);

        // The complete list, in another order and with a new channel: only the new one is given a slot.
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "bbb", "aaa"), CommandSlots.Count));
        Assert.Equal(new Dictionary<string, int> { ["aaa"] = 1, ["bbb"] = 2, ["ccc"] = 3, ["ddd"] = 4 }, slots);

        // Leaving a channel frees its slot and moves nothing else; the next new channel gets it.
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "ddd", "ccc", "aaa"), CommandSlots.Count));
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, "eee", "ddd", "ccc", "aaa"), CommandSlots.Count));
        Assert.Equal(new Dictionary<string, int> { ["aaa"] = 1, ["eee"] = 2, ["ccc"] = 3, ["ddd"] = 4 }, slots);

        // Moving a channel by hand swaps it with whatever held that slot.
        CommandSlots.Assign(slots, "ddd", 1);
        Assert.Equal(1, slots["ddd"]);
        Assert.Equal(4, slots["aaa"]);
    }

    [Fact]
    public void ThereIsASlotForEveryChannelTheServerAllows() {
        Assert.Equal(50, CommandSlots.Count);
        Assert.Equal((int) ProtocolInfo.DefaultLimits().MaxChannelsPerUser, CommandSlots.Count);
    }

    [Fact]
    public void FiftyChannelsGetFiftyStableSlots() {
        var ids = Enumerable.Range(1, 51).Select(i => $"ch{i:00}").ToArray();
        var slots = new Dictionary<string, int>();

        // The first 50 each get their own slot, 1 to 50, in list order; a 51st (over the server's limit) gets none.
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, ids), CommandSlots.Count));
        Assert.Equal(50, slots.Count);
        Assert.Equal(Enumerable.Range(1, 50), slots.Values.Order());
        for (var i = 0; i < 50; i++) {
            Assert.Equal(i + 1, slots[ids[i]]);
            Assert.Equal(ids[i], CommandSlots.ChannelIn(slots, i + 1));
        }

        Assert.False(slots.ContainsKey(ids[50]));
        var saved = new Dictionary<string, int>(slots);

        // A restart: partial and stale lists change nothing; the complete list in another order changes nothing either.
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false), CommandSlots.Count));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, false, ids[..10]), CommandSlots.Count));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Reconnecting, true, ids[40..]), CommandSlots.Count));
        Assert.False(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, ids[..50].Reverse().ToArray()), CommandSlots.Count));
        Assert.Equal(saved, slots);

        // Slot 50 is a real slot: moving a channel there swaps, like any other.
        CommandSlots.Assign(slots, ids[0], 50);
        Assert.Equal(50, slots[ids[0]]);
        Assert.Equal(1, slots[ids[49]]);

        // Leaving frees exactly that slot, which the next channel without one then gets.
        var remaining = ids.Where(id => id != ids[20]).ToArray();
        Assert.True(CommandSlots.Sync(slots, Snapshot(ConnectionState.Ready, true, remaining), CommandSlots.Count));
        Assert.Equal(21, slots[ids[50]]);
        Assert.False(slots.ContainsKey(ids[20]));
        Assert.Equal(50, slots.Count);
    }

    [Fact]
    public void AnUnassignedSlotCommandSaysSo() {
        var slots = new Dictionary<string, int> { ["aaa"] = 1, ["bbb"] = 50 };

        Assert.Equal(new ChannelCommand.Send("bbb", "hello there"), ChannelCommand.ForSlot(slots, 50, "  hello there "));
        Assert.Null(CommandSlots.ChannelIn(slots, 2));
        Assert.Equal(new ChannelCommand.NotFound("No channel is on /lgc2. Assign one in the main window (/lg)."), ChannelCommand.ForSlot(slots, 2, "hello"));
        Assert.Equal(new ChannelCommand.NotFound("No channel is on /lgc49. Assign one in the main window (/lg)."), ChannelCommand.ForSlot(slots, 49, "hello"));

        // No message: usage, whether or not the slot has a channel.
        Assert.Equal(new ChannelCommand.Usage("Usage: /lgc1 <message>"), ChannelCommand.ForSlot(slots, 1, "   "));
        Assert.Equal(new ChannelCommand.Usage("Usage: /lgc2 <message>"), ChannelCommand.ForSlot(slots, 2, ""));
    }

    [Theory]
    [InlineData("/lgc1", 1)]
    [InlineData("/lgc9", 9)]
    [InlineData("/lgc50", 50)]
    [InlineData("/lgc", null)]
    [InlineData("/lgc0", null)]
    [InlineData("/lgc51", null)]
    [InlineData("/lgcx", null)]
    [InlineData("/lookingglass", null)]
    public void SlotCommandsAreParsed(string command, int? expected) {
        Assert.Equal(expected, CommandSlots.SlotOfCommand(command));
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

    internal static SessionSnapshot Snapshot(ConnectionState state, bool loaded, params string[] channelIds) {
        return SessionSnapshot.Empty with {
            State = state,
            ChannelsLoaded = loaded,
            Channels = channelIds.Select(id => new ChannelView(id, null, 0, 0, false, false, Rank.Member, [])).ToImmutableArray(),
        };
    }
}
