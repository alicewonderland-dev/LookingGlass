# Windows only: in-game checklist

"Show LookingGlass messages only in windows" (see
[Windows only, never game chat](../design.md#windows-only-never-game-chat)).
You need character **A** (yours, on this build) in at least three channels,
here `sky` (`/lgc1`), `fc` (`/lgc2`) and `raid` (`/lgc3`). Give `sky` a
colour. Steps marked **(A and B)** also need character **B** (on another PC
or client) in `sky` and `fc` with A; for the others, `/lgdebug`'s simulated
messages do. Do it once with ChatTwo off and once with ChatTwo on, on A.

Start with the setting off, every channel's **Show in game chat** on, and no
channel window open.

## The setting

- [ ] 1. `/lg`, the gear, Settings. Under **Chat**, after **Verbose channel
      messages**: **Show LookingGlass messages only in windows**, unticked.
      Under it, greyed out, "For a channel no window shows:" with **Add it as
      a tab to the window used last** (chosen) and **Open a new window each
      time**. Hover each: a tooltip says what it does.
- [ ] 2. Tick the setting. The two choices are no longer greyed out.

## Nothing in game chat

- [ ] 3. **(A and B)** B sends `hello` in `sky`. Nothing shows in game chat
      (or ChatTwo). A window opens with `sky` as its only tab, showing
      `hello`. It doesn't take the keyboard: keep walking with the keys while
      it opens, and nothing you type goes into it.
- [ ] 4. **(A and B)** B sends again in `sky`: it shows in the window only,
      and no second window or tab opens.
- [ ] 5. A sends `hi` with `/lgc1 hi` from the game's chat box: it doesn't
      show in game chat; it shows in the `sky` tab, once. The same from the
      window's input box.
- [ ] 6. **(A and B)** B leaves `fc` and A invites B back (or someone else
      joins `fc`). The "left" and "invited" lines don't show in game chat. A
      tab for `fc` is added to the `sky` window, behind `sky`: `sky` stays
      selected, and the `fc` tab shows a count, as `(1)` or more if there
      were messages from others in `fc` since login.
- [ ] 7. `/lgc4 hi` (no channel on 4) and `/lgc` alone: the answers show in
      game chat, in blue, as before.
- [ ] 8. Talk in `raid` with `/lgc3` (no message): "Now talking in [raid]"
      shows in game chat (with **Verbose channel messages** on). Type `test`:
      it doesn't show in game chat; a `raid` tab is added to the window used
      last. Switch to `/s`: "Stopped talking in" shows in game chat (verbose
      on).
- [ ] 9. Disconnect (Settings, or stop the server) and send `/lgc1 hi`: "Not
      connected" shows in game chat. Connect again: the connection lines
      show in game chat as before.
- [ ] 10. **(A and B)** B invites A to a new channel: "B invited you to ..."
      shows in game chat (A isn't in it yet, so no window could show it).
- [ ] 11. A warning about `sky` (for example a message that fails its checks,
      with the debug tools) still shows in game chat, light red, and in the
      `sky` tab.
- [ ] 12. Right-click a player and **Invite to LookingGlass ▸** `sky`: "Invited
      ... to [sky]." (or why not) shows in game chat.

## Which window

- [ ] 13. Open a second window from the channel list (**Open in new window**
      on `fc`, after closing the `fc` tab). Click into the first window (the
      `sky` one) so it had the focus last, then click back into the game.
      Close the `raid` tab. Have a message arrive in `raid`: its tab is added
      to the `sky` window, not the `fc` one, and not selected.
- [ ] 14. Close the `sky` window. A message arrives in `sky`: it is added as a
      tab to the `fc` window (the one opened last).
- [ ] 15. In Settings choose **Open a new window each time**. Close the `sky`
      tab, and have a message arrive in `sky`: a new window opens with `sky`
      only, a little below and right of the window used last, without taking
      the keyboard. Have messages arrive in two channels no window shows: a
      window each.
- [ ] 16. Close every window. Have messages arrive in two channels: with
      **Add it as a tab to the window used last**, one window opens, with the
      first channel selected and the second as a tab behind it.

## In combat, cutscenes and loading screens

- [ ] 17. Close the `sky` tab. Start a fight (a striking dummy will do) and,
      while in combat, have a message arrive in `sky`: no window or tab
      appears. Leave combat: it appears then, with the message (and any others
      that came meanwhile).
- [ ] 18. The same during a cutscene (a quest cutscene, or the Inn's
      **Unending Journey**): nothing opens until it ends.
- [ ] 19. The same through a loading screen (teleport while a message
      arrives): it opens once you are in the new zone.

## Show in game chat, per channel

- [ ] 20. In `sky`'s ⋮ menu (main window) and its tab's right-click menu,
      **Show in game chat** is greyed out and can't be clicked. Hover it: it
      says "Show LookingGlass messages only in windows" is on and to change it
      in Settings, under Chat. Hover a tab: "Not shown in game chat: only
      windows show messages (change it in Settings)."
- [ ] 21. Close every window that has a channel turned off game chat on its
      own (turn one off before step 2 to try this): no "shows in game chat
      again" line, and it stays off.

## Turning it off

- [ ] 22. Untick the setting. **(A and B)** B sends in `sky`: it shows in game
      chat again at once (and in its window). Each channel's **Show in game
      chat** is as it was before: a channel you had turned off is still off.
- [ ] 23. A channel turned off game chat on its own that no window showed
      (step 21) gets a window when the setting is turned off, rather than going
      back to game chat.
- [ ] 24. Turn the setting on, log out, turn it off in Settings on the title
      screen (or another character), and log back in: the same as step 23
      happens once the channel list is in.

## Remembering

- [ ] 25. With the setting on, relog: it stays on, and the windows opened for
      it come back where they were. Messages sent while you were away (catch-up)
      don't show in game chat; a channel with some and no window gets one.
- [ ] 26. With the setting on, `/xllog`: no errors from LookingGlass while
      windows opened.
