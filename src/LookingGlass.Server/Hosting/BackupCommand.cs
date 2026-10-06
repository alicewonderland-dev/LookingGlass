using System.Globalization;
using System.Text.RegularExpressions;
using LookingGlass.Server.Data;

namespace LookingGlass.Server.Hosting;

/// <summary>
/// <c>LookingGlass.Server --backup &lt;file or folder&gt; [--keep N]</c>: an online backup of the configured database (see
/// <see cref="Database.BackupFile"/>), safe while the server runs, without starting a server. Given a folder (one that
/// exists, or a path ending in a separator), it writes <c>lookingglass-&lt;UTC time&gt;.db</c> there and, with
/// <c>--keep</c>, deletes all but the newest N such files in it (nothing else). For a systemd timer or cron job.
/// </summary>
public static partial class BackupCommand {
    public const string Option = "--backup";

    public sealed record Request(string Target, int Keep);

    /// <returns>The request, if the arguments ask for a backup; throws if they do but are wrong.</returns>
    /// <exception cref="ArgumentException">--backup without a target, or --keep without a number of at least 1.</exception>
    public static Request? Parse(string[] args) {
        var at = Array.IndexOf(args, Option);
        if (at < 0) {
            return null;
        }

        if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal)) {
            throw new ArgumentException("--backup needs a file or folder to write the backup to.");
        }

        var keep = 0;
        var keepAt = Array.IndexOf(args, "--keep");
        if (keepAt >= 0 && (keepAt + 1 >= args.Length || !int.TryParse(args[keepAt + 1], NumberStyles.None, CultureInfo.InvariantCulture, out keep) || keep < 1)) {
            throw new ArgumentException("--keep needs a number of backups to keep, at least 1.");
        }

        return new Request(args[at + 1], keep);
    }

    /// <returns>The process's exit code: 0 if the backup was written.</returns>
    public static int Run(Request request, string databasePath, TextWriter output, TextWriter errors) {
        try {
            var folder = Directory.Exists(request.Target) || request.Target.EndsWith(Path.DirectorySeparatorChar) || request.Target.EndsWith(Path.AltDirectorySeparatorChar);
            var target = request.Target;
            if (folder) {
                Directory.CreateDirectory(target);
                target = Path.Combine(target, $"lookingglass-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.db");
            } else if (Path.GetDirectoryName(Path.GetFullPath(target)) is { } parent) {
                Directory.CreateDirectory(parent);
            }

            var written = Database.BackupFile(databasePath, target);
            output.WriteLine($"Backed up {Path.GetFullPath(databasePath)} to {written}");
            if (folder && request.Keep > 0) {
                foreach (var old in Directory.GetFiles(request.Target).Where(file => BackupName().IsMatch(Path.GetFileName(file)))
                             .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal).Skip(request.Keep)) {
                    File.Delete(old);
                    output.WriteLine($"Deleted the older backup {old}");
                }
            }

            return 0;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) {
            errors.WriteLine($"The backup failed: {ex.Message}");
            return 1;
        }
    }

    [GeneratedRegex(@"^lookingglass-\d{8}-\d{6}-\d{3}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}
