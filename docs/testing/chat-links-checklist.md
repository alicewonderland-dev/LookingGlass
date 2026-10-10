# Chat links: in-game checklist

Item, map flag, status and party finder links in LookingGlass channel
messages (party finder links have their own section at the end; see
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
      each see a line "Check code: #12 48213 90412 33187 00921 55102" (the numbers
      will differ from these). Both see the same code. In advanced mode it is
      labelled "Log head", with the same code. Hovering it explains what it is
      for, in plain words in simple mode. After step 17's change, both codes
      move on to the same new one (the number at the start goes up). No member
      shows a warning sign for "Sees a different member list".

## Party finder links (new in the build after 0.2.14)

Party finder links now work in channels (see
[Links in messages](../design.md#links-in-messages), *Party finder
listings*). A and B as above, both on this build, on the same data centre
unless a step says otherwise. For a listing to link, A starts a recruitment
of their own in the Party Finder (any duty; a password keeps strangers out),
so it stays up for the whole test (a listing ends when it fills, or after an
hour).

How to link a listing: open the listing's recruitment window (your own, or
anyone's in the list) and click its chat button (the speech bubble). It
should put `<pfinder>` in the chat input, as linking an item puts `<item>`.

- [ ] 20. **What the chat button puts in the input.** ChatTwo off, with
      `/xllog` open: `/lgc1` on its own (talking in `[sky]`), then click the
      chat button. Write down what appears in the chat input (expected:
      `<pfinder>`), and the number after "kind" in the `[sticky]` line
      "link put in the chat input" (expected: 1120 or 1121).
- [ ] 21. **Sending.** `/s` on its own, then `/lgc1 join ` and the chat
      button, Enter. A sees, in `[sky]` in game chat: `join ` and then one
      link made of the game's link arrow, a party finder icon, "Looking for
      Party (A's name)", and, for a listing open to other worlds, the
      cross-world mark. Nothing in Say or Party. Screenshot it next to the
      game's own party finder link (type `/e `, click the chat button, Enter:
      echo, which only you see) so the two can be compared.
- [ ] 22. **Clicking it in game chat.** Click the link from step 21 → the
      Party Finder opens on that listing, as clicking the game's own party
      finder link does.
- [ ] 23. **In a channel window.** The same message in a `[sky]` channel
      window shows the listing as an orange link with a small people icon
      before it. Hovering says it is a party finder listing, named by whoever
      sent it; clicking it opens the listing in the Party Finder.
- [ ] 24. **Home world only.** Edit the recruitment so it's limited to your
      own world, open it again and send it as in step 21 → the link has no
      cross-world mark. Set it back to the whole data centre and send again →
      the mark is back. (The mark is the only thing this changes; report if
      it's wrong either way.)
- [ ] 25. **Talking in the channel.** `/lgc1` on its own, then `join ` and the
      chat button, Enter → as step 21. The chat button alone, Enter → sent
      (no "Not sent" line). `/lgc sky ` and the chat button → the same.
- [ ] 26. **ChatTwo on (A).** Click the chat button with ChatTwo's input
      focused: write down what appears in ChatTwo's input. Send it as in steps
      21 and 25 → as there. Clicking the link in ChatTwo's window opens the
      listing.
- [ ] 27. **B receives it (needs B).** B sees A's message from step 21 with
      the same link (in A's wording: the name comes from the sender). Clicking
      it opens A's listing: in the game's chat log with ChatTwo off, in
      ChatTwo's window with ChatTwo on, and in B's channel window.
- [ ] 28. **A listing that has ended (needs B).** A ends the recruitment. B
      clicks the old link (in chat and in the window) → the game's own answer
      (a message that the listing can't be found, or nothing); no crash, and
      no LookingGlass error or warning in chat or in `/xllog`.
- [ ] 29. **Another data centre (optional, needs B on another data centre).**
      B, in the channel from another data centre, clicks A's link → the
      game's own answer; no crash, no LookingGlass error or warning, and
      clicking again doesn't fill `/xllog` with lines.
- [ ] 30. **The log.** In `dalamud.log`, the `[sticky]` lines for these
      messages give counts only: no listing number and no leader's name.
- [ ] 31. **An older build (optional, needs B on 0.2.14 or older).** B sees
      `join [Looking for Party (A's name)]` as plain text, with no error.
