using System.Globalization;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// Local name colours: one custom colour (0xRRGGBB) per person, for their name wherever it shows (every channel's lines in
/// game chat and in channel windows, and the member lists), to tell people apart at a glance. Like channel colours they
/// never go to the server; unlike them, they are the same for every character played on this computer, and stay when the
/// person leaves a channel (they may be in another, or come back). No entry means the default: the name in the line's
/// colour, as before name colours.
/// </summary>
/// <remarks>
/// A person is kept by their user ID, never by name: a user ID is the character's Lodestone ID, which stays the same through
/// a name change or a move to another world, and is the same on every server. A test server's made-up accounts have
/// negative IDs made from the name alone, the same on every server; but anyone can register any such name on any test
/// server, so the same ID on two servers needn't be the same person, and those are kept with the server's address too.
/// </remarks>
public static class NameColours {
    /// <summary>Whether a person can have a colour: anyone known, not user ID 0 (a sender not known).</summary>
    public static bool CanHave(long userId) => userId != 0;

    /// <summary>The key a person's colour is kept under, or null for nobody known (user ID 0).</summary>
    public static string? KeyOf(long userId, string serverUrl) => userId switch {
        > 0 => userId.ToString(CultureInfo.InvariantCulture),
        < 0 => $"{userId.ToString(CultureInfo.InvariantCulture)}@{Server(serverUrl)}",
        _ => null,
    };

    /// <summary>
    /// A server's address as written into a key: the same however it was typed, so an edit to the setting that changes
    /// nothing (the case of the scheme or host, a default port, a trailing slash, spaces around it) keeps the colours. An
    /// address that isn't a URL is only trimmed.
    /// </summary>
    internal static string Server(string serverUrl) {
        var trimmed = serverUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) {
            return trimmed.TrimEnd('/').Trim();
        }

        // Uri gives the scheme and host in lower case; the path keeps its case (a server may tell paths apart by it).
        var port = uri.IsDefaultPort ? "" : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}";
    }

    /// <summary>A person's colour, or null for the default.</summary>
    /// <param name="colours">The colours; null (settings saved before name colours, or a hand-edited file) is none.</param>
    public static uint? Of(IReadOnlyDictionary<string, uint>? colours, long userId, string serverUrl) =>
        colours != null && KeyOf(userId, serverUrl) is { } key && colours.TryGetValue(key, out var rgb) ? rgb & 0xFFFFFF : null;

    /// <summary>A user's colour, or null for the default (or for no user).</summary>
    public static uint? Of(IReadOnlyDictionary<string, uint>? colours, User? user, string serverUrl) =>
        user == null ? null : Of(colours, user.UserId, serverUrl);

    /// <summary>Gives a person a colour, or with null sets their name back to the default. Nobody known (user ID 0) gets none.</summary>
    /// <returns>True if anything changed.</returns>
    public static bool Set(Dictionary<string, uint> colours, long userId, string serverUrl, uint? rgb) {
        if (KeyOf(userId, serverUrl) is not { } key) {
            return false;
        }

        if (rgb is not { } colour) {
            return colours.Remove(key);
        }

        colour &= 0xFFFFFF;
        if (colours.TryGetValue(key, out var old) && old == colour) {
            return false;
        }

        colours[key] = colour;
        return true;
    }
}

/// <summary>The words of the name colour menu and picker: the same in both modes, and in plain words (tested).</summary>
public static class NameColourWords {
    public const string MenuItem = "Name colour...";

    public const string MenuTooltip = "Give this name a colour of its own, to tell people apart at a glance. Only you see it.";

    public const string Title = "Name colour";

    public const string Explanation = "This name shows in this colour in every channel: in chat, in channel windows and in the member list. Only you see it.";

    public const string Default = "Default";

    public const string DefaultTooltip = "The name in the line's usual colour.";

    public const string Cancel = "Cancel";

    /// <summary>Every fixed string, for the plain-language test.</summary>
    public static IEnumerable<string> All() => [MenuItem, MenuTooltip, Title, Explanation, Default, DefaultTooltip, Cancel];
}
