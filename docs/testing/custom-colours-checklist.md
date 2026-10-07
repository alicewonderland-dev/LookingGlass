# Custom colours: in-game checklist

Custom channel colours (see
[Custom colours](../design.md#custom-colours)). The owner's decision
(2026-10-07): if exact colours don't show correctly in the game, LookingGlass
offers the game's whole UIColor table instead. This checklist decides that, so
note for each step what you actually saw.

You need character **A** (yours, on this build, registered) in a channel
`sky` (`/lgc1`, nickname `sky`) with at least one other member **B** who can
send messages (or use the echo bot on a test server: `/lgdebug` > Channel
tools > Simulate incoming works without anyone). Check with **Colour the whole
line in a channel's colour** on, then the chat steps again with it off. Do
steps 1 to 9 without ChatTwo, then 10 to 14 with ChatTwo on.

## The colour test (no channel changes)

- [ ] 1. `/lgdebug colours` prints fifteen lines: for each of five colours
      (`#FF66CC`, `#33DDAA`, `#8844FF`, `#FFAA00`, `#1A2B6D`), one "exact,
      game colour N under it", one "closest game colour N only", one "exact
      only, nothing under it". The debug window's **Colour test** section
      lists the same colours with their swatches and rows, and its button
      prints the same lines.
- [ ] 2. In the game's own chat log: does each "exact" line (the first and
      the third of each three) show the exact colour, matching the swatch in
      the debug window? Does each "closest only" line show a visibly
      different, nearby colour? Note any line that shows white or the chat
      channel's own colour instead.
- [ ] 3. `/lgdebug colours #3FA7D6 00ff00` prints six lines for those two;
      `/lgdebug colours #nope` prints a usage line and nothing else; plain
      `/lgdebug` still opens the debug window.

## The picker

- [ ] 4. Open `sky`'s menu (⋮) > **Colour...**: the 40 swatches, **Default**
      and **Custom...** are there. **Custom...** opens a colour wheel, a
      **Colour code** field, a **Preview** line on a dark background, a
      swatch of the closest game colour, **Use this colour** and **Back**.
- [ ] 5. Turn the wheel: the code follows. Type `#ff66cc` (and then
      `FF66CC`, without `#`): the wheel follows, and once you leave the field
      the code reads `#FF66CC`. Type `#FF66C` or `#GG66CC`: a plain line says
      what a code looks like, and **Use this colour** is greyed out.
- [ ] 6. The preview shows `sky`'s tag (and the rest of the line too, with
      the whole-line setting on) in the colour, and changes as you turn the
      wheel. Hovering the closest-colour swatch says "The closest game
      colour." (advanced mode: its UIColor row).
- [ ] 7. Pick a very dark colour (`#1A2B6D`, or `#000080`): the line "This
      colour is very dark, so it may be hard to read in chat. You can still
      use it." shows, and **Use this colour** still works. A light colour
      (`#FF66CC`) shows no such line.
- [ ] 8. Use `#FF66CC`. Open **Colour...** again: no swatch is outlined, and an
      outlined pink swatch sits beside **Custom...**; its tooltip names
      `#FF66CC`. Clicking it (or **Custom...**) opens the wheel on `#FF66CC`.
      Picking a swatch, then **Default**, works as before.

## The game (no ChatTwo)

With `sky` on `#FF66CC`:

- [ ] 9. **Everywhere it shows**, compare with the pink swatch:
  - a message in `sky` in the chat log: the tag (and with the whole-line
    setting on, the sender and text) in exact pink? With it off, the text
    after the tag back in the chat channel's own colour?
  - a message with an item link (or a map link) in it: the text after the
    link still pink, not the closest game colour or the channel's colour?
  - a caught-up message (log out, have B send, log in): the tag pink, the
    time uncoloured, the rest as above.
  - `/lgc1` with no message (talking in the channel): with verbose channel
    messages on, "Now talking in [sky]." has `[sky]` in pink within the blue;
    the server info bar shows "LG [sky]" in pink.
  - right-click B's name > **Invite to LookingGlass**: `[sky]` in pink in
    the submenu.
  - the channel list's bar and the dot before the name, a channel window's
    tab and B's name in it: pink (these are drawn by LookingGlass, always
    exact).

## ChatTwo

- [ ] 10. Turn ChatTwo on. `/lgdebug colours` again: in ChatTwo, do the
      "exact" lines show the exact colour, and the "closest only" lines the
      nearby one? (From its source we expect exact.) Does the text after
      "[LookingGlass] " start in blue and every line end cleanly (no colour
      running into the next line)?
- [ ] 11. A message in `sky`: tag (and whole line) in exact pink, as in the
      game's chat log; with the whole-line setting off, the text after the
      tag in the chat channel's colour. A message with an item link: the text
      after it still pink.
- [ ] 12. `/lgc1` with no message: ChatTwo's input shows "LookingGlass
      [sky]" in exact pink.
- [ ] 13. Right-click B's name in ChatTwo > Integrations > **Invite to
      LookingGlass**: `[sky] ...` in pink.
- [ ] 14. Note anything ChatTwo shows differently from the game's chat log.

## Persistence and older settings

- [ ] 15. Give `fc` a swatch colour and `sky` a custom one. Reload the plugin
      (`/xlplugins`, disable and enable) and log out and in: both kept. Log in
      with another character: its own colours, not these.
- [ ] 16. A channel coloured before this build (a swatch) keeps its colour
      unchanged after updating, everywhere.
- [ ] 17. Leave `sky` (or have it disbanded): its custom colour is gone from
      the settings file once the channel list is in (it doesn't come back if
      you're invited again).

## Decision

- [ ] The game's chat log shows exact colours: keep custom colours.
- [ ] It doesn't (or only some places do): note where, and the fallback (the
      closest row) is what shows there; offer the whole UIColor table instead.
