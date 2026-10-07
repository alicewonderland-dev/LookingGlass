using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LookingGlass.Tests;

/// <summary>
/// Harness servers and database tests keep their files in temporary "lgt-" folders. Deleting one while
/// servers are still shutting down races with them (and pooled SQLite connections often keep
/// the files open), so tests leave them; instead, each run clears those an earlier run left.
/// </summary>
internal static class TestFolders {
    // Old enough that no run still in progress (this one or another) can be using them.
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    // "lgt-" + a GUID, optionally with a word between ("lgt-db-…", "lgt-move-…"): only folders tests make.
    private static readonly Regex OursPattern = new("^lgt-(?:[a-z]+-)?[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The full path of <paramref name="path"/>, if tests may delete it: a folder directly in the temporary folder whose name
    /// starts with "lgt-" (and has more after it). Anything else is a mistake in a test, and is refused before anything is
    /// deleted, whether it exists or not.
    /// </summary>
    /// <exception cref="InvalidOperationException">It isn't one.</exception>
    internal static string CheckDeletable(string path) {
        string full;
        string temp;
        try {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            throw new InvalidOperationException($"Refusing to delete \"{path}\": not a path.", ex);
        }

        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("lgt-", StringComparison.Ordinal) || name.Length <= "lgt-".Length) {
            throw new InvalidOperationException($"Refusing to delete \"{path}\": tests delete only \"lgt-\" folders directly in {temp}.");
        }

        return full;
    }

    [ModuleInitializer]
    internal static void ClearOldOnes() {
        try {
            var cutoff = DateTime.UtcNow - KeepFor;
            foreach (var folder in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("lgt-*")) {
                if (OursPattern.IsMatch(folder.Name) && folder.LastWriteTimeUtc < cutoff) {
                    try {
                        Directory.Delete(CheckDeletable(folder.FullName), true);
                    } catch {
                        // In use or already gone: the next run gets it.
                    }
                }
            }
        } catch {
            // Never fail a test run over temporary folders.
        }
    }
}
