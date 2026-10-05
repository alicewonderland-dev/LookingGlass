using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LookingGlass.Core.Crypto;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Runs async actions started from the UI off the draw thread and remembers
/// the result to show next frame. Only one action runs at a time.
/// </summary>
/// <param name="shownCode">
/// The registration code the session checked and shows (if one is under way): the only code an error may show. Errors often
/// hold what a server said, so every other is removed (see <see cref="LodestoneCode.Redact"/>).
/// </param>
public sealed class UiActions(Func<string?>? shownCode = null) {
    /// <summary>How long a success stays in the status bar; the last part of it fades. Errors stay until dismissed or replaced.</summary>
    private const long SuccessShownMs = 8000;
    private const long FadeMs = 1500;

    private volatile Task? _running;
    private volatile string? _result;
    private volatile bool _resultIsError;
    private long _resultAt;

    public bool Busy => this._running is { IsCompleted: false };

    public void Run(string description, Func<Task> action) {
        if (this.Busy) {
            return;
        }

        this.SetResult($"{description}...", false);
        this._running = Task.Run(async () => {
            try {
                await action();
                this.SetResult($"{description}: done.", false);
            } catch (Exception ex) {
                this.SetResult(LodestoneCode.Redact($"{description} failed: {ex.Message}", shownCode?.Invoke()), true);
            }
        });
    }

    /// <summary>For synchronous session calls that save the keys, which mustn't hold up the game's frame.</summary>
    public void Run(string description, Action action) {
        this.Run(description, () => {
            action();
            return Task.CompletedTask;
        });
    }

    private void SetResult(string text, bool error) {
        this._resultIsError = error;
        Interlocked.Exchange(ref this._resultAt, Environment.TickCount64);
        this._result = text;
    }

    public void DrawStatus() {
        if (this._result is not { } result) {
            return;
        }

        if (this._resultIsError) {
            ImGui.TextColored(Widgets.Error, result);
        } else {
            ImGui.TextDisabled(result);
        }
    }

    /// <summary>
    /// The one-line footer of the main window: the latest action and how it went. A success fades
    /// after a few seconds; an error stays until clicked away or replaced.
    /// </summary>
    public void DrawStatusBar() {
        ImGui.AlignTextToFramePadding();
        if (this._result is not { } result) {
            // Keeps the footer's height.
            ImGui.TextUnformatted(" ");
            return;
        }

        var busy = this.Busy;
        var error = this._resultIsError && !busy;
        var age = Environment.TickCount64 - Interlocked.Read(ref this._resultAt);
        if (!busy && !error && age > SuccessShownMs) {
            this._result = null;
            ImGui.TextUnformatted(" ");
            return;
        }

        var alpha = busy || error ? 1f : Math.Clamp((SuccessShownMs - age) / (float) FadeMs, 0f, 1f);
        var colour = error ? Widgets.Error : Widgets.Muted;
        colour.W *= alpha;

        var icon = busy ? FontAwesomeIcon.Spinner : error ? FontAwesomeIcon.ExclamationCircle : FontAwesomeIcon.Check;
        Widgets.Icon(icon, colour);
        ImGui.SameLine(0, 6 * Widgets.Scale);
        var width = ImGui.GetContentRegionAvail().X;
        Widgets.TextEllipsis(result, width, colour, error ? $"{result}\n\nClick to dismiss." : null);
        if (error && ImGui.IsItemClicked()) {
            this._result = null;
        }
    }

    /// <summary>A button that only works while Ctrl is held, for destructive actions.</summary>
    public static bool ConfirmButton(string label) {
        var ctrl = ImGui.GetIO().KeyCtrl;
        ImGui.BeginDisabled(!ctrl);
        var clicked = ImGui.Button(label);
        ImGui.EndDisabled();
        if (!ctrl && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetTooltip("Hold Ctrl to enable.");
        }

        return clicked && ctrl;
    }
}
