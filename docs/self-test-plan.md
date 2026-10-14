# Self-test: a plan for approval

Status: approved by the owner (2026-10-13), with all ten open decisions taken
as recommended. **Steps 1 to 4 as built** (2026-10-13): the one-player
self-test (the runner, the plugin's seams, the window, the one-player suites);
the choices made in building it are in design.md's
[Decisions](design.md#decisions). Step 5 (two players) is still to build;
step 6's docs are written for what is built. The owner asked for "a debug
function that tests as many of the remaining checklist items as possible. If
human input is required, give the user instructions. Tests that require 2
players to confirm can be included. Require that the player be in an inn
room, apartment, or house to minimize issues with leaks."

[design.md](design.md) stays the source of truth: its
[Planned features](design.md#self-test-in-the-plugin),
[Decisions](design.md#decisions) and [Open questions](design.md#open-questions)
hold the short version. This file holds what the owner needs to approve it:
mockups, the coverage, the private-area check, isolation and clean-up, pacing,
the two-player protocol, the build order and the tests. It is folded into
design.md and the checklists as it is built, then deleted.

**"Remaining checklist items"** here means every task in
[testing/pending-tests.md](testing/pending-tests.md) that isn't ticked. The
repository only has the two "name or world change" tasks ticked; the owner's
own list of what testers have already done isn't in the repository, so the
coverage below treats every unticked task as remaining. A task already done
costs nothing: the tester can leave its suite unticked on the start screen.

## Contents

- [In short](#in-short)
- [Where it lives and how it starts](#where-it-lives-and-how-it-starts)
- [What a run looks like](#what-a-run-looks-like)
- [Mockups](#mockups)
- [The private-area check](#the-private-area-check)
- [Keeping the test apart from the tester's own chat](#keeping-the-test-apart-from-the-testers-own-chat)
- [What "automatic" can honestly check](#what-automatic-can-honestly-check)
- [Two players](#two-players)
- [Pacing: never near the server's limits](#pacing-never-near-the-servers-limits)
- [Privacy and safety](#privacy-and-safety)
- [Words](#words)
- [Coverage](#coverage)
- [Build order and size](#build-order-and-size)
- [How it is tested](#how-it-is-tested)
- [Open decisions for the owner](#open-decisions-for-the-owner)

## In short

- **`/lgdebug test`** (and a **Self-test...** button at the top of
  `/lgdebug`) opens a **LookingGlass self-test** window. No new top-level
  command.
- It runs **only in a private place**: an inn room, inside an apartment (not
  the lobby), or inside a house (any floor, private chambers, the company
  workshop). It checks again before every step that could reach game chat,
  and pauses if the player leaves.
- It makes **its own temporary test channel** ("LG self-test K7Q2"), never
  touches the tester's channels, **puts every setting it changes back** (even
  after a crash), and leaves the channel at the end.
- Each check ends as **passed**, **failed** (with why), **skipped** (with
  why), or waits on **"Needs you"**: a short instruction, then **Done** / **It
  didn't work** / **Skip**.
- **Two players**: one is the host, the other a partner. The partner's plugin
  joins the host's test channel and answers by itself (sends test lines when
  asked, says what it received), over ordinary encrypted channel messages. No
  server change.
- It is **paced far below every server limit**, and stops at the first
  refusal, so a run can't get anyone flagged or blocked, even on the public
  server.
- At the end, a **copyable report** in the "How to report" shape of
  pending-tests.md: checklist and steps, passed or failed, why. No names, no
  message text.
- **Coverage** of the 121 open tasks: 13 fully automatic, 11 automatic with a
  guided step, 21 guided, 39 with a partner (14 of them judged automatically),
  and 37 not covered (27 of them are the owner's server-side tasks). Of the 94
  player tasks, 84 are covered. See [Coverage](#coverage).

## Where it lives and how it starts

**Recommendation: inside `/lgdebug`.** `/lgdebug test` opens the self-test
window, and the debug window gets a **Self-test...** button at its top. Not a
new `/lgtest`: the command list stays as it is (one of the tests checks that
`/lgl` is listed once in `/xlhelp`), and the self-test is a testing tool, like
the rest of `/lgdebug`.

It is a window of its own (`SelfTestWindow`), not a tab in the debug window:
the run needs room for its list and the "Needs you" box, and the tester keeps
it open beside the game while doing what it asks.

It is in every build, as `/lgdebug` is: testers run release builds.

## What a run looks like

1. **Start screen**: what it will do (the consent), where the player is, and
   the suites to run, each with how many checks and whether it needs ChatTwo,
   a partner or a relog. Suites that can't run here say why and are unticked
   (for example "needs ChatTwo, which is off").
2. **Run**: the checks one after the other. A check is a list of steps; each
   step is automatic (the runner acts and judges) or guided (the runner says
   what to do, watches what it can, and asks). Before each step that types a
   line, the private-area check runs again.
3. **Report**: the results, a **Copy report** button, and **Clean up**, which
   runs by itself anyway when the run ends or is stopped.

**Results:**

| Result | Meaning |
| --- | --- |
| Passed | Every step that ran passed. A step not run is listed under it ("the part next to a stranger: not run, needs a stranger"). |
| Failed | A step didn't go as the checklist says. One line on what was expected and what happened, in fixed words (never what anyone typed). |
| Skipped | Not run, with why: needs ChatTwo, needs a partner, needs a striking dummy, the tester chose Skip, the partner stopped answering. |
| Needs you | Waiting on the tester. |
| Stopped | The run was stopped (by the tester, by leaving the private place, or by the server asking to slow down). |

**Relogs and plugin reloads.** Some checks need a relog ("log out and back
in"), turning LookingGlass off and on, or ChatTwo off and on. The run is kept
in a small file in the plugin's config folder (`selftest-run.json`: the run's
code, the results so far, where it was, the test channel's ID, the settings to
put back, and for two players who the other player is), so it carries on by
itself after the login or reload, once the private-area check passes again.
The file is deleted when the run ends. It holds no message text.

**Stopping.** **Stop and clean up** is on screen the whole time. Closing the
window doesn't stop a run (it says so, and `/lgdebug test` brings it back),
because a guided step may need the screen.

## Mockups

ImGui, at the plugin's usual size. Simple mode; advanced mode differs only
where noted under [Words](#words).

### Start screen and consent

```
+- LookingGlass self-test ------------------------------------------+
| Checks as much of the testing list as it can by itself, and tells |
| you what to do for the rest.                                      |
|                                                                   |
| What it does:                                                     |
|  - Makes a test channel, "LG self-test K7Q2", only for this test, |
|    and sends test lines in it, slowly. Your own channels aren't   |
|    used.                                                          |
|  - Types some test lines for you, such as "/s LG self-test 4,     |
|    please ignore". Some go to Say, where only people in this      |
|    room could see them.                                           |
|  - Changes some settings for a while, and puts them back at the   |
|    end, even if the game closes.  [Which ones?]                   |
|  - Closes your channel windows for a while, and opens them again  |
|    where they were.                                               |
|  - Don't type in chat while it runs, unless it asks you to.       |
| The report has no names and nothing anyone typed.                 |
|                                                                   |
| You're in: your inn room.  OK                                     |
| Stay here until it's done. You can stop at any time.              |
|                                                                   |
| [x] Talking in a channel and local chat   9 checks               |
| [x] Windows and counts                   12 checks               |
| [x] Colours                               6 checks, 2 relogs     |
| [ ] With ChatTwo                          6 checks  ChatTwo is off|
| [x] Right-click invites                   4 checks               |
| [x] Party finder links                    5 checks               |
| [x] Chat history and text files           4 checks               |
| [x] Settings                              2 checks               |
| About 35 minutes. About 60 test lines sent.                       |
|                                                                   |
|   [ Start ]     [ Two players... ]     [ Close ]                  |
+-------------------------------------------------------------------+
```

**Which ones?** opens a list: each setting the ticked suites change, what it
is now, and that it is put back (see
[Settings it changes](#settings-it-changes)).

### Not in a private place

```
+- LookingGlass self-test ------------------------------------------+
| The self-test only runs in a private place, so its test lines     |
| can't reach other players:                                        |
|   - an inn room                                                   |
|   - inside an apartment (not the lobby)                           |
|   - inside a house: any floor, private chambers, the workshop     |
|                                                                   |
| You're in: Limsa Lominsa Lower Decks.                             |
|                                                                   |
|   [ Check again ]     [ Close ]                                   |
+-------------------------------------------------------------------+
```

During a run, leaving pauses it before the next step that types a line:

```
| Paused: you left the private place (now: Mist). Go back inside to |
| carry on, or stop.          [ Carry on ]   [ Stop and clean up ]   |
```

In a house or apartment, a step that types a line also waits while another
player (other than the partner) is within 30 yalms:

```
| Waiting: someone else is nearby, and could see a test line in     |
| Say. It carries on when nobody else is near.  [ Stop and clean up ]|
```

### A run, with a guided step

```
+- LookingGlass self-test ------------------------------------------+
| Run K7Q2   14 of 48   12:40              [ Stop and clean up ]    |
|-------------------------------------------------------------------|
| Talking in a channel and local chat                               |
|  passed   Starting, and the once-only line      local-chat 29     |
|  passed   Typing never reaches Say              local-chat 30-31  |
|  FAILED   Commands and one-offs                 local-chat 32-33  |
|           "/s" with text went to the test channel, not to Say.    |
|  >        Ending it                             local-chat 34-36  |
|  .        Refused to start, and the privacy notice                |
|  .        Log and plain words                                     |
|-------------------------------------------------------------------|
| Needs you (step 2 of 6)                                           |
| You're talking in [Local] now. Click in the game's chat box and   |
| press Tab.                                                        |
|                                                                   |
| Watching: still talking in [Local].                               |
|                                                                   |
| [ Done ]   [ It didn't work ]   [ Skip this step ]                |
| Note (optional, no names): [________________________________]     |
+-------------------------------------------------------------------+
```

"Watching" says what the runner sees as it changes ("stopped talking in
[Local]: you pressed Tab"), so for many guided steps **Done** is pressed for
the tester: the runner moves on by itself when it sees the expected change,
and the buttons are for when it can't see it. Steps where only the tester's
eyes can judge (a colour, a menu, a flash) always wait for an answer:

```
| Needs you (step 1 of 3)                                           |
| Look at the line just printed in game chat, starting               |
| "[LG self-test]". Is the whole line pink, the same pink as this   |
| swatch, also after the item link?   [####]                        |
|                                                                   |
| [ Yes ]   [ No ]   [ Skip this step ]                             |
| Note (optional, no names): [________________________________]     |
```

### Two players: the host

```
+- Two-player checks -----------------------------------------------+
| Your partner needs LookingGlass, and has to be in a private place |
| too. For the local chat checks, both of you need to be inside the |
| same house or apartment, and on each other's friends list.        |
|                                                                   |
| Partner: [ Bob Hatter@Lich               ]  [ Use my target ]     |
| Tell them your name and this code: K7Q2                           |
|                                                                   |
| [ Invite them ]                                                   |
|                                                                   |
| Waiting for them to join "LG self-test K7Q2"...  1:42 left        |
| (They choose "Be a partner" in their self-test window.)           |
+-------------------------------------------------------------------+
```

Once joined:

```
| Partner joined. In a private place: yes. Same place as you: yes   |
| (12 yalms). Friends: yes. In your party: no (the party checks are |
| skipped unless you invite them; nobody else may be in it).        |
| ChatTwo on their side: off.                                       |
|                                                                   |
| [x] Local chat together             9 checks                      |
| [x] Messages, check codes            3 checks                     |
| [x] Windows and counts               8 checks, 3 relogs           |
| ...                                                               |
|   [ Start ]   [ Cancel ]                                          |
```

### Two players: the partner

```
+- Be a partner ----------------------------------------------------+
| Their name: [ Alice Wonder@Lich        ]  Their code: [ K7Q2 ]    |
|                                                                   |
| While it runs, your plugin answers their test by itself, in their |
| test channel only:                                                |
|  - joins "LG self-test K7Q2" when they invite you (no other       |
|    invite is accepted for them)                                   |
|  - sends test lines there, or to local chat, when asked (slowly,  |
|    at most 80)                                                    |
|  - says what it received: how many lines, which links, its check  |
|    code, where a line showed up                                   |
|  - leaves and joins again when asked                              |
| It never types in game chat by itself: when a step needs that, it |
| asks you. Some settings change for a while and are put back.      |
|                                                                   |
|   [ Start ]   [ Cancel ]                                          |
+-------------------------------------------------------------------+
```

Running:

```
| Partner for K7Q2. Joined "LG self-test K7Q2".   [ Stop ]          |
| Answered 23 requests. Last: sent 2 lines (12 s ago).              |
|-------------------------------------------------------------------|
| Needs you                                                         |
| Type in the game's chat box: /s hello                             |
| (Your partner right-clicks your name in their chat log.)          |
| [ Done ]   [ Skip ]                                               |
```

### The report

```
+- LookingGlass self-test: report ----------------------------------+
| Run K7Q2: 31 passed, 2 failed, 9 skipped. Cleaned up.             |
|                                                                   |
| [ Copy report ]   [ Run the failed ones again ]   [ Close ]       |
|-------------------------------------------------------------------|
| LookingGlass self-test report                                     |
| Plugin 0.2.17, ChatTwo off, simple mode, an inn room, with a      |
| partner (on 0.2.17)                                               |
| 2026-10-13 20:05 to 20:41 (UTC+02:00)                             |
|                                                                   |
| local-chat 29: passed                                             |
| local-chat 30-31: passed (not run: the part next to a stranger)   |
| local-chat 32-33: FAILED at 20:11: "/s" with text went to the     |
|   test channel, not to Say (step 32)                              |
| local-chat 34-36: passed (Tab, info bar, Send Tell: you said yes) |
| windows-only 17: skipped: needs a striking dummy                  |
| context-invites 15, 18 (that part): not run: they use LookingGlass, |
|   so nothing was sent (try someone who doesn't)                   |
| channel-windows 37c-37f: passed; your note: "flash is subtle"     |
| ...                                                               |
| Clean-up: test channel left (deleted); 7 settings put back;       |
|   3 channel windows opened again.                                 |
+-------------------------------------------------------------------+
```

The copied text is exactly what is shown: no character, world or channel
names (the partner is "a partner"), no message text, times to the minute with
the time zone (the owner finds the log lines by them, as pending-tests.md
asks). A tester's own note is copied as typed, with a reminder above the box
not to put names in it.

## The private-area check

`PrivateArea` (Core, pure, tested) decides from facts the plugin reads (in
`PrivateAreaReader`, on the framework thread). **It fails closed: anything it
can't read, or doesn't recognise, is "not private".**

**Read:**

- `GameMain.Instance()->CurrentTerritoryIntendedUseId` (FFXIVClientStructs
  enum `TerritoryIntendedUse`), and `IClientState.TerritoryType` (a `uint` in
  API 15; 0 during a loading screen, which is "not private, wait").
- As a cross-check, the same use read from the `TerritoryType` sheet
  (`TerritoryIntendedUse.RowId`); the two must agree.
- `HousingManager.Instance()`: `GetCurrentHousingTerritoryType()` (None,
  Outdoor, Indoor, Workshop), `GetCurrentIndoorHouseId()` (invalid as
  `ulong.MaxValue`, and 0 is treated as invalid too), `GetCurrentPlot()`
  (-128 or -127 for apartments), `GetCurrentRoom()` (0 in an apartment lobby
  and on a house's own floors; the room number in an apartment or a chamber),
  `IsInWorkshop()`.

**What counts** (values from FFXIVClientStructs' `TerritoryIntendedUse`: 2 Inn,
13 HousingOutdoor, 14 HousingIndoor; checked against the 7.56 sheets):

| Place | Rule | Counts? |
| --- | --- | --- |
| Inn room | use 2 (exactly the eight inn rooms: 177, 178, 179, 429, 629, 843, 990, 1205) | Yes. The only place nobody else can be. |
| House, any floor | use 14, housing type Indoor, indoor house ID valid, plot 0 or more, room 0 | Yes |
| Private chambers, FC chambers | use 14, Indoor, plot 0 or more, room 1 or more | Yes |
| Company workshop | use 14, housing type Workshop (or `IsInWorkshop()`) | Yes (recommended; see [open decisions](#open-decisions-for-the-owner)) |
| Apartment | use 14, Indoor, plot -128 or -127, room 1 or more | Yes |
| Apartment lobby | territory 573, 574, 575, 654 or 985 (a deny list), or plot -128/-127 with room 0 | **No**: everyone in the building passes through |
| Wards and yards | use 13, or housing type Outdoor | **No** |
| Anything else | | **No** |

Both kinds of fact must agree (the sheet's use and HousingManager's type): a
game patch that breaks a FFXIVClientStructs signature then makes the check
fail closed rather than let a ward through. New housing territories (the
"Minimalist" interiors 1249-1251 and 1374-1376 were added lately) keep use 14,
so the rule works on use plus HousingManager, with the lobby IDs as an
explicit deny list, not on a list of allowed IDs.

**Settling.** After a zone change the housing fields can lag behind the
territory. The check is "private" only once it has given the same answer for
two seconds; a change of territory resets that.

**Nobody else near (houses and apartments).** Only an inn room is certainly
empty; a house can have visitors, FC members, or a venue's crowd. So in a
house or apartment, a step that types a line also needs nobody in the object
table within 30 yalms who is a player character other than the tester and
the partner (Say's reach is "close" and Square Enix gives no number; 30 is
well past it in any house). If someone is, the step waits (mockup above). The
start screen says it plainly: "In a house, people who come in could see a test
line in Say."

**When it is checked.** At the start; on every territory change (Dalamud's
`TerritoryChanged`), which pauses the run at once if the answer changes;
again right before every step that types a line or sends anything to local
chat; and after every relog before carrying on. A two-player run also asks
the partner's plugin, which checks its own player the same way, before each
step that has the partner type or send to local chat.

**What "types a line" covers.** Every line the runner puts through the chat
box (see [Typing a line](#typing-a-line)), local chat (`/lgl`), and a guided
step asking the tester to type something. Channel messages to the test
channel and local-only actions (simulated incoming lines, opening windows,
changing a setting) can't reach game chat, but run only while the check
passes too: the owner's rule is that the self-test runs only in a private
place, and the simplest honest form of it is "the whole run".

## Keeping the test apart from the tester's own chat

### The test channel

- **Made by the run**, named `LG self-test <code>` (the code: four letters
  and digits, different each run, also the two-player pairing code). Its ID
  goes in the run file at once, before anything is sent.
- A second and third channel (`LG self-test K7Q2 b`, `... c`) only for the
  checks that need several channels (which window a new channel lands in, tab
  counts). At most three per run.
- Put on a free `/lgc` number (the first free one from 50 down, so the
  tester's own numbers never move), coloured and nicknamed only by the checks
  that need it. A tester with 48 or more channels (the cap is 50) is told the
  suites that need channels can't run.
- **Only test channels are used.** Every message the runner sends goes to a
  channel whose ID is in the run file. "Simulated incoming" lines go only to
  test channels too.

### Settings it changes

A setting is snapshotted (into the run file) before the first check that
changes it, and put back at the end. Only these, and only by the suites that
need them:

| Setting | Changed by | Why |
| --- | --- | --- |
| Show LookingGlass only in windows, New channels open | Windows and counts | The windows-only checks |
| Say when I start or stop talking in a channel | Talking in a channel, Windows | The "Now talking in" lines |
| Colour the whole line | Colours | Both ways |
| Advanced mode | The wording passes of guided checks | Both modes' words on screen (automatic checks read both wordings without changing it) |
| Keep chat history on this computer, Keep my chat history unencrypted | Chat history | The history checks |
| Privacy notice accepted (local chat) | Local chat | "Asks first", withdrawing; put back as it was |
| The once-only notes shown (local chat's, ChatTwo's) | Local chat, With ChatTwo | To see the note the first time |
| Local chat colour | Local chat together | The colour checks |
| Name colours (the tester's own and the partner's only) | Colours (two players) | Setting and default |
| Blocked users (the partner only) | Local chat together, Chat history | Blocking checks; unblocked at the end unless they were blocked before (then the blocking checks are skipped) |
| The tester's channel windows | Windows and counts | Some checks start with "close every window": they are closed and opened again where they were |
| The game's chat channel (Say, Party...) | Every step that types a line | Set to Say first, so a line that wrongly reached game chat would go to Say (see below); set back after |

Everything about a test channel (number, nickname, colour, Show in game chat,
its windows) goes when the channel goes. If the tester changes one of these
settings by hand during the run, the end puts back what it was before the run
(the start screen says so). Dalamud's own settings (reduced motion, fonts) are
never changed: a guided step asks the tester when a check needs one.

**The chat history.** If the history was **on** before the run, the test
channel's lines in it (and its text-file folder) are removed at clean-up,
and nothing else is touched. That needs one small new function, removing one
channel's lines from the log (`ChatLog.ForgetChannelAsync`). If it was **off**,
it is turned on for the run and, at clean-up, off again and deleted, since
everything in it is the test's. The checks that would cost the tester real
history (the 5 MB limit dropping the oldest lines, **Delete my chat
history**) run **only** when the history was off and empty before the run;
otherwise they are skipped with why.

### Clean-up

At the end of a run, when it is stopped, and after a partner stops answering:

1. Stop talking in any channel (sticky mode off, labels down).
2. Close the test channels' windows; open the tester's own windows again.
3. Put back the settings in the snapshot; set the game's chat channel back.
4. Leave each test channel. As its only member, leaving deletes it on the
   server, with its stored messages, and costs no rate limit; with a partner
   still in it, the partner is asked to leave first (its plugin does), and if
   it doesn't answer within a minute, the host **disbands** it instead.
5. Remove the test channels' lines from the chat history (as above).
6. Delete the run file.

Each step runs even if one before it fails, and the report's last line says
what was done.

### If a run is interrupted

A crash, the game closing, a disconnect or the plugin being turned off mid-run
leaves the run file behind. At the next start of the plugin:

- **Settings are put back at once**, in the plugin's constructor, before any
  session starts (the snapshot is in the file), and saved.
- Once connected and the channel list is in, **the test channels named in the
  file are left** (or disbanded, as above), and one blue line says so: "A
  LookingGlass self-test didn't finish. Your settings are as they were, and
  its test channel was removed."
- The run file goes, unless the tester chose to carry on after a relog (a run
  waiting on a relog is the same file, marked "waiting for a relog": it is
  not cleaned up, it carries on).

A channel named `LG self-test ...` that isn't in any run file (the file was
lost) is **never removed by itself**: the self-test window lists it under
"Left over from an earlier test?" with **Remove**, since only the name says it
is a test channel, and a player could name a real channel that.

A disconnect during a run pauses it; it carries on once connected again
(Connect automatically is on for nearly everyone), and after five minutes
without a connection, it stops and cleans up at the next connection.

## What "automatic" can honestly check

The runner sees the plugin from inside. It can check what the plugin did,
not what the game shows. Six small seams make it possible, none of which
changes what the plugin does when no run is going:

| Seam | What the runner learns | Change needed |
| --- | --- | --- |
| **ChatOutput tap** | Every line the plugin prints to game chat: message, notice or channel notice, its tone (blue, light red, dark red), its tag, the channel, its text in both modes' words | An observer on `ChatOutput` (an event, null unless a run is on). Held in memory for the run only; never logged or saved. |
| **Game chat watcher** | Where a line with the run's marker landed in the game's own chat: Say, Party, Emote, Echo, and from whom (the tester or the partner) | Dalamud's `IChatGui.ChatMessage`, subscribed only during a run, matching only the run's marker |
| **Typing a line** | Puts a line through the same gate as the game's own chat box | See [Typing a line](#typing-a-line) |
| **Log tap** | What LookingGlass writes to `dalamud.log`: errors counted, and every line checked for the run's marker text, the tester's and partner's names and the test channel's name, which must never appear | A wrapper around the plugin's `IPluginLog` that passes everything on unchanged and, during a run, counts and matches (never copies a line anywhere) |
| **State reads** | Sticky mode's state and the info bar's text; the chat input's label (read from the `ChatLog` addon's node); the session's snapshot (channels, members, check codes, blocked users, invites, recent notices); unread counts; channel windows open, their tabs, positions and tab counts, whether one is flashing; the chat history's lines and files | Read-only `internal` properties on `StickyMode` and `ChannelWindows`; everything else is already public |
| **Actions** | Create, invite, accept, leave, disband, rename, send, block, set a colour or nickname or number, Disconnect and Connect, Simulate incoming (local only, as in `/lgdebug`), and a new **Simulate warning** (a light red notice about the test channel, local only, for windows-only step 11) | `SessionManager.SimulateNotice`, the only new action |

### Typing a line

To check sticky mode as the player meets it, the runner puts a line through
`ShellCommandModule.ExecuteCommandInner`, the function the game's own chat box
calls when Enter is pressed (see `ChatInterop`): the same gate, the same
decision, with the line seen as "typed in the game" (`LineSource.Game`). It
is called on the framework thread, outside any other line, with a line built
as the game would (an item link built with `SeStringBuilder` for the link
checks). It isn't added to the chat box's history.

Not `UIModule.ProcessChatBoxEntry` (what ChatTwo calls): that is "from a
plugin", and without ChatTwo's input to vouch for it, every short command
counts as text there (the safe rule), so it would test a different path. The
ChatTwo checks are guided: the tester types in ChatTwo's input, and the
runner watches where each line went.

Before typing any line, the runner sets the game's chat channel to **Say**
(and back afterwards). So if a line that should stay out of game chat
wrongly reached it, it would go to Say in a private place, not to the FC or a
linkshell. Every typed line carries the marker and says what it is: `LG
self-test K7Q2 4, please ignore`.

`/p` lines are typed only when the party is exactly the tester and the
partner (checked in the party list); otherwise those steps are skipped. No
line is ever typed to Yell, Shout, a linkshell, the FC, the Novice Network or
a tell.

### What it can't see

- **What game chat looks like**: colours, where a link's colour stops, how a
  line wraps. It knows the colour the plugin asked for; the tester says
  whether the game shows it.
- **ChatTwo's own window and input**: ChatTwo's label, its menus, a line in
  its window. It knows what was sent to ChatTwo.
- **Menus**: right-click menus can't be opened by a plugin, and shouldn't be.
  It knows what the menu would offer (`ContextInvites.Offers`), and checks
  that; the tester says whether the menu shows it.
- **The player's own actions**: Tab, clicking the info bar, a striking dummy,
  a cutscene, a teleport, the Party Finder's chat button.
- **What another computer sees**, other than a partner's plugin's answers.
- **The server's side**: its log, its counts. Those stay the owner's.

So an automatic "passed" means: the plugin did what the checklist says, as
far as its own state, its lines, its log and the game's chat events show.
Each check's description in the window says which parts are judged by the
runner and which by the tester.

## Two players

### Pairing

1. Both open the self-test, in private places. For local chat checks, both
   inside the same house or apartment (inn rooms are one player each), and on
   each other's friends list; the runner checks both (distance from the
   object table; friends from the game's friend list) and says which suites
   can run.
2. The **host** enters the partner's name@world (or **Use my target**), and
   tells them the code (`K7Q2`), by voice or a tell. The host's runner makes
   the test channel and invites the partner (one lookup, one invite).
3. The **partner** chooses **Two players... > Be a partner**, and enters the
   host's name@world and the code. Its runner accepts **only** an invite from
   that character to a channel named `LG self-test <code>`, and then says
   hello in it.
4. Both carry on while either can stop. Pairing waits two minutes, then
   gives up.

### The protocol

Ordinary channel messages in the test channel, so encrypted and signed as any
message, and nothing new for the server. Every control line reads as text
for a human and is easy to parse:

```
LG self-test K7Q2 #12 send 2 link=item, please ignore
LG self-test K7Q2 #12 done got=0 sent=2
LG self-test K7Q2 #13 report check-code
LG self-test K7Q2 #13 done check=#41 12345 67890 13579 24680 11223
```

`#n` is the request's number; the answer repeats it. The runner reads only
lines that start with the run's exact prefix, from the paired character, in
the test channel. Anything else is ignored (and a line with the prefix from
anyone else ends the run: someone is meddling).

**What a partner does by itself** (a fixed list, each with a cap):

| Request | The partner's plugin | Cap per run |
| --- | --- | --- |
| `hello`, `still-there` | Answers with its version, private place yes/no, ChatTwo on/off, simple or advanced | |
| `send n [link=item\|map\|status\|pf]` | Sends n marked lines to the test channel (with that link kind) | n at most 5, 80 lines in all |
| `local n` | Sends n marked lines to local chat (after its own private-place check) | n at most 3, 20 in all |
| `report received` | How many marked lines it got since the last report, from whom (host or itself), which link kinds turned into working links, whether they were shown in game chat or a window, the tag | |
| `report check-code` | The test channel's check code | |
| `report saw <marker>` | Whether a line with that marker showed up in its game chat, in which channel (Say, Party, Local), and whether its plugin printed it | |
| `report windows` | Whether a window opened for the test channel, and its tab counts | |
| `leave`, then the host invites again | Leaves the test channel; accepts the next invite to it from the host | 5 times |
| `withdraw-local` / `restore-local` | Turns its local chat privacy acceptance off for "receiving before accepting", and back | once |
| `disconnect s` | Disconnects for s seconds (at most 60), then connects | 3 times |
| `bye` | Cleans up and stops | |

It never types in game chat, never invites or blocks anyone, never changes a
setting not in this list, and never acts on another channel. A step that
needs the partner's eyes or hands (say `/s hello` so the host can right-click
the name, click a party finder link, relog) shows the partner a "Needs you"
box; the answer goes back as `done yes`, `done no` or `done skipped`.

**Time-outs.** A request is answered within 60 seconds (a loading screen
included), or 10 minutes for one that waits on the partner's own guided step
or relog. Past that, the host marks that check "skipped: your partner didn't
answer", asks `still-there`, and after a second silence skips every check
left that needs the partner and cleans up. A partner hearing nothing for 15
minutes leaves the test channel and stops by itself.

**Relogs.** Both keep their run file; either side's runner, back from a relog,
says `back` and carries on. A check like "missed messages" uses it: the host
logs out, the partner's plugin sees the host go offline in the member list and
sends when asked before, then the host logs in and the runner checks what
caught up.

**Both sides' results.** The partner's answers are part of the host's
results, so the host's report covers the task. The partner gets a short
report of its own (what it was asked, what it answered, its own guided
steps), to send too if the owner wants it.

## Pacing: never near the server's limits

The server's defaults (code and `appsettings.json`; the public server may
override them, which the runner can read only for the few the server's
`Welcome` sends) and what a run spends:

| Action | Server limit (per user unless said) | The runner's pace | A run's most |
| --- | --- | --- | --- |
| Channel messages | 5 at once, then 1 a second | 1 every 2 seconds, at most 3 in a row | 100 (host), 80 (partner) |
| Local chat messages | 5 at once, then 1 a second; to one person 30, then 1 every 2 seconds | 1 every 4 seconds | 20 each |
| Create a channel | 10 at once, then 1 a minute; 50 channels | 1 at a time, at most 3 | 3 |
| Disband | 5 at once, then 1 a minute | Avoided: leaving as the only member deletes the channel, unlimited | 3 (only if the partner is gone) |
| Rename | 10 at once, then 1 every 10 seconds | 1 every 20 seconds | 2 |
| Look up by name | 60 at once, then 1 a second | Once per partner (the plugin reuses it 10 minutes) | 2 |
| Invites to one person | 20 at once, then 1 a minute (inviter 60, invitee 30) | 1 every 30 seconds | 8 |
| Rekeys (after joins and leaves) | 5 at once, then 1 every 2 seconds | Follow joins and leaves, which are 30 seconds apart | 10 |
| Reads (`ReadBudget`: lists, keys, logs) | 120 at once, then 4 a second | Reconnects at least 60 seconds apart | |
| Message pages (catch-up) | 100 at once, then 4 a second | One per channel per reconnect | |
| New connections per address | 60 a minute, 20 open, 4 not logged in | Disconnect and Connect at least 60 seconds apart | 5 (host), 3 (partner) |
| Device list | 20 at once, then 1 every 6 seconds | Only when a check opens Settings | 2 |
| Operator requests | Counted against non-operators | Never | 0 |

**The stop rule.** If the server refuses anything for a limit (a
`RateLimited` error, or one naming a limit), the runner stops sending at
once, marks the check "stopped: the server asked to slow down", waits a
minute and cleans up. It never retries a refused request. Flagging needs
refusals in 30 different minutes of an hour, or by 4 different limits within
10 minutes; an automatic block needs 1,000 refusals from one address in an
hour. A run makes at most one refusal, so neither can happen, even for a
household sharing one address with two testers.

**Between runs.** The runner keeps what it spent in the last hour (in the
plugin's config folder) and won't start a run that would take the hour past
an hourly budget; it says when it can, and what waits ("Wait about 20
minutes: the self-test's runs in the last hour used most of what it may use
in an hour for making test channels").

Only the actions whose server limit refills slower than the runner's own pace
could spend it over back-to-back runs have an hourly budget (owner,
2026-10-13), each under what the server allows at once:

| Action | Server limit (default) | The runner's pace | Hourly budget |
| --- | --- | --- | --- |
| Create a channel | 10 at once, then 1 a minute | 1 every 10 seconds | 8 |
| Disband | 5 at once, then 1 a minute | 1 every 20 seconds | 4 |
| Invites to one person | 20 at once, then 1 a minute | 1 every 30 seconds | 16 |

Everything else has only its pace and its most per run, since its server
limit refills at least as fast as the runner can spend it: channel messages
and protocol lines (1 a second; the runner sends 3 in 10 seconds at most),
local messages (1 a second, 1 every 2 seconds to one person; the runner, 1
every 4), lookups (1 a second; the runner, 1 every 2), renames (per user, 1
every 10 seconds; the runner, 1 every 20), reconnects (60 new connections a
minute per address, counted each minute, and a saved login, not a key login;
the runner, 1 a minute) and the device list (1 every 6 seconds; the runner,
1 every 10).

Other things the runner never does: register, reset an identity, sign out
other computers, forget an account, change the server address, ban, or send
anything to anyone but the test channel, local chat (in a private place) and
the partner.

## Privacy and safety

- **Consent first.** Nothing happens before **Start**: no channel, no line,
  no setting. The start screen says what it will do, in five lines, and
  **Which ones?** lists the settings.
- **Nothing anyone said leaves an encrypted channel.** Test lines are sent in
  the test channel (encrypted, as always) or local chat (encrypted). The only
  lines that reach game chat are the few the checklist needs there (`/s`,
  `/em`, `/p` one-offs), typed in a private place with Say as the channel,
  and saying `LG self-test K7Q2 4, please ignore`.
- **No message content in logs or the report.** The runner logs one line per
  check to `dalamud.log` (`[selftest] local-chat 32-33: failed (step 32)`),
  with no names and no text, so the owner finds the spot by time. The ChatOutput
  tap and the game chat watcher hold lines in memory for the run only, to
  match markers; they are dropped when the run ends.
- **Names.** The report has none. The run file holds the partner's name@world
  only while the run lasts (to invite them again after a leave or relog), and
  goes with it.
- **Only the run's own things.** Only test channels; only the tester's own
  and the partner's name colours; only the partner's block; the tester's
  windows reopened as they were.
- **Stop at any time.** **Stop and clean up** is always on screen, and
  works mid-step: a step in progress is abandoned (a line being typed is
  already sent or not, nothing half-done), then the clean-up runs.
- **Fail closed.** If the runner can't tell whether the place is private, or
  whether the game chat channel is Say, it doesn't type.

## Words

Short and plain in both modes. The owner allows "encrypted". Simple mode never
says key, fingerprint, epoch, log, signature or the like; the runner's words
are in Core (`SelfTestWords`), with both wordings, under the same tests as
every other word list (no banned words in simple mode).

Advanced mode differs only where a check is about something advanced mode
shows: "check code" becomes "log head (check code)", and a failure's line may
name the internal reason (`StickyRoute.Dropped`). The checklists' own words
(their step numbers, "sticky mode" in their names) are kept in the report,
since that is how the owner files results.

Words the window uses: **Self-test**, **Start**, **Stop and clean up**,
**Needs you**, **Done**, **It didn't work**, **Skip this step**, **Yes**,
**No**, **passed**, **failed**, **skipped**, **Two players...**, **Be a
partner**, **Invite them**, **Copy report**, **Run the failed ones again**,
**Check again**.

## Coverage

One row per open task in [pending-tests.md](testing/pending-tests.md), by its
section and name, with its checklist steps. Modes:

- **A**: automatic. The runner does every step and judges it. Parts it
  can't do are named, and reported as "not run".
- **A+G**: automatic, with one or more guided steps (a click, a look, a
  relog).
- **G**: guided. The runner sets things up, says what to do, watches what it
  can, and asks.
- **2P**: needs a partner. "(A)" if the runner judges it from the partner's
  answers, "(G)" if someone has to look or act.
- **No**: not covered, with why.

### Group 1: one player (54 tasks)

**Suite: Talking in a channel and local chat** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Is `/lgl` free, and what does it say | local-chat 1-3 | A+G | A: `/lgl` registered once, by LookingGlass (Dalamud's command list); its help text; the Colour "?" words. G: look at `/xlhelp`; turn LookingGlass off and on in `/xlplugins` (the run carries on after) and type `/lgl` while it's off. |
| The "ask first" window | local-chat 3a-3c | A+G | A: privacy withdrawn (snapshot); `/lgl LG self-test` typed: nothing sent, the blue line, the window open; typed again after Not now: asks again; Settings' state. G: click **Not now**, then **Accept and use local chat**. |
| Starting, and the once-only line | local-chat 29 | A | The once-only note reset (snapshot); `/lgl` typed: "Now talking in [Local].", the note once, the chat input's label `[Local]`, the info bar "LG [Local]" in local chat's colour (the colour asked for); typed again: only the first line. |
| Typing never reaches Say | local-chat 30-31 | A | Alone: a marked line and a line with only an item link typed: "Not sent... nobody is near enough", nothing in Say (the game chat watcher). Not run: next to a stranger (a private place has none). |
| Commands, one-offs and moving between channels | local-chat 32-33 | A | `/s`, `/em`, `/lgc<test>`, `/lgl` one-offs each land once where they should and local chat goes on; `/lgc<test>` alone, `/lgl` one-off, `/lgl` alone; nicknaming the test channel `Local` refused. `/p` only with the partner as the whole party, else not run. |
| Ending it | local-chat 34-36 | A+G | A: `/s` alone; Disconnect ("...: disconnected."), with verbose off too; verbose off: labels still change. G: Tab; clicking the info bar; **Send Tell** from a right-click; turning the plugin off ("...: LookingGlass was turned off."), and a relog (nothing on after). |
| Refused to start, and the privacy notice | local-chat 37-39 | A+G | A: disconnected, `/lgl` refused; withdrawn, `/lgl` asks and opens the window, nothing starts; withdrawn from Settings while talking: stops at once with its line, typing goes to Say again (the game chat watcher). G: accept in the window (told to type `/lgl` again); withdraw from the privacy window's own button. |
| Log and plain words | local-chat 40-41 | A | Log tap: `[sticky]` lines say "talking in [Local]", never a marker or a name. Every line printed during the suite checked against the banned words in simple mode. |

**Suite: With ChatTwo** (one player; ChatTwo on)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Local chat with ChatTwo | local-chat 42-46 | G | The tester types in ChatTwo's input and pop-out as told; the runner watches where each line went (local chat, Say, Party) and that local chat ends on ChatTwo's picker or a tab; refusal on a tell. The label's look: the tester. Party parts only with the partner as the party. |
| The ChatTwo note when you start talking in a channel | sticky-channel 28 | G | The note reset (snapshot); the tester switches ChatTwo to Party and types `/lgc<test>`; the runner checks the line and the note once, and the info bar. ChatTwo's label: the tester. |
| Pink everywhere, ChatTwo | custom-colours 11-14 | G | The runner sends a line with an item link in the pink test channel and starts talking in it; the tester says what ChatTwo shows. The invite submenu part needs a partner's name in ChatTwo. |
| ChatTwo turned off and on | context-invites 19-21 | G | The tester turns ChatTwo and LookingGlass off and on as told (the run carries on after a reload); the runner checks the menu registration count (one, not two) itself; the menu's look: the tester. |
| Party finder: with ChatTwo | chat-links 26 | G | As the party finder suite, with ChatTwo's input. |

**Suite: Windows and counts** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| The setting itself | windows-only 1-2 | G | The runner opens Settings; the tester checks the box, its "?" and the greyed choices. A: the "?" words. |
| Your own lines, and LookingGlass's replies | windows-only 5, 7, 8, 9, 12 | A+G | A: with the setting on, `/lgc<test>` typed: not in game chat, once in the tab; `/lgc<empty number>` and `/lgc` alone answer in game chat in blue; `/lgc<test>` alone then a line: to the tab; Disconnect and `/lgc<test>`: "Not connected" in game chat. G: typing in the window's own box. The invite result line needs the partner (2P). |
| Warnings still show in game chat | windows-only 11 | A+G | A: **Simulate warning** on the test channel: printed in game chat as a warning and added to the tab. G: light red, as the tester sees it. (This answers the task's "ask the owner how".) |
| Which window a new channel lands in | windows-only 13-16 | A | Three test channels; Simulate incoming; the window used last set as the checklist says; positions checked against the screen, each a step down and right, none off screen, also starting near the bottom-right corner (the runner moves the first window there). |
| Nothing pops up during combat | windows-only 17 | G | Only with a striking dummy at hand (a house's striking dummy furnishing); otherwise skipped. The runner simulates the line while the tester is in combat (it waits for combat) and checks no window until combat ends. |
| Show in game chat is locked while the setting is on | windows-only 20-21 | A+G | A: the state stays off; no "shows in game chat again" line on closing. G: the greyed red cross, its tooltip, the tab's hover. |
| Turning the setting off | windows-only 23, 23b, 23c, 24 | A+G | A: 23 without combat. G: the combat parts (striking dummy, else not run); the relog and the title screen part (the run carries on after). |
| No errors in the log | windows-only 26 | A | Log tap: no errors written by LookingGlass during the suite. (An error Dalamud writes about the plugin, outside its own log, isn't seen; the report says to glance at `/xllog` if something looked wrong.) |
| Counts on every tab not selected | channel-windows 17, 17b, 17c, 36, 37 | A | Three test channels as tabs; simulated lines; counts and the hover's words; selecting a tab; `/lgc<test>` typed clears its count; a second window: no count on the hidden tab or in the channel list. Not run: 17b's "someone joining" (2P). |
| A channel off game chat opens a window again | channel-windows 33, 33b, 33c, 33d, 33e, 33f | A+G | A: off game chat, windows closed, a simulated line opens one (or a tab), flashing, without the keyboard focus (the window's flags); `/lgc<test>` typed opens one with the line. G: the relog part; the combat parts (striking dummy, else not run). |
| Channel list counts for channels seen only in windows | channel-windows 37b; windows-only 6b | A | Selecting in the list keeps the count; the tab clears it; channels in game chat as before; with windows only on, counts stay until the tab or talking. |
| The flash | channel-windows 37c-37f | G | A: the flash starts with 3 pulses for a window that opened by itself, 1 for an added tab, none for one opened by the tester or at login. G: is it noticeable, or too much; reduced motion (the tester turns it on in Dalamud's settings, if they want to). |

**Suite: Colours** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| The colour is kept (name colours) | name-colours 21 | G | Needs a name colour: the tester's own (A+G: the runner sets it). The tester reloads the plugin and relogs; the runner checks the colour after each. Not run: another character. |
| What's saved in the settings file | name-colours 23 (first part) | A | Reads the saved settings: the colour under `NameColours`, keyed by a number only. |
| Colour test command, odd inputs | custom-colours 3 | A | Runs `/lgdebug colours #3FA7D6 00ff00` (six lines), `/lgdebug colours #nope` (a usage line), plain `/lgdebug` (the debug window opens). |
| The colour picker | custom-colours 4-8 | G | The runner opens the test channel's Colour... menu; the tester checks the swatches, Custom..., the code box, greyed-out codes, the dark note, the recent swatch. A: the code parsing words. |
| Pink everywhere, game chat (no ChatTwo) | custom-colours 9 (except the caught-up message) | G | The runner colours the test channel pink, sends a line with an item link in the middle, starts talking in it (the line and the info bar), opens its window; the tester compares each with the swatch shown in the step. The invite submenu part needs the partner. |
| Colours are kept | custom-colours 15-16 | G | The runner sets a swatch on one test channel and a custom colour on another; the tester reloads and relogs; the runner checks both after each. Not run: another character, and "unchanged since before 0.2.8". |
| Leaving drops the colour | custom-colours 17 | A | A throwaway test channel, a custom colour, left; once the list reloads, the colour is gone from the saved settings. (The clean-up does the same for the run's own channels.) Not run: being invited back. |

**Suite: Right-click invites** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Where it must not show | context-invites 7-9a | G | The tester right-clicks what the step lists that is at hand (their own name and character, a minion, items, an item link; NPCs or a retainer in a house); A: the runner checks `ContextInvites.Offers` gives nothing for each kind. The FC login line part needs ChatTwo and an FC. |
| No item when it can't work | context-invites 10-11 | G | The runner disconnects; the tester right-clicks (the partner, or any character in sight) and says whether the item is gone; then connects. Not run: a character with no channels of its own. |
| Inviting someone who isn't registered | context-invites 15, 18 (that part) | A+G | The tester types the name@world of someone they know who doesn't use LookingGlass (one lookup; nothing is sent to that person); the runner invites through the same path as the menu and checks the line, in both modes. G: the same from ChatTwo's menu, if on. |

**Suite: Settings** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| The labels and the "?"s | settings 1-9 | G | The runner opens Settings and moves it to the bottom-right corner when asked; the tester checks labels, the nine "?"s, closing them, the narrow window, a bigger font. A: the nine "?"s' words, lengths and plain words. |
| The list in Settings (computers signed in) | device-notices 1 | G | A look only: the line for this computer, the "?", the button (not pressed). |

**Suite: Party finder links** (one player; needs a recruitment of the tester's own)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| What the chat button puts in the input | chat-links 20 | G | The runner starts talking in the test channel; the tester clicks the chat button; the runner reads the link kind from its own hook (the "link put in the chat input" line) and asks what the input shows. |
| Sending one, and clicking it | chat-links 21-23 | G | The tester types `/lgc<test> join ` and clicks the chat button; the runner checks one party finder link was sent, with the cross-world mark as the listing says; the tester compares with the game's own and clicks it, in chat and in the window. |
| Home world only, and talking in the channel | chat-links 24-25 | G | As above, with the tester changing the recruitment as told; the runner checks the mark each time and the three ways of sending. |
| No names in the log | chat-links 30 | A | Log tap: the `[sticky]` lines have counts only, no listing number and no name. |

**Suite: Chat history and text files** (one player)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| The setting, and the warning first | chat-log 24-25 | G | The runner turns the history on (snapshot); the tester checks the setting, its "?" and the warning, presses Cancel then Turn on. A: the words, in both modes; it disappears while the history is off. |
| Only new lines, one file per channel per month | chat-log 26-28 | A | Two test channels; a sent line with an item link, a simulated line, one in the other channel; folders and files named as the checklist says; lines in the format, the item as `[Item name]`; appending while the runner holds the file open read-only (as Notepad does); the file deleted, a new one started. **Open folder** isn't pressed (the runner checks the folder it would open). |
| Renames, turning it off, deleting | chat-log 30-35, 31b | A+G | A: rename (the folder follows, same 16 characters); turning it off (files stop changing); the confirmation's words; the log tap (no channel, file or person named). Only when the history was off and empty before the run: the 5 MB limit (filled with simulated lines, local only) and **Delete my chat history...** (G: the tester presses it). Not run: the file held by Notepad++ or Excel (the runner holds it the way they do instead, A); another character. |
| Lots of history, and new messages while reading | chat-log 8-9 (group 2) | G | Moved to one player: 250 simulated lines (local only, nothing sent) instead of the partner sending them. The tester scrolls as told; the runner checks what loaded and the end line; a simulated line while reading: the tester says whether it jumped, and clicks **New messages**. |

**Not covered, group 1:**

| Task | Steps | Why |
| --- | --- | --- |
| A copy of this computer's login is noticed | device-notices 2-4 | A second install and copying the secrets file; on a test server |
| Told at the next login | device-notices 5-9 | A second install, Forget account, registering again |
| Sign out everywhere else | device-notices 10-14 | A second install; signing out the tester's other computers is never something a test does for them |
| Registering again lifts it | device-notices 15-16 | Registering again through the Lodestone |
| Signed out by a computer this one doesn't know | device-notices 17-20 | Copies of the secrets file, Reset my identity |

Group 1's two-player tasks (setting a name colour, the three "where the
invite shows" tasks, no count for channels shown in game chat) are in the
group 2 tables below, since they need the partner.

### Group 2: two players (40 tasks) and group 1's partner tasks (5)

**Suite: Local chat together** (both inside the same house or apartment, friends)

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Receiving before accepting | local-chat 3d | 2P (A) | The partner's acceptance withdrawn (and put back); host `/lgl` line; the partner reports it arrived. |
| Friends list not loaded yet | local-chat 4-7 | 2P (G) | Both relog as told (the run carries on), neither opens Friends; host `/lgl`; the partner reports; both open and close Friends; again; both look at the line. |
| A long friends list | local-chat 7a | 2P (G) | Only if the host's friends list has more than one page (the runner counts it) and the partner is near its end. |
| Replies, links and range | local-chat 8-11 | 2P (A+G) | A: replies both ways; an item link arrives as a working link, `<t>` as the target's name (the partner reports); the partner in another place (its own inn room): "nobody is near enough". G: walking away for the range, in a house big enough (the runner shows the distance, the tester walks). |
| A friend without LookingGlass | local-chat 14 (first part) | 2P (A) | The partner disconnects LookingGlass (`disconnect 60`): host `/lgl` told none of the friends near uses LookingGlass. Not run: with D and B both near (three players). |
| Blocking | local-chat 15-16 | 2P (A) | The host blocks the partner; the partner's `/lgl` doesn't show; the host's doesn't reach it, with the line; unblocked: shows again. |
| Plain words | local-chat 17-18 | 2P (A) | Every local chat line on both sides checked against the banned words in simple mode; advanced mode's the same check. |
| Colours and where local lines go | local-chat 19-23 | 2P (G) | The runner sets local chat's colour (swatch, custom, default) and the partner's name colour, and windows only; the partner sends; the tester looks. A: lines still in game chat with windows only on; `/lgl` one-off while talking in the test channel. |
| Logs keep names out (player side) | local-chat 25 (players) | 2P (A) | Log tap on both sides: counts only, no name, no marker. |
| Talking to a friend near you | local-chat 47 | 2P (A+G) | Host `/lgl` alone, lines typed (one with an item link, one with `<t>`): the partner reports `[Local]` lines, none in Say; `/s` alone then a line: the partner reports it in Say. G: the partner walks past 20 yalms. |

**Suite: Messages and check codes**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Normal chat is unchanged | chat-links 16-17 | 2P (A) | Lines both ways, with and without an item link; no warning on either side; the partner leaves and is invited back while both keep sending; still no warning. |
| Check codes match | chat-links 19 | 2P (A) | Both check codes compared, before and after the change; the "Log head" label in advanced mode from the words; no member with a warning. |

**Suite: Windows and counts, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Messages from B in tabs and new windows | channel-windows 17b, 17d; windows-only 3, 6 | 2P (A+G) | A: the partner sends twice behind the selected tab: the count; a join adds nothing; windows only, a window opens and flashes 3 times; the partner leaves and is invited back: a tab added, one flash. G: the relog part. |
| Missed messages open a window for a channel off game chat | channel-windows 33c (second part) | 2P (G) | The host relogs; the partner sends while the host is away (it sees the host offline); the runner checks no game chat lines and a window with the missed lines. |
| An information line alone opens a window for a channel off game chat | channel-windows 33f (second part) | 2P (A) | The partner leaves: the line isn't in game chat; a window opens, flashing, with it. |
| No count for channels shown in game chat | channel-windows 36b (group 1) | 2P (A) | The partner sends: no count while the channel shows in game chat; off game chat, window closed: counts, and a window opens. |
| Messages from B go only to a window | windows-only 3-4, 6, 10 | 2P (A+G) | A: as the checklist, with the partner sending, leaving, being invited back, and inviting the host to a second channel of its own ("invited you" in game chat). That one invite is the partner's only invite, and the host's runner accepts only it. G: keep walking while the window opens. |
| Messages from while you were away | windows-only 15b, 25 | 2P (G) | The host relogs; the partner sends in two test channels; the runner checks one window with both tabs, then windows back where they were. |
| Nothing pops up in cutscenes or loading screens | windows-only 18-19 | 2P (G) | The host watches the inn room's **Unending Journey** (or any cutscene); the runner asks the partner to send once it sees the cutscene start, and checks nothing opens until it ends. The loading screen part only through a door between private places (a house to its private chambers or workshop); a teleport leaves the private place, so it isn't asked. |
| Turning the setting off | windows-only 22 | 2P (A) | Unticked: the partner's next line in game chat at once and in its window; each channel's Show in game chat as before. |

**Suite: Colours, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Setting a colour | name-colours 1-6 (group 1) | 2P (G) | The partner in two test channels (one teal, nicknamed); the tester sets the colours through the menus as told; the runner checks the saved colours and refused codes. |
| Name colours in game chat and windows | name-colours 7-15 | 2P (G) | The partner sends as asked (with an item link); the tester looks; the missed message part with a relog. A: the window's right-click offers (from the code that builds them). |
| Name colours in ChatTwo | name-colours 16-19 | 2P (G) | As above, in ChatTwo. |
| Back to default | name-colours 20 | 2P (G) | The tester sets Default; the partner sends one line; the tester looks. A: no colour saved. |
| Leaving and rejoining | name-colours 22 | 2P (A) | The partner leaves one test channel: the colour stays in the other; invited back: the same colour. |
| Missed messages are pink too | custom-colours 9 (caught-up part) | 2P (G) | The host relogs; the partner sends; the tester looks at the caught-up line. |

**Suite: Right-click invites, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Where the invite shows: chat, target, other lists | context-invites 1, 3, 5 | 2P (G) | The partner says `/s hello` in the same house (its guided step); the tester right-clicks its name in chat, the character, the target bar, and any list they have. A: the offers and their order. |
| Where the invite shows: party and friends lists | context-invites 2, 4 | 2P (G) | With the partner in the party and friends list. |
| Where the invite shows: ChatTwo | context-invites 6 (group 1) | 2P (G) | The partner's `/s hello` in ChatTwo. |
| Inviting, and the greyed-out entries | context-invites 12-14, 16, 18 | 2P (G) | Two test channels the partner isn't in; the tester invites through the menu; the runner checks each line and the partner's invite arriving; the partner's runner accepts when asked; the block and unblock; again from ChatTwo. |

**Suite: Party finder links, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| B opens A's listing | chat-links 27 | 2P (G) | The host sends the link; the partner reports it arrived as a party finder link in the host's words; the partner clicks it (its guided step) in chat and in its window. |
| A listing that has ended | chat-links 28 | 2P (G) | The host ends the recruitment; the partner clicks; its runner checks no error in its log. |
| Another data centre | chat-links 29 | 2P (G) | Only if the partner is on another data centre (it says so in `hello`). |

**Suite: Chat history, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Off by default | chat-log 2 | 2P (A) | Only when the host's history was off and empty: lines with the partner, then no folder, no Show older messages. |
| B's lines are kept and shown later | chat-log 4, 6-7 (B's parts) | 2P (G) | Lines both ways, the partner leaves and rejoins, a rename; the host relogs; the tester clicks **Show older messages** and looks. A: the lines in the history. |
| Missed messages, and channels kept apart | chat-log 10-11 | 2P (A+G) | A: Disconnect, the partner sends two, Connect: once in the window; each test channel's window only its own lines; unread counts ignore older lines. G: the relog part. |
| Blocking hides old lines | chat-log 19 | 2P (A) | The block hides the partner's older lines from the window's lines; unblocked, back. |
| Text files: B's joins and missed messages | chat-log 29 | 2P (A) | The partner leaves and rejoins; Disconnect, two lines, Connect; the text file's lines checked word for word against the format. |

**Suite: Talking in a channel with ChatTwo, with a partner**

| Task | Steps | Mode | How |
| --- | --- | --- | --- |
| Short commands while ChatTwo is on Party | sticky-channel 30, 30b, 32b | 2P (G) | The partner as the whole party, next to the host. The tester types each line in ChatTwo as told; the partner's plugin reports where each landed (Party, Say, or LookingGlass); the runner checks its own side and notes each step's time for 30b. |

**Not covered, group 2:**

| Task | Steps | Why |
| --- | --- | --- |
| Not to strangers | local-chat 12-13 | Three players, one a stranger to both |
| A friend without LookingGlass (second part) | local-chat 14 | Three players (the first part is covered) |
| B resets their identity | local-chat 26 | Resetting an identity; never something a test does |
| No line when out of range, or from a stranger | local-chat 27 | Follows the reset; three players |
| An older plugin still talks | chat-links 18 | The partner on an older build has no self-test |
| An older plugin (party finder) | chat-links 31 | The same |

(The local chat "A friend without LookingGlass" task counts as covered: its
first part runs.)

### Group 3: owner only (27 tasks)

Not covered, all 27: they need the server (SSH, the journal, `LG --ban`,
settings and restarts), a test server, Discord, the operator web page or the
operator panel, and some flag and ban characters on purpose. A self-test must
never do that on the public server. The self-test helps with two: "Server
log keeps names out" and "What the journal says" need the times testers ran
local chat and device steps, which the report gives.

### Totals

| | Tasks | A | A+G | G | 2P (A) | 2P (G or A+G) | Not covered |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Group 1 | 54 | 13 | 11 | 20 | 1 | 4 | 5 |
| Group 2 | 40 | | | 1 | 13 | 21 | 5 |
| Group 3 | 27 | | | | | | 27 |
| All | 121 | 13 | 11 | 21 | 14 | 25 | 37 |

So: **13 fully automatic, 11 automatic with a guided step, 21 guided, 39
with a partner (14 judged automatically), 37 not covered.** Of the 94 tasks
for players, 84 are covered (10 aren't: a second install, three players, an
identity reset, an older build).

## Build order and size

Rough sizes in lines of code, tests separate.

| Step | What | Code | Tests |
| --- | --- | --- | --- |
| 1 | **Core runner**: `SelfTestRun` (the checks, their steps and results, the order, skips with why, stop and clean-up always running), `SelfTestPacer` (the table above; the stop rule; the hour's budget), `PrivateArea` (the rule table), `SelfTestReport` (the text), `SelfTestWords` (both modes), `SelfTestRunFile` (the run file, read and written atomically). All behind an `ISelfTestHost` interface, with a fake clock. | 1,000 | 900 |
| 2 | **Plugin seams**: the ChatOutput tap, the game chat watcher, the log tap (an `IPluginLog` wrapper), typing a line through the gate, read-only state on `StickyMode` and `ChannelWindows`, `SessionManager.SimulateNotice`, `ChatLog.ForgetChannelAsync`, `PrivateAreaReader`, the interrupted-run restore in the plugin's constructor. | 600 | 150 |
| 3 | **The window**: start and consent, the refusal and pauses, the run with "Needs you", the report, leftovers. `/lgdebug test` and the button. | 600 | |
| 4 | **One-player suites**: talking in a channel and local chat; windows and counts; colours; chat history; settings, invites, party finder; with ChatTwo. Each check is a small class listing its steps. | 1,400 | 300 |
| 5 | **Two players**: pairing, the protocol (parser, requests, caps, time-outs), the partner's runner, and the two-player suites. | 1,100 | 600 |
| 6 | **Docs**: `docs/testing/self-test-checklist.md` (checking the self-test itself in game), a line at the top of pending-tests.md on using it, and this plan folded into design.md. | | |

Steps 1 to 4 make a useful one-player self-test on their own; step 5 can
follow. About 4,700 lines of code and 2,000 of tests in all, mostly the
checks themselves.

**As built (steps 1 to 4, 2026-10-13):** `src/LookingGlass.Core/SelfTest`
(the runner, `PrivateArea`, `SelfTestPacer`, `SelfTestReport`,
`SelfTestWords`, `SelfTestRunFile`, `ISelfTestHost`, and `Suites`), the
plugin's `src/LookingGlass.Plugin/SelfTest` (the host, the log tap,
`PrivateAreaReader`, the settings, `SelfTestService`) and
`Ui/SelfTestWindow.cs`, `ChatLog.ForgetChannelAsync`, and the tests
(`SelfTest*Tests.cs`, `ChatLogForgetTests.cs`). The runner's design is ready
for step 5: the run file has the partner's place, PrivateAreaReader can
already leave a partner out of "someone near", and the protocol's requests
would be steps like any other. The two-player suites aren't listed on the start screen; it
says they come later.

**Step 5 as built (2026-10-13):** `src/LookingGlass.Core/SelfTest/Pairs`
(`SelfTestProtocol`, the parser and writer; `PartnerLink`, the host's side;
`PartnerSide`, the partner's runner), `Suites/PairingSuites.cs` (pairing, and
being a partner) and `Suites/Pairs` (the eight two-player suites, 39 checks,
and `PairSteps`), the plugin's `SelfTestHost.Pairs.cs` and a delivered-messages
tap in `SessionManager`, the service's `StartHost` and `StartPartner`, and the
window's **Two players...** screen; tests in `SelfTestProtocolTests`,
`SelfTestPairsHostTests`, `SelfTestPairsPartnerTests`,
`SelfTestPairsSuitesTests` and `SelfTestPacerTests`. Where it differs from the
plan above (each in design.md's Decisions):

- Protocol lines are their own action, paced with the test lines (one pace)
  but counted apart: at most 150 each side, besides the host's 100 and the
  partner's 80 test lines.
- The host may invite the partner 16 times (each leave's invite back) and
  look them up 12 times a run (once per ten minutes, as the session
  remembers a lookup), not 8 and 2.
- Suites are ticked before pairing (nothing happens before Start): pairing is
  the run's first check, the partner's facts show in the run's window, and a
  check needing what the pair hasn't got is skipped, saying why.
- Requests may name a test channel (`in=b`) for `send`, `leave` and
  `report check-code`; besides the list above, `send-away` (sent once the host
  shows offline), `report windows`, `report log` (counts only), `ask <step>`
  (one of nine fixed steps for the partner's player) and `invite-me` (the
  partner's one channel, `LG self-test K7Q2 p`).
- The partner's relog is a step it asks of its player; it answers once logged
  in again (its run stays in memory). A plugin reload on its side ends its
  run, cleaned up at its next start.
- Not run, as parts of covered tasks: the exact local chat distances and
  another zone (local-chat 10 to 11), a loading screen between private places
  (windows-only 19).
- After review: instead of a partner giving up after 15 minutes without a
  request, the host sends `alive` (never answered, at most 30 a run) every 5
  minutes while paired and quiet, from wherever the run waits (a gate, a guided
  step, a relog while still connected); the partner counts a host online in
  the test channel as alive, and gives up after 45 minutes offline, or an hour
  online without a single line. Its player is told what it waits for. The
  partner's player is asked at most 20 steps a run.

## How it is tested

**Unit tests** (`tests/LookingGlass.Tests`, Core only, as today):

- **The runner** with a fake host: checks run in order; a guided step waits
  and moves on when the fake reports the change; Skip, Stop and time-outs;
  a check that throws fails alone and the run goes on; **clean-up always
  runs**, in order, each step even when one before it fails; a run resumed
  from its file carries on at the right step; the private-area check is
  asked before every step that types, and a "no" pauses before it, never
  after.
- **The private-area rule**: every row of the table (inn, house, chambers,
  workshop, apartment, lobby by ID and by room 0, ward, yard, loading, the
  sheet and HousingManager disagreeing, an unreadable field), the settling
  time, and someone else nearby.
- **Pacing**: no sequence of the runner's requests goes past the table's
  pace, against a fake clock; the first refusal stops sending and nothing is
  retried; the hour's budget refuses a run that would go past it.
- **The protocol**: parsing and writing every request and answer; lines
  without the exact prefix, from someone else, in another channel, malformed,
  too long, with counts past the caps, or repeated, are ignored or end the
  run as the plan says; the partner never acts outside its list.
- **The run file and settings**: snapshot and restore round trip for every
  setting in the table; restore from a file left by an interrupted run;
  restoring twice changes nothing; a damaged file restores what it can and
  is set aside.
- **The report**: the fixed format; given fakes with character, world,
  channel names and marker text, **none of them appears** in the report or in
  the `[selftest]` log lines.
- **Words**: every `SelfTestWords` entry in both modes; no banned words in
  simple mode; lengths.
- **`ChatLog.ForgetChannelAsync`**: removes one channel's lines and text
  folder, and nothing of another channel's.

**In game**: `docs/testing/self-test-checklist.md`, written with step 3: the
gate in each kind of place (including the apartment lobby and a yard), a run
stopped mid-step, the game closed mid-run (settings back, channel gone), a
relog mid-run, a partner going silent, and a run with the server's limits in
view (the owner checks the journal for no refusals and no flag after a full
two-player run on the public server).

## Open decisions for the owner

1. **Where it lives.** Recommended: `/lgdebug test` and a **Self-test...**
   button in `/lgdebug`, opening its own window; no new `/lgtest` command.
2. **Which places count.** Recommended: inn rooms, inside an apartment, and
   inside a house on any floor, its private chambers, FC chambers and the
   company workshop (they are inside the estate, behind the same door);
   never the apartment lobby, a yard or a ward. In a house or apartment, a
   step that types also waits while anyone else is within 30 yalms.
3. **Local chat with two players needs the same house or apartment** (inn
   rooms are one player each). Recommended: allow visiting the other's
   house or apartment; the visitor counts as in a private place there.
4. **Public server.** Recommended: allowed, as paced above (no refusal, no
   flag, no block); the owner checks the journal after the first full run.
   Alternative: only on a server address the owner lists as a test server.
5. **The chat history when the tester has it on.** Recommended: the test's
   lines are removed at clean-up (a small new function), and the checks that
   would drop or delete the tester's own history run only when it was off and
   empty before. Alternative: skip the chat history suite unless it was off.
6. **Typing for the tester.** Recommended: the runner types its own test
   lines through the game's chat box path, after setting the game's chat
   channel to Say, and `/p` only when the party is just the two testers.
   Alternative: every typed line guided ("type this"), which is slower and
   covers about 8 fewer tasks automatically.
7. **The log tap** (a wrapper around the plugin's log, counting errors and
   checking no names or messages get in). Recommended: yes; it makes three
   tasks automatic (no errors, names out of the log, the party finder's log).
8. **The report.** Recommended: no names at all (the partner is "a
   partner"), times with the time zone, and the tester's optional notes as
   typed. Each tester copies their own; the host's covers the partner's
   answers.
9. **Results into pending-tests.md.** Recommended: the owner ticks tasks by
   hand from the reports, as now; the self-test never edits the list or sends
   anything anywhere by itself.
10. **One-player "lots of history" with simulated lines** (instead of the
    partner sending 250 lines, which would take 8 minutes at the pace above).
    Recommended: yes; it checks the same paging, and costs the server nothing.
