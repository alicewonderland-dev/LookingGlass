using LookingGlass.Core.Client;
using LookingGlass.Protocol;
using static LookingGlass.Tests.Harness;

namespace LookingGlass.Tests;

/// <summary>
/// The game's text commands (&lt;t&gt;, &lt;me&gt;) in a line sent to a channel: which are replaced, by what, and what is
/// left alone (see <see cref="TextCommands"/>). The game's own expander is the plugin's; here a fake stands in for it.
/// </summary>
public sealed class TextCommandTests {
    private static readonly TypedLink Potion = new(new ChatLink.Item(4551), "Potion");

    private static string M(int index) => LinkText.Marker(index).ToString();

    /// <summary>What the game would say now: a target, a focus target, the player, party member 2, a position.</summary>
    private static string? Game(string command) => command switch {
        "<t>" => "Bob Builder",
        "<f>" => "Alice LiddellOmega",
        "<me>" => "Mira Moon",
        "<2>" => "Kit Fox",
        "<pos>" => "Limsa Lominsa Lower Decks ( 9.5 , 11.2 )",
        _ => null,
    };

    private static (string Text, int Replaced) Resolve(string text, Func<string, string?>? resolve = null) {
        var (line, replaced) = TextCommands.Resolve(TypedLine.Plain(text), resolve ?? Game);
        return (line.Text, replaced);
    }

    [Fact]
    public void TheCommonTextCommandsAreTheOnesReplaced() {
        Assert.Equal(
            ["<t>", "<tt>", "<f>", "<me>", "<mo>", "<lt>", "<1>", "<2>", "<3>", "<4>", "<5>", "<6>", "<7>", "<8>", "<r>", "<pos>"],
            TextCommands.Supported);
    }

    [Fact]
    public void ATextCommandIsReplacedWithWhatItStandsFor() {
        Assert.Equal(("heal Bob Builder please", 1), Resolve("heal <t> please"));
        Assert.Equal(("Mira Moon here, Kit Fox there", 2), Resolve("<me> here, <2> there"));
        Assert.Equal(("at Limsa Lominsa Lower Decks ( 9.5 , 11.2 )", 1), Resolve("at <pos>"));
        // The game's cross-world mark before a world's name stays, as the game shows it.
        Assert.Equal(("focus: Alice LiddellOmega", 1), Resolve("focus: <f>"));
        // Side by side, and with nothing else.
        Assert.Equal(("Bob BuilderMira Moon", 2), Resolve("<t><me>"));
        Assert.Equal(("Bob Builder", 1), Resolve("<t>"));
    }

    [Fact]
    public void EachTextCommandIsAskedOnceAndAsTyped() {
        var asked = new List<string>();
        var (text, replaced) = Resolve("<t> and <t> and <T>", command => {
            asked.Add(command);
            return command == "<t>" ? "Bob" : null;
        });

        // The game decides about case: here it doesn't know <T>, so that one stays.
        Assert.Equal("Bob and Bob and <T>", text);
        Assert.Equal(2, replaced);
        Assert.Equal(["<t>", "<T>"], asked);
    }

    [Fact]
    public void UnknownAndUnresolvedTextCommandsStayAsTyped() {
        // Nothing targeted, no such party member: as typed.
        Assert.Equal(("<tt> and <mo> and <8>", 0), Resolve("<tt> and <mo> and <8>"));
        // Not one LookingGlass replaces: never even asked.
        var asked = new List<string>();
        var (text, replaced) = Resolve("<se.1> <hp> <wait.3> <job> <tank> <> < t> <t >", command => {
            asked.Add(command);
            return "X";
        });
        Assert.Equal("<se.1> <hp> <wait.3> <job> <tank> <> < t> <t >", text);
        Assert.Equal(0, replaced);
        Assert.Empty(asked);
        // Handed back unchanged, empty, blank, or the resolver failing: as typed.
        Assert.Equal(("<t>", 0), Resolve("<t>", command => command));
        Assert.Equal(("<t>", 0), Resolve("<t>", _ => ""));
        Assert.Equal(("<t>", 0), Resolve("<t>", _ => "  "));
        Assert.Equal(("<t> <me>", 0), Resolve("<t> <me>", _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void LinksAndLinkPlaceholdersAreLeftForTheLinkRules() {
        var asked = new List<string>();
        var line = new TypedLine($"look {M(0)} <t> <item> <flag> <status>", [Potion]);
        var (resolved, replaced) = TextCommands.Resolve(line, command => {
            asked.Add(command);
            return Game(command);
        });

        Assert.Equal($"look {M(0)} Bob Builder <item> <flag> <status>", resolved.Text);
        Assert.Equal(1, replaced);
        Assert.Equal(["<t>"], asked);
        Assert.Equal(line.Links, resolved.Links);

        // And the link still goes as a link, over its name, with the name as plain text beside it.
        var (message, leftOut) = LinkText.Compose(TextCommands.Resolve(new TypedLine($"give {M(0)} to <t>", [Potion]), Game).Line);
        Assert.False(leftOut);
        Assert.Equal("give [Potion] to Bob Builder", message.Text);
        var link = Assert.Single(message.Links);
        Assert.Equal("[Potion]", message.Text.Substring(link.Start, link.Length));
    }

    [Fact]
    public void WhatATextCommandStandsForIsPlainText() {
        // No game formatting, no line breaks, no link markers, nothing read as a placeholder or a text command again.
        Assert.Equal(("hi Bob Builder", 1), Resolve("hi <t>", _ => "Bob\u0002\u0003 Builder"));
        Assert.Equal(("hi Bob Builder", 1), Resolve("hi <t>", _ => "Bob\nBuilder"));
        Assert.Equal(("hi Bob", 1), Resolve("hi <t>", _ => $"Bob{M(0)}"));
        Assert.Equal(("hi (item) (me)", 1), Resolve("hi <t>", _ => "<item> <me>"));
        var (line, _) = TextCommands.Resolve(TypedLine.Plain("hi <t>"), _ => "<item>");
        Assert.False(LinkText.HasLink(line.Text));
        Assert.Null(TextCommands.CleanValue(null));
        Assert.Null(TextCommands.CleanValue("\u0002\u0003"));
    }

    [Fact]
    public void ALineWithoutTextCommandsIsLeftAsItIs() {
        var line = new TypedLine($"look {M(0)} <item>", [Potion]);
        var asked = 0;
        var (resolved, replaced) = TextCommands.Resolve(line, _ => {
            asked++;
            return "X";
        });
        Assert.Same(line, resolved);
        Assert.Equal(0, replaced);
        Assert.Equal(0, asked);
        Assert.False(TextCommands.HasCommand("a <b> c <hp>"));
        Assert.True(TextCommands.HasCommand("a <ME> c"));
    }

    [Fact]
    public void ALineIsCutIntoTextTextCommandsAndLinks() {
        var pieces = TextCommands.Split($"hi <t>{M(0)}<item> <se.1><1>");
        Assert.Equal([
            new TextCommands.Piece("hi ", TextCommands.PieceKind.Text),
            new TextCommands.Piece("<t>", TextCommands.PieceKind.Command),
            new TextCommands.Piece(M(0), TextCommands.PieceKind.Link),
            new TextCommands.Piece("<item>", TextCommands.PieceKind.Link),
            new TextCommands.Piece(" <se.1>", TextCommands.PieceKind.Text),
            new TextCommands.Piece("<1>", TextCommands.PieceKind.Command),
        ], pieces);
        Assert.Equal($"hi <t>{M(0)}<item> <se.1><1>", string.Concat(pieces.Select(piece => piece.Text)));
        Assert.Empty(TextCommands.Split(""));
    }

    [Fact]
    public async Task AReceivedMessageIsNeverLookedAtForTextCommands() {
        var server = new Harness();
        try {
            var alice = await server.RegisterAsync("Alice Test");
            var bob = await server.RegisterAsync("Bob Test");
            var channelId = await alice.Session.CreateChannelAsync("Tea Party", Ct);
            await alice.Session.InviteAsync(channelId, "Bob Test", ProtocolInfo.DebugWorldName, Ct);
            await WaitFor(() => bob.Session.Snapshot.Invites.FirstOrDefault(i => i.ChannelId == channelId && i.ChannelName != null));
            await bob.Session.RespondToInviteAsync(channelId, true, Ct);
            await WaitFor(() => bob.Session.Snapshot.FindChannel(channelId) is { HasKey: true, RekeyPending: false } c ? c : null);

            // As an older client (or ExtraChat's way) would send it: the text commands as typed. They arrive as typed.
            const string typed = "heal <t> please, <me> and <1>";
            await alice.Session.SendAsync(channelId, LinkedText.Plain(typed), Ct);
            var atBob = await WaitFor(() => bob.Messages.FirstOrDefault(m => !m.IsOwn && m.Text?.StartsWith("heal") == true));
            Assert.Equal(typed, atBob.Text);
        } finally {
            await server.DisposeAsync();
            DeleteDirectory(server.DataDirectory);
        }
    }
}
