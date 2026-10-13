using Dalamud.Plugin.Services;
using LookingGlass.Core.SelfTest;
using Serilog;
using Serilog.Events;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>
/// The self-test's log tap (docs/self-test-plan.md, "What automatic can honestly check"): a wrapper around the plugin's
/// <see cref="IPluginLog"/> that passes everything on unchanged and, only while a run is on, counts errors and warnings,
/// the "[sticky]" lines, and lines holding anything that must never be in the log (the run's marker, a name, the test
/// channel's name). It never copies a line anywhere.
/// </summary>
internal sealed class LogTap(IPluginLog inner) : IPluginLog {
    // What must never appear, as one pattern of whole words (case as given), or null when no run is on.
    private volatile System.Text.RegularExpressions.Regex? _forbidden;
    private volatile string[] _words = [];
    private int _errors;
    private int _warnings;
    private int _forbiddenHits;
    private int _sticky;
    private int _stickyLocal;
    private long _lastLinkKind = -1;

    /// <summary>Starts counting, from zero, with what must never appear.</summary>
    public void Watch(IEnumerable<string> forbidden) {
        Interlocked.Exchange(ref this._errors, 0);
        Interlocked.Exchange(ref this._warnings, 0);
        Interlocked.Exchange(ref this._forbiddenHits, 0);
        Interlocked.Exchange(ref this._sticky, 0);
        Interlocked.Exchange(ref this._stickyLocal, 0);
        Interlocked.Exchange(ref this._lastLinkKind, -1);
        this._words = forbidden.Where(word => word.Trim().Length >= 3).Select(word => word.Trim()).Distinct().ToArray();
        this._forbidden = Pattern(this._words);
    }

    /// <summary>Adds to what must never appear (a name typed in for a step).</summary>
    public void Forbid(string word) {
        if (this._forbidden != null && word.Trim().Length >= 3) {
            this._words = [.. this._words, word.Trim()];
            this._forbidden = Pattern(this._words);
        }
    }

    /// <summary>Stops counting.</summary>
    public void Stop() {
        this._forbidden = null;
        this._words = [];
    }

    /// <summary>The words as whole words (not inside others: a world called "Lich" isn't in "lichen"), case as given.</summary>
    private static System.Text.RegularExpressions.Regex Pattern(IEnumerable<string> words) => new(
        string.Join("|", words.Select(word => (char.IsLetterOrDigit(word[0]) ? @"(?<![\p{L}\p{N}])" : "") + System.Text.RegularExpressions.Regex.Escape(word)
                                              + (char.IsLetterOrDigit(word[^1]) ? @"(?![\p{L}\p{N}])" : "")).DefaultIfEmpty("(?!)")),
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public LogTapCounts Counts => new(Volatile.Read(ref this._errors), Volatile.Read(ref this._warnings), Volatile.Read(ref this._forbiddenHits),
        Volatile.Read(ref this._stickyLocal), Volatile.Read(ref this._sticky), Interlocked.Read(ref this._lastLinkKind) is >= 0 and var kind ? (uint) kind : null);

    private void Count(LogEventLevel level, Exception? exception, string template) {
        if (this._forbidden is not { } forbidden) {
            return;
        }

        try {
            if (level >= LogEventLevel.Error) {
                Interlocked.Increment(ref this._errors);
            } else if (level == LogEventLevel.Warning) {
                Interlocked.Increment(ref this._warnings);
            }

            if (template.StartsWith(Core.Client.StickyDiagnostics.Prefix, StringComparison.Ordinal)) {
                Interlocked.Increment(ref this._sticky);
                if (template.Contains($"talking in {Core.Client.LocalChat.Tag}", StringComparison.Ordinal)) {
                    Interlocked.Increment(ref this._stickyLocal);
                }

                // The link kind only (a number), from "link put in the chat input: ..., kind 1120, ...".
                if (template.Contains("link put in the chat input", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Match(template, @"kind (\d+)") is { Success: true } kind && uint.TryParse(kind.Groups[1].Value, out var number)) {
                    Interlocked.Exchange(ref this._lastLinkKind, number);
                }
            }

            var text = exception == null ? template : $"{template} {exception.Message}";
            if (forbidden.IsMatch(text)) {
                Interlocked.Increment(ref this._forbiddenHits);
            }
        } catch {
            // Counting must never be in the way of the log.
        }
    }

    public ILogger Logger => inner.Logger;

    public LogEventLevel MinimumLogLevel {
        get => inner.MinimumLogLevel;
        set => inner.MinimumLogLevel = value;
    }

    public void Fatal(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Fatal, null, messageTemplate);
        inner.Fatal(messageTemplate, values);
    }

    public void Fatal(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Fatal, exception, messageTemplate);
        inner.Fatal(exception, messageTemplate, values);
    }

    public void Error(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Error, null, messageTemplate);
        inner.Error(messageTemplate, values);
    }

    public void Error(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Error, exception, messageTemplate);
        inner.Error(exception, messageTemplate, values);
    }

    public void Warning(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Warning, null, messageTemplate);
        inner.Warning(messageTemplate, values);
    }

    public void Warning(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Warning, exception, messageTemplate);
        inner.Warning(exception, messageTemplate, values);
    }

    public void Information(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Information, null, messageTemplate);
        inner.Information(messageTemplate, values);
    }

    public void Information(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Information, exception, messageTemplate);
        inner.Information(exception, messageTemplate, values);
    }

    public void Info(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Information, null, messageTemplate);
        inner.Info(messageTemplate, values);
    }

    public void Info(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Information, exception, messageTemplate);
        inner.Info(exception, messageTemplate, values);
    }

    public void Debug(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Debug, null, messageTemplate);
        inner.Debug(messageTemplate, values);
    }

    public void Debug(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Debug, exception, messageTemplate);
        inner.Debug(exception, messageTemplate, values);
    }

    public void Verbose(string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Verbose, null, messageTemplate);
        inner.Verbose(messageTemplate, values);
    }

    public void Verbose(Exception? exception, string messageTemplate, params object[] values) {
        this.Count(LogEventLevel.Verbose, exception, messageTemplate);
        inner.Verbose(exception, messageTemplate, values);
    }

    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values) {
        this.Count(level, exception, messageTemplate);
        inner.Write(level, exception, messageTemplate, values);
    }
}
