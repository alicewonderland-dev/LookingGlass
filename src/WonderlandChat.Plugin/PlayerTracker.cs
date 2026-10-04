using Dalamud.Plugin.Services;

namespace WonderlandChat.Plugin;

public sealed record PlayerInfo(ulong ContentId, string Name, uint HomeWorldId, string HomeWorldName);

/// <summary>
/// Reads the logged-in character on the framework thread (the only thread
/// allowed to touch game state) and publishes an immutable copy.
/// </summary>
public sealed class PlayerTracker : IDisposable {
    private volatile PlayerInfo? _current;

    public PlayerTracker() {
        Services.Framework.Update += this.OnUpdate;
    }

    /// <summary>The current character, readable from any thread.</summary>
    public PlayerInfo? Current => this._current;

    /// <summary>Raised on the framework thread when the character logs in, out, or changes.</summary>
    public event Action<PlayerInfo?>? Changed;

    private void OnUpdate(IFramework framework) {
        PlayerInfo? info = null;
        var state = Services.PlayerState;
        if (Services.ClientState.IsLoggedIn && state.IsLoaded && state.ContentId != 0) {
            var world = state.HomeWorld;
            info = new PlayerInfo(state.ContentId, state.CharacterName, world.RowId, world.Value.Name.ExtractText());
        }

        if (info == this._current) {
            return;
        }

        this._current = info;
        try {
            this.Changed?.Invoke(info);
        } catch (Exception ex) {
            Services.Log.Error(ex, "Error handling character change");
        }
    }

    public void Dispose() {
        Services.Framework.Update -= this.OnUpdate;
    }
}
