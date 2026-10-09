using LookingGlass.Core.Client;
using Xunit;

namespace LookingGlass.Tests;

public sealed class AutoConnectTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static bool Start(bool enabled = true, bool loggedIn = true, bool hasSession = false, bool starting = false,
        bool stoppedByPlayer = false, bool gaveUp = false, DateTimeOffset? lastTry = null) =>
        AutoConnect.ShouldStart(new AutoConnectState(enabled, loggedIn, hasSession, starting, stoppedByPlayer, gaveUp, lastTry), Now);

    [Fact]
    public void LoggedInWithNoSessionStartsOne() {
        // The owner saw a plugin update leave a logged-in player on "Not connected" with Connect automatically on: whatever
        // the way there, no session while it should have one starts one.
        Assert.True(Start());
    }

    [Fact]
    public void NothingStartsWhenItShouldnt() {
        Assert.False(Start(enabled: false));
        Assert.False(Start(loggedIn: false));
        Assert.False(Start(hasSession: true));
        // A start already on its way (waiting for the last session's final save).
        Assert.False(Start(starting: true));
        // The player pressed Disconnect: until they connect again or log in again.
        Assert.False(Start(stoppedByPlayer: true));
        // Starting failed (the keys couldn't be read): said once, not every 30 seconds.
        Assert.False(Start(gaveUp: true));
    }

    [Fact]
    public void ItTriesAtMostEveryThirtySeconds() {
        Assert.False(Start(lastTry: Now - TimeSpan.FromSeconds(29)));
        Assert.True(Start(lastTry: Now - AutoConnect.RetryEvery));
        Assert.True(Start(lastTry: Now - TimeSpan.FromMinutes(5)));
    }
}
