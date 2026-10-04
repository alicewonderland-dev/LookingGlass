using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Runs async actions started from the UI off the draw thread and remembers
/// the result to show next frame. Only one action runs at a time.
/// </summary>
public sealed class UiActions {
    private volatile Task? _running;
    private volatile string? _result;
    private volatile bool _resultIsError;

    public bool Busy => this._running is { IsCompleted: false };

    public void Run(string description, Func<Task> action) {
        if (this.Busy) {
            return;
        }

        this._result = $"{description}...";
        this._resultIsError = false;
        this._running = Task.Run(async () => {
            try {
                await action();
                this._result = $"{description}: done.";
                this._resultIsError = false;
            } catch (Exception ex) {
                this._result = $"{description} failed: {ex.Message}";
                this._resultIsError = true;
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

    public void DrawStatus() {
        if (this._result is not { } result) {
            return;
        }

        if (this._resultIsError) {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), result);
        } else {
            ImGui.TextDisabled(result);
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
