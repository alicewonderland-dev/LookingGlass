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
      and **Also show in game chat** (ticked). Its other items work as before
      (colour, rename, leave).

## Tabs and the +

- [ ] 7. In the `fc` window, click **+**: it lists `sky` and `raid` (not
      `fc`), in the channel list's order. Choose `sky`: it is added as a tab
      and selected.
- [ ] 8. Click **+** in a window that has every channel: it says so.
- [ ] 9. Drag a tab to another place. It stays there.
- [ ] 10. Close a tab that isn't the last with its ×: the next one is
      selected. Right-click a tab: **Also show in game chat** and **Close
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

- [ ] 31. In `sky`'s ⋮ menu, untick **Also show in game chat**. B sends in
      `sky`: it shows in the window only, not in game chat (or ChatTwo).
      Someone joining `sky` shows in the window only too.
- [ ] 32. Close every window with `sky`, then turn **Also show in game chat**
      off for `fc` from its ⋮ menu while no window has `fc`: a window with `fc`
      opens.
- [ ] 33. With `sky` still off game chat, close every window (or tab) that has
      it. Game chat says, once, in blue with `[sky]` in its colour, "[sky] shows
      in game chat again, since no window shows it.", and the ⋮ menu has **Also
      show in game chat** ticked again. B sends in `sky`: it shows in game
      chat, and the channel list counts it as unread.
- [ ] 33b. Turn `sky` off game chat again (a window opens) and relog: the
      window comes back and `sky` stays off, with no "shows in game chat again"
      line.
- [ ] 34. Turn it back on (⋮ menu, or a tab's right-click menu): `sky`'s
      messages are in game chat again.
- [ ] 35. A warning about `sky` (if you can make one, for example a message
      that fails its checks with the debug tools) still shows in game chat while
      it is off.

## Unread

- [ ] 36. Click into the `sky` window (it has the focus) with `sky` selected,
      and have B send in `sky`: the main window's channel list doesn't count
      it. Click into the game instead (the window loses the focus), and have B
      send again: the channel list counts it.
- [ ] 37. With a count on `sky` in the channel list, click into the window
      with `sky` selected: the count goes.

## Remembering

- [ ] 38. Arrange two windows (move, resize, tabs reordered, a tab selected).
      Log out to the title screen: the windows close. Log back in to the same
      character: they open again where they were, with the same tabs in the
      same order and the same tab selected, empty of messages (only since
      login), and the game keeps the keyboard.
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
