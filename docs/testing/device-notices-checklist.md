# Other computers signing in: in-game checklist

Being told when your character signs in from another computer, the list of
signed-in computers in Settings, and **Sign out everywhere else** (see
[Other computers signing in](../design.md#other-computers-signing-in)). Needs
the plugin with device notices (the first release after 0.2.12) and a server
updated with it.

You need one character, **A**, registered on a **test server**, and two
installs of the plugin that can both read A's LookingGlass files:

- **Install 1:** your usual XIVLauncher and Dalamud.
- **Install 2:** a second Dalamud profile with its own config folder (for
  example XIVLauncher started with `--roamingPath` set to another folder),
  under the **same Windows user**. LookingGlass's files are protected by
  Windows for that user, so a copy only opens under the same Windows user (a
  second Windows account, or another PC, can't open it; that is the point of
  the protection).

Only one install can be logged in as A at a time (the game allows one), so
you switch between them: log out (or close the game) on one before logging in
on the other. Start in simple mode (Settings, **Advanced mode** off). Note the
time of each step: the lines below name times.

Being told while you're online (the other computer signs in while you play)
can't happen in game, since the game logs A out on one install when it logs
in on the other. The automated tests cover it.

## Setup

- [ ] 1. On install 1, log in as A. Settings, under **Your identity**: a
      label **Signed-in computers** with one line under it, "This computer:
      added …, in use now", and a **Sign out everywhere else...** button.
      Hovering the label explains it in a sentence or two, in everyday words.
      No warning line in chat about another computer.
- [ ] 2. Log out of the game. Copy A's secrets file
      (`secrets-<character>-<…>.bin`, from install 1's
      `pluginConfigs\LookingGlass` folder) to the same folder of install 2.

## A copy of the login (the limitation)

- [ ] 3. On install 2, log in as A. It connects with the copied login: no
      warning line, and Settings lists one computer, "This computer". (A copy
      of the login is the same computer to the server, so nobody is told:
      the design notes this. Only **Reset my identity** revokes it.)

## Registering again on the other computer

- [ ] 4. Still on install 2: `/lgdebug` > **Forget account**, then register
      again through the Lodestone (main window). It works, and Settings lists
      one computer. Note the time. Log out of the game.
- [ ] 5. On install 1, log in as A. It connects (no "Login not recognised":
      its login was revoked by step 4, and it signs in with its identity
      instead). Game chat shows one line in light red (warning colour):
      "Your LookingGlass character signed in from another computer on <the
      time of step 4>. If that wasn't you, use "Sign out everywhere else" in
      Settings and reset your identity." The date and time are in your local
      format.
- [ ] 6. Settings lists two computers: "This computer: added just now, in
      use now" and "Another computer: added <step 4's time, or "N min ago">,
      used …". Nothing else about them (no address, no name).
- [ ] 7. Log out and in again on install 1: no second warning line.

## Signing in from the other computer again

- [ ] 8. Log out on install 1. On install 2, log in as A. It connects with
      its own login, and shows the warning line about another computer
      signing in at the time of step 5. Settings lists two computers.

## Sign out everywhere else

- [ ] 9. On install 2, Settings > **Sign out everywhere else...**. A
      confirmation opens: it says this computer stays signed in, the others
      can only sign in again by registering again through the Lodestone, and
      to use **Reset my identity** too if you didn't recognise one. In simple
      mode it has no technical words. **Cancel** changes nothing.
- [ ] 10. Do it again and confirm. A blue line says "Signed out everywhere
      else (1 other computer). …". Settings lists only "This computer". A
      stays connected on install 2, and channels work.
- [ ] 11. Log out on install 2. On install 1, log in as A. The main window
      shows **You were signed out from another computer**, with what to do
      (register again through the Lodestone; if it wasn't you, **Reset my
      identity** instead), the server address, and **Open settings**, but no
      **Retry now**. The connection label at the top says **Signed out**.
      **Register again** steps are below.
- [ ] 12. Close and reopen the plugin (or the game) on install 1: it still
      says you were signed out (it doesn't sign itself back in).
- [ ] 13. Turn on **Advanced mode** on install 1: the text now says the
      server revoked this computer's login and your identity key can't sign
      in until the character is registered again. Turn it off again.

## Registering again lifts it

- [ ] 14. On install 1, register again through the Lodestone. It works, and
      your channels are as they were (no "restored" message: the identity is
      the same). Settings lists one computer.
- [ ] 15. Log out on install 1, log in on install 2. It connects (signing in
      with its identity, as in step 5) and shows the warning line about the
      registration of step 14.

## Older plugins

- [ ] 16. (Optional, needs 0.2.12 to hand.) Repeat 9–11 with install 1 on
      0.2.12: after being signed out it shows "Login not recognised", and
      nothing else changes. Install 2 on 0.2.12 shows no warning lines and no
      **Signed-in computers** in Settings.

## Clean up

- [ ] 17. On install 1, **Reset my identity...** and register again through
      the Lodestone (your channels come back with you). Install 2's copy is
      now useless: its login and identity are both refused, and it shows
      "Login not recognised". Delete the copied file from install 2's folder.
