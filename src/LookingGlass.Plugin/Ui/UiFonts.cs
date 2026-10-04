using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace LookingGlass.Plugin.Ui;

/// <summary>
/// Fonts the windows use besides Dalamud's own: the user's default font, larger, for the selected
/// channel's name. Made once with the plugin and disposed with it. Until the atlas has built it,
/// pushing it keeps the current font, so the name is simply drawn at the normal size meanwhile.
/// </summary>
public sealed class UiFonts : IDisposable {
    /// <summary>The header font's size, relative to the default font.</summary>
    private const float HeaderScale = 1.45f;

    public UiFonts(IUiBuilder ui) {
        // A negative size is relative to the default font's, so it follows the user's font settings.
        this.Header = ui.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(toolkit => toolkit.AddDalamudDefaultFont(-HeaderScale, null)));
    }

    public IFontHandle Header { get; }

    public void Dispose() => this.Header.Dispose();
}
