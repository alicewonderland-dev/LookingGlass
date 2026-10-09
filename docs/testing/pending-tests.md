# LookingGlass: in-game tests still to run

The tests are split into three groups: things one player can do alone, things
that need two players (or more), and things only the owner can do. Each task
names its checklist (linked) and step numbers. The full wording is in that
checklist, and that wording is the one to follow. The lines
here only say roughly what to do.

The owner hands out the tasks and ticks them off as results come in, so ask
which ones are yours before you start, and send your results to the owner.

**Plugin version:** 0.2.12 is current. Each feature below says the oldest
version it needs. Use 0.2.12 when you can.

**Settings names:** the tasks use the Settings window's names from its
redesign (the first build after 0.2.12). On an older build, these settings
still have their old names: **Server address** was "Server URL", **Show
messages in** was "Show messages in the chat channel", **Colour the whole
line** was "Colour the whole line in a channel's colour", **Use nicknames in
tags** was "Show nicknames in chat tags", **Say when I start or stop talking
in a channel** was "Verbose channel messages", **Show LookingGlass only in
windows** was "Show LookingGlass messages only in windows" (its choices **As a
tab** and **In a new window** were "Add it as a tab to the window used last"
and "Open a new window each time"), local chat's **Colour** was "Local chat
colour" (under Chat), **Read it** was "What it tells the server", and **Keep
chat history on this computer** was "Keep a chat history on this computer".

**"A" and "B":** A is you. B is the second player. Any task in group 2 can
also be done by one person playing two characters on two game clients.

## How to report

For every task, send:

- **Which test:** the checklist name and step numbers (e.g. "local-chat 9–11").
- **Pass or fail:** for each step.
- **What happened:** for a failure, what you saw and what you expected.
  Copy any LookingGlass lines word for word, or screenshot them.
- **When it happened:** the time to the minute, and your time zone. The owner
  uses this to find the right spot in the logs, so you don't need to copy any
  logs yourself.
- **Screenshots:** for anything visual (colours, menus, windows, where a
  line showed up).
- **Your setup:** plugin version, and whether ChatTwo was on or off.

---

# Group 1: one player

Each of these can be done by one player with the plugin. Some say they need
another character nearby (a stranger, a friend or one of your alts). That
character doesn't need LookingGlass unless the task says so.

### Local chat (`/lgl`), needs 0.2.11

- [ ] **Is `/lgl` free, and what does it say** ([local-chat](local-chat-checklist.md): steps 1–3). Type `/xlhelp`: `/lgl` is listed once, by LookingGlass. Turn LookingGlass off and type `/lgl`: the game says that command doesn't exist. Turn it back on: `/lgl`'s line in `/xlhelp` explains it (friends near you who use LookingGlass, about 20 yalms, and, in 0.2.12, that `/lgl` alone talks in local chat until `/s` on its own). Settings, under Local chat, has **Colour** with a **?** whose bubble says the same, briefly. Needs: nothing extra.
- [ ] **The "ask first" window** ([local-chat](local-chat-checklist.md): steps 3a–3c). Type `/lgl hello` for the first time → nothing is sent, a blue line says local chat asks first, and a window explains what the server learns, in plain words. **Not now** closes it, and the next `/lgl` asks again. **Accept and use local chat** turns it on. Settings then shows **Privacy notice accepted**, with **Read it** and **Withdraw** (Withdraw makes it ask again). Accept again at the end. Needs: nobody else. Every tester who will do the group 2 local chat tasks should do this first, except the B in "Receiving before accepting" (see group 2).

### Talking in local chat (`/lgl` alone), needs 0.2.12

Setup for all of these: you have accepted local chat (the "ask first" task
above), you're in a channel `sky` on `/lgc1`, you've picked a swatch as
local chat's **Colour**, and **Say when I start or stop talking in a channel** is on. Stand away from
everyone unless a task says otherwise. ChatTwo off until the ChatTwo task.

- [ ] **Starting, and the once-only line** ([local-chat](local-chat-checklist.md): step 29). `/lgl` alone → "Now talking in [Local]." and, the first time ever, a line saying typing now goes to [Local] and to type `/s` on its own to stop. The chat box names its channel `[Local]`, the server info bar shows "LG [Local]" in your local chat colour. `/lgl` again → only "Now talking in [Local].". Needs: nothing extra.
- [ ] **Typing never reaches Say** ([local-chat](local-chat-checklist.md): steps 30–31). Alone, `hello` → "Not sent to [Local] or game chat: nobody is near enough…", no Say line; a line with only an item link → the same. Next to a stranger, `hello` → told nobody near is a friend, and the stranger sees nothing. Needs: any other player nearby for the second part.
- [ ] **Commands, one-offs and moving between channels** ([local-chat](local-chat-checklist.md): steps 32–33). `/s hi`, `/p brb`, `/em waves`, `/lgc1 hi` and `/lgl hi` each go where they always do, once, and local chat goes on. `/lgc1` alone → talking in `sky`; `/lgl hi` there → local once; `/lgl` alone → back to `[Local]`. Nicknaming `sky` `Local` is refused. Needs: a party for `/p brb` (optional).
- [ ] **Ending it** ([local-chat](local-chat-checklist.md): steps 34–36). `/s` alone, Tab, clicking the info bar, and **Send Tell** from a right-click each end it. **Disconnect** says "…: disconnected." and turning the plugin off says "…: LookingGlass was turned off." even with verbose off; logging out leaves nothing on. With verbose off, starting and `/s` say nothing but the labels still change. Needs: nothing extra.
- [ ] **Refused to start, and the privacy notice** ([local-chat](local-chat-checklist.md): steps 37–39). Disconnected, `/lgl` alone → "Can't switch to [Local]: not connected…", nothing starts. Withdraw local chat in Settings → `/lgl` alone asks you to accept and opens the window, nothing starts; accept → told to type `/lgl` again to talk in local chat. Withdraw while talking in local chat (in Settings, then in the privacy window) → it stops at once, "Stopped talking in [Local]: you withdrew the privacy notice.", and typing goes to Say again. Accept again at the end. Needs: nothing extra.
- [ ] **Log and plain words** ([local-chat](local-chat-checklist.md): steps 40–41). `/xllog`'s `[sticky]` lines say "talking in [Local]", never what you typed or anyone's name. Every line above is in everyday words in simple mode. Needs: nothing extra.
- [ ] **With ChatTwo** ([local-chat](local-chat-checklist.md): steps 42–46). ChatTwo's label reads "LookingGlass [Local]" in your local chat colour. In its main input, `hello` → local chat, `/s hi` → Say once; on Party, `hi` → local chat and `/p hi` → Party once; a pop-out's `hi` and `/p hi` → local chat, `/party hi` → Party. ChatTwo's picker or a tab with another channel ends it. On a tell, `/lgl` alone is refused. Needs: ChatTwo installed and on, a party for the Party part.

### Windows only ("Show LookingGlass only in windows"), needs 0.2.9

Setup for all of these: three channels of your own (here called `sky` on
`/lgc1`, `fc` on `/lgc2` and `raid` on `/lgc3`; you can create them yourself).
Give `sky` a colour. When a task says "a message arrives", make one with
`/lgdebug` > Channel tools > pick the channel > **Simulate incoming**. Do each
task once with ChatTwo off and once with ChatTwo on.

- [ ] **The setting itself** ([windows-only](windows-only-checklist.md): steps 1–2). Settings > Chat: the new **Show LookingGlass only in windows** box is unticked, with a **?** that explains it and, under it, **New channels open** with two greyed-out choices, **As a tab** and **In a new window** (older builds: a dimmed explanation, and a tooltip on each choice). Tick it → the two choices become usable. Needs: starts with the setting off, every channel's Show in game chat on, and no channel windows open.
- [ ] **Your own lines, and LookingGlass's replies** ([windows-only](windows-only-checklist.md): steps 5, 7, 8, 9, 12). With the setting on: `/lgc1 hi` doesn't show in game chat, only once in the `sky` tab (also try typing it in the window's own box). `/lgc4 hi` and `/lgc` alone still answer in game chat, in blue. `/lgc3` alone, then `test` → it goes to a `raid` tab, not game chat. Press Disconnect in Settings and `/lgc1 hi` → "Not connected" in game chat. Right-click another player > **Invite to LookingGlass** > `sky` → the result line shows in game chat. Needs: **Say when I start or stop talking in a channel** on (for step 8), any other player to right-click (no plugin needed), and you as admin of `sky`. Do this after "The setting itself".
- [ ] **Warnings still show in game chat** ([windows-only](windows-only-checklist.md): step 11). A warning about `sky` (for example, a message that fails its checks) shows in game chat in light red, and also in the `sky` tab. Needs: a way to cause a warning. The checklist says "with the debug tools", but the debug window has no obvious button for this. Ask the owner how, or report "couldn't trigger".
- [ ] **Which window a new channel lands in** ([windows-only](windows-only-checklist.md): steps 13–16). Open `fc` in its own window, then click into the `sky` window last → a message in `raid` is added as a tab to the `sky` window, not selected. Close the `sky` window → a `sky` message goes to the `fc` window. Switch to **In a new window** → each new channel gets its own window, each a step down and right of the last, never stacked on top of another and never off screen (try it near the bottom-right corner too). Switch back, close every window, make messages arrive in two channels → one window, first channel selected, second as a tab. Needs: the setting on, Simulate incoming.
- [ ] **Nothing pops up during combat** ([windows-only](windows-only-checklist.md): step 17). Close the `sky` tab, attack a striking dummy, and Simulate incoming in `sky` while in combat → no window appears. Leave combat → it appears with the message. Needs: a striking dummy.
- [ ] **Show in game chat is locked while the setting is on** ([windows-only](windows-only-checklist.md): steps 20–21). In `sky`'s ⋮ menu and in its tab's right-click menu, **Show in game chat** shows a greyed-out red cross that can't be clicked. Its tooltip says the setting is on and where to change it. Hovering a tab says it isn't shown in game chat. Close a window whose channel you had turned off game chat yourself → no "shows in game chat again" line, and it stays off. Needs: before ticking the setting, turn one channel's Show in game chat off yourself.
- [ ] **Turning the setting off** ([windows-only](windows-only-checklist.md): steps 23, 23b, 24; changed in the build after 0.2.13). Untick the setting → the channel from step 21 (off game chat, no window) stays off and gets no window yet; Simulate incoming in it → no game chat line, and a window (or tab) opens by itself, flashing, without taking the keyboard. Then tick it, close every window, Simulate incoming in `raid` while hitting a striking dummy, untick it while still in combat → when combat ends nothing opens for `raid`, and its next message is in game chat (a channel off game chat on its own would get its window then). Then tick it, close that channel's window, log out, untick it on the title screen (or on another character), log back in → nothing opens at login; its next message opens a window. Needs: do right after the task above. The opening part of step 22 needs B (see group 2). On 0.2.13 or older, the old behaviour (a window at once when unticked) is expected instead.
- [ ] **No errors in the log** ([windows-only](windows-only-checklist.md): step 26). With the setting on, after a session of windows opening, `/xllog` shows no LookingGlass errors. Needs: nothing extra.

### Name colours, needs 0.2.9

Setup: two channels, `sky` (custom colour `#33DDAA`, nickname `sky`) and `fc`
(default colour). Another LookingGlass user, B, is a member of both. B doesn't
need to be online for these tasks.

- [ ] **Setting a colour** ([name-colours](name-colours-checklist.md): steps 1–6). Right-click B in `sky`'s member list → a small menu with B's name and **Name colour...** (B's ⋮ menu has it too). The colour window has a wheel, a code box and a preview. Typing `#FF66C` greys out **Use this colour**. Typing `#1A2B6D` shows a "very dark" note but still works. Choose `#FF66CC` → B's name turns pink in both channels' member lists. Give yourself `#FFAA00` the same way. Cancel, Escape and clicking outside change nothing. Needs: B in both channels with you.
- [ ] **The colour is kept** ([name-colours](name-colours-checklist.md): step 21). Reload the plugin (`/xlplugins`, disable then enable) and log out and in → B's colour is kept. Log in with another of your characters that shares a channel with B → B's name has the same colour there. Needs: an alt character in a channel with B. Do after "Setting a colour".
- [ ] **What's saved in the settings file** ([name-colours](name-colours-checklist.md): step 23, first part). Open `%APPDATA%\XIVLauncher\pluginConfigs\LookingGlass.json`: the colour is under `NameColours` at the top level, keyed by a number only (B's Lodestone ID, no name). Needs: a name colour set. The echo bot part of this step is in the owner group.

### Custom channel colours, needs 0.2.8

Setup: a channel `sky` (`/lgc1`) where you are the admin, coloured with
**Custom...** `#FF66CC` (pink). Check with **Colour the whole line** on, then
again with it off. The owner already checked that
plain coloured lines show right in game chat and ChatTwo. These tasks cover
the rest.

- [ ] **Colour test command, odd inputs** ([custom-colours](custom-colours-checklist.md): step 3). `/lgdebug colours #3FA7D6 00ff00` → six lines. `/lgdebug colours #nope` → only a usage line. Plain `/lgdebug` still opens the debug window. Needs: nothing extra.
- [ ] **The colour picker** ([custom-colours](custom-colours-checklist.md): steps 4–8). `sky`'s ⋮ > **Colour...** shows 40 swatches, **Default** and **Custom...**. Custom opens a wheel, a code box, a preview, and the closest game colour. Typing `#ff66cc` or `FF66CC` works, and the code reads `#FF66CC` once you leave the box. `#FF66C` or `#GG66CC` greys out **Use this colour**. A very dark colour shows a note but still works. After using pink, reopening Colour... shows an outlined pink swatch next to Custom... with `#FF66CC` in its tooltip. Swatches and Default still work. Needs: nothing extra.
- [ ] **Pink everywhere, game chat (no ChatTwo)** ([custom-colours](custom-colours-checklist.md): step 9, all parts except the caught-up message). Compare each against the pink swatch: your own `sky` message with an item link (or map link) in the middle (the text after the link is still pink); `/lgc1` alone ("Now talking in [sky]" has pink `[sky]`, and the server info bar shows "LG [sky]" in pink); right-click another player > **Invite to LookingGlass** (`[sky]` pink in the submenu); the channel list's bar and dot, and a channel window's tab, are pink. Needs: ChatTwo off, **Say when I start or stop talking in a channel** on, any other player to right-click. The caught-up part needs B (group 2).
- [ ] **Pink everywhere, ChatTwo** ([custom-colours](custom-colours-checklist.md): steps 11–14). With ChatTwo on: your message with an item link stays pink after the link. With the whole-line setting off, only the tag is pink. `/lgc1` alone → ChatTwo's input shows "LookingGlass [sky]" in pink. Right-click a player's name in ChatTwo > Integrations > **Invite to LookingGlass** → `[sky]` pink. Note anything that looks different from the game's own chat. Needs: ChatTwo installed and on, any other player's name in ChatTwo to right-click.
- [ ] **Colours are kept** ([custom-colours](custom-colours-checklist.md): steps 15–16). Give `fc` a swatch colour and `sky` a custom one. Reload the plugin and log out and in → both kept. Another character shows its own colours, not these. If you had a swatch colour set before 0.2.8, it's unchanged. Needs: an alt character with its own channels.
- [ ] **Leaving drops the colour** ([custom-colours](custom-colours-checklist.md): step 17). Create a throwaway channel, give it a custom colour, then disband or leave it → once the channel list reloads, its colour is gone from `LookingGlass.json`. Needs: nothing extra. The "doesn't come back if you're invited again" part needs someone else to invite you back (optional).

### Right-click invites, needs 0.2.8

Setup: you are the admin of `sky` (nickname, coloured) and `fc` (no nickname),
and if possible a plain member of someone else's channel (`ro`). Do it in
simple mode, then the result lines again in advanced mode. Throughout, the game
shouldn't stutter, and `/xllog` shouldn't name anyone.

- [ ] **Where the invite shows: chat, target, other lists** ([context-invites](context-invites-checklist.md): steps 1, 3, 5). Right-click another player's name in the chat log, then the player themselves, then their name in the target bar, then (if you have them) a linkshell, cross-world linkshell, Free Company or party finder list → each has **Invite to LookingGlass** (boxed blue "L") with a submenu of `[sky]` (in its colour) and `[LGC2]` (blue), in that order. `ro` isn't listed. Needs: any other player nearby who says something in /say (no plugin needed).
- [ ] **Where the invite shows: party and friends lists** ([context-invites](context-invites-checklist.md): steps 2, 4). Right-click the other player in the party list, in the Party Members window, and in Social > Friend List → same item and submenu. Needs: another character in your party and on your friends list (an alt or any friend, no plugin needed).
- [ ] **Where the invite shows: ChatTwo** ([context-invites](context-invites-checklist.md): step 6). Right-click a player's name in a ChatTwo line → Integrations > **Invite to LookingGlass** with the same coloured channels. Right-clicking the message text, an item link or empty space shows no LookingGlass item. Needs: ChatTwo on.
- [ ] **Where it must not show** ([context-invites](context-invites-checklist.md): steps 7–9a). No invite item when right-clicking: your own name or character (game menus and ChatTwo), NPCs, minions, retainers, Trust or duty support NPCs, items in your inventory, item links. In ChatTwo, an FC member's login or logout line has none, but the same player's name in an ordinary line does. Needs: ChatTwo for 7 and 9a, a Free Company for 9a.
- [ ] **No item when it can't work** ([context-invites](context-invites-checklist.md): steps 10–11). Settings > **Disconnect** → no invite item. Connect again → it's back. On a character that isn't admin or moderator of any channel → no item. Needs: a second character with no channels of its own to run.
- [ ] **Inviting someone who isn't registered** ([context-invites](context-invites-checklist.md): step 15, and the matching part of 18). Right-click a player who doesn't use LookingGlass > `[sky]` → a blue line says they aren't registered and need to install it and register first. In advanced mode it stops at "on this server.". Do it again from ChatTwo's menu → same line. Needs: any stranger (nothing is sent to them).
- [ ] **ChatTwo turned off and on** ([context-invites](context-invites-checklist.md): steps 19–21). Disable and enable ChatTwo → the invite item is still in ChatTwo's menu. Reload LookingGlass with ChatTwo on → the item shows once, not twice. Disable LookingGlass → it's gone from every menu. Start with ChatTwo off, load LookingGlass, then turn ChatTwo on → the item appears. Needs: ChatTwo installed, any player's name to right-click.

### Talking in a channel (sticky mode), with ChatTwo

- [ ] **The ChatTwo note when you start talking in a channel** ([sticky-channel](sticky-channel-checklist.md): step 28). Needs 0.2.10. Turn on **Say when I start or stop talking in a channel**. Switch ChatTwo's input to Party, type `/lgc1` → "Now talking in [sky]." in blue, followed once by a line explaining ChatTwo's "(Warning: …)", that typing still goes to [sky], and that a short command like `/p hi` goes to that game channel once. Typing `/lgc1` again shows only "Now talking in [sky].". ChatTwo's channel name reads "LookingGlass [sky] (Warning: Party)", and the info bar shows "LG [sky]". Needs: ChatTwo installed and on. You may need to be in a party for ChatTwo to switch to Party. This is also the setup for the group 2 sticky task.

### Settings: short labels and "?" bubbles, needs the build after 0.2.12

- [ ] **The labels and the "?"s** ([settings](settings-checklist.md): steps 1–9). Open Settings (the gear in `/lg`). The sections and labels are as the checklist lists them, with no dimmed paragraphs, and every setting still does what it did. A small round **?** follows exactly nine of them (eight before device notices, which added **Computers signed in**'s). Click each → a small bubble opens beside it, in everyday words; a click elsewhere, the **?** again, or Escape closes it, and Escape doesn't close Settings. In advanced mode, the chat history's **?** says it's encrypted. With Settings in the bottom right corner, every bubble stays on the screen. Narrow the window → the buttons move under their line, nothing runs off the edge. Do it once with a bigger Dalamud font too. Needs: nothing extra.

### Other computers signing in, needs the first release after 0.2.12

Setup for all of these: one character A on a **test server**, and two installs
of the plugin under the same Windows user (your usual one, and a second Dalamud
profile with its own config folder), as the checklist explains. Log out on one
install before logging in on the other.

- [ ] **The list in Settings** ([device-notices](device-notices-checklist.md): step 1). Settings > Your identity shows **Computers signed in** with a **?**, one line "This computer: added …, used just now", and **Sign out everywhere else...**. The **?** explains it in a sentence or two. Needs: nothing extra.
- [ ] **A copy of this computer's login is noticed** ([device-notices](device-notices-checklist.md): steps 2–4). Copy A's secrets file to install 2 and log in there (one computer listed); then on install 1, at login, one light red line says this computer's saved login may have been used somewhere else at that time, pointing to Sign out everywhere else, then Reset my identity if it happens again; not said again at the next login. Needs: install 2.
- [ ] **Told at the next login** ([device-notices](device-notices-checklist.md): steps 5–9). On install 2, Forget account (`/lgdebug`) and register again → on install 1, at login, a light red line "Your LookingGlass character signed in from another computer on <time>. If that wasn't you, …", and two computers listed; not told again at the next login. Install 2, at its next login, is told about install 1 the same way. Needs: install 2, the Lodestone.
- [ ] **Sign out everywhere else** ([device-notices](device-notices-checklist.md): steps 10–14). On install 2, the button asks first (plain words; says this computer couldn't sign itself in either should its login be lost; mentions Reset my identity); confirming says "Signed out everywhere else (1 other computer), and this computer has a new login" and lists only this computer, which still logs in afterwards. Install 1 then shows **You were signed out from another computer** ("another of your computers"; label **Signed out**, no Retry now), even after a restart; advanced mode words it technically. Needs: install 2.
- [ ] **Registering again lifts it** ([device-notices](device-notices-checklist.md): steps 15–16). Install 1 registers again → works, channels as before; install 2 is then told of it. Needs: install 2, the Lodestone.
- [ ] **Signed out by a computer this one doesn't know, and clean up** ([device-notices](device-notices-checklist.md): steps 17–20). Copy install 1's file to install 2 again and sign out everywhere else there → install 1 says **Signed out by a computer this one doesn't know**, says when that computer was added, and has **Reset my identity...** as the main button, with registering again offered second ("only if that computer was yours"). Optional: with 0.2.12, being signed out shows "Login not recognised". Finish with Reset my identity on install 1, and delete install 2's copy. Needs: install 2, the Lodestone.

### Window tab counts, flashing windows, and channels off game chat, needs the build after 0.2.13

Setup for all of these: three channels of your own (`sky` on `/lgc1` with a
colour, `fc` on `/lgc2`, `raid` on `/lgc3`). When a task says "a message
arrives", make one with `/lgdebug` > Channel tools > pick the channel >
**Simulate incoming** (it counts as a message from someone else). Start with
**Show LookingGlass only in windows** off unless a task says otherwise.

- [ ] **Counts on every tab not selected** ([channel-windows](channel-windows-checklist.md): steps 17, 17b, 17c). One window with `sky`, `fc` and `raid` as tabs, `raid` selected. One message arrives in `sky`, two in `fc` → `sky (1)`, `fc (2)`, and hovering `fc` says "2 new messages". Select `fc` → its count goes. `/lgc1 hi` from the game's chat box → `sky`'s count goes. Open a second window with `sky` selected; a message in `sky` → the first window's `sky` tab gets no count. Needs: Simulate incoming. (Step 17b's "someone joining" part needs a second player: skip it.)
- [ ] **A channel off game chat opens a window again** ([channel-windows](channel-windows-checklist.md): steps 33, 33b, 33c first part, 33d). Turn `sky`'s **Show in game chat** off (a window opens), then close every window with `sky` → nothing in game chat, and **Show in game chat** stays a red cross. A message arrives in `sky` → not in game chat; a window opens by itself (or a tab is added, with **As a tab** and another window open), flashing, and the game keeps the keyboard. Close it and relog → no `sky` window comes back; a message in `sky` opens one again. Close it, hit a striking dummy, and a message arrives during combat → nothing opens until combat ends. Needs: Simulate incoming, a striking dummy.
- [ ] **Channel list counts for channels seen only in windows** ([channel-windows](channel-windows-checklist.md): step 37b; [windows-only](windows-only-checklist.md): step 6b). With `sky` off game chat and a count on it, select `sky` in `/lg`'s channel list → the count stays; select the `sky` tab in its window (click into the window) → it goes. A channel that shows in game chat loses its count when selected in the list, as before. Then tick **Show LookingGlass only in windows**: every channel's count stays until its tab is looked at in a window, or you talk in it. Needs: Simulate incoming.
- [ ] **The flash** ([channel-windows](channel-windows-checklist.md): steps 37c–37f). A window that opens by itself → title bar and border pulse three times in the channel's colour over about 1.5 s, without coming to the front or taking the keyboard. A tab added by itself → its window flashes once. Windows you open yourself, and windows reopened at login, don't flash. With Dalamud's reduced motion on → a steady, softer tint for about 1.5 s, no pulsing. Report whether the flash is noticeable enough, or too much (the owner will tune it). Needs: Simulate incoming; a screen recording helps.

---

# Group 2: two players (or more)

A is you; B is a second player with LookingGlass on the same server. Where a
task needs a third or fourth character, it says "needs N players".

### Local chat (`/lgl`), needs 0.2.11 on everyone

Setup for all: A and B are on each other's in-game friends list, in the same
zone and instance, in simple mode, with A's local chat **Colour** at
**Default**. Both should do the "ask first" task from group 1 before these
(except where noted). Do the whole set once with ChatTwo off on A and once
with it on.

- [ ] **Receiving before accepting** ([local-chat](local-chat-checklist.md): step 3d). B hasn't accepted local chat yet; A has. A sends `/lgl hi` standing next to B → B sees it anyway (receiving doesn't need accepting). Needs: B who hasn't done the "ask first" step yet. Do this before B accepts.
- [ ] **Friends list not loaded yet** ([local-chat](local-chat-checklist.md): steps 4–7). Both log in afresh and don't open the Friends window. A sends `/lgl hello` next to B → note which happens: B sees it, or A is told to open the friends list once. If A's message went out but B saw nothing, B gets one blue hint (once only) to open the friends list. Both open and close Social > Friend List, then A sends again → B sees `[Local] <A@World> hello again` with a blue tag, and A sees its own line the same way. Needs: a fresh login for both. Do this before anything else in the session.
- [ ] **A long friends list** ([local-chat](local-chat-checklist.md): step 7a). On a character with more than one page of friends, log in afresh, stand next to a friend from near the end of the list, open and immediately close the Friends window, then `/lgl test` → does the friend get it? Then try after scrolling the whole list, or after waiting a few seconds. Needs: a character with a big friends list, and that friend using LookingGlass.
- [ ] **Replies, links and range** ([local-chat](local-chat-checklist.md): steps 8–11). B replies with `/lgl hi` → A sees it. An item link shows as a hoverable link, and `<t>` becomes the target's name. A walks away from B sending `/lgl test` every few yalms → it reaches up to about 20 yalms, then "nobody is near enough". Compare with how far `/say` reaches and note both distances. From about 25 yalms nothing is sent. From about 18 yalms, then walking away at once, B still gets it. B in another zone or instance → A is told nobody is near enough. Needs: room to walk (target B to see the distance).
- [ ] **Not to strangers** ([local-chat](local-chat-checklist.md): steps 12–13). **Needs 3 players.** C, who is nobody's friend and uses LookingGlass, stands next to A and B. A's `/lgl friends only` → B sees it, C sees nothing (game chat or ChatTwo). C's `/lgl hello` with only A near → C is told nobody near is on its friends list (or to open its list). A sees nothing. Needs: C with LookingGlass, not on A's or B's friends list.
- [ ] **A friend without LookingGlass** ([local-chat](local-chat-checklist.md): step 14). **Needs 3 players.** D is A's friend but doesn't use LookingGlass (or has it off). With only D near A: A's `/lgl hi` → told none of your friends near uses LookingGlass. With D and B both near → B gets it, and nothing more is said. Needs: D (A's friend, no LookingGlass).
- [ ] **Blocking** ([local-chat](local-chat-checklist.md): steps 15–16). A and B share a channel. A blocks B (right-click B in the member list > Block). B's `/lgl can you hear me` next to A → A sees nothing. A's own `/lgl` → B gets nothing, and if B was the only friend near, A is told none of its friends near uses LookingGlass. A unblocks B (Settings > Blocked users) → B's next `/lgl` shows. Needs: a channel together.
- [ ] **Plain words** ([local-chat](local-chat-checklist.md): steps 17–18). Over all the local chat lines above, nothing technical ("key", "encrypted", "signature") appears in simple mode, and advanced mode says the same. Do this alongside the other tasks. Needs: switch Settings > **Advanced mode** on for a second pass.
- [ ] **Colours and where local lines go** ([local-chat](local-chat-checklist.md): steps 19–23). On A, pick a local chat **Colour** swatch → the next local line is in it (whole line, or only `[Local]` with the whole-line setting off). **Custom...** shows exactly (in ChatTwo too). **Default** → only the tag, blue. Give B a name colour → B's name in local lines uses it. Turn on **Show LookingGlass only in windows** → local lines still show in game chat. Local lines go to the chat channel chosen in Settings, so a chat tab that hides that channel hides them. While talking in a channel (`/lgc1` alone), `/lgl hi` still goes to local, and you keep talking in the channel. Needs: standing within 20 yalms, a channel together.
- [ ] **Logs keep names out (player side)** ([local-chat](local-chat-checklist.md): step 25, players' part). After the tasks above, `/xllog` on A and on B: the local chat lines give counts only, never a name or a message. Needs: Dalamud's log open. The server log part is in the owner group.
- [ ] **B resets their identity** ([local-chat](local-chat-checklist.md): step 26). B uses **Reset my identity** and registers again, then sends `/lgl hi` next to A → A doesn't see it and gets no key warning, but one blue line says B's local message couldn't be checked (B may have set up again, or someone may be using their name) and to check with them over /tell. B sends again → no second line. After checking over /tell, A sends `/lgl hi` → A gets the "B set up LookingGlass again" warning, and B's next `/lgl` shows. Needs: B willing to reset their LookingGlass identity (use a spare character).
- [ ] **No line when out of range, or from a stranger** ([local-chat](local-chat-checklist.md): step 27). **Needs 3 players** for the C part. Same as the task above, but with B out of range, or with C (not a friend) having re-registered → no line at all. Needs: do right after "B resets their identity".
- [x] **Name or world change** ([local-chat](local-chat-checklist.md): step 28). **Not tested: a rename or world transfer costs real money, so this is left until it comes up after public release.** Optional, only if one is happening anyway. After A has had a local message from B, B is renamed or moved. B's next `/lgl` isn't shown, and one blue line says B may have changed name or world. A's `/lgl` to B then fixes it. Needs: a rename or world transfer.

### Talking in local chat (`/lgl` alone), needs 0.2.12 on A

- [ ] **Talking to a friend near you** ([local-chat](local-chat-checklist.md): step 47). Setup as for the local chat tasks above (both accepted, both opened the Friends window). A types `/lgl` alone, then `hello there` → B sees `[Local] <A@World> hello there`, nobody sees it in Say. A line with only an item link, and one with `<t>`, arrive as a link and the target's name. B walks beyond 20 yalms → A's next line says nobody is near enough, and nothing goes to Say. A types `/s` alone, then `bye` → B sees it in Say. Needs: B, A's friend, with LookingGlass.

### Log heads in messages, needs the build after 0.2.12 on A and B

Little new to see: messages now carry a check that only a malicious server
would set off, and the automated tests cover that. These tasks make sure
normal chat is unchanged, and that the new check code is the same for both.

- [ ] **Normal chat is unchanged** ([chat-links](chat-links-checklist.md): steps 16–17). A and B chat in `sky` both ways, with and without an item link → everything arrives as before, with no LookingGlass warning on either side (none saying someone "seems to see a different member list"). Then A invites a third character (or B leaves and A invites B back) while both go on chatting → still no warning. Needs: a third character for the invite, or B willing to leave and rejoin.
- [ ] **Check codes match** ([chat-links](chat-links-checklist.md): step 19). Above `sky`'s member list, A and B each see "Check code: #…" followed by five groups of five digits, the same on both. Advanced mode labels it "Log head", same code; hovering explains it. After the change in the task above, both move on to the same new code. Nobody in the member list has a warning sign for "Sees a different member list". Needs: do alongside the task above.
- [ ] **An older plugin still talks** ([chat-links](chat-links-checklist.md): step 18). Optional. A on this build, B on an older one (0.2.12 or before): both directions arrive as before, no error or warning on either side. Needs: B on an older build.

### Window tab counts, flashing windows, and channels off game chat, needs the build after 0.2.13 on A

Setup: as in group 1's task of the same name, with B in `sky` and `fc`.

- [ ] **Messages from B in tabs and new windows** ([channel-windows](channel-windows-checklist.md): steps 17b, 17d; [windows-only](windows-only-checklist.md): steps 3, 6). With `raid` selected in a window that also has `fc`, B sends twice in `fc` → `fc (2)`; someone joining `fc` adds nothing. Relog while B sends in a tab behind the selected one → once the window is back, that tab counts it. Tick **Show LookingGlass only in windows**, close every window, B sends in `sky` → a window opens and flashes three times; B leaves `fc` and A invites B back → an `fc` tab is added behind `sky` and the window flashes once. Needs: B sending at the right moments.
- [ ] **Missed messages open a window for a channel off game chat** ([channel-windows](channel-windows-checklist.md): step 33c, second part). `sky` off game chat, no window with `sky`. A logs out, B sends in `sky`, A logs back in → no game chat lines for `sky`; a window opens with the missed messages once the channel list is in. Needs: B sending while A is logged out, a server that keeps messages.

### Windows only, needs 0.2.9 on A

Setup: as in group 1 (`sky`, `fc`, `raid`), with B in `sky` and `fc`, and A
able to invite in `fc`. Do it once with ChatTwo off and once on, on A.

- [ ] **Messages from B go only to a window** ([windows-only](windows-only-checklist.md): steps 3–4, 6, 10). Tick the setting. B sends `hello` in `sky` → nothing in game chat, and a window opens with a `sky` tab. It doesn't steal the keyboard: keep walking while it opens. B sends again → same window, no new tab. B leaves `fc` and A invites B back → the "left"/"invited" lines aren't in game chat, an `fc` tab appears behind `sky` (sky stays selected), and its count goes up only for messages. B invites A to a new channel → "B invited you" shows in game chat. Needs: **Say when I start or stop talking in a channel** on.
- [ ] **Messages from while you were away** ([windows-only](windows-only-checklist.md): steps 15b, 25). With **In a new window**, close every window and log out. B sends in `sky` and `fc`. Log in → one new window with both as tabs. Then with the setting on, relog → it stays on, windows come back where they were, missed messages don't show in game chat, and a channel with some and no window gets one. Needs: B sending while A is logged out.
- [ ] **Nothing pops up in cutscenes or loading screens** ([windows-only](windows-only-checklist.md): steps 18–19). Close the `sky` tab. While A watches a cutscene (a quest one, or the Inn's **Unending Journey**), B sends in `sky` → nothing opens until the cutscene ends. Same while A teleports → it opens once A arrives. Needs: B sending at the right moment (voice chat helps).
- [ ] **Turning the setting off** ([windows-only](windows-only-checklist.md): step 22). Untick the setting. B sends in `sky` → it shows in game chat again at once (and in its window). Each channel's Show in game chat is as it was before. Needs: do before the group 1 "Turning the setting off" task (steps 23–24).

### Name colours, needs 0.2.9 on A

Setup: as in group 1 (B pink `#FF66CC`, A orange `#FFAA00`, `sky` teal
`#33DDAA`, `fc` default), whole-line setting on, ChatTwo off until stated.
Do the group 1 "Setting a colour" task first.

- [ ] **Name colours in game chat and windows** ([name-colours](name-colours-checklist.md): steps 7–15). B talks in `sky` → `[sky]`, brackets and message teal, B's name exactly pink. In `fc` → name pink, rest in normal chat colours. B sends "look at [item] here" → after the link, still teal, and the link still works. A's own line → name orange. Whole-line setting off → only tag and name coloured. `sky` on a green swatch → rest green, even after a link. A missed message (A logged out while B sent) → time uncoloured, name pink. In a `sky` channel window, names are coloured, and right-clicking B's line offers **Copy** and **Name colour...** (a change shows at once everywhere). A blue/red notice line offers only Copy. Needs: B sending, an item link.
- [ ] **Name colours in ChatTwo** ([name-colours](name-colours-checklist.md): steps 16–19). With ChatTwo on: B's name in its exact colour, the rest of the line teal, even after an item link, and nothing leaks into the next line. In `fc`, only the name is coloured. With the whole-line setting off, only the tag and name. Note any difference from the game's own chat. Needs: ChatTwo installed.
- [ ] **Back to default** ([name-colours](name-colours-checklist.md): step 20). Right-click B > Name colour... > **Default** → B's name goes back to normal everywhere (chat, window, both member lists). B's next line looks exactly as before name colours existed. Needs: B sending one line.
- [ ] **Leaving and rejoining** ([name-colours](name-colours-checklist.md): step 22). B leaves `fc` → B's colour stays in `sky`. A invites B back to `fc` → same colour there. Needs: A able to invite in `fc`.
- [x] **Name or world change** ([name-colours](name-colours-checklist.md): step 24). **Not tested: a rename or world transfer costs real money, so this is left until it comes up after public release.** Optional, only if one is happening anyway: B's colour stays with B.

### Custom channel colours, needs 0.2.8 on A

- [ ] **Missed messages are pink too** ([custom-colours](custom-colours-checklist.md): step 9, caught-up part). With `sky` on `#FF66CC`: A logs out, B sends in `sky`, A logs in → the caught-up line has a pink tag, an uncoloured time, and the rest as usual (pink with the whole-line setting on). Needs: B sending while A is offline.

### Right-click invites, needs 0.2.8 on A

Setup: A is the admin of `sky` and `fc`. B uses LookingGlass but is in
neither. Simple mode first, then the result lines again in advanced mode.

- [ ] **Inviting, and the greyed-out entries** ([context-invites](context-invites-checklist.md): steps 12–14, 16, 18). Right-click B > Invite to LookingGlass > `[sky]` → blue "Invited B@World to [sky]." (tag in sky's colour), and B gets the invite. Right-click B again → `[sky] … (already invited)` greyed out and unclickable, `[LGC2]` still pickable. B accepts → `[sky] … (already a member)`. Pick `[LGC2]` → "Invited … to [LGC2]." in blue. A blocks B → every channel shows "(you blocked them)", greyed out. Unblock in Settings → normal. Repeat the invite and the greyed-out checks from ChatTwo's menu → same lines. Needs: B with LookingGlass, not in A's channels. ChatTwo for the step 18 part.

### Chat history on this computer, needs 0.2.7 on A

Setup: A and B in `sky` (`/lgc1`), and A in `fc` (`/lgc2`). The chat history
lives in `%APPDATA%\XIVLauncher\pluginConfigs\LookingGlass`, in `chatlog-…`
folders. Throughout, game chat shouldn't change at all. Simple mode, then
advanced mode for wording. (The solo steps were already done.)

- [ ] **Off by default** ([chat-log](chat-log-checklist.md): step 2). With **Keep chat history on this computer** never turned on (or unticked, with the history deleted), open a `sky` window and chat with B for a bit → still no `chatlog-…` folder, and no **Show older messages** in the window. Needs: the setting off and no existing history.
- [ ] **B's lines are kept and shown later** ([chat-log](chat-log-checklist.md): steps 4, 6–7, B's parts). Turn the history on. Chat in `sky` (A with an item link and a `<flag>`, B a few lines). B leaves and rejoins `fc` (or someone is invited), and rename `fc` if you can. Relog A → the `sky` window says "No messages since you logged in" with **Show older messages**. Click it → B's lines appear under "Earlier: <date>", and B's leave/rejoin and the rename show as dimmed lines. Your own lines say "(you)" on hover. The item and flag links work. Needs: B in `sky` and `fc`.
- [ ] **Lots of history, and new messages while reading** ([chat-log](chat-log-checklist.md): steps 8–9). With over 200 lines in `sky` (B sends many), scroll up at the top → the next 200 load, and the line you were reading stays in place. At the very start: "That's everything in your chat history for this channel.". While reading old lines, B sends → the window doesn't jump. **New messages** appears, and clicking it goes to the bottom. Needs: B willing to send a lot of lines.
- [ ] **Missed messages, and channels kept apart** ([chat-log](chat-log-checklist.md): steps 10–11). A presses **Disconnect**, B sends two lines, A presses **Connect now** → they show once in the window, and not again among older lines after a relog. `fc`'s window shows only `fc`'s older lines and `sky`'s only `sky`'s. Unread counts don't count older lines. Needs: B sending in `sky`.
- [ ] **Blocking hides old lines** ([chat-log](chat-log-checklist.md): step 19). With B's older lines showing in `sky`, A blocks B → B's older messages disappear from the window. Unblock → they come back. Needs: B's lines in the history.

### Talking in a channel (sticky mode), with ChatTwo

Setup: B is in A's party and Free Company and stands next to A, so B can see
where each line lands (Say, Party, FC or LookingGlass). Both in `sky`
(`/lgc1`). ChatTwo on, on A. **Say when I start or stop talking in a channel** on.

- [ ] **Short commands while ChatTwo is on Party** ([sticky-channel](sticky-channel-checklist.md): steps 30, 30b, 32b; set up as in step 28). Needs 0.2.10 on A. ChatTwo on Party, `/lgc1`. Type `/p hi` → B sees it in Party once, and A is still talking in [sky]. Type `hi` → only LookingGlass. Switch ChatTwo to Say, `/lgc1`, `/s hi` → Say once. `hi` → only LookingGlass. Back on Party: `hi` followed by three spaces → only LookingGlass, never Party. Then `/s hi ` with a space after it (and once with a space before it too) → Say once, not [sky]. Needs: B in A's party, close enough to see Say. Note the time of each step: the owner checks the log for step 30b.

---

# Group 3: owner only

These need the server (SSH, the journal, `LG --ban`/`--bans`, settings,
restarts, Debug logging) or the owner's own decisions. Where a helper player is
needed, it says so.

### Flags and bans (abuse-bans checklist), plugin 0.2.11

The checklist says to use a **test server, never the public one**, because
these steps flag and ban characters. Decide which server before starting. A
is a character the owner can afford to have banned for a day (the owner's own
alt, or a willing helper). B is a helper in `sky` with A, on a **different
internet connection**.

- [ ] **Setup and starting checks** ([abuse-bans](abuse-bans-checklist.md): setup, steps 1–5). Define the `LG` shorthand, add the quick-flag and Debug logging drop-in, and restart. Connect A → "Connection from" shows A's real public address (not localhost, the server's own address or a 100.x one). Check the startup line's thresholds. `LG --bans` shows nothing, exit 0. `LG --ban` alone exits 2. `/8` is refused (exit 1). `127.0.0.1` and `::1` are refused (exit 1). Needs: SSH, A connected.
- [ ] **Flags** ([abuse-bans](abuse-bans-checklist.md): steps 6–8). As A, invite three players in a row → the third is refused ("looked up a lot of players"). Repeat the next minute. The journal has one Warning "Flagged user …" with no names, and a third minute adds no second one. `LG --bans` lists A's user and A's public address as flagged. Needs: A in game.
- [ ] **Banning an account** ([abuse-bans](abuse-bans-checklist.md): steps 9–15). `LG --ban "A@World" --days 1 --reason "Testing bans"` → A is disconnected within 30 s, with one warning (simple mode wording checked), and `/lg` shows **Blocked by the server** with **Try again now**. `/lgc1 hello` says not connected. A stays off for 2 minutes with no repeat warning. B sees A offline but still a member, and B's messages go through. Try again now → refused at once, no second warning. Plugin off and on → still blocked, warning once more. Needs: player A (the banned-player plugin steps 10–12, 14–15) and helper B in `sky` (step 13).
- [ ] **Lifting the ban** ([abuse-bans](abuse-bans-checklist.md): steps 16–19). `LG --bans` shows it in force, then `LG --unban` → A presses Try again now → Connected, and A and B chat both ways in `sky`. `--bans` then lists it as lifted. Needs: A and B. Do right after the ban.
- [ ] **Registering again while banned** ([abuse-bans](abuse-bans-checklist.md): steps 20–21). Ban by user ID. On A, `/lgdebug` > **Forget account**, then register again through the Lodestone → refused after Verify, shows Blocked. Unban, Try again now, register → works, and channels are back. Needs: A. The checklist says "on this test server only".
- [ ] **Banning an address** ([abuse-bans](abuse-bans-checklist.md): steps 22–24). Find B's real public address from "Connection from". `LG --ban <B's address> --reason "Testing address bans"` → B is disconnected within 30 s and told their internet address was blocked (others may share it), with the reason. A on another connection is unaffected. Unban → B reconnects (at once with Try again now). Needs: helper B on a different internet connection from A.
- [ ] **Automatic blocks** ([abuse-bans](abuse-bans-checklist.md): step 25). Lower the auto-block settings, restart, and fire 120 connections in a minute from a spare machine → the journal says the address was blocked automatically, `LG --bans` lists it, and there are no more "Connection from" lines for it. It's gone after 5 minutes. Put the settings back. Needs: a spare machine (not A's or B's connection).
- [ ] **Clean up** ([abuse-bans](abuse-bans-checklist.md): step 26). Remove all test settings and restart. `LG --bans` shows no bans in force. Needs: SSH.

### Local chat: server side, needs 0.2.11

- [ ] **Local chat turned off on the server** ([local-chat](local-chat-checklist.md): step 24). Set `LookingGlass:Limits:MaxLocalRecipients` to 0 and restart → `/lgl hi` says local chat isn't available on this server, and (in 0.2.12) so does `/lgl` alone, which doesn't start talking in local chat. Put it back. Needs: a server settings change and restart.
- [ ] **Server log keeps names out** ([local-chat](local-chat-checklist.md): step 25, server part). After the group 2 local chat tasks, check the server's log: local chat lines give counts only (friends near, sent, dropped, why), never a name or message. Needs: journal access, the times testers report.

### Right-click invites: limits, needs 0.2.8

- [ ] **Hitting the invite limit** ([context-invites](context-invites-checklist.md): step 17). Start the server with `LookingGlass__Limits__InviteBurstPerPair=2` (test server), and invite B to three channels → a blue "Couldn't invite …:" line says how long to wait, without "(RateLimited)" in simple mode and with it in advanced mode. The channel's **Invite** button says the same after "Inviting … failed:". Needs: a server settings change, and B (any LookingGlass user).

### Name colours: echo bot, needs 0.2.9

- [ ] **Echo bot colour key** ([name-colours](name-colours-checklist.md): step 23, echo bot part). On a test server with the echo bot, give it a name colour → its key in `LookingGlass.json` ends in `@` and the server's address. Change only the case of the server address's host in Settings (or add a trailing `/`) → the echo bot keeps its colour. Needs: a test server running the echo bot.

### Other computers signing in: server side, needs the first release after 0.2.12

- [ ] **Upgrading the server** (see [server.md](../server.md#upgrading), "From a server without device notices"). Update the test server and restart → it starts on schema 11 with no warning, and players already registered connect as before. Needs: SSH.
- [ ] **Letting a key sign in again** (see [server.md](../server.md#letting-a-key-sign-in-again)). After the group 1 task's step 12 (install 1 signed out), `LG --allow-key-login "A@World"` → exit 0, and install 1 signs in by itself at its next connection (or at once after closing and reopening the plugin); install 2 is then told that another computer signed in. Run it again → exit 1, "isn't turned off". `LG --allow-key-login 203.0.113.5` → exit 1. Needs: SSH, a tester at step 12.
- [ ] **What the journal says** ([device-notices](device-notices-checklist.md): steps 11–12, server part). After the group 1 task "Sign out everywhere else", the journal has "User … signed out their other devices (1)" and, when install 1 connects, "Key login for … refused: the account signed out its other devices": user IDs and the address only, never a name. Needs: journal access, the times the tester reports.

### Custom colours: the decision

- [ ] **Decide** ([custom-colours](custom-colours-checklist.md): "Decision"). Based on the results (basic colours already confirmed, plus the group 1 and 2 custom colour tasks), tick "keep custom colours", or note where exact colours fail and offer the full game colour table instead.
