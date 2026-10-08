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
**Advanced mode** off), and A's **Local chat colour** at **Default**. Watch
`/xllog` with debug output on, for the "Local chat" and "Local message"
lines. Do it once with ChatTwo off and once with ChatTwo on, on A.

## Before anything: is it free, and what does the game hold?

- [ ] 1. `/xlhelp`: `/lgl` is listed once, by LookingGlass, with a line on
      what it does. No other plugin you use claims it, and the game has no
      `/lgl` of its own (type `/lgl` with LookingGlass turned off: the game
      says the command doesn't exist).
- [ ] 2. `/lgl` alone: one blue line says how to use it (friends near you
      who use LookingGlass, about 20 yalms, only friends get it).
- [ ] 3. Settings, under **Chat**: **Local chat colour ([Local])** with a
      swatch, and under it the same line as 2.

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

- [ ] 19. Settings, **Local chat colour**: pick a swatch. The next local line
      on A is in that colour (the whole line, or only `[Local]` with **Colour
      the whole line in a channel's colour** off). **Custom...**: a custom
      colour shows exactly (in ChatTwo too). **Default**: only the tag, blue.
- [ ] 20. Give B a name colour (right-click B in a channel member list, Name
      colour...): B's name in local lines takes it.
- [ ] 21. Turn on **Show LookingGlass messages only in windows**: local lines
      still show in game chat (no window opens for them). Its tooltip says so.
- [ ] 22. Local lines go to the chat channel chosen in Settings (**Show
      messages in the chat channel**), so a chat tab filtering it hides them.
- [ ] 23. While talking in a channel (`/lgc1` with no message), `/lgl hi`
      still goes to local chat, and talking in the channel goes on.

## Server and logs

- [ ] 24. A server with `LookingGlass:Limits:MaxLocalRecipients` set to 0:
      `/lgl hi` says local chat isn't available on this server.
- [ ] 25. `/xllog` on A and B, and the server's log: lines about local chat
      give counts only (how many friends near, sent, dropped and why), never
      a name or what was said.
