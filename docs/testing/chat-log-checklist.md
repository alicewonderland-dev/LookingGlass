# Chat log on this computer: in-game checklist

The opt-in chat log (see
[Chat log on this computer](../design.md#chat-log-on-this-computer)). You need
two characters, **A** (yours, on this build) and **B** (on another PC or
client), in a channel together, here `sky` (`/lgc1`), and A in a second, `fc`
(`/lgc2`). The plugin's config folder is
`%APPDATA%\XIVLauncher\pluginConfigs\LookingGlass`; its chat logs are the
`chatlog-…` folders there. Check each step in simple mode, then the wording
steps (3, 13, 16, 24, 25) again in advanced mode.

Throughout, check that nothing changes in game chat: the same lines as with
the log off, nothing extra printed when it is turned on or off, deleted, or
read back.

## Off by default

- [ ] 1. On a fresh install (or with the setting never touched), open Settings
      (the gear in `/lg`). Under **Chat history**, **Keep chat history on
      this computer** is unticked, with no size slider. No `chatlog-…` folder
      exists in the config folder.
- [ ] 2. Open a `sky` window and chat with B for a bit. Still no `chatlog-…`
      folder, and the window has no **Show older messages**.

## Turning it on

- [ ] 3. **(changed)** Tick **Keep chat history on this computer**. No text
      under it: click the **?** after it. A small bubble says channel windows
      show older messages next time you play, that it's stored scrambled and
      never uploaded, and that the oldest go first when it's full; no "log",
      "encrypted" or "key" in simple mode (in advanced mode it says encrypted,
      and names the protection, such as Windows DPAPI). A **Size limit**
      slider shows 50 MB, from 5 MB to 1024 MB.
- [ ] 4. Send a few lines in `sky` (one with `<item>` after linking an item,
      one with `<flag>`), have B send a few and leave and rejoin `fc` (or
      invite someone), and rename `fc` if you can. A `chatlog-<id>-<hash>`
      folder appears, with `chatlog.key` and a `0000000001.lgl`. Settings shows
      "Your chat history uses … on this computer." and **Delete my chat
      history...**.
- [ ] 5. Open the `.lgl` and `chatlog.key` files in a text or hex editor:
      no message text, no names (A's, B's, worlds) and no channel names
      anywhere, only unreadable bytes after the first few letters (`LGCL`,
      `LGCK`).

## Older lines in windows

- [ ] 6. Log out to the title screen and log A back in (or `/xlplugins` reload
      LookingGlass). The `sky` window opens with "No messages since you logged
      in", and above it **Show older messages**. The window opened at once.
- [ ] 7. Click **Show older messages**: the lines from step 4 appear above,
      under a dimmed "Earlier: <weekday> <date>" line, with a dimmed "Since you
      logged in" line between them and anything new. Each shows its time, and
      its day if it wasn't today. Your own lines say "(you)" in the name's
      tooltip; the item and the flag are links (hover the item, click the flag:
      the map opens there); B's leave and rejoin and the rename are dimmed
      lines; no warnings and no "Not sent" lines came back.
- [ ] 8. With more than 200 lines in `sky` (have B send many, or keep the log
      over a few sessions): scrolling up with the mouse wheel at the top loads
      the next 200 above, and the line you were reading stays where it was
      (no jump). Once the start is reached, "That's everything in your chat
      history for this channel." shows at the top.
- [ ] 9. While reading old lines, have B send something: the window doesn't
      jump down; **New messages** shows; clicking it goes to the bottom, which
      follows new lines again.
- [ ] 10. Disconnect (Settings, **Disconnect**), have B send two lines, then
      **Connect now**: the caught-up lines show once in the window, not again
      among the older ones after a relog and **Show older messages**.
- [ ] 11. `fc`'s window shows only `fc`'s older lines, and `sky`'s only
      `sky`'s. Unread counts don't count older lines.

## Size limit

- [ ] 12. Set the slider to 5 MB (drag, or Ctrl+click and type 5). Nothing
      changes until it's let go. With a log over 5 MB (a long test, or a debug
      build that fills it), the size shown drops to at most 5 MB, and the
      oldest lines are the ones gone. Set it back to 50.

## Deleting, and turning it off

- [ ] 13. Click **Delete my chat history...**: a confirmation says it deletes
      the chat history of every character and server on this computer, with
      its size, and that it can't be undone. **Cancel** keeps it. Confirm:
      the `chatlog-…` folders are gone, Settings no longer shows the size or
      the button, and **Show older messages** finds nothing. Lines since login
      are still in the windows. New lines start a new log.
- [ ] 14. Log in a second character (or change server and back): each has its
      own `chatlog-…` folder, and each one's windows show only its own older
      lines.
- [ ] 15. **Reset my identity** (on a test character): the log is still there
      afterwards, and its older lines still show.
- [ ] 16. Untick **Keep chat history on this computer**: a dialog asks
      whether to delete what was kept (with its size); **Cancel** keeps the
      files, and **Delete my chat history...** stays in Settings while any
      exist. Send more lines: the files don't change. Windows no longer show
      older lines. Tick it again: older lines (from before it was turned off)
      show again after the next login.

## Elsewhere

- [ ] 17. Copy a `chatlog-…` folder to another Windows account or computer
      (with the same character): Settings says the chat history can't be
      opened there, nothing is added to it, and deleting it starts a new one.
- [ ] 18. `/xllog`: no message text, names or channel names in any
      LookingGlass line about the chat log.

## Blocking, turning it off and on, and a new address

- [ ] 19. With B's older lines showing in `sky`, block B (a member's menu):
      B's older messages disappear from the window. Unblock B: they show again.
- [ ] 20. Untick and tick **Keep chat history on this computer** in one
      session, then **Show older messages**: nothing from this session shows
      twice (not above and below "Since you logged in").
- [ ] 21. Change the server address to another address of the same server and
      choose **Keep my identity**: the character's `chatlog-…` folder is
      renamed to the new address's, and after reconnecting, **Show older
      messages** shows the lines from before. Choosing **Start afresh there**
      moves nothing.

## A new channel key

A channel's key is now replaced once it is a week old (see
[Keys have a maximum age](../design.md#keys-have-a-maximum-age)). Waiting a
week isn't practical in game, so automated tests cover it (`EpochMaxAgeTests`:
not before a week, once after, one member's key when several are online, catch-up
across it, older plugins, a server lying about it). This only checks that a new
key, made by hand the same way, changes nothing you can see.

- [ ] 22. With B online in `sky`, open `/lgdebug`, choose `sky` and press
      **Force rekey**: the key epoch shown goes up by one. Nothing appears in
      game chat, in `sky`'s window or anywhere else, for A or for B.
- [ ] 23. Send a line each way: both show as usual. Log out and in: **Show
      older messages** shows the lines from before and after the new key.
      `/xllog`, with Dalamud's log level at Debug (the rekey's lines show only
      then): they show no names and no channel names.

## Kept as text files too

The chat history can also be kept unencrypted, as plain text files (see
[Kept unencrypted too](../design.md#kept-unencrypted-too-opt-in)). Start with
**Keep chat history on this computer** on, and give `sky` and `fc` names you
can recognise. Steps 24 to 28 and 30 to 35 need only A: for "B sends", use
`/lgdebug` > Channel tools > pick the channel > **Simulate incoming** (it
arrives as a message from "Simulated Sender"). Step 29 needs B.

- [ ] 24. Under **Size limit**, **Keep my chat history unscrambled** is
      unticked, with a **?**: its bubble says it also writes new messages to
      text files any program can open, one per channel per month, and that
      anything that can read your files can read them. No "log", "encrypted"
      or "key" in simple mode; in advanced mode the setting reads **Keep my
      chat log unencrypted**. Untick **Keep chat history on this computer**:
      the setting disappears with the size slider (Cancel the delete offer),
      then tick it again.
- [ ] 25. Tick **Keep my chat history unscrambled**: a dialog asks first. It
      says the files aren't scrambled, that anyone or anything that can read
      your files can read them, including backup and cloud-sync tools, that
      nothing kept before is copied to them, that they count towards the size
      limit, and that **Delete my chat history** deletes them too. **Cancel**:
      it stays unticked, and there is no `text` folder in the `chatlog-…`
      folder. Tick it again and press **Turn on**: it is ticked, and **Open
      folder** shows under it.
- [ ] 26. Press **Open folder**: the file manager opens a `text` folder inside
      this character's `chatlog-…` folder, and it is empty: nothing kept
      before was converted. The game doesn't stutter.
- [ ] 27. Send a few lines in `sky` (one with an item link), have B send one,
      and send one in `fc`. A folder appears for each channel, named like
      `<sky's name> (LGC1) 1a2b3c4d`, each with a file for this month
      (`2026-10.txt`). Open one in Notepad: one line per message, like
      `[2026-10-10 21:03] [sky] Alice Liddell@Twintania: pulling at 9`, with
      the same times as the window, your local time; the item shows as
      `[Item name]`; accents and symbols (é, ☆) show correctly. Nothing else
      changed: the window and game chat show the same as before.
- [ ] 28. Leave Notepad open on the file, and send two more lines: no error,
      nothing in game chat; close and reopen the file in Notepad: the new
      lines are at the end, each on its own line. Delete the file in the file
      manager while playing, then send a line: a new file starts with it.
- [ ] 29. **(needs B)** B leaves and rejoins `sky`: the leave and rejoin show
      as lines in everyday words (no "key"). Then A presses **Disconnect**, B
      sends two lines, A presses **Connect now**: the file gets a line saying
      2 messages were sent while you were away, then B's two lines, each
      starting with the time it was sent and when it arrived, like
      `[2026-10-10 20:15, arrived 21:03]`. No warning lines and no "Not
      sent" lines ever show in the files.
- [ ] 30. Rename `sky` (or move it to another number in the channel list) and
      send a line: the folder is renamed to the new name or number, with the
      same 8 characters at the end, and the older lines are still in its
      file. Create a second channel with the same name as `sky`, send in it:
      it gets a folder of its own, with other characters at the end.
- [ ] 31. Settings' "Your chat history uses ..." grows as the text files do
      (compare with the files' sizes in the file manager: the `chatlog-…`
      folder's size, text folder included, matches). Optional, with a large
      history: set the size limit to 5 MB: the size drops to at most 5 MB, and
      the text files that went (if any) are a whole oldest month's, in every
      channel's folder. Set it back to 50.
- [ ] 32. Untick **Keep my chat history unscrambled**: no dialog, **Open
      folder** disappears. Send more lines: the text files don't change, but
      after a relog **Show older messages** still shows them (the scrambled
      history goes on). The text files stay.
- [ ] 33. Tick it again (**Turn on**), send a line, then **Delete my chat
      history...**: the confirmation says the text files go too. Confirm:
      the `chatlog-…` folders are gone, text files and all. Send a line: a
      new `text` folder starts, with only that line.
- [ ] 34. Log in a second character (or another server): **Open folder**
      opens that character's own `text` folder; the first character's files
      aren't in it.
- [ ] 35. `/xllog`: the lines about turning it on or off name no channel,
      folder, file or person, and say nothing that was said.
