namespace LookingGlass.Core.Client;

/// <summary>How a LookingGlass line in the chat log looks: three tones, each one colour.</summary>
public enum NoticeTone {
    /// <summary>Information and status (Now talking in, Stopped talking in, Not sent, usage): LookingGlass blue.</summary>
    Info,

    /// <summary>Something to look at (a key changed, ExtraChat is on, the server not showing a membership): light red.</summary>
    Warning,

    /// <summary>
    /// Something that may mean messages reach someone they shouldn't, or the server is lying (a forked membership, members
    /// shown different memberships, a removal that hasn't taken effect, a relayed registration code): dark red.
    /// </summary>
    Critical,
}

/// <summary>
/// The one place the colours of LookingGlass's own lines in the chat log are chosen: rows of the game's UIColor sheet, so
/// chat shows them natively (ChatTwo too). Values checked against the sheet (its Dark column, 0xRRGGBBAA).
/// </summary>
public static class NoticeColours {
    /// <summary>LookingGlass blue (0x0099FF): the "[LookingGlass]" prefix, information, and the default [LGC] tag.</summary>
    public const ushort Blue = 37;

    /// <summary>Light red (0xFF8080), for warnings.</summary>
    public const ushort LightRed = 508;

    /// <summary>Dark red (0xAE0000), for critical warnings.</summary>
    public const ushort DarkRed = 534;

    /// <summary>The kinds of notice that are critical whatever their level.</summary>
    public static readonly IReadOnlySet<NoticeKind> CriticalKinds = new HashSet<NoticeKind> {
        NoticeKind.MembershipForked,
        NoticeKind.MembersShownDifferently,
        NoticeKind.RemovalNotInEffect,
        NoticeKind.ServerRefusesKey,
        NoticeKind.RelayedRegistrationCode,
    };

    /// <summary>
    /// The tone of a notice: <see cref="CriticalKinds"/> are critical; otherwise a warning or an error is a warning, and
    /// anything else information.
    /// </summary>
    public static NoticeTone ToneOf(NoticeLevel level, NoticeKind kind = NoticeKind.General) =>
        CriticalKinds.Contains(kind) ? NoticeTone.Critical
        : level >= NoticeLevel.Warning ? NoticeTone.Warning
        : NoticeTone.Info;

    /// <summary>The UIColor row a tone is shown in.</summary>
    public static ushort Of(NoticeTone tone) => tone switch {
        NoticeTone.Critical => DarkRed,
        NoticeTone.Warning => LightRed,
        _ => Blue,
    };
}
