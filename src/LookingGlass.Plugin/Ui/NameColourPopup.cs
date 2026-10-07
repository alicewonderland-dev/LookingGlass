using System.Numerics;
using Dalamud.Bindings.ImGui;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;
using LookingGlass.Protocol;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// "Name colour...": one person's name colour (see <see cref="NameColours"/>), picked on the same colour wheel as a channel's
/// custom colour, with a preview of a chat line, Use, Default (back to the line's colour) and Cancel. Opened from a right-click
/// on a name in a channel window's messages or in a channel's member list. Each place that opens it keeps its own, and calls
/// <see cref="Open"/> and <see cref="Draw"/> at the same place in ImGui's ID stack (not inside another popup). Draw thread only.
/// </summary>
internal sealed class NameColourPopup(SessionManager sessions) {
    private const string Id = "name-colour";

    private readonly ColourWheel _wheel = new();
    private User? _user;
    private ChannelColour? _channel;
    private User? _opening;

    /// <summary>Whether a user can have a name colour: anyone known (not user ID 0, a sender not known).</summary>
    public static bool CanColour(User user) => NameColours.CanHave(user.UserId);

    /// <summary>The popup is open (or about to open) for this person: their row in a list can stay lit meanwhile.</summary>
    public bool IsOpenFor(User user) => (this._opening ?? this._user)?.UserId == user.UserId;

    /// <summary>
    /// Asks for the popup for a person, to open at the next <see cref="Draw"/> (so it can be asked for from inside a menu).
    /// Nobody known (user ID 0) gets none.
    /// </summary>
    /// <param name="channel">The colour of the channel it was opened in, for the preview, and where the wheel starts for a name without a colour.</param>
    public void Open(User user, ChannelColour? channel) {
        if (!CanColour(user)) {
            return;
        }

        this._opening = user;
        this._channel = channel;
    }

    public void Draw() {
        if (this._opening is { } opening) {
            this._opening = null;
            this._user = opening;
            this._wheel.Start(sessions.NameColourOf(opening) ?? ChannelPane.StartingRgb(this._channel));
            ImGui.OpenPopup(Id);
        }

        if (this._user is not { } user) {
            return;
        }

        if (!ImGui.BeginPopup(Id)) {
            // Closed (or never drawn here again): it is nobody's any more.
            this._user = null;
            return;
        }

        var width = 260 * Widgets.Scale;
        var current = sessions.NameColourOf(user);
        var rgb = this._wheel.Draw(NameColourWords.Title, NameColourWords.Explanation, width, sessions.AdvancedMode,
            (colour, previewWidth) => this.DrawPreview(user, colour, previewWidth));
        ImGui.BeginDisabled(this._wheel.CodeBad);
        if (ImGui.Button(ColourWords.Use) && !this._wheel.CodeBad) {
            sessions.SetNameColour(user, rgb);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Widgets.GhostButton(current == null ? $"{NameColourWords.Default} (selected)" : NameColourWords.Default, NameColourWords.DefaultTooltip)) {
            sessions.SetNameColour(user, null);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (Widgets.GhostButton(NameColourWords.Cancel)) {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>A sample chat line: the name in the colour, the rest as the channel's line would be.</summary>
    private void DrawPreview(User user, uint rgb, float width) {
        // The rest of the line: in the channel's colour if the whole line takes it, else light grey, like most chat text.
        var rest = this._channel is { } channel && sessions.ColourWholeLine && ChannelPalette.ColourOf(channel) is { } line
            ? line
            : new Vector4(0.85f, 0.85f, 0.85f, 1);
        ColourWheel.DrawSampleLine(width,
            ("<", rest),
            ($"{TextSanitizer.Name(user.Name)}@{TextSanitizer.Name(user.WorldName)}", ChannelPalette.OfRgb(rgb)),
            ("> " + ColourWords.SampleText, rest));
    }
}
