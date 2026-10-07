using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>
/// The plugin's memory of whom a "Name@World" was looked up as (see <see cref="ClientSession.InviteAsync(string, string, string, CancellationToken)"/>):
/// reused for 10 minutes, only while the session's identity for them still has that name, and bounded.
/// </summary>
public sealed class LookupMemoryTests {
    private static UserIdentity Identity(long userId, string name, string world) => new() { User = new User { UserId = userId, Name = name, WorldName = world } };

    [Fact]
    public void ALookupIsReusedForTenMinutesInAnyCase() {
        var clock = new ManualClock();
        var memory = new LookupMemory(clock);
        var bob = Identity(7, "Bob Hatter", "Lich");
        memory.Remember("Bob Hatter@Lich", 7);
        Assert.Same(bob, memory.Recall("bob hatter@LICH", id => id == 7 ? bob : null));

        clock.Offset += LookupMemory.ReusedFor - TimeSpan.FromSeconds(1);
        Assert.Same(bob, memory.Recall("Bob Hatter@Lich", _ => bob));
        clock.Offset += TimeSpan.FromSeconds(1);
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => bob));
        Assert.Equal(0, memory.Count);
    }

    /// <summary>Only while the session still knows them by that name (a character renamed is looked up again), and has them at all.</summary>
    [Fact]
    public void ALookupIsReusedOnlyForWhoStillHasThatName() {
        var memory = new LookupMemory(TimeProvider.System);
        memory.Remember("Bob Hatter@Lich", 7);
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => Identity(7, "Robert Hatter", "Lich")));
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => Identity(7, "Bob Hatter", "Odin")));
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => null));
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => new UserIdentity()));
        Assert.NotNull(memory.Recall("Bob Hatter@Lich", _ => Identity(7, "Bob Hatter", "Lich")));
    }

    [Fact]
    public void AForgottenLookupIsntReused() {
        var memory = new LookupMemory(TimeProvider.System);
        memory.Remember("Bob Hatter@Lich", 7);
        memory.Forget("bob hatter@lich");
        Assert.Null(memory.Recall("Bob Hatter@Lich", _ => Identity(7, "Bob Hatter", "Lich")));
    }

    /// <summary>At most <see cref="LookupMemory.MaxRemembered"/>: past it, the expired go, and if none had, all.</summary>
    [Fact]
    public void LookupsRememberedAreBounded() {
        var clock = new ManualClock();
        var memory = new LookupMemory(clock);
        for (var i = 0; i < LookupMemory.MaxRemembered; i++) {
            memory.Remember($"Player {i}@Lich", i);
        }

        Assert.Equal(LookupMemory.MaxRemembered, memory.Count);
        memory.Remember("One More@Lich", 1000);
        Assert.Equal(1, memory.Count);

        // Some expired: only those go.
        clock.Offset += LookupMemory.ReusedFor;
        for (var i = 0; i < LookupMemory.MaxRemembered - 1; i++) {
            memory.Remember($"Fresh {i}@Lich", i);
        }

        memory.Remember("Last One@Lich", 2000);
        Assert.Equal(LookupMemory.MaxRemembered, memory.Count);
        Assert.NotNull(memory.Recall("Fresh 0@Lich", _ => Identity(0, "Fresh 0", "Lich")));
    }
}
