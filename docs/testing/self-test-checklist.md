# Self-test: in-game checklist

Checking the self-test itself (`/lgdebug test`; see
[Self-test in the plugin](../design.md#self-test-in-the-plugin) and
[self-test-plan.md](../self-test-plan.md)), before its reports are trusted.
One character on this build, registered and connected, with an inn room, and
if possible a house or apartment (yours or a friend's). ChatTwo off unless a
step says otherwise. Keep `/xllog` open: every check writes one `[selftest]`
line, with no names and no text.

Some facts the self-test relies on were read from FFXIVClientStructs and can
only be confirmed in game; each is a step below (marked **(confirm)**). If one
is wrong, the self-test fails closed (it doesn't type, or calls the place not
private) rather than doing something it shouldn't: report it.

## Opening it

- [ ] 1. `/lgdebug test` opens the **LookingGlass self-test** window. `/lgdebug`
      has a **Self-test...** button at its top that opens the same window.
      `/lgdebug` and `/lgdebug colours` still work as before, and `/xlhelp`
      lists no new command.
- [ ] 2. In simple mode, nothing on the start screen has a technical word
      ("encrypted" is allowed). **Which ones?** lists the settings the ticked
      suites change, each "put back at the end".

## Where it runs

- [ ] 3. **(confirm)** In your inn room the start screen says "You're in: an
      inn room. OK", and **Start** is usable.
- [ ] 4. **(confirm)** Outside (a city, a ward): the start screen only says the
      self-test runs in a private place, lists the three kinds, names where you
      are, and has **Check again** and **Close**. In a housing ward or yard it
      says "a ward or yard".
- [ ] 5. **(confirm)** Inside a house (any floor), its private chambers, a
      company workshop, and inside an apartment: each counts, and the start
      screen adds the line about people who come in seeing a test line.
- [ ] 6. **(confirm)** An apartment building's lobby does **not** count.
- [ ] 7. With another player within 30 yalms in a house: a run's next step
      that types a line waits ("Waiting: someone else is nearby..."), and
      carries on once they leave.
- [ ] 8. Start a run, then walk out of the inn room (or teleport): before its
      next step the run says "Paused: you left the private place", and types
      nothing outside (watch Say: nothing). Walk back in: it carries on by
      itself. Nothing was typed while outside.

## Typing and the chat channel

- [ ] 9. **(confirm)** Start with the game's chat channel on Free Company (or
      Party, or a linkshell). When the first step types a line, or asks you to
      type one, the chat channel is set to Say first; at the end of the run
      (also after a reload in the middle) it is put back as it was, a
      linkshell to the same linkshell. Started on a tell, it is left on Say,
      and the report's last line says so. No line ever reaches Free Company, a
      linkshell, Shout, Yell, a tell or the Novice Network.
- [ ] 9b. With ChatTwo on and its input on Party: before a step asks you to
      type in ChatTwo, it asks you to put ChatTwo's input on Say, and waits.
      After the steps that have you move it (to Party, a tell, another channel)
      it asks for it back on Say, even when that step failed.
- [ ] 9c. In a house, have someone come within 30 yalms while a step asks you
      to type a line: the request goes off the screen ("Waiting: someone else
      is nearby..."), and comes back once they leave.
- [ ] 10. **(confirm)** The lines it types are judged as typed in the game's
      chat box: in `/xllog` the `[sticky]` lines for them say the line came
      from the game (as your own typing would), never "from a plugin".
- [ ] 11. **(confirm)** A line it types with an item link (local-chat 30, the
      chat history checks) carries a working item link (a Potion).
- [ ] 12. Every test line in Say reads "LG self-test XXXX n, please ignore".

## A run

- [ ] 13. A run shows each check as it goes: passed, FAILED (with why), skipped
      (with why), stopped. A guided step shows "Needs you (step n of m)", what
      to do, and **Done** / **It didn't work** / **Skip this step** (or
      **Yes** / **No** / **Skip this step**). Where the runner can see the
      change (Tab, the info bar), it goes on by itself, saying what it sees
      under "Watching:".
- [ ] 14. Closing the window doesn't stop the run; `/lgdebug test` brings it
      back.
- [ ] 15. **Stop and clean up** mid-step (during a guided step, and during an
      automatic one): the run stops, the clean-up runs, and the report says
      "stopped: you stopped the run".
- [ ] 16. A step that asks you to relog: go to the title screen and back in;
      the run carries on by itself with the next step.
- [ ] 17. A step that asks you to turn LookingGlass off and on: do it; once
      you're back in and connected, the self-test window opens by itself and
      the run carries on with the next step.
- [ ] 18. The test channel (`LG self-test XXXX`) is on `/lgc50` (or the
      highest free number); your own channels keep their numbers.

## Putting everything back

- [ ] 19. After a full run: your channel windows are open again where they
      were; every setting in **Which ones?** is as before; the test channels
      are gone from your channel list; the game's chat channel is as before
      (on Say if it was a tell); with the chat history off and empty before
      the run, there is no `chatlog-...` folder in the plugin's config folder
      afterwards (with it on before, the history suite says it was skipped);
      the report's last line says what the clean-up did.
- [ ] 20. With **Keep chat history on this computer** on before the run: the
      history suite is skipped, saying why; after the run, the test channels'
      lines aren't among the older lines of any window, and no text folder of a
      test channel is left. Your own channels' history is untouched, and no
      text file of yours was started.
- [ ] 20a. With the chat history off and empty, run the history suite, and
      before it ends open one of its text files in a program that locks it
      (Notepad++ or Excel), so the clean-up can't delete the history. Then turn
      **Keep chat history on this computer** on and chat a little. Turn
      LookingGlass off and on: nothing is deleted, and the self-test window asks
      about the test's chat history (**Delete the test's chat history** / **Keep
      it**). Delete removes only folders nothing has written to since the test,
      never your own newer lines.
- [ ] 20b. Press **Stop and clean up** while a step has LookingGlass
      disconnected (local-chat 35 or 37): it connects again and cleans up.
      Relog onto another character in the middle of a run: it pauses ("another
      character is in") and does nothing until you come back.
- [ ] 21. Close the game (or turn LookingGlass off in `/xlplugins`) in the
      middle of a run, not at a relog or reload step. Start it again and log
      in: your settings are as before the run at once, and once connected one
      blue line says "A LookingGlass self-test didn't finish. Your settings are
      as they were, and its test channel was removed."; the test channel is
      gone.
- [ ] 22. With a test channel left that no run knows about (for example, rename
      a throwaway channel of yours `LG self-test ABCD`): the start screen lists
      it under "Left over from an earlier test?" with **Remove**; nothing
      removes it by itself.

## The report and the log

- [ ] 23. **Copy report** copies text in the shape of pending-tests.md's "How
      to report": the plugin version, ChatTwo on or off, the mode, the kind of
      place, the times to the minute with your time zone, one line per check,
      and the clean-up. No character, world or channel name appears in it, nor
      the run's code, nor anything you typed but your notes.
- [ ] 24. A name@world typed for "Inviting someone who isn't registered" is in
      neither the report nor `/xllog`.
- [ ] 25. `/xllog`'s `[selftest]` lines have the checklist steps and passed,
      failed, skipped or stopped, nothing else.
- [ ] 26. **Run the failed ones again** runs only the checks that failed (with
      a new test channel), and its report lists only those.

## Pacing (owner)

- [ ] 27. After a full run (all suites) on the public server, the server's
      journal shows no refusal and no flag for the tester. A second full run
      within the hour is refused at the start ("Wait about ... minutes"), as
      the run's test channels count against the hour.

## Two players

Two characters on this build, A (the host) and B (the partner), registered on
the same server, on different PCs or clients, each in a private place. For the
local chat suite both inside the same house or apartment (either one's), on
each other's friends list. Both keep `/xllog` open.

- [ ] 28. **Two players...** on the start screen opens a screen with **Invite a
      partner** and **Be a partner**. As the host: the partner's name@world
      box, **Use my target** (target B: their name@world fills in), "Tell them
      your name and this code: XXXX", what it does, and the eight two-player
      suites (one unticked with why if it can't run here: ChatTwo off, say).
      Typing your own name, or no world, keeps **Start** off, saying why.
- [ ] 29. B chooses **Be a partner**, types A's name@world and the code, and
      presses **Start**: "Waiting for their invite". A presses **Start**:
      "Waiting for your partner to join... 1:58 left". Within a few seconds B
      joins `LG self-test XXXX` by itself, and A's run shows "Partner joined.
      In a private place: yes. Near you: ... Friends: ... In your party: ...
      ChatTwo on their side: ...". B's window says how many requests it has
      answered.
- [ ] 30. **Only the host's invite.** Before A starts, a third character C
      invites B to a channel named `LG self-test XXXX` (or A invites B to a
      channel of another name): B's plugin doesn't accept it (it stays in B's
      invites). A wrong code on B's side: B never joins, and A's pairing gives
      up after two minutes ("your partner didn't join within two minutes"),
      skipping every check that needs the partner, and cleans up.
- [ ] 31. **Never typing for B.** Whenever a step needs B to type (`/s` a line
      for A to right-click), B's window shows "Needs you" with the line, only
      in a private place, nobody else near, and Say as B's chat channel; B's
      plugin itself never types in game chat (watch B's Say). In a house, the
      other tester standing next to B doesn't count as "someone near".
- [ ] 32. **B goes silent.** Mid-run, B turns LookingGlass off. A's next
      request waits a minute, asks "still-there", waits again, then the run
      says "your partner has left the test" for every check left, and cleans
      up (disbanding a test channel B may still be in). B turned back on and
      logged in: its settings are back, and it has left A's channels.
- [ ] 33. **A goes silent.** Mid-run, A turns LookingGlass off (or closes the
      game). B's run stops by itself within 15 minutes ("no request from the
      other tester for 15 minutes", or "the other tester's test channel is
      gone" once A's next start cleans up), leaving A's channels.
- [ ] 34. **Old and repeated requests.** While a run is on, A types
      `/lgc50 LG self-test XXXX #1 send 5` by hand (an old number): B's plugin
      ignores it and sends no test line for it (check B's `/xllog` and the
      channel). Nobody but A and B is in the test channel, so the rule that a
      line with the prefix from anyone else stops the run is left to the unit
      tests.
- [ ] 35. **Relogs.** When A's run asks A to relog, B's plugin sends its lines
      only once A shows offline in the member list, and A's run, back, finds
      them caught up. When B is asked to relog, B's window says so, and B's
      answer reaches A once B is back.
- [ ] 36. **`/p`.** The ChatTwo suite asks A to type `/p` only once the party is
      exactly A and B; with a third member, the check is skipped ("needs a
      party of just the two of you"). The runner itself never types `/p`.
- [ ] 37. **Clean-up on both sides.** At the end: B leaves A's test channels
      (and its own `LG self-test XXXX p` goes: A left it first); A leaves last,
      and nothing is disbanded unless B didn't leave. A block A's run made (the
      blocking checks) is undone; B, blocked before the run, stays blocked and
      the blocking checks are skipped. The partner's name colour and every
      other setting are as before on both sides.
- [ ] 38. **Names.** Neither report has A's or B's name or world (A's says
      "with a partner"; B's says what it answered, in numbers); `/xllog` on
      both sides has no name, no channel name and no test line. A's
      `selftest-run.json`, if one is left, has no name.
- [ ] 39. **Pacing.** After a full two-player run on the public server, the
      server's journal shows no refusal and no flag for either tester; the
      start screens say when to wait before running the rest within the hour.
