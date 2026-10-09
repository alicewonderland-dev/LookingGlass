# Channel windows: in-game checklist

Channel windows, LookingGlass's pop-out chat (see
[Channel windows](../design.md#channel-windows)). You need two characters,
**A** (yours, on this build) and **B** (on another PC or client), in at least
two channels together, here `sky` (`/lgc1`) and `fc` (`/lgc2`), and A in a
third, `raid` (`/lgc3`). Give `sky` a colour. Do it once with ChatTwo off and
once with ChatTwo on, on A.

Throughout, check that nothing typed in a window ever shows in Say, Party or
any linkshell, for A or B.

## Opening

- [ ] 1. In the main window (`/lg`), right-click `sky` in the channel list.
      A menu shows its name, then **Open in new window**, **Add to window**
      (greyed: no window yet) and nothing else. Choose **Open in new window**.
      A window opens by the mouse, titled with `sky`'s name, with one tab,
      `sky`, in `sky`'s colour.
- [ ] 2. Right-click `sky` again and choose **Open in new window**: a second
      window opens, also with `sky`. Close it with its ×.
- [ ] 3. Right-click `fc` and choose **Open in new window**: a second window
      opens with `fc`. Right-click `sky`: **Show its window** is there now;
      choose it, and the `sky` window comes to the front.
- [ ] 4. Right-click `raid`, hover **Add to window**: both windows are listed
      by their tabs (`sky`, `fc`), neither ticked. Choose the `sky` window.
      `raid` becomes its second tab, selected, and the window comes to the
      front.
- [ ] 5. Right-click `raid`, **Add to window**: the `sky, raid` window is
      ticked. Choose it: no second `raid` tab; `raid` is selected.
- [ ] 6. The channel's ⋮ menu (in the main window) has **Open in new window**
      and **Show in game chat** (a green check). Its other items work as before
      (colour, rename, leave).

## Tabs and the +

- [ ] 7. In the `fc` window, click **+**: it lists `sky` and `raid` (not
      `fc`), in the channel list's order. Choose `sky`: it is added as a tab
      and selected.
- [ ] 8. Click **+** in a window that has every channel: it says so.
- [ ] 9. Drag a tab to another place. It stays there.
- [ ] 10. Close a tab that isn't the last with its ×: the next one is
      selected. Right-click a tab: **Show in game chat** and **Close
      tab**; **Close tab** works too.
- [ ] 11. Close the last tab of a window: the window closes.
- [ ] 12. Hover a tab: its full name, its commands (`/lgc1 or /lgc sky`).
- [ ] 13. The window's title is the selected tab's channel; switch tabs and
      it changes.

## Reading

- [ ] 14. B sends `hello` in `sky`. A's `sky` tab shows `HH:mm  B's name
      hello`, the time in A's local time, the name in `sky`'s colour (hover
      it: name@world). It shows in game chat too, once.
- [ ] 15. B sends a long message. It wraps under the name, the times stay a
      column.
- [ ] 16. Scroll up in the tab, and have B send something: the tab doesn't
      jump, and **New messages** shows at the bottom; click it to go down.
      At the bottom, new messages scroll into view by themselves.
- [ ] 17. With `raid` selected in a window that also has `sky`, B sends two
      lines in `sky`: the `sky` tab shows `(2)`. Select it: the count goes.
- [ ] 17b. **(new, after 0.2.13)** Tab counts on every tab not selected. With
      three tabs `sky`, `fc`, `raid` in one window and `raid` selected, B sends
      one line in `sky` and two in `fc`: `sky (1)` and `fc (2)`; hover `fc`:
      "2 new messages". Someone joining `fc` adds nothing (only messages
      count). A sends `/lgc2 hi` from the game's chat box: the `fc` count goes
      (talking in a channel reads it).
- [ ] 17c. **(new, after 0.2.13)** Open a second window with `sky` selected
      (**Open in new window**). B sends in `sky`: the first window's `sky` tab
      (not selected) gets no count, since the other window shows `sky`.
      Select `fc` in the second window and have B send in `sky` again: both
      windows' `sky` tabs count it until one of them shows `sky`, then both
      lose the count.
- [ ] 17d. **(new, after 0.2.13)** Relog with the window open: B sends in a
      tab behind the selected one while A logs in; once the window is back,
      that tab counts the messages since login, and selecting it clears it.
- [ ] 18. B links an item: A sees `[Name]` in colour; hovering shows its name
      (and "High quality" for an HQ one). B links a map flag: clicking it opens
      A's map at the flag. B links a status: hovering shows its name.
- [ ] 19. B joins or leaves a channel, or is invited: A's tab shows the line,
      dimmed. B renames the channel (B admin): A's tab says "The channel is now
      called ...".
- [ ] 20. Right-click a line: **Copy**; paste it somewhere: time, name@world
      and text.
- [ ] 21. A's own messages (from the window, from `/lgc1`, while sticky) show
      in the tab, once each, with A's name.
- [ ] 22. Close a window and open `sky` again: its earlier lines are all
      there.

## Sending

- [ ] 23. In `sky`'s tab, type `hi from the window`, Enter. B sees it in
      `sky`; A sees it in the tab and in game chat. The box stays active: type
      the next line and Enter again.
- [ ] 24. Enter on an empty box, or Escape: the keyboard goes back to the game
      (your character moves with the keys). After Escape, what was typed is
      still in the box.
- [ ] 25. Text commands: target someone, type `<t> hi` in the window: B sees
      the target's name. `<me>` too.
- [ ] 26. Placeholders: set a map flag, type `meet at <flag>`: B gets a map
      link. Link an item in game (right-click it, **Link**; it goes into the
      game's chat box: clear it there), then type `<item>` in the window: B gets
      that item as a link. Say whether the game's link went anywhere you didn't
      want.
- [ ] 27. Paste or type a very long line: the box itself stops at 500
      characters (what it shows is all that is sent; also in Japanese text), and
      a counter shows from 400.
- [ ] 28. "Not sent": `/lg`, Settings, disconnect (or stop the server), then
      send from the window. The tab says "Not sent: ..." in LookingGlass blue,
      nothing in game chat, and the text is back in the box.
- [ ] 29. Send many lines fast until the server's rate limit refuses one: the
      "Not sent" line is in the tab, in blue. Keep the box active (don't click
      away): the refused line shows in the box itself once it is empty, and
      typing goes on from it; Enter sends exactly what the box shows.
- [ ] 30. In a channel whose key is being changed (or waiting for a member),
      the tab shows the same note at the top as the channel pane.

## Game chat on and off

- [ ] 31. In `sky`'s ⋮ menu, turn off **Show in game chat** (it becomes a red
      cross). B sends in `sky`: it shows in the window only, not in game chat (or ChatTwo).
      Someone joining `sky` shows in the window only too.
- [ ] 32. Close every window with `sky`, then turn **Show in game chat**
      off for `fc` from its ⋮ menu while no window has `fc`: a window with `fc`
      opens.
- [ ] 33. **(changed, after 0.2.13)** With `sky` still off game chat, close
      every window (or tab) that has it. Nothing is said in game chat, and the
      ⋮ menu's **Show in game chat** stays a red cross. Keep walking with the
      keys: B sends in `sky`. It doesn't show in game chat; a `sky` window opens
      by itself (or, with **New channels open** at **As a tab** and another
      window open, a `sky` tab is added to the window used last, not
      selected), flashes briefly in `sky`'s colour, and doesn't take the
      keyboard. Counts: a new window shows `sky` selected, so neither the
      window nor the channel list counts the message (it is on screen, focused
      or not); a tab added behind another shows `(1)` and the channel list
      counts it, both until you select that tab.
- [ ] 33b. Relog: the window comes back and `sky` stays off, with no "shows in
      game chat again" line.
- [ ] 33c. **(new, after 0.2.13)** Close every window with `sky` and relog: no
      `sky` window comes back, and `sky` stays off. B sends in `sky`: a window
      opens by itself, as in step 33. Close it again, log out, have B send in
      `sky` while A is away, and log back in: the caught-up messages open a
      window too, not game chat.
- [ ] 33d. **(new, after 0.2.13)** Close every window with `sky`, start a fight
      (a striking dummy), and have B send in `sky`: nothing opens while in
      combat; it opens once combat ends, with the message.
- [ ] 33e. **(new, after 0.2.13)** As in 33d, but turn `sky`'s **Show in game
      chat** back on (⋮ menu) while still in combat, after B's message: once
      combat ends the window still opens, with that message (it was never in
      game chat). B's next message shows in game chat.
- [ ] 33f. **(new, after 0.2.13)** `sky` off game chat, no window with `sky`:
      A sends `/lgc1 hi` from the game's chat box: not in game chat; a `sky`
      window opens by itself, flashing, with A's line. Close it; B leaves
      `sky` (or A invites someone): the information line alone opens a window
      too.
- [ ] 34. Turn it back on (⋮ menu, or a tab's right-click menu): `sky`'s
      messages are in game chat again.
- [ ] 35. A warning about `sky` (if you can make one, for example a message
      that fails its checks with the debug tools) still shows in game chat while
      it is off.

## Unread

- [ ] 36. **(changed, after 0.2.13)** With `sky` selected in an open window,
      have B send in `sky`, once with the window focused (clicked into) and
      once with the game focused: the main window's channel list counts
      neither (the tab is on screen). Select another tab in that window and
      have B send again: the channel list counts it, and so does the `sky` tab.
      Collapse the window (double-click its title bar): what arrives in its
      selected channel counts again.
- [ ] 37. With a count on `sky` in the channel list, select the `sky` tab in a
      window (no need to click into it otherwise): the count goes, in the
      channel list and on that tab.
- [ ] 37b. **(new, after 0.2.13)** With `sky` off game chat and a count on it in
      the channel list, select `sky` in the main window's channel list: the
      count stays (the main window shows no messages). Select a channel that
      shows in game chat with a count: its count goes, as before. Send in
      `sky` (`/lgc1 hi`): its count goes.

## Flash

- [ ] 37c. **(new, after 0.2.13)** A window that opens by itself (step 33)
      flashes: its title bar and border pulse three times in the channel's
      colour (LookingGlass blue for a channel with none) over about a second and
      a half, then look as usual. It doesn't come to the front over a window
      you're using, and the game keeps the keyboard.
- [ ] 37d. **(new, after 0.2.13)** With **As a tab**, a tab added by itself to
      an open window: that window flashes once, in the new channel's colour.
- [ ] 37e. **(new, after 0.2.13)** Windows you open yourself (**Open in new
      window**, **Add to window**, the **+**), and windows reopened at login,
      don't flash.
- [ ] 37f. **(new, after 0.2.13)** Dalamud Settings, turn on reduced motion
      (**Reduce motions**, under Look & Feel or Experimental, depending on the
      Dalamud version) and repeat step 37c: no pulsing, a steady softer tint
      and border for about a second and a half instead.

## Remembering

- [ ] 38. Arrange two windows (move, resize, tabs reordered, a tab selected).
      Log out to the title screen: the windows close. Log back in to the same
      character: they open again where they were, with the same tabs in the
      same order and the same tab selected, empty of messages (only since
      login, and any sent while you were logged out: see below), and the game
      keeps the keyboard.
- [ ] 39. Leave a channel that is a tab (or have B remove you): its tab goes; a
      window left with no tab closes. After a relog it doesn't come back.
- [ ] 40. Close a window with its ×, relog: it doesn't come back.
- [ ] 41. Unload and reload the plugin (or restart the game): the windows come
      back.
- [ ] 42. Log in to another character: none of the first character's windows
      show; that character's own windows (if any) do. Back on the first, its
      windows come back.
- [ ] 43. Change the server address in Settings: the windows close, and come
      back when you switch back.

## Messages sent while you were away

Message catch-up (see [Message catch-up](../design.md#message-catch-up)), on
a server that keeps messages (the debug window says how long).

- [ ] 43b. Log A out to the title screen. B sends three messages in `sky`.
      Log A back in: game chat shows, in blue with `[sky]` in its colour,
      "[sky] 3 messages were sent while you were away:", then the three, each
      with the time B sent it (`[HH:mm]` after the tag), once. The `sky` window
      shows the same line (dimmed), then the three with B's times; the channel
      list counts them as unread.
- [ ] 43c. B sends something while A is logging back in (on the loading
      screen): it shows once, after the missed ones.
- [ ] 43d. Log A out again; B sends 60 short messages in `sky` (one a second
      after the first five, as the server allows). Log A in: game chat
      shows the line saying 60 were sent, that the last 50 are below and the
      10 before them are in the window, then 50 messages. The window has all 60.
- [ ] 43e. With `sky` off game chat (see above), repeat 43b: nothing in game
      chat, all of it in the window.
- [ ] 43f. Relog A again without anything new: nothing is shown again.

## Look

- [ ] 44. Simple mode: no technical words in the window (warnings, notices,
      notes). Switch to advanced mode: the same lines in technical words, at
      once.
- [ ] 45. Dalamud's window menu (the title bar's button): set the window's
      opacity, pin it, click-through: it behaves like the main window. With a
      transparent background the text is still readable.
- [ ] 46. Change Dalamud's global font scale: the window, its tabs and lines
      scale with it.
- [ ] 47. Sticky mode (`/lgc1` with no message) works as before while windows
      are open, and typing in a window while sticky goes to the window's
      channel only.
