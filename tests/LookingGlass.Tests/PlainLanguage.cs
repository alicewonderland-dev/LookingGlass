using System.Text.RegularExpressions;
using LookingGlass.Core.Client;

namespace LookingGlass.Tests;

/// <summary>
/// What simple mode, the default, must never say: the technical words advanced mode uses. "Key" is banned outright, as a
/// noun in every form ("new key", "identity key", "keys"): simple mode says "LookingGlass", "setup" or "update" instead.
/// "Log" is banned as a word, not inside "login" or "logged".
/// </summary>
public static partial class PlainLanguage {
    [GeneratedRegex(@"\b(fingerprints?|keys?|epochs?|re-?key\w*|forks?|forked|logs?|pinned|pinning|signatures?|signed|cipher\w*|encrypt\w*|decrypt\w*|sealed|sealing)\b",
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
            var text = Quoted().Replace(notice.TextFor(advanced: false), "\"\"");
            foreach (var name in (names ?? []).Where(name => name.Length > 0).OrderByDescending(name => name.Length)) {
                text = text.Replace(name, "Someone", StringComparison.Ordinal);
            }

            AssertPlain(text);
        }
    }

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Quoted();
}
