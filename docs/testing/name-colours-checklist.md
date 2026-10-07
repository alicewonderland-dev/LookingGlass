# Name colours: in-game checklist

A colour for a person's name (see
[Name colours](../design.md#name-colours)). The owner's decision
(2026-10-07): one colour per person, everywhere, kept on this computer only.

You need character **A** (yours, on this build, registered) in two channels,
`sky` (`/lgc1`, nickname `sky`) and `fc` (`/lgc2`), and character **B** (on
another PC or client, registered on the same server) in both. Steps marked
**(A and B)** need B to send messages; on a test server the echo bot can stand
in for B in one channel (`/lgdebug` > Channel tools > Simulate incoming also
works without anyone, as "Simulated Sender"). Unless a step says otherwise,
**Colour the whole line in a channel's colour** is on, `sky` has the custom
colour `#33DDAA` (teal) and `fc` the default colour. Do steps 1 to 15 without
ChatTwo, then 16 to 19 with ChatTwo on.

Throughout: nothing LookingGlass prints in `dalamud.log` (`/xllog`) names a
person together with a colour.

## Setting a colour

- [ ] 1. In the main window, select `sky`. Right-click B's row in the member
      list: a small menu shows B's `Name@World` and **Name colour...** (with a
      palette icon). B's ⋮ menu has **Name colour...** too.
- [ ] 2. Choose **Name colour...**: the colour wheel, the **Colour code**
      field, a **Preview** on a dark background (`<`, B's `Name@World` in the
      colour, `> Hello! This is how it will look.` in teal), the closest game
      colour, and **Use this colour**, **Default (selected)** and **Cancel**.
      The wheel starts on teal (the channel's colour).
- [ ] 3. Type `#FF66CC`: the wheel and the preview's name follow. Type
      `#FF66C`: the plain line about colour codes shows and **Use this
      colour** is greyed out. Type `#1A2B6D`: the line about very dark colours
      shows, and **Use this colour** still works. Go back to `#FF66CC` and
      **Use this colour**.
- [ ] 4. B's name in `sky`'s member list is pink. Select `fc`: B's name is
      pink there too.
- [ ] 5. Right-click your own row (A) in the member list: the same menu.
      Give A `#FFAA00` (orange). A's name is orange in both member lists.
- [ ] 6. **Cancel** changes nothing; pressing Escape or clicking outside the
      popup changes nothing.

## Game chat (no ChatTwo)

- [ ] 7. **(A and B)** B says something in `sky`. In the game's chat log:
      `[sky]` teal, `<` teal, B's `Name@World` exact pink, `> ` and the
      message teal again (not pink, not the closest game colour, not the chat
      channel's own colour).
- [ ] 8. **(A and B)** B says something in `fc` (default colour): `[LGC2]` in
      LookingGlass blue, B's `Name@World` pink, the brackets and the message
      in the chat channel's own colour.
- [ ] 9. **(A and B)** B sends a message with an item link in the middle in
      `sky` (say "look at [item] here"): the name pink, "look at " teal, the
      item link as the game shows item links (clickable), and " here" after
      it still teal. Hover and click the link: it works as before.
- [ ] 10. A sends a message in `sky`: A's name orange, the rest teal.
- [ ] 11. Settings: turn **Colour the whole line in a channel's colour** off.
      **(A and B)** B says something in `sky`: `[sky]` teal, B's name pink,
      everything else (brackets, message, an item link's surrounding text) in
      the chat channel's own colour. Turn the setting back on.
- [ ] 12. Give `sky` a swatch colour (a game colour, say a green) instead of
      teal. **(A and B)** B says something with an item link: the name pink,
      the rest green, including after the link. Put `sky` back on `#33DDAA`.
- [ ] 13. **(A and B)** A caught-up message: log out, have B send in `sky`,
      log in. `[sky]` teal, the time uncoloured, B's name pink, the message
      teal.

## Channel windows

- [ ] 14. Open `sky` in a channel window (right-click it in the channel list).
      B's name on B's lines is pink, A's orange; the messages are as before.
      Older lines from the chat log on this computer (if kept) show the same
      colours.
- [ ] 15. Right-click one of B's lines (on the name or the message): the menu
      has **Copy** and **Name colour...**. Pick another colour (say
      `#8844FF`): B's name changes at once in the window, in both member
      lists, and in the next chat line. A notice line (blue or red) has only
      **Copy**.

## ChatTwo

- [ ] 16. Turn ChatTwo on. **(A and B)** B says something in `sky`: the name in
      B's exact colour, `> ` and the message back in teal; with an item link,
      the text after it still teal. Nothing runs on into the next line.
- [ ] 17. **(A and B)** The same in `fc` (default colour): the name coloured,
      the rest in the chat channel's colour.
- [ ] 18. With the whole-line setting off, a line in `sky`: only the tag and
      the name coloured.
- [ ] 19. Note anything ChatTwo shows differently from the game's chat log.

## Back to the default, and keeping

- [ ] 20. Right-click B > **Name colour...** > **Default**: B's name is back to
      the line's colour everywhere (the chat line, the window, both member
      lists). The next line in game chat is exactly as before name colours.
- [ ] 21. Give B a colour again. Reload the plugin (`/xlplugins`, disable and
      enable) and log out and in: kept. Log in with another character: B's
      name has no colour there (colours are per character).
- [ ] 22. **(A and B)** B leaves `fc`: B's colour stays in `sky`. Invite B to
      `fc` again: the same colour there.
- [ ] 23. The settings file (`LookingGlass.json`) has the colour under
      `NameColours`, keyed by B's Lodestone ID only (no name). On a test
      server, the echo bot's key ends in `@` and the server's address.
- [ ] 24. **(A and B)** If B can change their character's name (or a world
      transfer is at hand): B's colour stays with B.
