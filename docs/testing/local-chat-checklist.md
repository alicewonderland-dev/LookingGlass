# Local chat: in-game checklist

Local chat with friends near you, `/lgl` (see
[Local chat (friends only)](../design.md#local-chat-friends-only)). You need
three characters on this build, each registered on the same server, on three
clients (or PCs) in the same zone and instance:

- **A** and **B** are on each other's in-game friends list.
- **C** is on neither's friends list.

Optionally a fourth character **D** who is A's friend but doesn't use
LookingGlass (or has it turned off). Start with every character's Friends
window not yet opened this session (log in afresh), simple mode (Settings,
**Advanced mode** off), and A's local chat **Colour** at **Default**. Watch
`/xllog` with debug output on, for the "Local chat" and "Local message"
lines. Do it once with ChatTwo off and once with ChatTwo on, on A.

## Before anything: is it free, and what does the game hold?

- [ ] 1. `/xlhelp`: `/lgl` is listed once, by LookingGlass, with a line on
      what it does. No other plugin you use claims it, and the game has no
      `/lgl` of its own (type `/lgl` with LookingGlass turned off: the game
      says the command doesn't exist).
- [ ] 2. **(changed)** In `/xlhelp`, `/lgl`'s line says how to use it:
      `/lgl <message>` talks to friends near you who use LookingGlass (about
      20 yalms, only friends get it), and `/lgl` alone sends everything you
      type there until you type `/s` (or another channel) on its own. (`/lgl`
      alone no longer explains itself: it talks in local chat, see *Talking in
      local chat* below.)
- [ ] 3. **(changed)** Settings, under **Local chat** (a section of its
      own): **Colour** with a **?** and a swatch. The **?** opens a small
      bubble: "/lgl message talks to friends near you who use LookingGlass.
      /lgl on its own keeps talking there until you type /s."

## Asking first (each of A, B and C, once)

- [ ] 3a. With a friend near, type `/lgl hello`. Nothing is sent (the friend
      sees nothing); game chat says, in blue, "Not sent: local chat first asks
      you to accept…", and a window opens: **Local chat: what the server
      learns**. It says the server learns the names of friends near you, those
      who don't use LookingGlass too, and who you send to and when; that it
      never sees what you say; and that receiving needs none of it. In simple
      mode it has no technical words.
- [ ] 3b. **Not now**: the window closes; `/lgl hello` again asks again.
- [ ] 3c. **(changed)** **Accept and use local chat**: the window closes, and game chat says
      "Local chat is on. Send your message again with /lgl <message>."
      `/lgl hello` now sends. Settings, under **Local chat**, says **Privacy
      notice accepted** (before: **Privacy notice not accepted yet**, with
      only **Read it**), with a **?** and the buttons **Read it** and
      **Withdraw**, all visible at the window's usual width (on one line, or
      the buttons under it if the window is narrow). The **?** says the
      server learns the names of friends near you when you use /lgl, and
      never sees what you say. **Read it** opens the window with **Withdraw**
      and **Close**. Either **Withdraw** makes `/lgl` ask again. Accept again
      before going on.
- [ ] 3d. Before accepting, local messages from a friend who has accepted are
      still shown (receiving doesn't need it).

## Friends list not loaded yet

Before anyone opens their Friends window this session.

- [ ] 4. A and B stand next to each other. A types `/lgl hello`. Note what
      happens: either B sees `[Local] <A@World> hello` (then the game marks a
      friend near you as one without the list: record this, it is the
      `StatusFlags.Friend` check), or A is told "Not sent: nobody near you
      shows as a friend. The game may not have loaded your friends list yet:
      open your friends list once (Social menu, Friend List), then try again."
- [ ] 5. If B saw nothing in 4 but A's message was sent (after A opened its
      list, say): B is told once, in blue, that a player near sent a local
      message and to open the friends list once. Only once this session,
      however many more arrive.
- [ ] 6. A and B each open the Friends window once (Social menu, Friend
      List), and close it.
- [ ] 7. A types `/lgl hello again`: B sees `[Local] <A@World> hello again`,
      the tag in LookingGlass blue. A sees its own line the same way.

- [ ] 7a. **A partly loaded friends list.** On a character with many friends
      (more than one page of the Friends window), log in afresh, stand next
      to a friend from near the end of the list, open the Friends window and
      at once (before scrolling) close it, then `/lgl test`. Record whether the
      friend gets it, and whether it does after scrolling through the whole
      list or waiting a few seconds. (The plugin counts the list as loaded as
      soon as it holds anyone, so a list the game fills a page at a time could
      miss friends further down.)

## Friends in range and out of range

- [ ] 8. B replies with `/lgl hi`: A sees it. A link (`/lgl look <item>` with
      an item linked) shows as an item link that shows its tooltip, and
      `<t>` is sent as the target's name.
- [ ] 9. A walks away from B, checking `/lgl test` every few yalms (the
      distance shows in the target bar if B is targeted). Up to about 20
      yalms B gets it; beyond, A is told "Not sent: nobody is near enough to
      hear you." Compare with `/say`: does `/say` reach about as far? Record
      the distance where each stops.
- [ ] 10. A stands about 25 yalms from B (just out of reach) and B stands
      still: A's `/lgl` isn't sent to B. Then A sends from about 18 yalms and
      walks away at once: B still sees it (a receiver allows 30 yalms).
- [ ] 11. B goes to another zone, or another instance of the same zone: A's
      `/lgl` says nobody is near enough, and B gets nothing.

## Non-friends

- [ ] 12. C stands next to A and B. A types `/lgl friends only`: B sees it,
      C sees nothing, in game chat or ChatTwo.
- [ ] 13. C types `/lgl hello`, with only A near (B away): C is told none of
      the players near is on its friends list (or, with C's list empty, to
      open it once). A sees nothing.
- [ ] 14. With D (A's friend without LookingGlass) next to A and B away: A's
      `/lgl hi` says none of your friends near you uses LookingGlass on this
      server. With D and B both near: B gets it, and nothing more is said.

## Blocked user

- [ ] 15. B and A share a channel. A blocks B (right-click B in the channel's
      member list, Block). B sends `/lgl can you hear me` next to A: A sees
      nothing. A unblocks B (Settings, Blocked users): B's next `/lgl` shows.
- [ ] 16. A's own `/lgl` with B blocked: B isn't sent a copy (B sees nothing,
      and A is told none of its friends near uses LookingGlass, if B was the
      only one).

## Simple and advanced mode

- [ ] 17. In simple mode, every line from steps 2 to 16 is in everyday words:
      no "key", "encrypted", "signature" and the like.
- [ ] 18. Turn on **Advanced mode**: the same lines say the same (local
      chat's lines have nothing technical in them).

## Colours and where it shows

- [ ] 19. Settings, under **Local chat**, **Colour**: pick a swatch. The next
      local line on A is in that colour (the whole line, or only `[Local]`
      with **Colour the whole line** off). **Custom...**: a custom
      colour shows exactly (in ChatTwo too). **Default**: only the tag, blue.
- [ ] 20. Give B a name colour (right-click B in a channel member list, Name
      colour...): B's name in local lines takes it.
- [ ] 21. Turn on **Show LookingGlass only in windows**: local lines still
      show in game chat (no window opens for them).
- [ ] 22. Local lines go to the chat channel chosen in Settings (**Show
      messages in**), so a chat tab filtering it hides them.
- [ ] 23. While talking in a channel (`/lgc1` with no message), `/lgl hi`
      still goes to local chat, and talking in the channel goes on.

## Server and logs

- [ ] 24. A server with `LookingGlass:Limits:MaxLocalRecipients` set to 0:
      `/lgl hi` says local chat isn't available on this server. **(new)**
      `/lgl` alone says the same, and nothing starts (needs a build newer
      than 0.2.11).
- [ ] 25. `/xllog` on A and B, and the server's log: lines about local chat
      give counts only (how many friends near, sent, dropped and why), never
      a name or what was said.

## Keys

- [ ] 26. B uses **Reset my identity** and registers again. B sends `/lgl hi`
      next to A: A doesn't see the message, and no key warning (a local
      message never changes the keys A holds for B), but one blue line says B
      sent a local message that couldn't be checked, that B may have set up
      LookingGlass again or someone else may be using their name, and to
      check with them over /tell (it never says A will be warned). B sends
      again: no second line this session. After checking with B over /tell,
      A sends `/lgl hi` to B: A is told B set up LookingGlass again (the key
      warning), and B's next `/lgl` shows.
- [ ] 27. The same with B standing far from A (out of range), or with C
      (not a friend) having registered again: no line at all.
- [ ] 28. **Not tested before public release (owner, 2026-10-08): a rename or world transfer costs real money; deal with it if it comes up.** If you can, rename B (or move B to another world) after A has had
      a local message from B: B's next `/lgl` isn't shown, and one blue line
      says B may have changed their name or world. A's `/lgl` to B updates it.

## Talking in local chat

`/lgl` with no message talks in local chat, as `/lgc1` with no message talks
in a channel (see *Talking in local chat* in
[Local chat (friends only)](../design.md#local-chat-friends-only)). Needs
0.2.12. Setup: A has accepted local chat (3c), is a member
of a channel `sky` on `/lgc1`, has a swatch picked as local chat's **Colour**
(so the colour can be told from the default), and has **Say when I start or
stop talking in a channel** on (Settings, under Chat) unless a step says off. ChatTwo off until
the ChatTwo steps. Watch `/xllog` for the `[sticky]` lines. Steps 29 to 41
need only A, standing away from everyone unless a step says otherwise; 42 to
46 need ChatTwo; 47 needs B, A's friend.

### One player

- [ ] 29. **The first time ever.** `/lgl` alone: "Now talking in [Local]." in
      blue, `[Local]` in local chat's colour, then once ever one more line:
      "Typing now goes to [Local], your friends near you who use
      LookingGlass, and never to game chat. Type /s (or another channel) on
      its own to stop." The game's chat input names its channel `[Local]`,
      and the server info bar shows "LG [Local]" in local chat's colour; its
      tooltip says what you type goes to your friends near you, not to game
      chat, and to click to stop. `/lgl` again: only "Now talking in
      [Local]." (the second line never comes again, even after reloading the
      plugin).
- [ ] 30. **Typing goes to local chat, never to Say.** Alone (nobody within 20
      yalms), type `hello`: "Not sent to [Local] or game chat: nobody is near
      enough to hear you (about 20 yalms, as far as /say)." No Say line in
      your chat log. A line
      with only an item link: the same line (not how to use `/lgl`). A line of
      only spaces: nothing at all.
- [ ] 31. **A stranger near.** Stand next to a player who isn't your friend
      (anyone): `hello` says "Not sent to [Local] or game chat: none of the
      players near you is on your friends list…" (or, with your friends list
      not opened yet this session, to open it once). They see nothing: no Say
      line.
- [ ] 32. **Commands and one-offs.** While talking in local chat: `/s hi`
      goes to Say once, and `/p brb` (in a party) to Party once; `/em waves`
      works; `/lgc1 hi` goes to `sky` once; `/lgl hi` goes to local chat once
      (the nobody-near line). After each, the labels still say `[Local]` and
      the next plain line still goes to local chat.
- [ ] 33. **Moving between local chat and a channel.** `/lgc1` alone: "Now
      talking in [sky].", the labels say `[sky]`, and plain text goes to `sky`.
      `/lgl hi` there: local chat once, and `sky` goes on. `/lgl` alone: "Now
      talking in [Local]." and the labels say `[Local]` again. In the main
      window, try to give `sky` the nickname `Local` (or `local`): refused,
      "Local is used by local chat; choose another nickname."
- [ ] 34. **Ending it yourself.** `/s` on its own: "Stopped talking in
      [Local].", the labels go back to Say, and `hello` now goes to Say. Start
      again with `/lgl`, then: press Tab to change the chat channel (ends);
      start again and click the server info bar entry (ends); start again and
      right-click a player's name > **Send Tell** (ends).
- [ ] 35. **Ending it without choosing to.** Start with `/lgl`, then press
      **Disconnect** (Settings): "Stopped talking in [Local]: disconnected.",
      even with **Say when I start or stop talking in a channel** off. Connect, start again, and turn
      LookingGlass off in `/xlplugins`: "Stopped talking in [Local]:
      LookingGlass was turned off.", and the chat input names Say again. Turn
      it back on, start again and log out to the title screen: on logging in,
      nothing is still talking in local chat (no label, no info bar entry).
- [ ] 36. **Say when I start or stop talking in a channel off.** `/lgl` alone: no "Now talking"
      line, but the labels and the info bar show `[Local]`. `/s` alone: no
      line, and the labels go away. Turn it back on.
- [ ] 37. **Not connected.** Press **Disconnect**, then `/lgl` alone: "Can't
      switch to [Local]: not connected to LookingGlass." Nothing starts (no
      label, no info bar entry), and `hello` goes to Say as usual. Connect
      again.
- [ ] 38. **Asked first.** Settings, under Local chat, **Withdraw**. `/lgl` alone:
      "Local chat first asks you to accept what it tells the LookingGlass
      server. See the window that opened, then type /lgl again." and the
      privacy window opens. Nothing starts. **Accept and use local chat**:
      "Local chat is on. Type /lgl again to talk in local chat." `/lgl` alone
      now starts.
- [ ] 39. **Withdrawn while talking in local chat.** While talking in local
      chat, **Withdraw** in Settings: at once, "Stopped talking in [Local]: you
      withdrew the privacy notice." (even with **Say when I start or stop
      talking in a channel** off), the labels and the info bar entry go away,
      and `hello` now goes to Say. Start again (accept first), then
      **Withdraw** in the privacy window (Settings, **Read it**): the same.
      Accept again.
- [ ] 40. **The diagnostic log.** In `/xllog`, the `[sticky]` lines from the
      steps above say "talking in [Local]" with sizes and fixed words (and
      `/lgl` as a command's name), never what you typed or anyone's name.
- [ ] 41. **Simple and advanced mode.** Every line from 29 to 39 is in
      everyday words in simple mode, and the same in advanced mode.

### With ChatTwo (on A)

- [ ] 42. Turn ChatTwo on, its input on Say. `/lgl` alone: ChatTwo's input
      label reads "LookingGlass [Local]" in local chat's colour (perhaps with
      ChatTwo's "(Warning: Say)" after it). If you never started talking in a
      channel with ChatTwo on before, the ChatTwo line follows once, naming
      `[Local]`.
- [ ] 43. In ChatTwo's main input on Say, type `hello`: local chat (the
      nobody-near line), not Say. Type `/s hi`: Say once, and local chat goes
      on. Type `/s` alone (it ends), and in a party switch ChatTwo's input
      to Party, then `/lgl` alone: `hi` goes to local chat, and `/p hi` typed
      as it is goes to Party once.
- [ ] 44. A ChatTwo pop-out with its own input: `hi` goes to local chat, and
      `/p hi` too (the strict rule); `/party hi` goes to Party once.
- [ ] 45. Pick another channel in ChatTwo's channel picker, or click a ChatTwo
      tab with another channel: talking in local chat ends, ChatTwo's label
      goes back to its own.
- [ ] 46. Put ChatTwo's input on a tell, then `/lgl` alone: "Switch ChatTwo
      off the tell first (type /s), then try again." Nothing starts.

### With a friend (needs B)

- [ ] 47. A and B are friends (both have opened their Friends window this
      session, and accepted local chat), next to each other. A types `/lgl`
      alone, then `hello there`: B sees `[Local] <A@World> hello there`, and A
      its own line; nobody sees it in Say. A sends a line with only an item
      link, then one with `<t>`: B sees the link and the target's name. B
      walks away beyond 20 yalms: A's next line says nobody is near enough,
      and still nothing goes to Say. A types `/s` alone, then `bye`: B sees
      `bye` in Say, not in local chat.
