# Sticky channel: in-game checklist

Talking in a channel without typing `/lgc` every time (`/lgc3` or `/lgc sky`
with no message). The unit tests cover the rules; only the game can show that
the hooks, the chat box label and ChatTwo behave as the design says
([design.md](../design.md#talking-in-a-channel-without-lgc)).

**(changed)** means the expected result is different from last round.
**(new)** marks a step that wasn't there before. Those are the ones to test
again; the rest passed last round.

What changed this round:

- Short channel commands work as usual again: `/s hi`, `/p brb`, `/fc hi`
  typed while you talk in [sky] go to that game channel once, and you stay in
  [sky]. Only plain text goes to LookingGlass. The one exception is in ChatTwo:
  the short command of ChatTwo's own channel (`/p hi` while ChatTwo is on
  Party) goes to LookingGlass, because that is exactly how ChatTwo sends plain
  typing; use `/party hi` for that one.
- Colours: LookingGlass's own lines are blue (information, like "Now talking
  in", "Stopped talking in", "Not sent"), light red (warnings) or dark red
  (critical warnings). A channel's tag in them keeps its own colour.
- A message with text and a link sends the link as its name, like
  `look [Potion]`, with no error.

You don't need to copy anything out of the game. While you talk in a channel,
LookingGlass writes one line per thing it decides to Dalamud's log
(`%APPDATA%\XIVLauncher\dalamud.log`, lines with `[LookingGlass] [sticky]`):
which command a line started with, its size, where it went, why, and which
rule applied, plus every channel switch, start and stop. It never writes what
you typed or what a link holds. After testing, just say roughly what time you
did a step that went wrong (and its number), and we read the log from there.

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
for all of Part 1: every step here is about the game's own chat box. B, your
second character, checks every step: the label alone proves nothing.

### Starting

- [ ] 1. **(changed)** In Say, type `/lgc1`.
  - One short line: "Now talking in [sky]." in LookingGlass blue, with `[sky]`
    in sky's own colour.
  - The chat box's channel name (where it said "Say") now says `[sky]`.
  - The server info bar (top right) shows "LG [sky]". Hovering it shows a
    tooltip.
- [ ] 2. Type `hello`. It shows only in LookingGlass. B must check Say:
      `hello` must not appear there.
- [ ] 3. Press the up arrow. `hello` comes back in the chat box. Clear it.
- [ ] 4. Type `/lgc sky` (the nickname). It says "Now talking in [sky]" again,
      with no error.
- [ ] 5. Type `/lgc2`. It now says "Now talking in [moon]", and the label
      shows the moon tag. Type `hi`: only in moon. Type `/lgc1` to go back to
      sky.
- [ ] 6. Type `/lgc` with nothing after it. It shows the usage text (blue),
      and you are still in [sky].
- [ ] 7. Type `/lgc 3`. It says "No channel has the nickname '3'." (blue).

### Commands still work while in [sky]

- [ ] 7b. Commands from other plugins still work while in [sky]: type `/lg`
      (the window opens) and `/xlhelp` (Dalamud's help).
- [ ] 7c. Run a macro with a line `/em waves` and a line `hello` while in
      [sky]: the emote plays, and `hello` goes only to LookingGlass (B sees
      nothing in Say). A macro's plain text going to the channel is expected.
- [ ] 7d. **(changed)** A macro with the line `/p Pull in 5`, run while in
      [sky]: it goes to Party (B sees it there), and you are still in [sky].
- [ ] 7e. Have B send A a tell. While in [sky], A types `/r hi`. B gets the
      tell "hi", and nothing reaches LookingGlass (B sees nothing in the
      channel). Still in [sky], or a "Stopped" line if the game's reply
      switched channel: either is fine, as long as the tell arrived and the
      channel got nothing.

- [ ] 8. Type `/em waves`. The emote plays.
- [ ] 9. **(changed)** Type `/p brb`. It goes to Party once: B sees "brb" in
      Party, and nothing in LookingGlass. You are still in [sky]: the label
      says `[sky]`. Type `hello`: only in LookingGlass.
- [ ] 9b. Type `/party brb` (the long form). It goes to Party (B sees it
      there), and you are still in [sky].
- [ ] 9c. **(new)** Type `/s hi`. It goes to Say once (B sees it in Say), and
      you are still in [sky]. Type `hello`: only in LookingGlass.
- [ ] 10. Type `  /s hi` (with two spaces before the slash). It goes only to
      LookingGlass, as "/s hi", and not to Say.
- [ ] 11. Link an item on its own (no text) and press Enter. You see "Not sent
      to [sky] or game chat: no text (links can't be sent)." in blue. Nothing
      appears in Say, Party or any linkshell.
- [ ] 11c. **(changed)** Type `/cwl1` and press Enter (the game is now on the
      cross-world linkshell; it stops talking in [sky]), then `/lgc1`. Link an
      item on its own, press Enter: the same "Not sent" line; B sees nothing in
      the linkshell. Then type `/cwl1 ` and link an item after it, press
      Enter: that is a one-off to the linkshell, as usual, so the link goes to
      the linkshell, and you are still in [sky].
- [ ] 11b. **(changed)** Link a map flag on its own (`<flag>`), press Enter:
      the same "Not sent" line, nothing in game chat. Then type `look ` and
      link an item after it, press Enter: only LookingGlass gets it, as
      `look [the item's name]`, with no error line and nothing in game chat.
      Do the same with `meet at ` and a map flag: `meet at [place ( x , y )]`.
- [ ] 11d. **(new)** If a link's name can't be found, the rest of the message
      is sent and one blue line says "The link wasn't sent (links can't be
      sent yet)." (You may not be able to make this happen; skip it if not.)

### Leaving

- [ ] 12. While in Say and in [sky], type `/s` and press Enter. At once a
      line says "Stopped talking in [sky].", the label says "Say" and the info
      bar entry goes away. Type `test`: it goes to Say.
- [ ] 12b. **(changed)** `/lgc1`, then type `/s test` in one line. It goes to
      Say once (B sees "test" in Say), and you are still in [sky].
- [ ] 12c. The same as 12 from the cross-world linkshell: the game on `/cwl1`,
      `/lgc1`, then `/s`. "Stopped talking in [sky].", the label says "Say";
      `test` goes to Say.
- [ ] 12d. **(changed)** At no point in Part 1 may the label say `[sky]` while
      *plain text* you type goes to game chat. (A short command like `/p brb`
      going to its game channel once is expected now.) If it ever does, note
      the time and the step.
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

### When something goes wrong (plain text may never reach Say)

- [ ] 18. `/lgc1`. Open `/lg` and press **Disconnect**. At once a line says
      "Stopped talking in [sky]: disconnected.", the label shows the game
      channel again and the info bar entry goes away. Type `test`: it goes to
      that game channel, as the label says.
- [ ] 19. Connect again, `/lgc1`, then make the connection drop without
      pressing Disconnect (stop the server, or unplug the network for a
      moment). No "Stopped" line: you are still in [sky]. Type `secret`: "Not
      sent to [sky] or game chat: not connected to LookingGlass." (blue). B
      sees nothing anywhere. Once it has reconnected, type `hello`: only in
      LookingGlass.
- [ ] 20. `/lgc1`, then type ten quick messages in a row until one is refused
      for going too fast. The refused one says "Not sent to [sky]: …" and
      doesn't appear in Say. Press the up arrow: it comes back, to send again.
- [ ] 21. `/lgc1`, then leave sky in `/lg` (or have its admin remove you). A
      line says "Stopped talking in [sky]: you're no longer in it." The label
      shows the game channel you were in before (not necessarily Say).
- [ ] 22. `/lgc1`, then log out to the title screen and back in. It is no
      longer on (no label, no info bar entry).
- [ ] 23. `/lgc1`, then turn the plugin off (or reload it). A line says
      "Stopped talking in [sky]: LookingGlass was turned off.", and the label
      shows the real channel.
- [ ] 24. Type `/lgc3` for a channel number you don't have. It says "No channel
      is on /lgc3."
- [ ] 25. Right after logging in, before your channels have loaded, type
      `/lgc1`. It says LookingGlass is still loading, or that you're not
      connected. Try again a moment later: it works.

### The label

- [ ] 26. Give a channel a 16-letter nickname and start it. The label shows the
      whole tag, or cuts it off neatly; nothing overlaps.
- [ ] 27. Without a nickname, the label shows `[LGC1]`.

### Colours

- [ ] 27b. **(new)** Looking back over Part 1: "[LookingGlass]" at the start
      of every LookingGlass line is blue. "Now talking in", "Stopped talking
      in", every "Not sent" and the usage text are blue too, with the channel's
      tag in its own colour. Nothing in Part 1 was red. (Light red is for
      warnings, such as the ExtraChat one in step 48; dark red is for critical
      ones, such as the server showing two different member lists, which you
      shouldn't see in a normal test.)

## Part 2: ChatTwo (ChatTwo enabled again)

### The case from the screenshot

- [ ] 28. **(changed)** Switch ChatTwo to Party. Type `/lgc1`.
  - "Now talking in [sky]." in blue. One more line follows once, even if you
    saw last round's: ChatTwo's "(Warning: …)" names its own channel, typing
    still goes to [sky], and so does that channel's short command (`/p hi` on
    Party), so use `/party hi`. `/lgc1` again: only "Now talking in [sky]."
    this time.
  - ChatTwo's channel name reads "LookingGlass [sky] (Warning: Party)". On a
    tab with a channel of its own, ChatTwo shows that tab's channel ("Party")
    instead; that's ChatTwo's choice, and the safe way round. Then the info
    bar is the sign that you're talking in [sky].
  - The server info bar shows "LG [sky]".
- [ ] 29. Type `hello`. It shows only in LookingGlass. B sees nothing in Party.
- [ ] 29b. **(new)** Repeat 28–29 with ChatTwo on each other channel you have:
      Say, FC, a linkshell (`/l1`), and cross-world linkshells 1 and 2 if you
      have them. Each time `hello` reaches only LookingGlass, never that
      channel. (This checks that LookingGlass knows the command ChatTwo puts
      in front of your text for every channel.)

### Short and long commands (ChatTwo still on Party)

- [ ] 30. Type `/p hi`. It goes only to LookingGlass (as "hi"), not to Party.
      This is the one expected exception: ChatTwo sends what you type as
      `/p …` while it is on Party, so LookingGlass can't tell them apart.
- [ ] 31. Type `/party hi`. It goes to Party. You're still in [sky].
- [ ] 32. **(changed)** Type `/s hi`. It goes to Say once (B sees it in Say),
      and you're still in [sky]. `/say hi` goes to Say too.
- [ ] 33. **(changed)** Type `/fc hi`: it goes to FC once (B sees it in FC),
      and you're still in [sky]. `/freecompany hi`: to FC too.
- [ ] 33b. **(new)** Type `hello` once more after those: only in LookingGlass.
      The label still says "LookingGlass [sky]".
- [ ] 34. Type `/e note to self`. It shows as an echo, only to you.
- [ ] 35. Type `/em waves`. The emote plays.
- [ ] 35b. Switch ChatTwo (and the game) to a cross-world linkshell (or any
      channel), `/lgc1`, then link an item on its own in ChatTwo's input and
      press Enter. "Not sent to [sky] or game chat: no text (links can't be
      sent)." in blue. Nothing in the linkshell or anywhere else.
- [ ] 35c. **(changed)** Type `look ` and link an item after it, in ChatTwo.
      Only LookingGlass gets it, as `look [the item's name]`, with no error
      line; nothing in the linkshell.
- [ ] 35d. Have B send A a tell. While in [sky], type `/r hi` in ChatTwo. B
      gets the tell "hi", and nothing reaches LookingGlass.

### Tabs

A ChatTwo tab either has a channel of its own (its **Input channel** setting
in ChatTwo's tab settings) or none. Last round all your tabs had their own
channel, so switching tabs was step 38, not 36.

- [ ] 36. Only if you have a tab with **no** channel of its own: switch to it
      and back. You are still in [sky]: no "Stopped" line, the info bar still
      shows it. Type `hello`: only in LookingGlass.
- [ ] 37. Click into ChatTwo's input and press Escape, or click away. Still in
      [sky].
- [ ] 38. Switch to a ChatTwo tab whose own channel is different from the
      game's (for example a tab on FC while the game is on Party). A "Stopped
      talking in [sky]" line appears, and the info bar entry goes away. Type
      `test`: it goes to FC. Switching back to the first tab does not start
      [sky] again: type `/lgc1`. (That is how it is meant to work: you decided
      tabs don't remember it.)
- [ ] 39. Go back to the first tab, `/lgc1`. Pick another channel in ChatTwo's
      channel picker (the speech bubble). It stops, with a line.

### Pop-out with its own input

Skip this part if none of your pop-outs has an input box.

- [ ] 40. Pop out a tab with "Supports input" on, and set the pop-out's channel
      picker to **Say** (the main window stays on FC, say). In the main window,
      type `/lgc1`.
- [ ] 41. Type `hi` in the pop-out. It goes only to LookingGlass. B sees
      nothing in Say.
- [ ] 42. Set the pop-out to Party and type `hi` again. Only LookingGlass.
- [ ] 42b. **(new)** In the pop-out, type `/s hi`. It goes only to
      LookingGlass: a pop-out's typing can't be told apart from a typed short
      command, so in a pop-out use the long form (`/say hi`), which goes to
      Say.
- [ ] 43. Set the pop-out to a tell to B, and type `hi`. It goes to B as a tell
      (ChatTwo sends tells itself; the pop-out's channel name shows the tell).

### Tells and leaving in ChatTwo

- [ ] 44. Switch ChatTwo's main input to a tell, then type `/lgc1`. It is
      refused: "Switch ChatTwo off the tell first (type /s), then try again."
- [ ] 45. With ChatTwo and the game on Say, `/lgc1`, then type `/s` in
      ChatTwo. At once: "Stopped talking in [sky].", ChatTwo's channel name
      goes back to plain "Say", the info bar entry goes away. Type `test`: it
      goes to Say, as the label says.
- [ ] 45b. The same from a cross-world linkshell: ChatTwo on `/cwl1`, `/lgc1`,
      type `/s` in ChatTwo. The same "Stopped" line, and the label says "Say".
      Then `/lgc1` again and type `/cwl1` on its own: the same, back to the
      linkshell.
- [ ] 45c. **(changed)** At no point in steps 28 to 47 may ChatTwo's channel
      name say "LookingGlass" while *plain text* you type goes to game chat.
      (A short command for another channel going there once is expected now.)
      If it ever does, note the step.
- [ ] 46. `/lgc1`, then press **Disconnect** in `/lg`. At once "Stopped talking
      in [sky]: disconnected.", and ChatTwo's channel name is plain again. Type
      `test` in ChatTwo: it goes to the channel the name shows.
- [ ] 47. `/lgc1`, then turn ChatTwo off and on again in the plugin installer.
      Its channel name shows "LookingGlass [sky] …" again once it's back.

## Part 3: ExtraChat

- [ ] 48. **(changed)** Turn ExtraChat (or ExtraChat Reborn) on. `/lgc1`.
      Besides "Now talking in", a warning in light red says "ExtraChat is on
      too and may take what you type…". Type `hello`: note where it went
      (LookingGlass, ExtraChat, or both), and what ChatTwo's channel name
      shows.
- [ ] 49. Turn ExtraChat off again.

## What to send back

For any box you couldn't tick, note the step number, what you saw, and
whether ChatTwo was on. If plain text reached Say, Party or FC when the steps
say it shouldn't, that's the most important thing to report.
