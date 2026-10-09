namespace LookingGlass.Core.Client;

/// <summary>What the plugin knows, each frame, about whether a session should be started by itself.</summary>
/// <param name="Enabled">"Connect automatically" is on.</param>
/// <param name="LoggedIn">A character is logged in.</param>
/// <param name="HasSession">A session exists (connecting, connected, reconnecting or registering).</param>
/// <param name="Starting">A start is on its way (waiting for the last session's final save).</param>
/// <param name="StoppedByPlayer">The player pressed Disconnect, and hasn't connected or logged in again since.</param>
/// <param name="GaveUp">Starting failed by itself (the keys couldn't be read), and was said: no retrying until the player acts.</param>
/// <param name="LastTry">When it last started a session by itself, if ever.</param>
public sealed record AutoConnectState(bool Enabled, bool LoggedIn, bool HasSession, bool Starting, bool StoppedByPlayer, bool GaveUp,
    DateTimeOffset? LastTry);

/// <summary>
/// Connecting by itself: not only when the character logs in, but whenever a logged-in player with "Connect automatically"
/// on has no session, however that came about (a plugin update or reload, a start that was lost). Checked every frame; it
/// starts one at most every <see cref="RetryEvery"/>, never after the player pressed Disconnect, and never again after a
/// start that failed (that is said once, and waits for the player).
/// </summary>
public static class AutoConnect {
    /// <summary>The least time between two starts made by itself.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    public static bool ShouldStart(AutoConnectState state, DateTimeOffset now) =>
        state is { Enabled: true, LoggedIn: true, HasSession: false, Starting: false, StoppedByPlayer: false, GaveUp: false }
        && (state.LastTry is not { } last || now - last >= RetryEvery);
}
