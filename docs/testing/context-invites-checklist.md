# Context menu invites: in-game checklist

Right-click invites (see
[Context menu invites](../design.md#context-menu-invites)). You need
character **A** (yours, on this build, registered) and character **B** (on
another PC or client, registered on the same server, ideally on another world
or data centre), plus a character **C** who has never registered with
LookingGlass. A is the admin of `sky` (`/lgc1`, nickname `sky`, a colour set)
and of `fc` (`/lgc2`, no nickname, default colour), and only a member of
`ro` (`/lgc3`), where someone else is the admin. B is in none of them.
Check each step in simple mode, then the result lines (12 to 17) again in
advanced mode.

Throughout: the game never stutters when an invite is sent, and nothing
LookingGlass prints names anyone in `dalamud.log` (`/xllog`).

## Where it shows

- [ ] 1. **Game chat.** Have B say something in /say or a /tell. Right-click
      B's name in the chat log. Below the game's own items there is **Invite
      to LookingGlass** with a boxed blue "L" in front and an arrow. Hover or
      click it: a submenu lists `[sky]` (in sky's colour) and its name, and
      `[LGC2]` (in LookingGlass blue) and `fc`'s name, in that order. `ro` is
      not listed (A is only a member there).
- [ ] 2. **Party list.** Invite B to a party. Right-click B in the party list
      (`_PartyList`), and in the Party Members window: the same item and
      submenu.
- [ ] 3. **Target.** Target B in the world and right-click B's character, then
      the target bar's name: the same item and submenu.
- [ ] 4. **Friend list.** With B on A's friend list, right-click B in the
      friend list (Social > Friend List): the same item and submenu.
- [ ] 5. **Other lists** (if handy): a linkshell or cross-world linkshell
      member list, the Free Company member list, the party finder: the same.
- [ ] 6. **ChatTwo.** With ChatTwo on, right-click B's name in a ChatTwo chat
      line. At the bottom of ChatTwo's menu, **Integrations** has **Invite to
      LookingGlass**, opening the same channels, each line in its colour.
      Right-clicking the message text (not the name), an item link or empty
      space shows no LookingGlass item.

## Where it doesn't

- [ ] 7. Right-click A's own name (in chat, the party list, on A's
      character): no **Invite to LookingGlass**, in the game's menus or in
      ChatTwo's.
- [ ] 8. Right-click an NPC in the world, a minion, a retainer (in the
      retainer list, or one standing at a summoning bell), and a Trust or duty
      support NPC in the party list: no item.
- [ ] 9. Right-click an item in your inventory and an item link in chat: no
      item.
- [ ] 9a. In ChatTwo, right-click a name in a Free Company member's login or
      logout line: no item is expected there (ChatTwo gives those names no
      public world). The same player's name in an ordinary chat line has it.
- [ ] 10. `/lg`, Settings, disconnect (or set a server address that doesn't
      answer). Right-click B: no item. Connect again: it is back.
- [ ] 11. On a character that is a member, but no moderator or admin, of every
      channel (or has none): right-clicking B shows no item.

## Results

- [ ] 12. Right-click B, **Invite to LookingGlass > [sky] …**. A blue line:
      "[LookingGlass] Invited B@World to [sky]." with `[sky]` in sky's colour.
      B gets the invite (the envelope in B's `/lg`).
- [ ] 13. Right-click B again: `[sky] … (already invited)` is greyed out and
      can't be picked; `[LGC2] …` still can. Have B accept, then right-click B:
      `[sky] … (already a member)`, greyed out.
- [ ] 14. Pick `[LGC2] …` for B: "Invited B@World to [LGC2]." with the tag in
      LookingGlass blue.
- [ ] 15. Right-click C (never registered): pick `[sky] …`. A blue line:
      "Couldn't invite C@World to [sky]: C@World isn't registered with
      LookingGlass on this server: they need to install it and register their
      character first." In advanced mode, the line ends at "on this server.".
- [ ] 16. Block B (from a channel's member menu, or decline an invite with
      **Block**), then right-click B: every channel shows "(you blocked them)",
      greyed out. Unblock B in Settings > Blocked users: back to normal.
- [ ] 17. Invite several unregistered or different characters quickly (more
      than the invite limits allow, see `docs/server.md`): the refusal is a
      blue "Couldn't invite …: " line with the server's reason ("You're
      sending invites too quickly; try again later."), without "(RateLimited)"
      in simple mode, and with it in advanced mode.
- [ ] 18. The same from ChatTwo's menu: steps 12, 13 (greyed out entries can't
      be clicked) and 15 give the same lines.

## ChatTwo reloads

- [ ] 19. In `/xlplugins`, disable ChatTwo, then enable it again. Right-click
      B's name in ChatTwo: **Integrations > Invite to LookingGlass** is still
      there (LookingGlass registered again when ChatTwo said it was back).
- [ ] 20. Reload LookingGlass (disable and enable it) with ChatTwo on: the
      item is there once, not twice. Disable LookingGlass: the item is gone
      from ChatTwo's menu (and from the game's menus).
- [ ] 21. Start with ChatTwo off, load LookingGlass, then turn ChatTwo on: the
      item appears in ChatTwo's menu.
