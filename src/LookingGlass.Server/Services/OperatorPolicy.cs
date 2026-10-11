using LookingGlass.Server.Data;

namespace LookingGlass.Server.Services;

/// <summary>The tools an operator acts with (see "Operator actions, one layer for every tool" in docs/design.md).</summary>
public enum OperatorTool {
    /// <summary><c>LookingGlass.Server --ban</c> and the rest, run on the server (another process).</summary>
    CommandLine,

    /// <summary>The operator web page, over Tailscale only (see <see cref="Admin.OperatorPage"/>).</summary>
    WebPage,

    /// <summary>The plugin's operator panel, for key-pinned operator characters (see <see cref="Realtime.RequestHandler"/>, capability "operator.v1").</summary>
    Plugin,
}

/// <summary>What an operator tool asks <see cref="OperatorActions"/> to do.</summary>
public enum OperatorAction {
    /// <summary>List the flags and the bans.</summary>
    See,

    Ban,
    Lift,

    /// <summary>Pin (or restore) an operator character's keys.</summary>
    Pin,

    /// <summary>Let a character's identity key sign in again after its "Sign out everywhere else".</summary>
    AllowKeyLogin,
}

/// <summary>Why <see cref="OperatorActions"/> didn't do what it was asked; <see cref="None"/> when it did.</summary>
public enum OperatorRefusal {
    None,

    /// <summary>This tool can't do that at all (the plugin pinning keys, say).</summary>
    NotThisTool,

    /// <summary>The days or the reason aren't allowed (the command line checks them before it asks).</summary>
    NotValid,

    /// <summary>No character of that name@world is registered.</summary>
    NoSuchCharacter,

    /// <summary>Not a user ID, a name@world, or an address or prefix within the widths a ban may cover.</summary>
    NotATarget,

    /// <summary>This tool may not ban or lift addresses (the plugin).</summary>
    AddressesNotHere,

    /// <summary>This tool may not force a ban on the server's own or its proxy's address (the plugin).</summary>
    ForceNotHere,

    /// <summary>The server's own or its proxy's address, or a prefix holding one, without force.</summary>
    ProxyAddress,

    /// <summary>A character listed as an operator, which this tool may not ban (or not without force).</summary>
    OperatorCharacter,

    /// <summary>This tool's bans for the hour are used up.</summary>
    HourLimit,

    /// <summary>No ban on that in force.</summary>
    NothingToLift,

    /// <summary>The character isn't registered (so it has no keys to pin, nor a key login to turn back on).</summary>
    NotRegistered,

    /// <summary>The character isn't listed in <c>LookingGlass:Operators:Characters</c>.</summary>
    NotListed,

    /// <summary>The keys typed or shown aren't the character's current keys.</summary>
    KeysDiffer,

    /// <summary>Its key login isn't turned off: nothing to do.</summary>
    KeyLoginNotOff,
}

/// <summary>What a ban by one tool is about, as far as the policy goes.</summary>
/// <param name="IsAddress">An address or prefix, not a character.</param>
/// <param name="IsProxyAddress">The server's own or its proxy's address, or a prefix holding one (see <see cref="Hosting.ClientAddresses.ProxyProblem"/>).</param>
/// <param name="IsOperatorCharacter">A character listed in <c>LookingGlass:Operators:Characters</c>.</param>
public readonly record struct PolicyTarget(bool IsAddress, bool IsProxyAddress = false, bool IsOperatorCharacter = false);

/// <summary>What one tool may do: one row of <see cref="OperatorPolicy"/>'s table.</summary>
/// <param name="FullAddresses">Sees addresses in full; otherwise shortened (see <see cref="Hosting.ClientAddresses.Shorten"/>).</param>
/// <param name="Characters">May ban and lift bans on characters.</param>
/// <param name="Addresses">May ban and lift bans on addresses and prefixes (automatic blocks included).</param>
/// <param name="Force">May force a ban on the server's own or its proxy's address.</param>
/// <param name="OperatorCharactersWithForce">May ban a character listed as an operator, with force (otherwise never).</param>
/// <param name="BansPerHour">Bans it may make in an hour; null for no limit.</param>
/// <param name="BansPerHourPerOperator">The hour's bans are counted per operator character, not for the tool as a whole.</param>
/// <param name="Pin">May pin or restore an operator character's keys.</param>
/// <param name="AllowKeyLogin">May let a character's key sign in again.</param>
/// <param name="Who">Who the tool records as having done it.</param>
public sealed record ToolRules(bool FullAddresses, bool Characters, bool Addresses, bool Force, bool OperatorCharactersWithForce, int? BansPerHour,
    bool BansPerHourPerOperator, bool Pin, bool AllowKeyLogin, string Who);

/// <summary>
/// What each operator tool may do, written once (see the table in "Operator actions, one layer for every tool" in
/// docs/design.md). <see cref="OperatorActions"/> asks it before every ban, lift, pin and key login change, whichever tool
/// asks; the target is worked out first (whom, and whether it is an address, the proxy's, or an operator character).
/// </summary>
public static class OperatorPolicy {
    /// <summary>The plugin's bans an hour, per operator character.</summary>
    public const int PluginBansPerHour = 10;

    /// <summary>The web page's bans an hour, whoever is signed in.</summary>
    public const int WebPageBansPerHour = 30;

    private static readonly ToolRules CommandLine = new(FullAddresses: true, Characters: true, Addresses: true, Force: true, OperatorCharactersWithForce: true,
        BansPerHour: null, BansPerHourPerOperator: false, Pin: true, AllowKeyLogin: true, Who: "the Unix user who ran it (SUDO_USER)");

    private static readonly ToolRules WebPage = new(FullAddresses: true, Characters: true, Addresses: true, Force: true, OperatorCharactersWithForce: false,
        BansPerHour: WebPageBansPerHour, BansPerHourPerOperator: false, Pin: true, AllowKeyLogin: false, Who: "the Tailscale login");

    private static readonly ToolRules Plugin = new(FullAddresses: false, Characters: true, Addresses: false, Force: false, OperatorCharactersWithForce: false,
        BansPerHour: PluginBansPerHour, BansPerHourPerOperator: true, Pin: false, AllowKeyLogin: false, Who: "the operator character");

    /// <summary>One tool's row of the table.</summary>
    public static ToolRules For(OperatorTool tool) => tool switch {
        OperatorTool.CommandLine => CommandLine,
        OperatorTool.WebPage => WebPage,
        OperatorTool.Plugin => Plugin,
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>The tool as the database records it (see <see cref="BanTools"/>).</summary>
    public static string Code(OperatorTool tool) => tool switch {
        OperatorTool.CommandLine => BanTools.CommandLine,
        OperatorTool.WebPage => BanTools.WebPage,
        OperatorTool.Plugin => BanTools.Plugin,
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>Whether <paramref name="tool"/> may do <paramref name="action"/> to <paramref name="target"/>, and if not, why.</summary>
    /// <param name="force">The tool says the server's own or its proxy's address (or, on the command line, an operator character) is meant.</param>
    /// <param name="bansThisHour">The bans this tool (or, for the plugin, this operator character) made in the last hour.</param>
    public static OperatorRefusal Check(OperatorTool tool, OperatorAction action, PolicyTarget target = default, bool force = false, int bansThisHour = 0) {
        var rules = For(tool);
        switch (action) {
            case OperatorAction.See:
                return OperatorRefusal.None;
            case OperatorAction.Pin:
                return rules.Pin ? OperatorRefusal.None : OperatorRefusal.NotThisTool;
            case OperatorAction.AllowKeyLogin:
                return rules.AllowKeyLogin ? OperatorRefusal.None : OperatorRefusal.NotThisTool;
            case OperatorAction.Lift:
                return target.IsAddress ? (rules.Addresses ? OperatorRefusal.None : OperatorRefusal.AddressesNotHere)
                    : rules.Characters ? OperatorRefusal.None : OperatorRefusal.NotThisTool;
            case OperatorAction.Ban:
                if (target.IsAddress ? !rules.Addresses : !rules.Characters) {
                    return target.IsAddress ? OperatorRefusal.AddressesNotHere : OperatorRefusal.NotThisTool;
                }

                if (force && !rules.Force) {
                    return OperatorRefusal.ForceNotHere;
                }

                if (target.IsProxyAddress && !force) {
                    return OperatorRefusal.ProxyAddress;
                }

                if (target.IsOperatorCharacter && !(rules.OperatorCharactersWithForce && force)) {
                    return OperatorRefusal.OperatorCharacter;
                }

                return rules.BansPerHour is { } limit && bansThisHour >= limit ? OperatorRefusal.HourLimit : OperatorRefusal.None;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
