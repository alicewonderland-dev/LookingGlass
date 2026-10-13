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
