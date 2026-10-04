using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Small drawing helpers shared by the windows. Colours come from the current Dalamud style, or
/// for meaning (warning, error, success) from <see cref="ImGuiColors"/>, so they suit any theme.
/// </summary>
internal static class Widgets {
    public static float Scale => ImGuiHelpers.GlobalScale;

    public static Vector4 Muted => ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled];

    public static Vector4 Warning => ImGuiColors.DalamudOrange;

    public static Vector4 Error => ImGuiColors.DalamudRed;

    public static Vector4 Success => ImGuiColors.HealerGreen;

    /// <summary>A FontAwesome icon as text, in the current text colour or another.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? colour = null) {
        using (Services.PluginInterface.UiBuilder.IconFontHandle.Push()) {
            if (colour is { } c) {
                ImGui.TextColored(c, icon.ToIconString());
            } else {
                ImGui.TextUnformatted(icon.ToIconString());
            }
        }
    }

    public static Vector2 IconSize(FontAwesomeIcon icon) {
        using (Services.PluginInterface.UiBuilder.IconFontHandle.Push()) {
            return ImGui.CalcTextSize(icon.ToIconString());
        }
    }

    /// <summary>An icon followed by text on the same line.</summary>
    public static void IconText(FontAwesomeIcon icon, string text, Vector4? colour = null) {
        Icon(icon, colour);
        ImGui.SameLine(0, 4 * Scale);
        if (colour is { } c) {
            ImGui.TextColored(c, text);
        } else {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>The width of an <see cref="IconButton"/>: the icon and the frame's padding.</summary>
    public static float IconButtonWidth(FontAwesomeIcon icon) => IconSize(icon).X + ImGui.GetStyle().FramePadding.X * 2;

    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip) {
        var clicked = ImGuiComponents.IconButton(id, icon);
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>Shows a wrapped tooltip while the last item is hovered, even when it's disabled.</summary>
    public static void Tooltip(string text) {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void WrappedColoured(Vector4 colour, string text) {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Text cut to fit <paramref name="maxWidth"/>, ending in "...", with the whole of it in a tooltip when cut.</summary>
    public static void TextEllipsis(string text, float maxWidth, Vector4? colour = null, string? tooltip = null) {
        var shown = Ellipsize(text, maxWidth);
        if (colour is { } c) {
            ImGui.TextColored(c, shown);
        } else {
            ImGui.TextUnformatted(shown);
        }

        if (tooltip != null) {
            Tooltip(tooltip);
        } else if (!ReferenceEquals(shown, text)) {
            Tooltip(text);
        }
    }

    /// <summary>The longest start of <paramref name="text"/> that fits with "..." after it, or the text itself if it fits.</summary>
    public static string Ellipsize(string text, float maxWidth) {
        if (ImGui.CalcTextSize(text).X <= maxWidth) {
            return text;
        }

        const string dots = "...";
        int low = 0, high = text.Length;
        while (low < high) {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + dots).X <= maxWidth) {
                low = middle;
            } else {
                high = middle - 1;
            }
        }

        // Never split a surrogate pair.
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) {
            low--;
        }

        return text[..low].TrimEnd() + dots;
    }

    /// <summary>A rounded label with a coloured dot, such as the connection state.</summary>
    public static void Pill(string text, Vector4 colour, string? tooltip = null) {
        var scale = Scale;
        var height = ImGui.GetFrameHeight();
        var dot = height * 0.16f;
        var padding = 8 * scale;
        var textSize = ImGui.CalcTextSize(text);
        var size = new Vector2(padding * 2 + dot * 2 + 6 * scale + textSize.X, height);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(colour with { W = colour.W * 0.2f }), height / 2);
        var middle = pos.Y + height / 2;
        drawList.AddCircleFilled(new Vector2(pos.X + padding + dot, middle), dot, ImGui.GetColorU32(colour));
        drawList.AddText(new Vector2(pos.X + padding + dot * 2 + 6 * scale, middle - textSize.Y / 2), ImGui.GetColorU32(ImGuiCol.Text), text);

        if (tooltip != null) {
            Tooltip(tooltip);
        }
    }

    /// <summary>A small rounded count (unread messages, invites) with its right edge at <paramref name="right"/>.</summary>
    /// <returns>Its width.</returns>
    public static float Badge(Vector2 right, string text, Vector4 colour) {
        var scale = Scale;
        var textSize = ImGui.CalcTextSize(text);
        var height = textSize.Y + 2 * scale;
        var width = Math.Max(height, textSize.X + 10 * scale);
        var min = new Vector2(right.X - width, right.Y - height / 2);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(colour), height / 2);
        // White on the saturated badge colours reads in light and dark themes alike.
        drawList.AddText(new Vector2(min.X + (width - textSize.X) / 2, min.Y + (height - textSize.Y) / 2), 0xFFFFFFFF, text);
        return width;
    }

    /// <summary>A coloured square for a channel's colour.</summary>
    public static void Swatch(Vector4 colour, float size) {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size, size));
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(size, size), ImGui.GetColorU32(colour), 3 * Scale);
    }

    /// <summary>Puts the next item on the same line if <paramref name="width"/> still fits there.</summary>
    public static void SameLineIfFits(float width) {
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) {
            ImGui.SameLine();
        }
    }

    public static float ButtonWidth(string label) => ImGui.CalcTextSize(label, true).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>A muted heading for a group of settings or a list.</summary>
    public static void Heading(string text) {
        ImGui.Spacing();
        ImGui.TextColored(Muted, text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>Lines centred in the space left, for empty states.</summary>
    public static void Centred(FontAwesomeIcon icon, string title, string? text) {
        var width = ImGui.GetContentRegionAvail().X;
        var wrap = Math.Min(width - 20 * Scale, 360 * Scale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, ImGui.GetContentRegionAvail().Y * 0.25f));

        var iconWidth = IconSize(icon).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - iconWidth) / 2);
        Icon(icon, Muted);
        ImGui.Spacing();
        CentredLine(title, width, null);
        if (text != null) {
            ImGui.Spacing();
            foreach (var line in Wrap(text, wrap)) {
                CentredLine(line, width, Muted);
            }
        }

        ImGui.Spacing();
    }

    /// <summary>Moves the cursor so that an item of <paramref name="itemWidth"/> is centred in <paramref name="width"/>.</summary>
    public static void CentreNext(float itemWidth, float width) {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - itemWidth) / 2));
    }

    private static void CentredLine(string line, float width, Vector4? colour) {
        CentreNext(ImGui.CalcTextSize(line).X, width);
        if (colour is { } c) {
            ImGui.TextColored(c, line);
        } else {
            ImGui.TextUnformatted(line);
        }
    }

    /// <summary>Splits text into lines no wider than <paramref name="width"/>, at spaces.</summary>
    private static IEnumerable<string> Wrap(string text, float width) {
        var line = "";
        foreach (var word in text.Split(' ')) {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && ImGui.CalcTextSize(candidate).X > width) {
                yield return line;
                line = word;
            } else {
                line = candidate;
            }
        }

        if (line.Length > 0) {
            yield return line;
        }
    }
}
