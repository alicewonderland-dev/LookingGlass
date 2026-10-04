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

    [ModuleInitializer]
    internal static void ClearOldOnes() {
        try {
            var cutoff = DateTime.UtcNow - KeepFor;
            foreach (var folder in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("lgt-*")) {
                if (OursPattern.IsMatch(folder.Name) && folder.LastWriteTimeUtc < cutoff) {
                    try {
                        folder.Delete(true);
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
