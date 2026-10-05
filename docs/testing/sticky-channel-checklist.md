# Sticky channel: in-game checklist

Talking in a channel without typing `/lgc` every time (`/lgc3` or `/lgc sky`
with no message). The unit tests cover the rules; only the game can show that
the hooks, the chat box label and ChatTwo behave as the design says
([design.md](../design.md#talking-in-a-channel-without-lgc)).

Steps marked **(changed)** are new, or expect something different since the
last round: those are the ones to test again. Steps numbered like 11b are new.

You don't need to copy anything out of the game. While you talk in a channel,
LookingGlass writes one line per thing it decides to Dalamud's log
(`%APPDATA%\XIVLauncher\dalamud.log`, lines with `[LookingGlass] [sticky]`):
which command a line started with, its size, and where it went and why, plus
every channel switch, start and stop. It never writes what you typed or what a
link holds. After testing, just say roughly what time you did a step that went
wrong (and its number), and we read the log from there.

## Before you start

- [ ] Build and load this branch's plugin as a dev plugin.
- [ ] Use two characters (a second client, or a friend). Character **A** does
      the typing. Character **B** is in the same party and Free Company, and
      stands next to A, so B sees everything A says in Say, Party and FC.
- [ ] A and B are both in one LookingGlass channel, here called **sky** on
      `/lgc1`. Give it the nickname `sky`. A is also in a second channel,
      **moon**, on `/lgc2`.
- [ ] Turn ExtraChat off (it is tested on its own at the end).
- [ ] In every step, "only in LookingGlass" means: B sees it in the
      LookingGlass channel, and nowhere in Say, Party or FC.

## Part 1: the game's own chat box (ChatTwo disabled)

Disable ChatTwo in the plugin installer before step 1, and keep it disabled
for all of Part 1: every step here is about the game's own chat box. (Last
round's Part 1 results, B and C below, were with ChatTwo disabled.)

The game's chat box has a one-line channel of its own: typing a channel
command and a space at the start of the input (`/s `, `/p `) switches the
input to that channel for the next line. While you talk in [sky], doing that
now stops talking in [sky] at once, with a line, and the chat box shows the
game channel again.

### Starting

- [ ] 1. **(changed)** In Say, type `/lgc1`.
  - One short line in sky's colour: "Now talking in [sky]."
  - The chat box's channel name (where it said "Say") now says `[sky]`.
  - The server info bar (top right) shows "LG [sky]". Hovering it shows a
    tooltip.
- [ ] 2. Type `hello`. It shows only in LookingGlass.
- [ ] 3. Press the up arrow. `hello` comes back in the chat box. Clear it.
- [ ] 4. Type `/lgc sky` (the nickname). It says "Now talking in [sky]" again,
      with no error.
- [ ] 5. Type `/lgc2`. It now says "Now talking in [moon]", and the label
      shows the moon tag. Type `hi`: only in moon. Type `/lgc1` to go back to
      sky.
- [ ] 6. Type `/lgc` with nothing after it. It shows the usage text, and you
      are still in [sky].
- [ ] 7. Type `/lgc 3`. It says "No channel has the nickname '3'."

### Commands still work while in [sky]

- [ ] 8. Type `/em waves`. The emote plays.
- [ ] 9. **(changed)** Type `/p brb` and press Enter. One of these two, and
      say which:
      - as soon as you type the space after `/p`: "Stopped talking in
        [sky].", the label says "Party", and `brb` goes to Party; or
      - nothing happens while typing, the label stays `[sky]`, and `brb` goes
        only to LookingGlass.
      Never: the label says `[sky]` and `brb` reaches Party.
- [ ] 9b. **(changed)** `/lgc1` if needed, then type `/party brb`. As in 9,
      either it stops at the space and `brb` goes to Party, or it goes to
      Party and you are still in [sky] (the label says `[sky]`).
- [ ] 10. Type `  /s hi` (with two spaces before the slash). It goes only to
      LookingGlass, as "/s hi", and not to Say.
- [ ] 11. **(changed, last round's B)** Link an item on its own (no text) and
      press Enter. You see "Not sent to [sky] or game chat: no text (links
      can't be sent)." Nothing appears in Say, Party or any linkshell.
- [ ] 11c. **(changed, B)** Type `/cwl1` and press Enter (the game is now on
      the cross-world linkshell; it stops talking in [sky]), then `/lgc1`.
      Link an item on its own, press Enter: the same "Not sent" line, nothing
      in the linkshell. Then type `/cwl1 ` with a space and link an item:
      "Stopped talking in [sky]." at the space, and the label names the
      linkshell; only then may the link go there.
- [ ] 11b. **(changed)** Link a map flag on its own (`<flag>`), press Enter:
      the same "Not sent" line, nothing in game chat. Then type `look ` and
      link an item after it: only LookingGlass gets the line (the link shows as
      its name, or as `<item>`), nothing in game chat.

### Leaving

- [ ] 12. **(changed, last round's C)** While in Say and in [sky], type `/s`
      and press Enter. At once a line says "Stopped talking in [sky].", the
      label says "Say" and the info bar entry goes away. Type `test`: it goes
      to Say.
- [ ] 12b. **(changed, C)** `/lgc1`, then type `/s ` with a space (don't press
      Enter yet). "Stopped talking in [sky]." appears as you type the space,
      and the label says "Say". Now type `test` and press Enter: it goes to
      Say. If instead nothing appears and the label stays `[sky]`, press
      Enter on `test`: it must go only to LookingGlass.
- [ ] 12c. **(changed, C)** The same as 12b from the cross-world linkshell
      (the game on `/cwl1`), typing `/s ` there.
- [ ] 12d. **(changed)** At no point in Part 1 may the label say `[sky]` while
      what you type goes to game chat. If it ever does, note the time and the
      step.
- [ ] 13. Go back with `/lgc1`. Type `/p`. It stops, and the label says
      "Party" (not "Say").
- [ ] 14. `/lgc1`, then press Tab in the chat box to change the channel. It
      stops, and the label shows the new channel.
- [ ] 15. `/lgc1`, then `/l1` (or `/cwl1` if you have a cross-world
      linkshell). It stops, and the label shows the linkshell.
- [ ] 16. `/lgc1`, then `/t <B's name>`. It stops, and the label shows the
      tell.
- [ ] 17. `/lgc1`, then click "LG [sky]" in the server info bar. It stops, and
      the info bar entry goes away.

### When something goes wrong (nothing may ever reach Say)

- [ ] 18. **(changed)** `/lgc1`. Open `/lg` and press **Disconnect**. At once a
      line says "Stopped talking in [sky]: disconnected.", the label shows the
      game channel again and the info bar entry goes away. Type `test`: it goes
      to that game channel, as the label says.
- [ ] 19. **(changed)** Connect again, `/lgc1`, then make the connection drop
      without pressing Disconnect (stop the server, or unplug the network for a
      moment). No "Stopped" line: you are still in [sky]. Type `secret`: "Not
      sent to [sky] or game chat: not connected to LookingGlass." B sees
      nothing anywhere. Once it has reconnected, type `hello`: only in
      LookingGlass.
- [ ] 20. `/lgc1`, then type ten quick messages in a row until one is refused
      for going too fast. The refused one says "Not sent to [sky]: …" and
      doesn't appear in Say. Press the up arrow: it comes back, to send again.
- [ ] 21. **(changed)** `/lgc1`, then leave sky in `/lg` (or have its admin
      remove you). A line says "Stopped talking in [sky]: you're no longer in
      it." The label shows the game channel you were in before (not
      necessarily Say).
- [ ] 22. `/lgc1`, then log out to the title screen and back in. It is no
      longer on (no label, no info bar entry).
- [ ] 23. **(changed)** `/lgc1`, then turn the plugin off (or reload it). A line
      says "Stopped talking in [sky]: LookingGlass was turned off.", and the
      label shows the real channel.
- [ ] 24. Type `/lgc3` for a channel number you don't have. It says "No channel
      is on /lgc3."
- [ ] 25. Right after logging in, before your channels have loaded, type
      `/lgc1`. It says LookingGlass is still loading, or that you're not
      connected. Try again a moment later: it works.

### The label

- [ ] 26. Give a channel a 16-letter nickname and start it. The label shows the
      whole tag, or cuts it off neatly; nothing overlaps.
- [ ] 27. Without a nickname, the label shows `[LGC1]`.

## Part 2: ChatTwo (ChatTwo enabled again)

### The case from the screenshot

- [ ] 28. **(changed)** Switch ChatTwo to Party. Type `/lgc1`.
  - "Now talking in [sky]." The very first time ever with ChatTwo, one more
    line follows: ChatTwo's "(Warning: …)" only names the game channel
    underneath, messages still go only to [sky], and the long form (`/party
    hi`) talks in a game channel once. `/lgc1` again: only "Now talking in
    [sky]." this time.
  - ChatTwo's channel name reads "LookingGlass [sky] (Warning: Party)".
  - The server info bar shows "LG [sky]".
- [ ] 29. Type `hello`. It shows only in LookingGlass. B sees nothing in Party.

### Short and long commands

- [ ] 30. Type `/p hi`. It goes only to LookingGlass (as "hi"), not to Party.
- [ ] 31. Type `/party hi`. It goes to Party. You're still in [sky].
- [ ] 32. Type `/say hi`. It goes to Say. Type `/s hi`: only to LookingGlass.
- [ ] 33. Type `/fc hi`: only LookingGlass. `/freecompany hi`: to FC.
- [ ] 34. Type `/e note to self`. It shows as an echo, only to you.
- [ ] 35. Type `/em waves`. The emote plays.
- [ ] 35b. **(changed)** Switch ChatTwo (and the game) to a cross-world
      linkshell (or any channel), `/lgc1`, then link an item on its own in
      ChatTwo's input and press Enter. "Not sent to [sky] or game chat: no text
      (links can't be sent)." Nothing in the linkshell or anywhere else.
- [ ] 35c. **(changed)** Type `look ` and link an item after it, in ChatTwo.
      Only LookingGlass gets it; nothing in the linkshell.

### Tabs

- [ ] 36. Switch to another ChatTwo tab that has no channel of its own, and
      back. You are still in [sky]: no "Stopped" line, the info bar still
      shows it. Type `hello`: only in LookingGlass.
- [ ] 37. Click into ChatTwo's input and press Escape, or click away. Still in
      [sky].
- [ ] 38. Switch to a ChatTwo tab whose fixed channel is different from the
      game's (for example a tab fixed to FC while the game is on Party). A
      "Stopped talking in [sky]" line appears, and the info bar entry goes
      away. Type `test`: it goes to FC.
- [ ] 39. Go back to the first tab, `/lgc1`. Pick another channel in ChatTwo's
      channel picker (the speech bubble). It stops, with a line.

### Pop-out with its own input

- [ ] 40. Pop out a tab with "Supports input" on, and set the pop-out's channel
      picker to **Say** (the main window stays on FC, say). In the main window,
      type `/lgc1`.
- [ ] 41. Type `hi` in the pop-out. It goes only to LookingGlass. B sees
      nothing in Say.
- [ ] 42. Set the pop-out to Party and type `hi` again. Only LookingGlass.
- [ ] 43. Set the pop-out to a tell to B, and type `hi`. It goes to B as a tell
      (ChatTwo sends tells itself; the pop-out's channel name shows the tell).

### Tells and leaving in ChatTwo

- [ ] 44. **(changed)** Switch ChatTwo's main input to a tell, then type
      `/lgc1`. It is refused: "Switch ChatTwo off the tell first (type /s),
      then try again."
- [ ] 45. **(changed)** With ChatTwo and the game on Say, `/lgc1`, then type
      `/s` in ChatTwo. At once: "Stopped talking in [sky].", ChatTwo's channel
      name goes back to plain "Say", the info bar entry goes away. Type
      `test`: it goes to Say, as the label says.
- [ ] 45b. **(changed)** The same from a cross-world linkshell: ChatTwo on
      `/cwl1`, `/lgc1`, type `/s` in ChatTwo. The same "Stopped" line, and the
      label says "Say". Then `/lgc1` again and type `/cwl1` on its own: the
      same, back to the linkshell.
- [ ] 45c. **(changed)** At no point in steps 28 to 47 may ChatTwo's channel
      name say "LookingGlass" while what you type goes to game chat. If it
      ever does, note the step.
- [ ] 46. **(changed)** `/lgc1`, then press **Disconnect** in `/lg`. At once
      "Stopped talking in [sky]: disconnected.", and ChatTwo's channel name is
      plain again. Type `test` in ChatTwo: it goes to the channel the name
      shows.
- [ ] 47. `/lgc1`, then turn ChatTwo off and on again in the plugin installer.
      Its channel name shows "LookingGlass [sky] …" again once it's back.

## Part 3: ExtraChat

- [ ] 48. Turn ExtraChat (or ExtraChat Reborn) on. `/lgc1`. Besides "Now
      talking in", a warning says "ExtraChat is on too and may take what you
      type…".
      Type `hello`: note where it went (LookingGlass, ExtraChat, or both), and
      what ChatTwo's channel name shows.
- [ ] 49. Turn ExtraChat off again.

## What to send back

For any box you couldn't tick, note the step number, what you saw, and
whether ChatTwo was on. If anything reached Say, Party or FC when the steps
say it shouldn't, that's the most important thing to report.
