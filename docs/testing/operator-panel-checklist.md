# The plugin's operator panel: in-game checklist

The Operator section at the end of Settings, **Ban from server...** in the
right-click menus, the "Ban from server" dialog, the lines said in chat, and
what an operator character sees once its keys change (see
[The plugin's operator panel](../design.md#the-plugins-operator-panel) and
[Operator characters](../server.md#operator-characters)). Needs the plugin
with the operator panel (the first build after 0.2.15) and a server updated
with it. **Owner only**: it needs the server's settings, SSH and Discord.

Use a **test server, never the public one**: these steps ban characters. You
need:

- **A**: one of your own characters, the operator. Listed in
  `LookingGlass:Operators:Characters` (a drop-in line
  `Environment=LookingGlass__Operators__Characters__0=<A's user ID>`, then
  restart), with the `LG` helper from [server.md](../server.md#flags-and-bans).
- **B**: another character with LookingGlass on the same server (an alt on a
  second game client, or a willing helper), which A will ban for a day.
- **C**: a character listed as an operator too (another alt, listed as
  `Characters__1`), or just a Lodestone ID listed there that never registers.
- The operator web page and Discord alerts on, as the abuse-bans checklist
  sets them up (steps 33 and 45), so you can watch what is posted.

Start in simple mode (Settings, **Advanced mode** off). Note the time of each
step. Information lines are LookingGlass blue, warnings light red.

## Before the keys are pinned

- [ ] 1. With A listed but not pinned (`LG --operators` says "not an operator
      yet"), log in as A. One light red line: "This server lists this
      character as an operator, but hasn't confirmed your LookingGlass setup
      yet. Confirm it on the server's operator web page." Settings ends with
      **Operator** and a **?**: "This server's operator hasn't confirmed your
      LookingGlass setup yet.", **Your code now** with A's fingerprint (the
      same as under **Your identity** in advanced mode) and a copy button,
      and "To confirm it, open the web page and check it shows these same
      numbers." The **?** says operators are tied to their LookingGlass setup.
      No **Ban from server...** in any menu.
- [ ] 2. Reconnect (Settings, **Disconnect**, then **Connect now**): the line
      of step 1 isn't said again. In advanced mode the section says the keys
      aren't pinned, and the line (after a relog) names A's fingerprint.

## Pinned: the section

- [ ] 3. Pin A on the web page (**Operators**, **Pin...**, compare the
      fingerprint with step 1's, confirm). The page says A "is an operator
      now, with keys ... Its plugin sees the Operator section from its next
      login." Relog A: no light red line. Settings ends with **Operator** and
      a **?** ("You see this because the server lists this character as one
      of its operators. Bans you make here are on the character, and are
      posted to the alerts channel."), the counts ("0 flagged · 0 bans in
      force · 0 blocked automatically"), **Refresh** at the right, the
      **Flagged** and **Bans in force** lists ("Nobody flagged.", "No bans in
      force."), **Lifted or ended lately** folded, **Ban someone** with its
      hint, the line about shortened addresses, and **Copy the web page's
      address** (it copies the page's address).
- [ ] 4. Flag B (as the abuse-bans checklist's step 6 does, with B's own
      invites) and press **Refresh**: B is under **Flagged** with why and
      "N min ago", and a **Ban...** button. If B's address is flagged too, it
      shows as "address 203.0.113.x" (shortened, never the full address) with
      no button. The counts follow.
- [ ] 5. Close and reopen Settings: the lists are asked for again (the counts
      are current). With Settings closed, nothing is asked for (no traffic in
      `/lgdebug`'s trace while A just stands there).
- [ ] 6. Narrow the Settings window as far as it goes, and try a bigger
      Dalamud font: the buttons stay inside the window, names are cut with
      "..." (the whole name in a tooltip), nothing overlaps.

## The menu item

- [ ] 7. Right-click B (in chat, in the world, in the party list, on the
      target bar): **Ban from server...** with the blue "L", under **Invite to
      LookingGlass** if that is there, and even if A has no channel to invite
      B to. On A itself, on NPCs, minions and retainers: none.
- [ ] 8. In ChatTwo (on): right-click B's name > **Integrations** > **Ban from
      server...** is there, beside **Invite to LookingGlass**.
- [ ] 9. With Settings opened once this session (so the lists are in),
      right-click C: **Ban from server... (an operator)**, greyed out, in the
      game's menu and in ChatTwo's. Before Settings was opened in a session,
      it may show enabled: then the dialog says C "is listed as one of this
      server's operators, so it can't be banned here", with no ban button.
- [ ] 10. B on a character not registered on this server (any stranger):
      the dialog says "... isn't registered on this server, so there's
      nothing to ban here. To stop the character registering, ban its
      Lodestone ID on the web page."

## The dialog, and banning

- [ ] 11. Right-click B > **Ban from server...**: "Looking them up...", then
      the dialog: B as Name@World, "Flagged ... ago: ... (limits)." in
      orange, **How long** with 1 day, **7 days** (chosen), 30 days, Until
      lifted and a box for days, **Reason** with "The player is shown this."
      and "0 / 300", what a ban does, **Cancel** and **Ban B@World**.
- [ ] 12. Type 0 or 40000 in the days box: "A number of days from 1 to
      36,500." and the ban button greys out. Paste a reason over 300
      characters: the counter turns orange, a line says so, and the button
      greys out. **Cancel** and Escape close it, nothing done.
- [ ] 13. Ban B for 1 day, reason "Testing the panel": the dialog closes, a
      blue line "Banned B@World for 1 day.", B is disconnected at once and
      sees it is blocked, with the reason. The section shows B under **Bans
      in force**, "until <time> (1 day) · "Testing the panel"", "made in the
      plugin by you", and **Lift...**. Under Flagged, B's **Ban...** is gone.
- [ ] 14. Discord: "**Ban made** in the plugin by A@World: B@World (user ...),
      for 1 day ..." with "If you didn't make this, lift it ...", no ping. `LG
      --bans`: made in the plugin by A@World. `LG --audit`: the ban, by
      A@World, from A's address.
- [ ] 15. Right-click B again (from a chat line): the dialog says "Banned
      until ... (1 day), made in the plugin by you. Reason: Testing the
      panel", with **Replace the ban** and **Lift the ban**. Replace it with 7
      days: "Banned B@World for 7 days.", and Discord says "replacing the ban
      made ...".
- [ ] 16. **Lift...** on B's line in Settings: the same dialog; **Lift the
      ban** → "Lifted the ban on B@World.", B can connect again (at once with
      **Try again now**), the lists show it under **Lifted or ended lately**
      ("..., lifted in the plugin by you"), and Discord posts the lift naming
      A.
- [ ] 17. **Ban someone**: type B's name@world → the dialog for B. Type B's
      Lodestone ID → the same. Type "Bob" or "203.0.113.9" → "Type a
      character as Name Surname@World, or its Lodestone ID (a number)." and
      no dialog.
- [ ] 18. Ban C by typing its ID in **Ban someone**: the dialog says it is an
      operator. Ban A itself by its ID: "That's your own character: it can't
      be banned here."
- [ ] 19. The hour's limit: ban and lift B (or any spare IDs) until 10 bans
      were made within an hour: the section says "Bans left this hour: N of
      10" once fewer than 10 are left; the 11th ban says "Couldn't ban ...:
      This operator character has made 10 bans in the last hour, the most the
      plugin allows; use the web page for more." in blue, and in the dialog.
      Lifts still work.
- [ ] 20. An address ban made on the web page shows in the section as
      "address ...x" (shortened), "made on the web page" (no login), with no
      **Lift...**; an automatic block, as "blocked automatically, N minutes
      left".
- [ ] 21. In advanced mode, step 19's line ends with "(LimitReached)"; in
      simple mode it doesn't. Every line above is in everyday words in simple
      mode.

## When the keys change

- [ ] 22. On A, **Reset my identity** and register again (or register on a
      new computer). At login, one light red line: "You set up LookingGlass
      again on this character, so this server no longer treats you as an
      operator. Restore it on the server's operator web page." (Advanced mode:
      both fingerprints.) Discord pings: "an operator character signed in with
      other keys". The section gives way to "This server no longer treats you
      as an operator: you set up LookingGlass again on this character (new
      computer, or a reset).", **Your code now** (the new fingerprint), in
      advanced mode **Pinned keys** too, and "To restore it, open the web page
      and check it shows these same numbers." No **Ban from server...** in
      the menus.
- [ ] 23. Restore operator status on the web page (comparing the new
      fingerprint) → Discord pings "Operator status restored". Relog A: the
      section is back, and no light red line.

## Older plugins and servers, and logs

- [ ] 24. Optional: A on 0.2.15 or older, on the updated server: no Operator
      section, no menu item, nothing in chat. A on this build, on a server
      from before the panel: the same.
- [ ] 25. `/xllog`: no LookingGlass errors; the panel's lines name nobody
      ("A ban from the operator panel failed: ..." gives only the kind of
      error). The server's journal: "Operator user ... banned user ... from
      the plugin" with user IDs only.
- [ ] 26. Clean up: lift every test ban (`LG --bans` shows none in force),
      and unlist C if it was a spare alt.
