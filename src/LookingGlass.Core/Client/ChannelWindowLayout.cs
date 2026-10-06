namespace LookingGlass.Core.Client;

/// <summary>
/// A channel window as it is remembered (in the plugin's settings, per character and server address): its channels as
/// tabs, in order, the one selected, and where it was. Plain properties, so it saves as it is.
/// </summary>
public sealed class ChannelWindowLayout {
    /// <summary>Its own, for as long as it is open: what tells its window from the others (and ImGui's ID for it).</summary>
    public string Id { get; set; } = "";

    /// <summary>Channel IDs, in tab order. Never empty while the window is open: closing the last tab closes it.</summary>
    public List<string> Tabs { get; set; } = new();

    /// <summary>The tab selected: one of <see cref="Tabs"/>.</summary>
    public string? Selected { get; set; }

    /// <summary>Where it was on the screen, and how big, in pixels; null until it has been drawn.</summary>
    public WindowPlace? Place { get; set; }
}

/// <summary>A window's position and size, in screen pixels.</summary>
public sealed record WindowPlace(float X, float Y, float Width, float Height) {
    /// <summary>Within half a pixel: not worth saving again.</summary>
    public bool Near(WindowPlace? other) =>
        other != null && Math.Abs(this.X - other.X) < 0.5f && Math.Abs(this.Y - other.Y) < 0.5f
        && Math.Abs(this.Width - other.Width) < 0.5f && Math.Abs(this.Height - other.Height) < 0.5f;
}

/// <summary>
/// The rules for a character's channel windows on one server (see <see cref="ChannelWindowLayout"/>): opening one, adding,
/// closing and reordering tabs, and dropping channels the character is no longer in. No ImGui here: the plugin's windows
/// call these and draw what they say.
/// </summary>
public static class ChannelWindowLayouts {
    /// <summary>A new window with one channel as its only tab, added to <paramref name="windows"/>.</summary>
    public static ChannelWindowLayout Open(List<ChannelWindowLayout> windows, string channelId) {
        var layout = new ChannelWindowLayout { Id = NewId(windows), Tabs = [channelId], Selected = channelId };
        windows.Add(layout);
        return layout;
    }

    /// <summary>Adds a channel as the last tab and selects it; if the window has it already, only selects it.</summary>
    /// <returns>True if a tab was added.</returns>
    public static bool AddTab(ChannelWindowLayout window, string channelId) {
        window.Selected = channelId;
        if (window.Tabs.Contains(channelId)) {
            return false;
        }

        window.Tabs.Add(channelId);
        return true;
    }

    /// <summary>
    /// Closes a tab. If it was selected, the tab that took its place is (the one after it, or else the one before).
    /// </summary>
    /// <returns>True if that was the last tab: the window closes.</returns>
    public static bool CloseTab(ChannelWindowLayout window, string channelId) {
        var at = window.Tabs.IndexOf(channelId);
        if (at < 0) {
            return window.Tabs.Count == 0;
        }

        window.Tabs.RemoveAt(at);
        if (window.Selected == channelId) {
            window.Selected = window.Tabs.Count == 0 ? null : window.Tabs[Math.Min(at, window.Tabs.Count - 1)];
        }

        return window.Tabs.Count == 0;
    }

    /// <summary>
    /// The tabs in the order the window shows them now (the user dragged one). Only an order of exactly the same tabs is
    /// taken: anything else (a tab added or closed meanwhile) is ignored.
    /// </summary>
    /// <returns>True if the order changed.</returns>
    public static bool Reorder(ChannelWindowLayout window, IReadOnlyList<string> shown) {
        if (shown.Count != window.Tabs.Count || shown.SequenceEqual(window.Tabs)
            || !shown.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(window.Tabs.OrderBy(id => id, StringComparer.Ordinal))) {
            return false;
        }

        window.Tabs.Clear();
        window.Tabs.AddRange(shown);
        return true;
    }

    /// <summary>The windows that have a channel as a tab.</summary>
    public static IEnumerable<ChannelWindowLayout> Showing(IEnumerable<ChannelWindowLayout> windows, string channelId) =>
        windows.Where(window => window.Tabs.Contains(channelId));

    /// <summary>The channels of a list that a window doesn't have yet, in the list's order: what its "+" offers.</summary>
    public static IEnumerable<string> Addable(ChannelWindowLayout window, IEnumerable<string> channelIds) =>
        channelIds.Where(id => !window.Tabs.Contains(id));

    /// <summary>
    /// Tidies what was saved, and follows the channel list: drops tabs of channels the character is no longer in (only
    /// against the complete list, as with command slots), a tab twice, and windows left without tabs or an ID; a window's
    /// selected tab is one of its tabs. What a hand-edited settings file may hold (no window, no tabs) counts as nothing.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(List<ChannelWindowLayout> windows, SessionSnapshot snapshot) {
        var complete = snapshot is { State: ConnectionState.Ready, ChannelsLoaded: true };
        var listed = complete ? snapshot.Channels.Select(channel => channel.Id).ToHashSet() : null;
        var changed = windows.RemoveAll(window => window == null) > 0;
        var ids = new HashSet<string>();
        foreach (var window in windows.ToList()) {
            var saved = window.Tabs ?? [];
            var tabs = saved.Where(id => !string.IsNullOrEmpty(id) && (listed == null || listed.Contains(id))).Distinct().ToList();
            if (window.Tabs == null || !tabs.SequenceEqual(saved)) {
                window.Tabs = tabs;
                changed = true;
            }

            if (tabs.Count == 0 || string.IsNullOrEmpty(window.Id) || !ids.Add(window.Id)) {
                windows.Remove(window);
                changed = true;
                continue;
            }

            if (window.Selected == null || !tabs.Contains(window.Selected)) {
                window.Selected = tabs[0];
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>An ID no other window has: short, as it shows in ImGui's settings.</summary>
    private static string NewId(List<ChannelWindowLayout> windows) {
        while (true) {
            var id = Guid.NewGuid().ToString("N")[..12];
            if (windows.All(window => window.Id != id)) {
                return id;
            }
        }
    }
}

/// <summary>
/// Which channels also show in the game's chat log ("Also show in game chat", on for every channel unless turned off).
/// Kept per character like colours and nicknames, as the set of channels turned off, and dropped by the same rule.
/// </summary>
public static class GameChatChannels {
    /// <summary>Whether a channel's messages also go to the game's chat log.</summary>
    public static bool Shows(IReadOnlySet<string> off, string channelId) => !off.Contains(channelId);

    /// <summary>Turns showing a channel in game chat on or off.</summary>
    /// <returns>True if it changed.</returns>
    public static bool Set(HashSet<string> off, string channelId, bool show) => show ? off.Remove(channelId) : off.Add(channelId);

    /// <summary>
    /// Where a channel's notice goes: always to the channel's history; to game chat too if the channel shows there, or if it
    /// is a warning (a warning is never kept from the chat log), or it is about no channel.
    /// </summary>
    public static bool NoticeToGameChat(IReadOnlySet<string> off, SessionNotice notice) =>
        notice.ChannelId is not { } channelId || notice.Level >= NoticeLevel.Warning || Shows(off, channelId);

    /// <summary>
    /// The channels turned off game chat that no window has (its last tab or window was closed, or none was opened again at
    /// login): only the complete channel list's, as they show nowhere now. They go back to game chat, with
    /// <see cref="BackInGameChat"/> said, so a channel never ends up shown nowhere but in the unread counts.
    /// </summary>
    public static IReadOnlyList<string> ShownNowhere(IReadOnlySet<string> off, IEnumerable<ChannelWindowLayout?> windows, SessionSnapshot snapshot) {
        if (off.Count == 0 || snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return [];
        }

        var shown = windows.SelectMany(window => window?.Tabs ?? []).ToHashSet();
        return snapshot.Channels.Select(channel => channel.Id).Where(id => off.Contains(id) && !shown.Contains(id)).ToList();
    }

    /// <summary>Said (in game chat) when a channel goes back to game chat because no window shows it; the same in both modes.</summary>
    public static string BackInGameChat(string tag) => $"{tag} shows in game chat again, since no window shows it.";

    /// <summary>Forgets channels you're no longer in, only against the complete channel list.</summary>
    /// <returns>True if anything changed.</returns>
    public static bool Sync(HashSet<string> off, SessionSnapshot snapshot) {
        if (snapshot.State != ConnectionState.Ready || !snapshot.ChannelsLoaded) {
            return false;
        }

        var listed = snapshot.Channels.Select(channel => channel.Id).ToHashSet();
        return off.RemoveWhere(id => !listed.Contains(id)) > 0;
    }
}
