using System.Text.RegularExpressions;
using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// What simple mode, the default, must never say: the technical words advanced mode uses. "Key" is banned outright, as a
/// noun in every form ("new key", "identity key", "keys"): simple mode says "LookingGlass", "setup" or "update" instead.
/// "Log" is banned as a word, not inside "login" or "logged"; "signed" too, but not in "signed in" or "signed out".
/// </summary>
public static partial class PlainLanguage {
    [GeneratedRegex(@"\b(fingerprints?|keys?|epochs?|re-?key\w*|forks?|forked|logs?|pinned|pinning|signatures?|signed(?!\s+(?:in|out)\b)|cipher\w*|encrypt\w*|decrypt\w*|sealed|sealing)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Banned();

    /// <summary>The technical words in <paramref name="text"/>, if any.</summary>
    public static IReadOnlyList<string> Jargon(string text) => Banned().Matches(text).Select(match => match.Value).ToList();

    /// <summary>Asserts <paramref name="text"/> is something to show (not empty), in simple mode's words.</summary>
    public static void AssertPlain(string? text) {
        Assert.False(string.IsNullOrWhiteSpace(text), "Simple mode would show nothing.");
        Assert.True(Jargon(text).Count == 0, $"Simple mode's words use jargon ({string.Join(", ", Jargon(text))}): {text}");
        Assert.DoesNotContain("{0}", text);
    }

    /// <summary>
    /// Asserts a notice is shown in both modes, whichever is set, and in plain words in simple mode if it is about something
    /// technical (its kind isn't <see cref="NoticeKind.General"/>, words from elsewhere, such as the server's announcements, or
    /// <see cref="NoticeKind.BackgroundFailure"/>, which holds whatever went wrong).
    /// </summary>
    /// <param name="names">
    /// Names of characters and channels, which are the users' own words (a test's "Alice Same Keys Again", or a channel
    /// called "Forked"), left out of the check, as is anything quoted.
    /// </param>
    public static void AssertShownInBothModes(SessionNotice notice, IEnumerable<string>? names = null) {
        Assert.False(string.IsNullOrWhiteSpace(notice.TextFor(advanced: true)), "Advanced mode would show nothing.");
        Assert.False(string.IsNullOrWhiteSpace(notice.TextFor(advanced: false)), "Simple mode would show nothing.");
        if (notice.Kind is not (NoticeKind.General or NoticeKind.BackgroundFailure)) {
            Assert.NotNull(notice.Plain);
            AssertPlain(WithoutNames(notice.TextFor(advanced: false), names ?? []));
        }
    }

    /// <summary>
    /// Asserts everything a snapshot shows is shown in both modes: each channel's warning whenever it has one, the channel
    /// list's reasons whenever it flags a channel (the same warnings, as many, at the same level), the status and the
    /// address hint; and in plain words in simple mode.
    /// </summary>
    /// <param name="names">As for <see cref="AssertShownInBothModes"/>.</param>
    public static void AssertSnapshotInBothModes(SessionSnapshot snapshot, IReadOnlyCollection<string> names) {
        foreach (var channel in snapshot.Channels) {
            if (channel.MembershipWarning != null) {
                Assert.False(string.IsNullOrWhiteSpace(channel.WarningFor(advanced: true)), "Advanced mode would hide a channel's warning.");
                Assert.False(string.IsNullOrWhiteSpace(channel.WarningFor(advanced: false)), $"Simple mode would hide a channel's warning: {channel.MembershipWarning}");
                AssertPlain(WithoutNames(channel.WarningFor(advanced: false)!, names));
            }

            var advanced = ChannelAttention.Of(channel, advanced: true);
            var simple = ChannelAttention.Of(channel, advanced: false);
            Assert.Equal(advanced.Level, simple.Level);
            Assert.Equal(advanced.Reasons.Length, simple.Reasons.Length);
            if (simple.Level != AttentionLevel.None) {
                Assert.NotEmpty(simple.Reasons);
                Assert.All(simple.Reasons, reason => AssertPlain(WithoutNames(reason, names)));
            }
        }

        foreach (var advanced in new[] { true, false }) {
            if (snapshot.StatusText != null) {
                Assert.False(string.IsNullOrWhiteSpace(snapshot.StatusFor(advanced)), "A mode would show no status.");
            }

            if (snapshot.AddressNotListed != null) {
                Assert.False(string.IsNullOrWhiteSpace(snapshot.AddressNotListedFor(advanced)), "A mode would hide the address hint.");
            }
        }

        if (snapshot.StatusFor(advanced: false) is { } status) {
            AssertPlain(WithoutNames(status, names));
        }

        if (snapshot.AddressNotListedFor(advanced: false) is { } hint) {
            AssertPlain(WithoutNames(hint, names));
        }
    }

    /// <summary>The text with names and anything quoted left out, for the jargon check.</summary>
    private static string WithoutNames(string text, IEnumerable<string> names) {
        text = Quoted().Replace(text, "\"\"");
        foreach (var name in names.Where(name => name.Length > 0).OrderByDescending(name => name.Length)) {
            text = text.Replace(name, "Someone", StringComparison.Ordinal);
        }

        return text;
    }

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Quoted();
}
