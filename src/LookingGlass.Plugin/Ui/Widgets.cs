using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Small drawing helpers shared by the windows. Colours come from the current Dalamud style, or
/// for meaning (warning, error, success) from <see cref="ImGuiColors"/>, so they suit any theme.
/// Every size is in unscaled pixels times <see cref="Scale"/>.
/// </summary>
internal static class Widgets {
    public static float Scale => ImGuiHelpers.GlobalScale;

    public static Vector4 Text => ImGui.GetStyle().Colors[(int) ImGuiCol.Text];

    public static Vector4 Muted => ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled];

    public static Vector4 Warning => ImGuiColors.DalamudOrange;

    public static Vector4 Error => ImGuiColors.DalamudRed;

    public static Vector4 Success => ImGuiColors.HealerGreen;

    private static IFontHandle IconFont => Services.PluginInterface.UiBuilder.IconFontHandle;

    /// <summary>Icons of one width, so buttons and menu rows line up.</summary>
    private static IFontHandle FixedIconFont => Services.PluginInterface.UiBuilder.IconFontFixedWidthHandle;

    public static IFontHandle MonoFont => Services.PluginInterface.UiBuilder.MonoFontHandle;

    // ================================================================ icons and text

    /// <summary>A FontAwesome icon as text, in the current text colour or another.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? colour = null) {
        using (IconFont.Push()) {
            if (colour is { } c) {
                ImGui.TextColored(c, icon.ToIconString());
            } else {
                ImGui.TextUnformatted(icon.ToIconString());
            }
        }
    }

    public static Vector2 IconSize(FontAwesomeIcon icon) {
        using (IconFont.Push()) {
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

    /// <summary>Draws an icon (fixed width) centred on a point, without making an item.</summary>
    public static void DrawIcon(ImDrawListPtr drawList, FontAwesomeIcon icon, Vector2 centre, uint colour) {
        using (FixedIconFont.Push()) {
            var text = icon.ToIconString();
            drawList.AddText(Snap(centre - ImGui.CalcTextSize(text) / 2), colour, text);
        }
    }

    /// <summary>The width of an icon in the fixed-width icon font.</summary>
    public static float FixedIconWidth(FontAwesomeIcon icon) {
        using (FixedIconFont.Push()) {
            return ImGui.CalcTextSize(icon.ToIconString()).X;
        }
    }

    /// <summary>Whole pixels, so text drawn on the draw list stays sharp.</summary>
    public static Vector2 Snap(Vector2 position) => new(MathF.Floor(position.X), MathF.Floor(position.Y));

    /// <summary>Shows a wrapped tooltip while the last item is hovered, even when it's disabled; a hint, if any, follows in muted text.</summary>
    public static void Tooltip(string text, string? hint = null) {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35);
        ImGui.TextUnformatted(text);
        if (hint != null) {
            ImGui.TextColored(Muted, hint);
        }

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

    /// <summary>A small filled circle as an item, centred in a line of <paramref name="lineHeight"/>.</summary>
    public static void Dot(Vector4 colour, float lineHeight, float? radius = null) {
        var r = radius ?? MathF.Round(ImGui.GetFontSize() * 0.22f);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(r * 2, lineHeight));
        ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(pos.X + r, pos.Y + lineHeight / 2), r, ImGui.GetColorU32(colour));
    }

    // ================================================================ buttons

    /// <summary>
    /// A square icon button with no background until hovered (the theme's hover and press colours).
    /// The icon is muted, and brightens on hover, unless given a colour.
    /// </summary>
    public static bool GhostIconButton(string id, FontAwesomeIcon icon, string? tooltip, Vector4? colour = null) {
        var size = new Vector2(ImGui.GetFrameHeight());
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button(id, size);
        ImGui.PopStyleColor();

        var lit = ImGui.IsItemHovered() || ImGui.IsItemActive();
        var centre = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        DrawIcon(ImGui.GetWindowDrawList(), icon, centre, ImGui.GetColorU32(colour ?? (lit ? Text : Muted)));
        if (tooltip != null) {
            Tooltip(tooltip);
        }

        return clicked;
    }

    public static float GhostIconButtonWidth => ImGui.GetFrameHeight();

    /// <summary>A text button with no background until hovered.</summary>
    public static bool GhostButton(string label, string? tooltip = null) {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor();
        if (tooltip != null) {
            Tooltip(tooltip);
        }

        return clicked;
    }

    /// <summary>A full-width row with an icon and a label and no background until hovered, such as "New channel".</summary>
    public static bool GhostRow(string id, FontAwesomeIcon icon, string label, string? tooltip = null) {
        var height = ImGui.GetFrameHeight();
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button(id, new Vector2(ImGui.GetContentRegionAvail().X, height));
        ImGui.PopStyleColor();

        var lit = ImGui.IsItemHovered() || ImGui.IsItemActive();
        DrawIconLabel(ImGui.GetItemRectMin(), height, icon, label, ImGui.GetColorU32(lit ? Text : Muted));
        if (tooltip != null) {
            Tooltip(tooltip);
        }

        return clicked;
    }

    /// <summary>
    /// A popup menu row: an icon, a label, and the whole width of the menu to click. Closes the menu.
    /// When disabled it is greyed out, but still shows a tooltip given right after it.
    /// </summary>
    public static bool MenuItem(FontAwesomeIcon icon, string label, bool enabled = true, Vector4? colour = null, Vector4? iconColour = null) {
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeight();
        var width = IconLabelWidth(icon, label) + style.FramePadding.X;
        var pos = ImGui.GetCursorScreenPos();

        ImGui.BeginDisabled(!enabled);
        // Its width counts towards the menu's, but it spans the whole menu.
        var clicked = ImGui.Selectable($"##{label}", false, (ImGuiSelectableFlags) ImGuiSelectableFlagsPrivate.SpanAvailWidth, new Vector2(width, height));
        DrawIconLabel(pos, height, icon, label, ImGui.GetColorU32(colour ?? Text), iconColour is { } own ? ImGui.GetColorU32(own) : null);
        ImGui.EndDisabled();
        return clicked && enabled;
    }

    /// <summary>
    /// A popup menu row for something on or off: a green check while on, a red cross while off (the shapes differ too, for
    /// anyone who can't tell the colours apart), then the label. Closes the menu.
    /// </summary>
    public static bool ToggleMenuItem(string label, bool on, bool enabled = true) =>
        MenuItem(on ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, label, enabled, iconColour: on ? Success : Error);

    private static float IconLabelWidth(FontAwesomeIcon icon, string label) {
        var style = ImGui.GetStyle();
        return style.FramePadding.X + FixedIconWidth(icon) + style.ItemInnerSpacing.X * 2 + ImGui.CalcTextSize(label).X;
    }

    private static void DrawIconLabel(Vector2 min, float height, FontAwesomeIcon icon, string label, uint colour, uint? iconColour = null) {
        var style = ImGui.GetStyle();
        var drawList = ImGui.GetWindowDrawList();
        var iconWidth = FixedIconWidth(icon);
        var x = min.X + style.FramePadding.X;
        DrawIcon(drawList, icon, new Vector2(x + iconWidth / 2, min.Y + height / 2), iconColour ?? colour);
        x += iconWidth + style.ItemInnerSpacing.X * 2;
        drawList.AddText(Snap(new Vector2(x, min.Y + (height - ImGui.GetTextLineHeight()) / 2)), colour, label);
    }

    /// <summary>
    /// A small rounded tag with a frame-coloured background, such as a command (in the monospaced
    /// font). It is a button: it lights up on hover. A placeholder ("+ nickname") is only outlined.
    /// </summary>
    public static bool Chip(string id, string text, bool mono, bool placeholder = false) {
        var scale = Scale;
        var frame = ImGui.GetFrameHeight();
        Vector2 textSize;
        using (mono ? MonoFont.Push() : null) {
            textSize = ImGui.CalcTextSize(text);
        }

        var padding = new Vector2(7 * scale, 2 * scale);
        var tagHeight = Math.Min(frame, textSize.Y + padding.Y * 2);
        var pos = ImGui.GetCursorScreenPos();
        // The item is a frame high, to line up with text beside it; the tag inside it is a little lower.
        var clicked = ImGui.InvisibleButton(id, new Vector2(textSize.X + padding.X * 2, frame));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered) {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var drawList = ImGui.GetWindowDrawList();
        var min = new Vector2(pos.X, pos.Y + MathF.Round((frame - tagHeight) / 2));
        var max = new Vector2(pos.X + textSize.X + padding.X * 2, min.Y + tagHeight);
        var rounding = 4 * scale;
        var background = active ? ImGuiCol.FrameBgActive : hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg;
        if (!placeholder || hovered) {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(background), rounding);
        }

        if (placeholder) {
            drawList.AddRect(min, max, ImGui.GetColorU32(Muted with { W = Muted.W * 0.6f }), rounding, ImDrawFlags.None, Math.Max(1, scale));
        }

        using (mono ? MonoFont.Push() : null) {
            var colour = placeholder && !hovered ? Muted : Text;
            drawList.AddText(Snap(new Vector2(min.X + padding.X, min.Y + (tagHeight - textSize.Y) / 2)), ImGui.GetColorU32(colour), text);
        }

        return clicked;
    }

    // ================================================================ badges

    /// <summary>A small rounded count (unread messages, invites) with its right edge at <paramref name="right"/>, centred on its Y.</summary>
    /// <returns>Its width.</returns>
    public static float Badge(Vector2 right, string text, Vector4 colour) {
        var scale = Scale;
        var textSize = ImGui.CalcTextSize(text);
        var height = MathF.Round(textSize.Y + 1 * scale);
        var width = Math.Max(height, MathF.Round(textSize.X + 9 * scale));
        var min = Snap(new Vector2(right.X - width, right.Y - height / 2));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(colour), height / 2);
        // White on the saturated badge colours reads in light and dark themes alike.
        drawList.AddText(Snap(new Vector2(min.X + (width - textSize.X) / 2, min.Y + (height - textSize.Y) / 2)), 0xFFFFFFFF, text);
        return width;
    }

    /// <summary>A count on the top-right corner of the last item, such as invites on the envelope. May reach into the window's padding.</summary>
    public static void CornerBadge(string text, Vector4 colour) {
        var max = ImGui.GetItemRectMax();
        var min = ImGui.GetItemRectMin();
        var windowMin = ImGui.GetWindowPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(windowMin, windowMin + ImGui.GetWindowSize(), false);
        Badge(new Vector2(max.X + 4 * Scale, min.Y + 3 * Scale), text, colour);
        drawList.PopClipRect();
    }

    // ================================================================ layout

    /// <summary>Puts the next item on the same line if <paramref name="width"/> still fits there.</summary>
    public static void SameLineIfFits(float width, float spacing = -1) {
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var gap = spacing >= 0 ? spacing : ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + gap + width <= right) {
            ImGui.SameLine(0, spacing);
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
        ImGuiHelpers.ScaledDummy(4);
        CentredLine(title, width, null);
        if (text != null) {
            ImGuiHelpers.ScaledDummy(2);
            foreach (var line in Wrap(text, wrap)) {
                CentredLine(line, width, Muted);
            }
        }

        ImGuiHelpers.ScaledDummy(8);
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
