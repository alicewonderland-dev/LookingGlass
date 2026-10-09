# Chat links: in-game checklist

Item, map flag and status links in LookingGlass channel messages (see
[Links in messages](../design.md#links-in-messages)). You need two characters
in the same channel, **A** (sending) and **B** (receiving, on another PC or
client), both on this build. Do it once with ChatTwo off and once with
ChatTwo on, on both characters. Below, `[sky]` is the channel's tag and
`/lgc1` its command.

How to link: in the game's chat box, right-click an item in your inventory
and pick **Link** (it puts `<item>` in the input); `<flag>` once a map flag is
set (right-click the map); `<status>` for a status (the game keeps the one
last picked from a status's right-click menu; ChatTwo's popup on a status
link has a link item too). If you can't find a way to link a status, skip
the status steps and say so. In ChatTwo, use the same menus, or ChatTwo's own
**Link** item.

Every time, check on B: the link is where it was in the sentence, in the
game's link style (the arrow, an item in its rarity's colour), and nothing of
it shows as odd characters. Nothing appears in Say, Party or any linkshell.

## Game chat box (ChatTwo off)

- [ ] 1. `/lgc1 look ` and an item (normal quality), Enter. B sees
      `look [▸Potion]` as a link. Hovering it shows the item's tooltip;
      right-click works as on any item link.
- [ ] 2. The same with a high-quality item. B's link has the HQ mark and the
      HQ tooltip.
- [ ] 3. (If you have one.) The same with a collectable. B's tooltip shows it
      as a collectable.
- [ ] 4. `/lgc1 meet at ` and `<flag>`, Enter. B sees the place and
      coordinates as a map link; clicking it opens the map with the flag there.
- [ ] 5. `/lgc1 ` and `<status>` (a status of yours), Enter. B sees the
      status as a link; hovering shows its tooltip.
- [ ] 6. A link on its own: `/lgc1 ` and an item, nothing else. It is sent
      (no "Not sent" line), and B sees the link.
- [ ] 7. Text and two links: `/lgc1 trade ` item ` for ` another item, Enter.
      Both are links, each where it was, the text between them intact.
- [ ] 8. `/lgc sky ` and an item (by nickname): the same as step 1.
- [ ] 9. Talking in the channel: `/lgc1` on its own, then type `look ` and an
      item, Enter. The same as step 1, and nothing in game chat. Then an item
      on its own, Enter: sent, as in step 6. Then a map flag on its own.
- [ ] 10. Still talking in the channel, type `/p ` and an item, Enter: that is
      a one-off to Party, as before (B, if in your party, sees it in Party;
      not in [sky]).
- [ ] 11. A sees its own message in [sky] with the same links, and can click
      them too.
- [ ] 11b. **(new: /lgc messages now come from the chat line itself)** Target
      someone, then type `/lgc1 hi <t>` and press Enter. Write down what B
      sees. The owner's test showed `hi <t>` as typed; since then,
      LookingGlass replaces text commands itself, so B should see `hi` and the
      target's name (the full checks are in
      [placeholders-gagspeak-checklist.md](placeholders-gagspeak-checklist.md)).
      Do the same with `/lgc1 I am <me>`.
- [ ] 11c. In a duty or an instanced area (a trial, a dungeon, the Diadem,
      a private house or chamber), set a flag and send `/lgc1 here <flag>`. B
      sees a map link that opens the right map, or at least
      `here [place name]` (or `here [flag]`) as text, never nothing.
- [ ] 11d. (Only if GagSpeak is installed.) With your gag on, send
      `/lgc1 hello`. Write down what B sees: the gagged text or `hello`.

## ChatTwo on

- [ ] 12. Steps 1, 2, 4, 5, 6 and 7 from ChatTwo's input (with `/lgc1` in
      front, and again while talking in the channel). On B, with ChatTwo:
      each link is clickable in ChatTwo's window (hover an item or a status
      for ChatTwo's tooltip, click the map link to open the map). With ChatTwo
      off on B, the same messages are clickable in the game's chat log.
- [ ] 13. In ChatTwo, a link in a pop-out tab's input (if you use one) goes
      to [sky] the same way while talking in the channel.

## Afterwards

- [ ] 14. In `dalamud.log`, the `[sticky]` lines for these messages say
      "payload yes" and sizes only, and the "sending" lines counts only: no
      item, place or status names, and no ids.
- [ ] 15. (Optional, if a second character has an older LookingGlass build.)
      It sees each message as plain text, `look [Potion]`, with no error.

## Log heads in messages (little to see)

Messages now also carry, inside the encryption, the newest membership change
the sender's plugin has checked, and the receiver compares it with its own
(see [Log heads in messages](../design.md#log-heads-in-messages)). What it
catches, a server showing members different member lists, can't be made to
happen in game without a malicious server, so the automated tests cover it.
In game, check that nothing changes for normal chat, and that both see the
same check code. Both A and B on this build unless a step says otherwise.

- [ ] 16. A and B chat in `[sky]` for a few lines each way, with and without
      links. Everything arrives as before, and no LookingGlass warning shows
      on either side (in particular none saying someone "seems to see a
      different member list", or that the server showed two different member
      lists).
- [ ] 17. The members change while you chat: A invites a third character
      (or B leaves and A invites B back), and A and B go on chatting at once,
      while it happens. Still no warning on either side.
- [ ] 18. (Optional, if a second character has an older LookingGlass build.)
      A on this build and B on the older one chat both ways: every message
      arrives as before, with no error or warning on either side.
- [ ] 19. Check codes. In the channel window, above the member list, A and B
      each see a line "Check code: #12 48213 90412 33187 00921" (the numbers
      will differ from these). Both see the same code. In advanced mode it is
      labelled "Log head", with the same code. Hovering it explains what it is
      for, in plain words in simple mode. After step 17's change, both codes
      move on to the same new one (the number at the start goes up). No member
      shows a warning sign for "Sees a different member list".
