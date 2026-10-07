using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using LookingGlass.Core.Client;
using LookingGlass.Core.Util;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// The custom colour picker, as channel colours ("Custom...") and name colours ("Name colour...") both use it: a colour wheel
/// and the colour's code, kept in step both ways; a preview; a warning for a colour too dark to read; and the closest game
/// colour, used where the exact one can't be shown. Its words are <see cref="ColourWords"/>. Draw thread only.
/// </summary>
internal sealed class ColourWheel {
    // The wheel's colour, the code as typed, and whether that can't be read.
    private Vector3 _colour;
    private string _code = "";
    private bool _codeBad;

    /// <summary>The code typed can't be read: nothing can be picked until it is fixed.</summary>
    public bool CodeBad => this._codeBad;

    /// <summary>Starts the wheel and the code on a colour (0xRRGGBB).</summary>
    public void Start(uint rgb) {
        var colour = ChannelPalette.OfRgb(rgb);
        this._colour = new Vector3(colour.X, colour.Y, colour.Z);
        this._code = HexColour.Format(rgb);
        this._codeBad = false;
    }

    /// <summary>
    /// Draws the title and explanation, the wheel, the code, the preview (<paramref name="preview"/>, given the colour and the
    /// width), the warning and the closest game colour, <paramref name="width"/> wide. The buttons are the caller's.
    /// </summary>
    /// <returns>The colour on the wheel (0xRRGGBB); not to be used while <see cref="CodeBad"/>.</returns>
    public uint Draw(string title, string explanation, float width, bool advanced, Action<uint, float> preview) {
        var scale = Widgets.Scale;
        ImGui.TextUnformatted(title);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(Widgets.Muted, explanation);
        ImGuiHelpers.ScaledDummy(4);

        ImGui.SetNextItemWidth(width * 0.8f);
        if (ImGui.ColorPicker3("##custom-wheel", ref this._colour,
                ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoInputs |
                ImGuiColorEditFlags.NoLabel)) {
            this._code = HexColour.Format(ChannelPalette.RgbOf(this._colour));
            this._codeBad = false;
        }

        ImGuiHelpers.ScaledDummy(2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Colour code");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * scale);
        if (ImGui.InputTextWithHint("##custom-code", ColourWords.CodeHint, ref this._code, 16)) {
            if (HexColour.TryParse(this._code, out var typed)) {
                var colour = ChannelPalette.OfRgb(typed);
                this._colour = new Vector3(colour.X, colour.Y, colour.Z);
                this._codeBad = false;
            } else {
                this._codeBad = true;
            }
        }

        var rgb = ChannelPalette.RgbOf(this._colour);
        if (!ImGui.IsItemActive() && !this._codeBad) {
            // Tidied once it's typed: "3fa7d6" reads "#3FA7D6".
            this._code = HexColour.Format(rgb);
        }

        if (this._codeBad) {
            Widgets.WrappedColoured(Widgets.Error, ColourWords.CodeProblem);
        }

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextColored(Widgets.Muted, ColourWords.Preview);
        preview(rgb, width);

        if (ColourMatch.HardToRead(rgb)) {
            ImGuiHelpers.ScaledDummy(2);
            Widgets.WrappedColoured(Widgets.Warning, ColourWords.HardToRead);
        }

        if (ChannelPalette.Nearest(rgb) is { } nearest && ChannelPalette.ColourOf(nearest) is { } nearestColour) {
            ImGuiHelpers.ScaledDummy(2);
            ImGui.TextColored(Widgets.Muted, ColourWords.Fallback);
            var swatch = 18 * scale;
            ImGui.ColorButton("##custom-nearest", nearestColour, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoAlpha, new Vector2(swatch, swatch));
            Widgets.Tooltip(advanced ? $"UIColor {nearest}" : "The closest game colour.");
        }

        ImGui.PopTextWrapPos();
        ImGuiHelpers.ScaledDummy(4);
        return rgb;
    }

    /// <summary>
    /// A sample chat line on a dark background like the chat's, <paramref name="width"/> wide: pieces of text, each in its
    /// colour, wrapped as one line would be.
    /// </summary>
    public static void DrawSampleLine(float width, params (string Text, Vector4 Colour)[] pieces) {
        var scale = Widgets.Scale;
        var padding = 6 * scale;
        var inner = width - 2 * padding;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        // Drawn on a channel of its own behind the text, once the text's height is known.
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(pos + new Vector2(padding, padding));
        var start = ImGui.GetCursorPosX();
        var first = true;
        foreach (var (text, colour) in pieces) {
            foreach (var word in Words(text)) {
                var size = ImGui.CalcTextSize(word).X;
                if (!first) {
                    ImGui.SameLine(0, 0);
                    if (ImGui.GetCursorPosX() + size > start + inner) {
                        ImGui.NewLine();
                        ImGui.SetCursorPosX(start);
                    }
                }

                first = false;
                ImGui.TextColored(colour, word);
            }
        }

        var bottom = ImGui.GetItemRectMax().Y + padding;
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(pos, new Vector2(pos.X + width, bottom), ImGui.GetColorU32(ChannelPalette.OfRgb(ColourMatch.TypicalChatBackground)), 4 * scale);
        drawList.ChannelsMerge();
        ImGui.SetCursorScreenPos(pos with { Y = bottom });
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>Text cut into words, each with the spaces after it, to wrap at.</summary>
    private static IEnumerable<string> Words(string text) {
        var start = 0;
        for (var at = 0; at < text.Length; at++) {
            if (text[at] == ' ' && (at + 1 == text.Length || text[at + 1] != ' ')) {
                yield return text[start..(at + 1)];
                start = at + 1;
            }
        }

        if (start < text.Length) {
            yield return text[start..];
        }
    }
}
