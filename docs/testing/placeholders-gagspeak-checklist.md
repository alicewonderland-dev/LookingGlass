# Text commands and GagSpeak: in-game checklist

Text commands such as `<t>` and `<me>` in LookingGlass channel messages (see
[Text commands in messages](../design.md#text-commands-in-messages)), and the
GagSpeak finding (see
[Garbled speech with GagSpeak](../design.md#garbled-speech-with-gagspeak)).
You need two characters in the same channel, **A** (sending) and **B**
(receiving, on another PC or client), both on this build. Below, `[sky]` is
the channel's tag and `/lgc1` its command. "Sticky" means after `/lgc1` on its
own, typing the line without a command.

Every time, check on B: the text command is replaced by a plain name (not a
link, no odd characters), or stays as typed where the step says so. Nothing
appears in Say, Party or any linkshell. For a cross-world name, compare with
what the same text command shows in `/echo` on A: the same name, world and
cross-world mark.

## Each text command

For each row, send `/lgc1 test X` and, sticky, `test X`, in the game's chat
box (ChatTwo off) and in ChatTwo's main input (ChatTwo on): four ticks a row.

| Text command | Set up | Vanilla /lgc | Vanilla sticky | ChatTwo /lgc | ChatTwo sticky |
|---|---|---|---|---|---|
| `<t>` | target a player (one from another world too) | [ ] | [ ] | [ ] | [ ] |
| `<t>` | target an NPC or a monster | [ ] | [ ] | [ ] | [ ] |
| `<tt>` | target someone who targets someone | [ ] | [ ] | [ ] | [ ] |
| `<f>` | set a focus target | [ ] | [ ] | [ ] | [ ] |
| `<me>` | (none) | [ ] | [ ] | [ ] | [ ] |
| `<mo>` | hover a player, then press Enter without moving the mouse | [ ] | [ ] | [ ] | [ ] |
| `<lt>` | target someone, then clear the target | [ ] | [ ] | [ ] | [ ] |
| `<1>` to `<8>` | in a party (`<2>` and the last member at least) | [ ] | [ ] | [ ] | [ ] |
| `<r>` | after someone sent A a tell | [ ] | [ ] | [ ] | [ ] |
| `<pos>` | anywhere in the open world | [ ] | [ ] | [ ] | [ ] |

- [ ] `<pos>` arrives as the place and coordinates as text (no map link, no
      link arrow). Write down what the game itself sends for `<pos>` in
      `/echo`, for comparison.
- [ ] `<r>`: if it stays as typed even after a tell, say so (the game may not
      expand it through this function).
- [ ] `/lgc sky hi <t>` (by nickname): the same as by number.
- [ ] Two at once, with text between: `/lgc1 <me> heals <t>`.
- [ ] Upper case: `/lgc1 <T>`. Compare with what `/echo <T>` shows: B should
      see the same (replaced or as typed).
- [ ] A text command beside a link: `/lgc1 give ` an item ` to <t>`. The item
      is a link, the name is text after it.

## Not replaced

- [ ] With nothing targeted, `/lgc1 heal <t>`: B sees `heal <t>`. Then, with
      nothing targeted, `/s heal <t>` in game chat (or `/echo heal <t>`):
      write down what the game does (as typed, or nothing). If the game sends
      nothing, say so.
- [ ] `<8>` with no eighth party member: stays as typed.
- [ ] An unknown text command, `/lgc1 hp <hp> job <job> se <se.1>`: B sees it
      exactly as typed.
- [ ] B sends `<t>` from a build before this one (or types it with nothing
      targeted): A sees `<t>`, never A's own target's name.
- [ ] `dalamud.log` while sticky: the "sending" entry ends with "N text
      command(s) replaced", and no name appears anywhere in the
      `[LookingGlass]` lines.
- [ ] Game chat still expands after LookingGlass has: target someone, type
      `/lgc1 hi <t>`, then `/s hi <t>` and `/echo <t>`, and run a macro with
      `/echo <t>`. Each shows the target's name as normal. (LookingGlass uses
      the game's own expander, so this checks it leaves the game's result
      alone.)

## GagSpeak (nothing to turn on yet)

GagSpeak has no way for another plugin to garble a text (see design.md), so
there is no option yet. Check only that nothing changed:

- [ ] With GagSpeak not loaded: a channel's menu (the ⋮ button) has no
      GagSpeak option.
- [ ] With GagSpeak loaded and a gag on: a channel's menu still has no
      GagSpeak option; `/lgc1 hello <t>` and the same line sticky are sent
      (write down whether B sees it garbled or not: GagSpeak's own hook may
      or may not have rewritten the line first), and links in it are still
      links.
- [ ] With GagSpeak loaded, garbler on and Say picked in GagSpeak: `/s hello`
      is garbled in Say as usual, and LookingGlass says nothing about it.
