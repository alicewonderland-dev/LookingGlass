using LookingGlass.Core.Util;

namespace LookingGlass.Core.Client;

/// <summary>
/// The words of the channel colour picker's custom colour part (see <see cref="ChannelColour"/>): the same in both modes,
/// and in plain words (tested).
/// </summary>
public static class ColourWords {
    public const string CustomButton = "Custom...";

    public const string CustomTooltip = "Pick any colour: on the wheel, or by typing its code.";

    public const string CustomTitle = "Custom colour";

    public const string CustomExplanation = "Turn the wheel, or type a colour code such as #3FA7D6.";

    public const string CodeHint = "#RRGGBB";

    public const string CodeProblem = "Type a colour code like #3FA7D6: a # and then six digits or letters from A to F.";

    public const string HardToRead = "This colour is very dark, so it may be hard to read in chat. You can still use it.";

    public const string Preview = "Preview";

    /// <summary>The sample message in the preview.</summary>
    public const string SampleText = "Hello! This is how it will look.";

    public const string Fallback = "Where the exact colour can't be shown, the closest game colour is used:";

    public const string Use = "Use this colour";

    public const string Back = "Back";

    /// <summary>The tooltip of the custom colour's swatch when the channel has one.</summary>
    public static string CurrentCustom(uint rgb) => $"This channel's custom colour, {HexColour.Format(rgb)}. Click to change it.";

    /// <summary>Every fixed string, for the plain-language test.</summary>
    public static IEnumerable<string> All() => [
        CustomButton, CustomTooltip, CustomTitle, CustomExplanation, CodeProblem, HardToRead, Preview, SampleText, Fallback, Use, Back,
        CurrentCustom(0x3FA7D6),
    ];
}
