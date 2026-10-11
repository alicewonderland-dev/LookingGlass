# Operator tools: a plan for approval

Status: approved by the owner (2026-10-10). **Step 1 is built** (2026-10-10):
`OperatorActions` and `OperatorPolicy`, schema 12, the command line moved onto
them, `--audit`, `--operators` and `--operator-pin`; it is folded into
[design.md](design.md#operator-actions-one-layer-for-every-tool) and
[server.md](server.md#flags-and-bans). **Step 2 is built** (2026-10-10):
Discord alerts, folded into
[design.md](design.md#alerts-to-a-discord-channel) and
[server.md](server.md#alerts-to-discord). **Step 3 is built** (2026-10-10):
the operator web page, folded into
[design.md](design.md#the-operator-web-page) and
[server.md](server.md#the-operator-web-page). Step 4 (the plugin's panel)
isn't started (see
[Build order, size and tests](#build-order-size-and-tests)). The decisions themselves are kept
in [design.md](design.md) (under
[Planned features](design.md#planned-features) and
[Decisions](design.md#decisions)), which stays the source of truth; this
file holds what the owner needs to approve them: mockups, the security
analysis, the settings, the build order and the tests. It is folded into
design.md and server.md as each part is built, then deleted.

Banning today needs SSH and a command (`LG --ban ...`). The owner asked for
three things, designed together:

1. **A web page**, so a ban can be made without logging in to the game. Over
   Tailscale only: never on the public internet.
2. **An operator panel in the plugin**, for the owner's own characters, tied
   to their keys (key-pinned operators, decided 2026-10-10).
3. **Alerts to a Discord channel**, with the owner's answers to that plan's
   open questions.

All three sit on **one operator actions layer** in the server, which the
command line moves onto as well, so every tool follows the same rules.

## Contents

- [One layer for every tool](#one-layer-for-every-tool)
- [The web page](#the-web-page)
- [The plugin's operator panel](#the-plugins-operator-panel)
- [Discord alerts](#discord-alerts)
- [Security analysis](#security-analysis)
- [Settings](#settings)
- [Setting it up on the server](#setting-it-up-on-the-server)
- [What changes in server.md](#what-changes-in-servermd)
- [Build order, size and tests](#build-order-size-and-tests)
- [Decided (owner, 2026-10-10)](#decided-owner-2026-10-10)

## One layer for every tool

`OperatorActions`, a server service, does the four things every tool needs:
list the flags, list the bans (in force, and lifted or ended lately), ban,
and lift a ban; and, for the plugin's operators, lists them with their
pinned and current keys and pins or restores them (see
[Key-pinned operators](#key-pinned-operators)). The command line (`--bans`,
`--ban`, `--unban`, `--operators`, `--operator-pin`), the web page and the
plugin all call it, so the rules are written once:

- **Whom.** A character by its user ID (its Lodestone ID; one not registered
  yet can be banned so it can't register), a registered character by
  name@world, or an address or prefix within today's widths (IPv4 /16 to /32;
  IPv6 /32 to /64, an address alone standing for its /64).
- **Never the server's own or its proxy's address**, nor a prefix holding one,
  unless `--force` (on the web page, its tick box) says it is meant.
- **Days** 1 to 36,500, or until lifted; a **reason** of one line, at most 300
  characters, shown to the player.
- **What each tool may do** is one policy table (`OperatorPolicy`), tested on
  its own:

| | Command line | Web page | Plugin |
| --- | --- | --- | --- |
| See flags and bans | Yes, full addresses | Yes, full addresses | Yes, addresses shortened (as in Discord) |
| Ban or lift a character | Yes | Yes | Yes |
| Ban or lift an address or prefix | Yes | Yes | No |
| `--force` (the server's own or the proxy's address) | Yes | Yes (a tick box) | No |
| Ban a character listed as an operator | Only with `--force` | No | No |
| Bans an hour | No limit | 30 | 10 per operator character |
| Pin or restore an operator's keys | Yes, with the fingerprint typed | Yes, after a confirmation page | No |
| Say who did it | The Unix user (`SUDO_USER`) | The Tailscale login | The operator character |

- **Applied at once.** A ban from the web page or the plugin is made inside
  the running server, which then reads the bans and closes the connections
  the ban covers straight away, rather than within 30 seconds. The command
  line works as today (another process; picked up within
  `Abuse:BanCheckSeconds`).
- **Recorded.** Every ban and lift says which tool made it and who (schema 12
  adds `made_via`, `made_by`, `lifted_via` and `lifted_by` to `bans`), and
  every attempt, refused ones too, goes into an audit trail (a new
  `operator_log` table: when, which tool, who, from where, what, and the
  outcome), kept 365 days. `--bans` and the web page show who made each ban;
  the web page and a new `--audit` command show the trail.
- **The command line's words stay as they are**, with "made on the web page
  by alice@github" (or "in the plugin by Alice Wonderland@Lich", "on the
  command line by alice") added to each ban `--bans` lists.

Schema 12 also adds `made_announced` and `lifted_announced` to `bans` (so a
ban is posted to Discord once, whichever process made it, and a restart
neither repeats nor loses one; the upgrade marks existing bans as posted) and
a small `server_state` table (when the daily summary was last posted, and
whether the server last stopped cleanly), an `operator_pins` table (each
operator character's pinned keys) and `users.keys_registered_at` (when the
account's current keys were registered; unknown for keys registered before
the upgrade). An older server opens a schema 12 database and ignores what it
doesn't know (and has no operators: it doesn't know the capability).

## The web page

### How it is reached

The owner's decision: **Tailscale only**. The page never listens where the
public can reach it:

- **Its own listener**, separate from the public WebSocket endpoint: a small
  second web host inside the same server process, with only the page's
  routes. The public host has no admin routes at all, so nothing in front of
  it (Funnel, Caddy) can reach the page, however it is set up.
- **On a Unix socket** (`/run/lookingglass/admin.sock`, made by the service
  with the unit's `UMask=0077`, so only the `lookingglass` user and root can
  open it), on Linux. A loopback TCP port (`http://127.0.0.1:5181`) is
  accepted too, for Windows and Docker, with a warning at startup that any
  program on the machine could then pretend to be an allowed login (see
  [Threats and what stops them](#threats-and-what-stops-them)). Any other
  address is refused at start.
- **Reached through `tailscale serve`**, tailnet only, on an HTTPS port
  Funnel can't use (Funnel only ever serves ports 443, 8443 and 10000), such
  as 8444: `https://<machine>.<tailnet>.ts.net:8444/`. The server refuses to
  start with the page on 443, 8443 or 10000, so the page can't share the
  public server's Funnel port, where every handler would be public.
- **Only listed Tailscale accounts get in.** `tailscale serve` removes any
  `Tailscale-User-Login`, `Tailscale-User-Name` and
  `Tailscale-User-Profile-Pic` headers a request arrives with, and sets them
  from the tailnet's own record of the device the request came from (its
  WireGuard key, which can't be faked). It sets none for a tagged device, or
  for a request through Funnel (which it marks `Tailscale-Funnel-Request: ?1`
  instead). **A request from the machine itself** (a `curl` to the page's
  ts.net address, run by any user there) comes from the machine's own
  tailnet address, which Tailscale names by the machine's own login: on a
  server signed in as the owner and not tagged, the owner's (corrected by the
  review, 2026-10-11; this plan first said it gets none). So the page refuses
  any request whose `X-Forwarded-For` (which `tailscale serve` sets to the
  device's address) is one of the machine's own addresses, or that has none.
  Tagging the server's node is stronger still: a tagged node's requests carry
  no login at all, and the owner reaches the page from their own untagged
  devices. The page lets in a
  request only when `Tailscale-User-Login` is exactly one of
  `Admin:TailscaleLogins` (ignoring case), no `Tailscale-Funnel-Request` is
  present, it came from another device, and the address it was asked for
  (`X-Forwarded-Host`, which `tailscale serve` also sets) is the one in
  `Admin:Url`. Everything else gets **403**. (Checked against Tailscale's documentation and its source,
  `ipn/ipnlocal/serve.go`, 2026-10-10; Unix socket targets need Tailscale
  1.94 or later.)

### What it shows and does

- A line of counts: flagged, bans in force, blocked automatically, players
  online now.
- **Ban someone**: whom (name@world, Lodestone ID, or address or prefix),
  days (empty: until lifted), reason, and a tick box for `--force`.
  **Continue...** leads to a confirmation page; only its button bans.
- **Flagged in the last 24 hours**, with full addresses (it is the operator's
  own machine), the Lodestone link for characters, and **Ban...** on each
  (filling the form in, with 7 days).
- **Bans in force**, with who made each and with which tool, and **Lift...**
  on each (a confirmation page too).
- **Lifted or ended in the last 90 days.**
- **Operators** (when `Operators:Characters` lists any): each character with
  its pinned fingerprint and its current one, and **Pin...** (not pinned
  yet) or **Restore operator status...** (the keys changed) on each that
  isn't an operator now (see [Key-pinned operators](#key-pinned-operators)).
- **What operators did**: the last 50 entries of the audit trail.
- **Send a test alert** (when alerts are on), which posts one message to the
  Discord channel.
- Not on the page at first: `--allow-key-login`, settings, anything about
  channels or messages.

It is plain HTML and one small stylesheet, both inside the server: no
JavaScript at all, nothing from another site, no cookies. It works on a
phone browser (one column below about 700 pixels wide; big buttons) and
follows the device's light or dark setting. Times are in the owner's time
zone, `Admin:TimeZone` (an IANA name such as `Europe/London`; UTC by
default; the server won't start with a name it doesn't know), shown as
"14:02 BST" with the zone's abbreviation, so a time copied from the page
is never ambiguous; `--bans` stays in UTC. It doesn't refresh by itself: a
**Refresh** link reloads it.

### Mockup: desktop

```text
+------------------------------------------------------------------------------------------------------+
| LookingGlass operator                              Server 0.2.16, up 3 days   Signed in: alice@github |
+------------------------------------------------------------------------------------------------------+
|  2 flagged    3 bans in force    1 blocked automatically    37 players online           [Refresh]    |
|                                                                                                      |
|  Ban someone                                                                                         |
|    Whom    [ name@world, Lodestone ID, or address or prefix              ]                           |
|    For     [    ] days    Leave empty to ban until you lift it.                                      |
|    Reason  [                                                             ]                           |
|            The player is shown this. One line, at most 300 characters.                               |
|    [ ] This is the server's own or its proxy's address, and I mean it                                |
|                                                                        [ Continue... ]               |
|                                                                                                      |
|  Flagged in the last 24 hours (2)                                                                    |
|    Who                          Why                                    Limits                Last    |
|    Bob Hatter@Lich (31337)      refused in 30 of the last 60 minutes   InviteBurstPerPair,   11:55   |
|      Lodestone                                                         LookupBurst          [Ban...] |
|    address 203.0.113.9          refused by 4 different limits          ConnectionsPerIp,     11:40   |
|                                 within 10 minutes                      LookupBurst, +2      [Ban...] |
|                                                                                                      |
|  Bans in force (3)                                                                                   |
|    Who                          Until                      Made                Reason                |
|    Carol Queen@Odin (4242)      2026-10-17 12:00 (7 days)  in the plugin by    Spamming invites      |
|      Lodestone                                             Alice Wonderland@Lich              [Lift...] |
|    address 203.0.113.0/24       2026-10-11 12:05 (1 day)   on the web page by  Flooding              |
|                                                            alice@github                    [Lift...] |
|    address 198.51.100.7         13:16 (15 minutes)         automatically                   [Lift...] |
|                                                                                                      |
|  Lifted or ended in the last 90 days (1)                                                             |
|    Dan Dodo@Ragnarok (777)      2026-10-01 to 2026-10-08, ended.  On the command line by alice.      |
|                                 Reason: Spamming invites                                             |
|                                                                                                      |
|  Operators (3)                                                                                       |
|    Alice Wonderland@Lich (31337)  Operator. Keys 48213 90551 17342 66018 30927, pinned 2026-10-10     |
|                                   on the web page by alice@github.                                   |
|    Alice Rabbit@Odin (55555)      NOT an operator now: its keys changed 2026-10-10 14:02 BST.        |
|                                   Pinned:  11873 40229 95310 72646 08154                             |
|                                   Now:     63390 21187 50462 99805 41733  [Restore operator status...] |
|    Alice Hearts@Lich (77777)      Not pinned yet. Keys now 20518 77394 61025 38841 90066    [Pin...]  |
|                                                                                                      |
|  What operators did (last 50)                                                                        |
|    2026-10-10 14:20  web page      alice@github from 100.101.102.103   banned 203.0.113.0/24, 1 day   |
|    2026-10-10 13:00  plugin        Alice Wonderland@Lich               banned Carol Queen@Odin, 7 days|
|    2026-10-09 23:10  command line  alice                               lifted the ban on Dan Dodo@... |
|    2026-10-09 23:09  web page      alice@github from 100.101.102.103   refused: 127.0.0.1 is this    |
|                                                                        server's own address           |
|                                                                                                      |
|  [ Send a test alert ]                                     Times are Europe/London (BST).           |
+------------------------------------------------------------------------------------------------------+
```

### Mockup: phone

```text
+--------------------------------+
| LookingGlass operator          |
| alice@github        [Refresh]  |
|--------------------------------|
| 2 flagged · 3 bans · 1 blocked |
| 37 players online              |
|--------------------------------|
| Ban someone                    |
| Whom                           |
| [                            ] |
| For (days, empty: until lifted)|
| [    ]                         |
| Reason (the player sees it)    |
| [                            ] |
| [ ] Server's own or proxy's    |
|     address, and I mean it     |
| [        Continue...         ] |
|--------------------------------|
| Flagged (2)                    |
| +----------------------------+ |
| | Bob Hatter@Lich (31337)    | |
| | Refused in 30 of the last  | |
| | 60 minutes                 | |
| | InviteBurstPerPair,        | |
| | LookupBurst                | |
| | Last 12:55 BST · Lodestone | |
| | [         Ban...         ] | |
| +----------------------------+ |
| +----------------------------+ |
| | address 203.0.113.9        | |
| | 4 limits within 10 minutes | |
| | [         Ban...         ] | |
| +----------------------------+ |
|--------------------------------|
| Bans in force (3)              |
| +----------------------------+ |
| | Carol Queen@Odin (4242)    | |
| | Until 2026-10-17 13:00 BST | |
| | Plugin: Alice Wonderland   | |
| | "Spamming invites"         | |
| | [        Lift...         ] | |
| +----------------------------+ |
|  ...                           |
|--------------------------------|
| Operators (3) · 1 to restore ▸ |
| Lifted or ended (1)      ▸     |
| What operators did       ▸     |
+--------------------------------+
```

(The last three open with HTML's own `<details>`, which needs no JavaScript;
**Operators** opens by itself while one needs restoring.)

### Mockup: confirming

```text
+--------------------------------------------------------------+
| Ban Bob Hatter@Lich?                                         |
|                                                              |
| Character: Bob Hatter@Lich, Lodestone ID 31337 (Lodestone)   |
| For: 7 days, until 2026-10-17 15:02 BST                      |
| Reason, which the player is shown: Spamming invites          |
|                                                              |
| They can't sign in, or register this character again with    |
| any keys, until the ban ends. Their connection closes now.   |
| Their places in channels stay; channel admins aren't told.   |
|                                                              |
|                  [ Cancel ]   [ Ban Bob Hatter@Lich ]        |
+--------------------------------------------------------------+
```

An address ban's page says "Everyone connecting from 203.0.113.0/24 (256
addresses) is shut out, including people who share it." A ban refused by the
rules comes back to the form with why, in the command line's words ("127.0.0.1
is this server's own address: when the proxy's forwarded address is missing,
that is every player. Nothing was banned."). Lifting: "Lift the ban on Carol
Queen@Odin? Made 2026-10-10 13:00 BST in the plugin by Alice
Wonderland@Lich, until 2026-10-17. Reason: Spamming invites. [Cancel] [Lift
the ban]". Afterwards the main page shows "Banned Bob Hatter@Lich for 7
days." or "Lifted the ban on Carol Queen@Odin." at the top.

The refusal page, for anyone not let in: "**Not allowed.** This page is only
for this server's operators, reached through Tailscale from an account they
listed." Nothing more (not which check failed; the server's log says that,
at most once a minute).

### Mockup: restoring operator status

```text
+----------------------------------------------------------------------+
| Restore operator status for Alice Rabbit@Odin?                       |
|                                                                      |
| Its keys changed on 2026-10-10 at 14:02 BST: the character was       |
| registered again through the Lodestone (a new computer, "Reset my    |
| identity", or recovering it). Since then this server hasn't treated  |
| it as an operator.                                                   |
|                                                                      |
|   Pinned keys (key 2):   11873 40229 95310 72646 08154               |
|   Keys now   (key 3):    63390 21187 50462 99805 41733               |
|                                                                      |
| Before you confirm: on your own computer, open LookingGlass's        |
| Settings with Advanced mode on, and check that the fingerprint under |
| "Your identity" is exactly the "Keys now" one above.                 |
|                                                                      |
| If it isn't, or you didn't register this character again, don't     |
| restore: someone else did, which only needs control of its Lodestone |
| page (its Square Enix account). Secure that account, register the    |
| character again from your own computer, then come back here.         |
|                                                                      |
|            [ Cancel ]   [ Restore operator status ]                  |
+----------------------------------------------------------------------+
```

**Pin...**, for a character listed but never pinned, is the same page with
"Pin operator keys for Alice Hearts@Lich?", only "Keys now", and the same
advice to compare. Afterwards the main page shows "Alice Rabbit@Odin is an
operator again, with keys 63390 21187 50462 99805 41733. Its plugin sees the
Operator section from its next login."

### Forms and CSRF

The identity headers come with every request from an allowed device, as a
cookie would, so a page on another site that the owner happens to open could
send a form to the page and the headers would come with it. So:

- **Nothing changes on a GET.**
- **Every form carries a token** the page made: an HMAC, under a key the
  server makes at each start and never stores, of the Tailscale login, the
  time, and, on a confirmation page, the exact action (ban or lift, whom,
  days, the reason's hash; for a pin or restore, the character and the full
  hash of the keys the page showed, so keys that change again before the
  click aren't pinned unseen). A confirmation token works for that action only,
  for 30 minutes, once. Another site can't read the page, so it can't get
  one. After a restart old forms say "This page is out of date (the server
  restarted). Check and confirm again."
- **`Origin` must be exactly the page's own** (`Admin:Url`'s; `null` is
  refused), and a `Sec-Fetch-Site` that is there must be `same-origin`; only
  without `Origin` may `Sec-Fetch-Site: same-origin` alone stand for it; a
  POST with neither is refused.
- **Headers on every answer:** a Content Security Policy that allows only the
  page's own stylesheet and forms (`default-src 'none'; style-src 'self';
  img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri
  'none'`), so no script runs and no other site can frame it;
  `X-Content-Type-Options: nosniff`; `Referrer-Policy: same-origin` (a
  Lodestone link doesn't tell the Lodestone the page's address; first
  `no-referrer`, changed by the review, 2026-10-11, since `no-referrer` makes
  browsers send `Origin: null` with the page's own forms);
  `Cache-Control: no-store` (full addresses aren't kept in a phone's cache).
- **Everything shown is HTML-encoded**: names, worlds, reasons, limits.

## The plugin's operator panel

### Who sees it

- **Operators are characters with pinned keys.** `Operators:Characters`
  lists, by user ID (Lodestone ID), the characters that may be operators.
  IDs, not names, since a character can be renamed; the server logs the names
  it found at startup ("Operators: Alice Wonderland@Lich (31337), keys
  pinned"). A listed character is an operator only while its account's
  current keys are the ones pinned for it (see
  [Key-pinned operators](#key-pinned-operators)).
- **Only the operator learns they are one.** The server says so in its answer
  to the operator's own login (`AuthenticateOk.operator_status`), only when
  both sides agreed to the capability `operator.v1`. Nobody else is told who
  the operators are, or that the feature exists for them.
- **The plugin shows an Operator section at the end of Settings**, and a
  **Ban from server...** item in the right-click menus, only while connected
  and logged in as an operator (owner, 2026-10-10: a section in Settings,
  not a window of its own).

### Key-pinned operators

The owner's decision (2026-10-10). Registering a character needs only
control of its Lodestone profile. Someone who took over the owner's Square
Enix account could register the operator character again, with keys of their
own; with operators listed by Lodestone ID alone, they would get the
plugin's operator panel. So an operator is a character **and its keys**:

- **The pin is the full key hash** the fingerprint is cut from
  (`IdentityKeys.KeyHash`: SHA-256, under the context
  `lookingglass/fingerprint/v1`, of the signing and the agreement public
  keys; membership log entries name their signer's keys by it). It is shown
  everywhere as the 25-digit fingerprint (`IdentityKeys.FingerprintOf`, five
  groups of five, the code players compare, and the one under **Your
  identity** in the plugin's Settings with Advanced mode on). The server
  compares the full hash, never the 25 digits.
- **Where pins live: the database, listed by the configuration.**
  `Operators:Characters` (configuration) says which characters may ever be
  operators; only the server's settings, and a restart, add one. The
  `operator_pins` table holds each one's pinned key hash, when, by which tool
  and by whom, and so can be changed by the web page and the command line.
  So the web page can restore an operator's keys, but can't make anyone an
  operator who isn't listed. A pin for a character no longer listed is
  deleted at startup (and logged).
- **Never pinned automatically.** A listed character is pinned only by the
  operator, on the web page (**Pin...**, after a confirmation page showing
  the fingerprint to compare) or the command line (`--operator-pin <whom>
  --fingerprint "48213 90551 17342 66018 30927"`, which pins only if the
  fingerprint typed is the current keys'). Not on its first login: that
  would trust whatever keys the character has when it is listed, which is
  exactly the gap if its Square Enix account was taken first. It costs one
  step at setup.
- **A key change drops it.** Registering again (a new computer, "Reset my
  identity", recovering the character) gives the account new keys, which
  don't match the pin: from that moment the character is not an operator,
  on its next login and on any request already in flight (every operator
  request checks the pin against the account's current keys). Nothing
  re-pins by itself.
- **Restoring** is the web page's **Restore operator status...** (the owner's
  request), or `--operator-pin` again on the command line: both show or need
  the new fingerprint, and the web page's confirmation shows the old and new
  fingerprints, when the keys changed, and asks to compare with the plugin's
  Settings first. Every pin and restore is in the audit trail and posted to
  Discord, with the ping (see [Discord alerts](#discord-alerts)): it is rare,
  and it is exactly what someone would do with both a stolen Square Enix
  account and a stolen tailnet device.
- **The operator is told, and nobody else.** A listed character that logs in
  with keys that don't match its pin (or before any pin) is told so in its
  own login's answer, and the plugin says it once in chat and in place of
  the Operator section (see the mockup below). This tells whoever logs in as
  that character that it is a listed operator; with new keys, that is
  someone who has the Lodestone profile already, and it gains them nothing.
- **Discord hears of it at once**: a listed character logging in with keys
  that don't match its pin is posted as urgent, with the ping, once for each
  new set of keys (the last one posted is kept with the pin). It is the sign
  of a taken Square Enix account, or the owner's own new computer.

The command line also gets `--operators`, listing each listed character, its
pinned and current fingerprints, and whether it is an operator now.

### What the operator's keys can do, and why less than the web page

An operator's character keys become worth more. Pinning means registering
the character again gets nobody the panel, but a copy of the secrets file
holds the pinned keys themselves, and would. The owner already gets told of
a new login (device notices), but it is still worth keeping the panel
narrower than the page:

- **Characters only** (owner, 2026-10-10). The plugin bans and lifts bans on
  characters (any tool's). Address bans, and lifting them or automatic
  blocks, are on the web page and the command line: an address ban shuts out
  everyone behind it, and needs the full address, which the plugin never
  gets; an automatic block ends by itself in 15 minutes anyway.
- **No pinning or restoring** from the plugin: that is what a stolen key or
  a re-registered character would want, so it is on the web page and the
  command line only.
- **Addresses shortened**, as in Discord: a stolen key mustn't hand out
  players' addresses.
- **No `--force`, no banning an operator character**, so a stolen key can't
  lock the owner's own characters out.
- **10 bans an hour per operator character**; past it the server says to use
  the web page.
- **Signed with the identity key.** Reading the lists needs the operator's
  login; each ban and lift must also be signed by the character's current
  identity key, as "Sign out everywhere else" is: a copy of the login alone
  can't ban. The signature covers the action, whom, the days, the reason's
  hash, the user ID, the hash of this connection's login, the server's
  address (so it can't be relayed to another server) and a nonce from the
  server's last answer on this connection (single use, a few minutes; so it
  can't be replayed).
- **Every plugin action is posted to Discord**, naming the character, so a
  ban the owner didn't make is seen at once.

Lists that are read in the plugin are the same trust as the channels a login
reads already, so they don't need a signature.

### Protocol

Additive, behind the capability `operator.v1`, so **no protocol version
change** (still 3), as with `devices.v1`:

| Addition | What it is |
| --- | --- |
| Capability `operator.v1` | Agreed in `Hello`/`Welcome` |
| `AuthenticateOk.operator_status` (enum, field 6) | Only with the capability: not listed (the default, and all anyone else ever sees), an operator, listed but its keys don't match the pin (with the pinned fingerprint's hash, so the plugin can show both), or listed but not pinned yet |
| `GetOperatorView` (`ClientFrame` 38) | Asks for the lists; answered with `OperatorView` |
| `OperatorView` (`Response` 25) | Flags, bans in force, lifted or ended lately (characters with names; addresses shortened), the web page's address if set, bans left this hour, and a nonce for one ban or lift |
| `OperatorBan` (`ClientFrame` 39) | A user ID, days (0: until lifted), the reason, the server URL, the nonce, the signature; answered with a fresh `OperatorView` |
| `OperatorUnban` (`ClientFrame` 40) | A user ID, the server URL, the nonce, the signature; answered likewise |
| `OperatorActionProof` (core crypto) | What is signed, with its own context string |

A name@world is turned into a user ID first with the existing `LookupUser`,
so the dialog shows exactly which character the ban is for.

The server checks, in order: logged in; the capability agreed; the character
is listed and its account's current keys match its pin (otherwise
`FORBIDDEN`, "Only this server's operators can do that.", counted towards
flagging like any refusal); the signature, by the
account's current key; the nonce; then the policy (not an operator, not
itself, within the hour's bans). Reading the lists has its own per-user
limit (20 at once, then 1 every 3 seconds).

**Older plugins** never send these and are never told they are operators.
**Older servers** don't agree to `operator.v1`, so the plugin shows no
Operator section and no menu item. A server that falsely tells a plugin it is
an operator gains nothing: the plugin's signatures are bound to that
server's address.

### Mockup: the Operator section (Settings)

```text
 Operator (?)
   2 flagged · 3 bans in force · 1 blocked automatically          [Refresh]

   Flagged
     Bob Hatter@Lich        refused in 30 of the last 60 minutes   2 hours ago   [Ban...]
     address 203.0.113.x    4 limits within 10 minutes             20 min ago
   Bans in force
     Carol Queen@Odin       until Oct 17 (7 days) · "Spamming invites"            [Lift...]
                            made in the plugin by you
     user 4242              until lifted · made on the web page                   [Lift...]
     address 198.51.100.x   blocked automatically, 9 minutes left
   Lifted or ended lately ▸

   Ban someone   [ name@world or Lodestone ID          ]  [Ban...]

   Addresses are shortened here. Address bans, and lifting them, are on the
   web page.  [Copy the web page's address]
```

The "?" says: "You see this because the server lists this character, with
its keys, as an operator. Bans you make here are on the character, and are posted to the
alerts channel."

Each list shows at most 10 lines, newest first, with "and 4 more (see the
web page)". The section asks for the lists when Settings opens, after each
action, and on **Refresh**; nothing in the background. Times are the
player's local time, as elsewhere in the plugin.

### Mockup: when the keys no longer match

Once per login, a line in chat, in LookingGlass's warning colour:

```text
Simple mode:
  [LookingGlass] You set up LookingGlass again on this character, so this server no longer treats you
  as an operator. Restore it on the server's operator web page.

Advanced mode:
  [LookingGlass] Your identity keys changed (now 63390 21187 50462 99805 41733; the server's operator
  pin is 11873 40229 95310 72646 08154), so this server no longer treats you as an operator. Compare
  the fingerprint, then restore operator status on the web page.
```

And in place of the Operator section:

```text
 Operator (?)
   This server no longer treats you as an operator: you set up LookingGlass
   again on this character (new computer, or a reset).
   Your keys now:  63390 21187 50462 99805 41733
   To restore it, open the web page and check it shows these same numbers.
   [Copy the web page's address]
```

The "?" says: "Operators are tied to their keys, so someone who registers
your character again elsewhere doesn't become one. Only the web page can
restore it." A character listed but not pinned yet sees "This server's
operator hasn't confirmed your keys yet. Your keys: ... Pin them on the web
page." The fingerprint is shown in simple mode too here: the owner compares
it with the web page's.

### Mockup: right-click menu

```text
  Bob Hatter
  ─────────────────────
  Send Tell
  Invite to Party
  ...
  [L] Invite to LookingGlass  ▸
  [L] Ban from server...
```

Shown for any player with a home world (the same rules as the invite item),
whether or not there is a channel to invite them to, but not for the
player's own character. In ChatTwo it is in the **Integrations** submenu
beside the invite item. Greyed out, with why, for a character listed as an
operator ("an operator"). It opens the dialog.

### Mockup: the "Ban from server" dialog

```text
+-- Ban from server --------------------------------------------+
|  Bob Hatter@Lich                                              |
|  Flagged 2 hours ago: refused in 30 of the last 60 minutes    |
|  (InviteBurstPerPair, LookupBurst).                           |
|                                                               |
|  How long   ( ) 1 day   (•) 7 days   ( ) 30 days              |
|             ( ) Until lifted   ( ) [   ] days                 |
|                                                               |
|  Reason     [ Spamming invites                            ]   |
|             The player is shown this.               16 / 300  |
|                                                               |
|  They can't sign in, or register this character again, until  |
|  the ban ends. Their places in channels stay.                 |
|                                                               |
|                          [ Cancel ]   [ Ban Bob Hatter@Lich ] |
+---------------------------------------------------------------+
```

- Already banned: "Banned until Oct 17 (7 days), made on the web page.
  Reason: Spamming invites." with **Replace the ban** and **Lift the ban**.
- Not registered on this server: "Bob Hatter@Lich isn't registered on this
  server, so there's nothing to ban here. To stop the character registering,
  ban its Lodestone ID on the web page." (The plugin doesn't ask the
  Lodestone.)
- Afterwards a line in chat, in LookingGlass blue: "Banned Bob Hatter@Lich
  for 7 days." or "Couldn't ban Bob Hatter@Lich: " and why, in the mode's
  words.

The words go in the core library (`OperatorWords`), tested as the Settings
words are: both modes' words, the "?" at most 180 characters and three
sentences, plain in simple mode.

## Discord alerts

The plan in design.md (a webhook, a bounded queue on its own task, batching,
`Retry-After`, escaping, nothing anyone said) stands. The owner's answers:

- **How much to post:** the character as name@world, with its user ID and its
  Lodestone link; **addresses shortened**. Full addresses stay on the server,
  in `--bans` and on the web page.
- **Shortening, precisely:** an IPv4 address shows its first three parts,
  `203.0.113.x` (its /24); a prefix wider than a /24 shows as it is
  (`203.0.0.0/16`). An IPv6 address or prefix shows its first 40 bits as a
  /40, `2001:db8:ab00::/40`; a prefix wider than a /40 shows as it is.
  (The owner chose the /40, 2026-10-10, over the /48 in the planning brief,
  which came from the AI's brief, not the owner: a /48 is often one
  customer's whole allocation, so it points at one household as a full
  address does; a /40 holds 256 of those, much as a /24 holds 256 IPv4
  addresses.) The server's own and the proxy's address (in the proxy
  warning) shows in full: it is the server's.
- **Pings:** an optional role (`Alerts:PingRoleId`) is pinged for **urgent
  alerts only**: an address blocked automatically, the proxy warning, a
  request to the web page through Funnel, and, with key-pinned operators, an
  operator character logging in with keys that don't match its pin, and an
  operator's keys pinned or restored (rare, and worth seeing at once: a
  restore is what someone with both a taken Square Enix account and a
  stolen tailnet device would do). Flags and bans never ping.
  `allowed_mentions` lists that role alone on an urgent alert, and nothing at
  all on any other, so no name or reason can ping anyone.
- **New alerts:** the server starting (and whether it last stopped cleanly),
  and a daily summary, with a quiet-day message so the owner knows alerts
  still work.
- **Pointers:** each alert links the web page (`Admin:Url`, if set) as well as
  giving the command, and a ban or lift says which tool made it.
- **Who:** a ban or lift names its tool; one from the plugin also names the
  operator character (to spot a stolen key). The Tailscale login and the Unix
  user stay on the web page and in `--bans`, out of Discord.
- **Times** use Discord's own time stamps (`<t:1760702400:f>`), which each
  reader's Discord shows in their own time zone.
- **Link previews are off** (the message's suppress-embeds flag), so Discord
  fetches neither the Lodestone nor the web page.
- **Batched:** alerts within a minute go as one message (urgent ones at
  once); a message past Discord's 2,000 characters ends "and 12 more: see the
  web page".
- **The test:** the restart alert is the test that alerts work, and the web
  page's **Send a test alert** posts one at will. The `--alert-test` command
  in the earlier plan is dropped: the webhook is a secret only the service
  reads (see [Settings](#settings)), so the `LG` helper can't post.

### Mockups: one of each

In Discord's markdown; `<t:...>` shows as a local time.

**A flag:**

```text
**Flagged:** Bob Hatter@Lich (user 31337): refused by limits in 30 of the last 60 minutes, by InviteBurstPerPair, LookupBurst.
Lodestone: https://na.finalfantasyxiv.com/lodestone/character/31337/
Look: https://<machine>.<tailnet>.ts.net:8444/ or `LG --bans`
Ban: `LG --ban 31337 --days 7 --reason "..."`
The flag ends by itself 24 hours after the last refusal. Nothing was banned.
```

```text
**Flagged:** address 203.0.113.x (shortened): refused by 4 different limits within 10 minutes (ConnectionsPerIp, LookupBurst, RegistrationsPerHourPerIp, KeyLoginsPerHourPerIp).
It may be shared (a household, a mobile network). Full address on the web page: https://<machine>.<tailnet>.ts.net:8444/ or `LG --bans`
```

**An automatic block, with the ping:**

```text
@LookingGlass ops **Urgent: blocked automatically:** address 198.51.100.x (shortened), refused 1,000 times within 60 minutes. Blocked for 15 minutes, until <t:1760101000:t>.
Look: https://<machine>.<tailnet>.ts.net:8444/ (ban it longer, or lift the block)
```

**An operator character with other keys, with the ping:**

```text
@LookingGlass ops **Urgent: an operator character signed in with other keys:** Alice Rabbit@Odin (user 55555), keys 63390 21187 50462 99805 41733, not the pinned 11873 40229 95310 72646 08154. It registered again through the Lodestone at <t:1760101320:t>, and this server no longer treats it as an operator.
If it was you (a new computer, "Reset my identity"), compare the fingerprint in the plugin's Settings, then restore it: https://<machine>.<tailnet>.ts.net:8444/
If not, someone has that character's Lodestone page, so its Square Enix account: secure the account first. Nothing they do gives them operator powers.
```

**Operator status restored (or first pinned), with the ping:**

```text
@LookingGlass ops **Operator status restored** on the web page: Alice Rabbit@Odin (user 55555), now pinned to keys 63390 21187 50462 99805 41733 (was 11873 40229 95310 72646 08154).
If you didn't do this, unlist the character in the server's settings and restart, then check the web page's audit trail.
```

**A ban made:**

```text
**Ban made** on the web page: Bob Hatter@Lich (user 31337), for 7 days, until <t:1760702400:f>. Reason, which the player is shown: Spamming invites
Lodestone: https://na.finalfantasyxiv.com/lodestone/character/31337/
Lift: https://<machine>.<tailnet>.ts.net:8444/ or `LG --unban 31337`
```

```text
**Ban made** in the plugin by Alice Wonderland@Lich: Carol Queen@Odin (user 4242), until lifted. No reason given.
If you didn't make this, lift it on the web page and check that character's computers.
```

**A ban lifted:**

```text
**Ban lifted** on the command line: address 203.0.113.0/24 (shortened to 203.0.113.x). It had been made on the web page, 2026-10-10, for 1 day.
```

**The proxy warning, with the ping:**

```text
@LookingGlass ops **Urgent: every player seems to come from 127.0.0.1.** The proxy isn't passing the client's address on (X-Forwarded-For), so per-address limits apply to everyone together, and no address can be flagged or blocked. Fix the proxy rather than banning anything: see "Behind a reverse proxy" in docs/server.md.
```

**A request through Funnel, with the ping:**

```text
@LookingGlass ops **Urgent: the web page was asked for through Tailscale Funnel**, from the public internet. It was refused, but Funnel must never lead to the page. On the server, `tailscale funnel status` shows which public port does; turn that one off (`sudo tailscale funnel --https=<port> off`), and check the public server still answers.
```

**A restart:**

```text
**Server started:** LookingGlass 0.2.16, in Production. It stopped cleanly at <t:1760097300:t>.
```

```text
**Server started:** LookingGlass 0.2.16, in Production. It didn't stop cleanly: it had been running since <t:1759900000:f> (a crash, the machine losing power, or the process being killed). See `journalctl -u lookingglass`.
```

**A daily summary:**

```text
**Daily summary**, the 24 hours to <t:1760086800:f>
Flagged: 3 (2 characters, 1 address). Blocked automatically: 1.
Bans made: 2 (1 on the web page, 1 in the plugin). Lifted: 1. Ended: 0. In force now: 4.
Players: at most 37 online at once (at <t:1760040000:t>); 3 characters registered for the first time.
Server: 0.2.16, up 6 days; database 212 MB. Alerts not delivered: none.
Web page: https://<machine>.<tailnet>.ts.net:8444/
```

**A quiet day:**

```text
**Daily summary**, the 24 hours to <t:1760086800:f>: a quiet day. Nothing flagged, blocked, banned or lifted. In force now: 4 bans. At most 31 online at once; 1 character registered for the first time. Alerts are working.
```

The daily summary's extras are only what the server knows for next to
nothing: the most players online at once (one number the connection registry
keeps as players log in; since the restart, if it restarted that day, and
says so), characters registered for the first time (a count of `users` by
when they were first registered), the uptime, the database file's size, and
how many alerts couldn't be posted (a counter). It is posted once a day at
`Alerts:DailySummaryHourUtc`; the time it was last posted is kept, so a
restart neither skips nor repeats one.

## Security analysis

### Threats and what stops them

**A taken Square Enix account** (so control of an operator character's
Lodestone page). Whoever has it can register the character again with keys
of their own, as anyone can with their own character. Those keys don't match
the pin, so **they get nothing operator-wise**: no panel, no menu item,
every operator request refused. Discord gets an urgent alert, with the ping,
at their first login. (They do get what registering again gives anyone: the
character's places in channels move to their keys and the members are told,
as with any recovery; that is outside these tools.) To get the panel they
would also need the web page or the command line to restore the pin.

**A copy of an operator character's keys** (its secrets file, on a
compromised PC). The pin doesn't help here: these are the pinned keys. The
holder can see the lists (addresses shortened) and ban or lift bans on
characters, 10 bans an hour. They can't ban addresses, use `--force`, ban
an operator character, pin or restore keys, or see full addresses. The owner
is told of the new login (device notices) and of every plugin action
(Discord, naming the character). Undo: lift the bans on the web page, and
"Reset my identity" in the plugin (or sign out everywhere else): the reset
gives the character new keys, which drops the pin by itself, so the copied
keys are worth nothing; then restore operator status on the web page for the
new keys. Taking the character out of `Operators:Characters` and restarting
works too.

**A copy of an operator's login alone** (the device token, without the key):
the lists, nothing more; bans and lifts need the key's signature.

**A compromised tailnet device of an allowed account** (a stolen, unlocked
phone, say). The holder has the web page's powers: bans on characters and
addresses, `--force`, lifting, and pinning or restoring an operator's keys.
A restore only ever pins the account's current keys, which are the owner's
own unless the character's Square Enix account was taken too; that pair
together would give the plugin's panel, and both halves raise urgent alerts
(the login with other keys, then the restore). Limits on the damage: 30 bans an hour; every
ban is posted to Discord and recorded with the login and the device's
tailnet address; every ban can be lifted (on the command line too); the page
can't change settings, delete anything, or show anything said (the server
never has it). Tailscale's own controls do the rest, and are the owner's:
removing the device or expiring its key in the admin console, multi-factor
sign-in at the identity provider, and optionally an access rule letting only
named devices reach port 8444. Keep `Admin:TailscaleLogins` to the owner's
own account.

**Forged identity headers.**

- *Through the public proxy* (Caddy passes any header it is sent): the
  public host has no admin routes, so there is nothing to forge them to.
- *From the tailnet, around `tailscale serve`*: the page doesn't listen on
  the tailnet's interface at all, only on a Unix socket (or loopback).
- *From another program on the server*: the Unix socket can be opened only
  by the `lookingglass` user and root, who can already run `LG --ban` and
  read the database. With a loopback TCP port instead, any local user could;
  the server warns about that at startup, and server.md says to use the
  socket on Linux.
- *From another program on the server, through `tailscale serve`*: a request
  to the page's ts.net address from the machine itself comes from the
  machine's own tailnet address, and Tailscale names it by the machine's own
  login, which on an untagged server signed in as the owner is the owner's:
  any local user could reach the page as the owner, around the socket's
  permissions (found by the review, 2026-10-11). So the page refuses any
  request whose `X-Forwarded-For` is one of the machine's own addresses
  (every interface's, Tailscale's 100.x and fd7a:115c:a1e0:: ones included,
  read again whenever an address isn't among those known, since Tailscale's
  can come after the server starts), or that has none. Stronger still:
  tag the server's node in Tailscale, so that no request from it carries a
  login at all.
- *Through Funnel*: the page's port is one Funnel can't serve (the server
  won't start otherwise); `tailscale serve` never sets identity headers on a
  Funnel request and removes any the request brings; and a request marked
  `Tailscale-Funnel-Request` is refused and raises an urgent alert.
- *From a tagged device, or a device shared from another tailnet*: no headers,
  or that account's own login, which isn't listed: 403.
- *DNS rebinding*: the browser would get the ts.net certificate for another
  name and stop; and the page checks the name asked for anyway.

Considered and not done: asking `tailscaled` itself who the device is (its
local API's `whois`, on the address in `X-Forwarded-For`). It only guards
against a program that can open the socket, which already has more power
than the page.

**CSRF and clickjacking**: tokens bound to the login and the exact action,
the `Origin` check, no change on GET, `frame-ancestors 'none'` (see
[Forms and CSRF](#forms-and-csrf)).

**Cross-site scripting**: everything shown is encoded, and the Content
Security Policy allows no script at all.

**A replayed or relayed plugin action**: single-use nonces bound to the
connection, and signatures bound to the server's address and the login.

**A leaked webhook URL**: whoever has it can post in the alerts channel (fake
alerts, spam), and nothing else: it can't read the channel, and alerts are
never instructions the server acts on. A fake alert could suggest a harmful
command or link: the commands in real alerts never include `--force`, and
the page's address is one the owner knows; read a command before pasting it.
The URL lives only in a root-only file, is never logged, and the startup
line says only "Discord alerts on". If it leaks: delete the webhook in
Discord, make a new one, put it in the file, restart.

**What Discord learns**, and keeps: character names, worlds and user IDs (all
public on the Lodestone), shortened addresses, ban reasons, which tool made
a ban (and the operator character for the plugin's), which characters are
operators and their fingerprints (when one is pinned, restored, or logs in
with other keys; fingerprints are what any channel member sees), counts, the server's
version, uptime, database size and the most players online at once, the web
page's address (its name is already public: HTTPS certificates for ts.net
names are listed in public certificate logs) and the ping role. Never: full
addresses, Tailscale logins, Unix users, tokens, keys, channels, or
anything said.

**Alerts getting in the way**: a bounded queue on its own task; a slow or
failing Discord never delays a request; failures logged at most once every
ten minutes, without the URL, and counted in the daily summary.

### Out of scope

- Root, or the `lookingglass` user, on the server: they have the database.
- A takeover of the owner's Tailscale or identity-provider account, or of
  Discord.
- Several operators with different powers, or roles: one owner, a list of
  logins and characters.
- Appeals from banned players, and telling channel admins about bans (still
  the operator's matter).
- The web page on the public internet, or Funnel: never.

## Settings

All under `LookingGlass`. The server won't start with any of these out of
range, as with the others.

| Setting | Default | What it does |
| --- | --- | --- |
| `Operators:Characters` | empty | User IDs (Lodestone IDs) of characters that may be operators in the plugin. Each is one only once its keys are pinned (on the web page or with `--operator-pin`), and only while its keys match the pin; the pins are in the database. Empty: nobody |
| `Admin:Listen` | empty (no web page) | Where the page listens: `unix:/run/lookingglass/admin.sock` (Linux), or `http://127.0.0.1:<port>` (loopback only; warns). Never the public listener's port |
| `Admin:Url` | empty | The page's address as reached through `tailscale serve`, such as `https://<machine>.<tailnet>.ts.net:8444/`. Required with `Listen`; HTTPS; not port 443, 8443 or 10000. Also the link in alerts and in the plugin |
| `Admin:TailscaleLogins` | empty | Tailscale accounts let in (`alice@github`, `alice@example.com`). Required with `Listen` |
| `Admin:TimeZone` | `UTC` | The time zone the page shows times in, as an IANA name (`Europe/London`, `America/New_York`); each time carries the zone's abbreviation. The server won't start with a name it doesn't know |
| `Alerts:DiscordWebhookUrl` | empty (no alerts) | The webhook. A secret: in a root-only `EnvironmentFile=`, never in `appsettings.json`. Must be `https://discord.com/api/webhooks/...` |
| `Alerts:PingRoleId` | empty (never ping) | The Discord role pinged by urgent alerts, as its number |
| `Alerts:Restarts` | true | Post when the server starts |
| `Alerts:DailySummaryHourUtc` | 9 | The hour (0 to 23, UTC) the daily summary is posted; -1 for none (owner, 2026-10-10: 09:00 UTC by default) |

Fixed, not settings: the plugin's 10 bans an hour per operator, the page's
30, the audit trail's 365 days, alerts batched over a minute.

**Where secrets go.** Only the webhook URL is one. It goes in
`/etc/lookingglass/alerts.env` (`root:root`, mode 0600), named by a drop-in's
`EnvironmentFile=`; systemd reads it as root before starting the service, so
the service user can't read the file (only its own environment). The rest are
plain `Environment=` lines in a drop-in. `Operators__Characters` is added to
what the `LG` helper passes on, since `--ban` needs `--force` for an operator
character.

## Setting it up on the server

Once built, server.md will say, in short:

1. Update the server (the unit gains `RuntimeDirectory=lookingglass`, so
   `/run/lookingglass` exists for the socket).
2. `sudo systemctl edit lookingglass` and add:

   ```ini
   [Service]
   Environment=LookingGlass__Admin__Listen=unix:/run/lookingglass/admin.sock
   Environment=LookingGlass__Admin__Url=https://<machine>.<tailnet>.ts.net:8444/
   Environment=LookingGlass__Admin__TailscaleLogins__0=alice@github
   Environment=LookingGlass__Admin__TimeZone=Europe/London
   Environment=LookingGlass__Operators__Characters__0=31337
   Environment=LookingGlass__Alerts__PingRoleId=123456789012345678
   EnvironmentFile=/etc/lookingglass/alerts.env
   ```

   with `/etc/lookingglass/alerts.env` holding
   `LookingGlass__Alerts__DiscordWebhookUrl=https://discord.com/api/webhooks/...`
   (`sudo install -m 600 -o root -g root /dev/null /etc/lookingglass/alerts.env`,
   then edit it). Restart, and look for the "Server started" alert.
3. Serve the page on the tailnet only (Tailscale 1.94 or later):

   ```sh
   sudo tailscale serve --bg --https=8444 unix:/run/lookingglass/admin.sock
   tailscale serve status      # :8444 must say "tailnet only"
   tailscale funnel status     # must list only the public server's port (443)
   ```

4. Optional, in the tailnet's access rules: let only the owner's own devices
   reach port 8444 on the server.
5. Open `https://<machine>.<tailnet>.ts.net:8444/` on a phone signed in to
   the tailnet: the page, signed in as the owner. From a device signed in as
   anyone else: "Not allowed".
6. Pin the operator character's keys: in game, open Settings with Advanced
   mode on and note the fingerprint under **Your identity**; on the page,
   **Operators**, **Pin...** on the character, check the fingerprint is the
   same, and confirm (or `LG --operator-pin 31337 --fingerprint "..."`).
   The character sees the Operator section from its next login.

The exact `tailscale serve` syntax is checked on the server's Tailscale
version when this is built.

## What changes in server.md

- **Settings**: the `Operators:`, `Admin:` and `Alerts:` rows, pointing to a
  new section.
- **Flags and bans**: "Three ways to ban" (the command line, the web page,
  the plugin), the policy table, `--audit`, who made each ban in `--bans`,
  and the `LG` helper passing `Operators__` on.
- **New section "Operators in the plugin"**: listing characters, pinning
  their keys, why a key change drops it, restoring it (web page,
  `--operator-pin`), `--operators`, and what the urgent "other keys" alert
  means.
- **New section "The operator web page"**: the steps above, why only
  Tailscale, why a socket, the time zone, and what to do if it says "Not
  allowed".
- **New section "Alerts to Discord"**: making the webhook and the role, the
  secrets file, what is posted, what Discord learns, and replacing a leaked
  webhook.
- **What the unit does**: `RuntimeDirectory`.
- **Logs**: refused page requests (at most once a minute), alert failures
  (once every ten minutes, without the URL).
- **Upgrading**: "From a server without operator tools (schema 11)": the new
  columns and tables, nothing to set (all off by default), and older plugins
  unaffected.
- **Deploying / Putting TLS in front**: a warning never to funnel the page's
  port.

And in design.md as each part is built: the planned sections move into
[Spotting abuse, and banning](design.md#spotting-abuse-and-banning), the
schema version becomes 12, `operator.v1` joins [Versions](design.md#versions),
and the plugin's section joins [The Settings window](design.md#the-settings-window).

## Build order, size and tests

| Step | What | Update needed | Rough size |
| --- | --- | --- | --- |
| 1 | `OperatorActions` and `OperatorPolicy`, schema 12 (with `operator_pins` and `users.keys_registered_at`), the command line moved onto them (same words), `--audit`, `--operators`, `--operator-pin`. **Built 2026-10-10** (see below) | Server only | Medium: about 750 lines and 600 of tests (as built: about 1,300, with its comments, and 1,100) |
| 2 | Discord alerts: queue, posting, batching, every alert kind (the operator-keys ones included), restart and daily summary. **Built 2026-10-10** (see below) | Server only | Medium: about 750 lines and 650 of tests (as built: about 1,300, with its comments, and 1,050) |
| 3 | The web page: second host, Unix socket, identity checks, the time zone, pages (Operators, pin and restore included), CSRF, stylesheet; unit and docs. **Built 2026-10-10** (see below) | Server only | Large: about 1,050 lines and 800 of tests (as built: about 1,650, with its comments and the stylesheet, and 1,300) |
| 4 | The plugin's panel: `operator.v1` in the protocol, server handling (the pin checked at login and on every operator request, the "other keys" alert), `OperatorActionProof`, `OperatorWords`, the Settings section, the keys-changed notice, the dialog, the menu items | Server and plugin | Large: about 450 server, 400 core, 750 plugin, 650 of tests |

Steps 1 to 3 need no plugin release, and older plugins are unaffected. Step
2 can come before 3 (its alerts then give only commands, and gain the page's
link in 3). Step 4 needs both updated; either alone does nothing new.

**Step 1 as built** (2026-10-10), where it adds to or settles what is above:

- The policy table has one more row, **let a key sign in again**
  (`--allow-key-login`): the command line only, as the web page doesn't offer
  it at first. It is audited like the rest.
- **What the audit trail records**: every attempt to change something (ban,
  lift, pin, key login allowed again) by any tool, done or refused, including
  a target that names nobody or an address too wide; not lists, and not a
  command line that couldn't be read (exit code 2: a missing target, a bad
  `--days`, `--reason` or `--fingerprint`), which never reaches the service.
  A change and its entry are written in one transaction. The hour's limits
  are counted from it (bans done, not refused). What was typed is kept with
  control and format characters shown as escapes (`\u001B`), and cut (whom
  at 200 characters); the command line prints everything the same way.
- **`--audit`** lists the last 50 entries; `--audit --days N` (1 to 365) all
  of the last N days.
- **`keys_registered_at`** is null (unknown) for keys registered before the
  upgrade, and kept by two triggers in the database (a new account's keys
  are dated when it is made, changed keys when the account is updated), so it
  stays true when an older server registers keys on a schema 12 database.
- **`made_announced`** is 0 for every ban made from now on, by any process,
  and **`lifted_announced`** 0 for every lift; the upgrade sets both to 1 for
  what was made or lifted before. **For step 2 (decided by the review,
  2026-10-10; not built): whether a ban was posted is decided by the posting
  server, never by whoever wrote it.** The writer can't know: the `LG`
  command line doesn't see the Discord settings (the webhook is a secret
  only the service reads), and an older server writes 0. So the posting
  server keeps an "alerts on since" time in `server_state` (set when it
  starts with alerts on and finds none, or alerts were off), and quietly
  marks as posted every unposted ban and lift from before that time; it
  posts only those from after.
- **`replaced_by`**: a ban replaced by banning the same subject again is
  lifted by the new ban's tool and maker, with `replaced_by` naming the new
  ban (its `lifted_announced` is 0 like any lift). Step 2 posts it once, as
  part of the new ban ("replacing the ban made ... on the web page"), and
  marks the old one's lift as posted with it, rather than posting a lift of
  its own. An automatic block never replaces a ban (it checks for a covering
  one in its own transaction), so there is no "replaced automatically".
- **`operator_pins.mismatch_announced_hash`** is there for step 4's "other
  keys" alert (cleared whenever the character is pinned again).
- **`server_state`** is a key and value table; nothing uses it yet.
- **The plugin's hour** is counted per operator character by user ID, not
  by name (a rename doesn't reset it).
- **The command line's user**: `SUDO_USER`, or the user it runs as without
  sudo (`USER`, `USERNAME`), or "unknown".
- **User IDs**: 0 names nobody; a negative ID (debug accounts) names only a
  registered account, or any where `Dev:AllowDebugAccounts` is on.
- **Without the operator list**, `--ban` can't tell an operator character
  from another; it and `--operators` warn when they see no list while the
  database holds pins.

**Step 2 as built** (2026-10-10), where it adds to or settles what is above:

- **No link to the web page yet.** `Admin:Url` is checked against
  `Admin:Listen` (required with it), which comes with the page, so it isn't
  a setting yet; alerts give the commands only, as the build order allows.
  Step 3 adds the page's link to the words (`AlertWords`).
- **Posted at most once.** A ban or lift is marked as posted before it is
  posted (a conditional update, so whichever look marks it posts it): never
  twice, and lost only if the server dies between marking and posting (what
  is waiting is posted as it stops, within 5 seconds).
- **"Alerts on since"**, as decided for step 2: set when the server starts
  with alerts on and finds none; forgotten when it starts with them off. If
  it can't be read (the database failing), nothing is marked until it can.
- **Pins without a schema change.** The keys last posted as pinned for each
  operator character are kept in `server_state` (`alerts_pin_posted:<id>`);
  a pin whose keys differ is posted (as restored, with the keys before, when
  there were some), and one from before "alerts on since" is noted without
  being posted. Each says when it was pinned: a restore made while alerts
  were off (the entry kept from before) is posted when they are on again,
  still urgent, as it is what someone with a stolen tailnet device would do. A pin deleted at startup forgets its entry, so pinning the
  character again is posted.
- **The other keys** are checked when a login is answered (`Authenticate`:
  every sign-in, by saved login, key login or a new registration, ends in
  one), only for a listed character with a pin: one never pinned was never an
  operator, and has no pin to mark "posted" in. Each new set of keys is posted
  once (`operator_pins.mismatch_announced_hash`, cleared by a new pin). Step 4
  tells the character itself in the same place.
- **Automatic blocks** are posted from the bans table like every ban (the
  monitor asks for a look at once), urgent; "refused 1,000 times" is the
  threshold, `Abuse:AutoBlockAfterRefusals`. One found already ended (an
  older server made it, before going back to this one) is marked without being
  posted; the daily summary counts it.
- **Ready for step 3:** `ServerAlerts.FunnelRequest()` (urgent; at most one
  alert every ten minutes, counting the rest, so the public can't make it
  ping again and again), `ServerAlerts.SendTest()` (false when alerts are off)
  and `ServerAlerts.Nudge()` (a ban or lift made inside the server already
  calls it; the page's pins should too, or they are found at the next look,
  within `Abuse:BanCheckSeconds`).
- **Timing:** a queue of 1,000; alerts within a minute as one message; an
  urgent one at once with what is waiting, at most one message every 2
  seconds; the restart alert, the summary and the test at once, without the
  ping. **Urgent alerts the public can set off** (automatic blocks, requests
  through Funnel) go at once and ping only once every ten minutes; between,
  they wait for the minute's message, without the ping (the review found
  that someone with many addresses could otherwise make the role ping every
  2 seconds, and use up the channel's messages). The operator keys' alerts
  and the proxy warning always go at once and ping. Alerts waiting for the
  minute are kept to 1,000 too. The client that posts follows no redirect. A `Retry-After` up to 15
  minutes is honoured, five times for one message; a 5xx, a timeout or no
  connection is tried twice more, 5 and 30 seconds later; any other 4xx (a
  deleted webhook) isn't tried again. Then the message's alerts are counted
  as not delivered (in memory: "since the restart" in the summary when there
  was one that day).
- **The restart alert's marker:** `server_started_at` and
  `server_stopped_cleanly_at` in `server_state`, written whether alerts are
  on or not, so turning them on later tells the truth about the last stop.
- **The daily summary:** the end of the day it covered is kept
  (`daily_summary_posted_to`). The first start with alerts on sets it to the
  hour last passed, so the first summary comes when the hour next comes round
  (perhaps minutes later); like every one, it covers the 24 hours before it,
  from the database, whether alerts were on for all of them or not. "Ended" counts automatic blocks that ran out as well as bans; "Lifted"
  doesn't count bans replaced by a new one (those are bans made).
- **Escaping:** names, worlds, reasons, limits and the plugin's operator
  character go through `VisibleText` (control and format characters as
  escapes), then the backslash, `*`, `_`, `~`, the backtick, `|`, `[`, `]`, `<`,
  `>`, `:` and `#` are escaped with a backslash, and a zero-width space follows
  every `@`. Addresses as the server wrote them are
  shown as they are. A debug account has no Lodestone line.
- **The test alert**: "**Test alert** from LookingGlass 0.2.x: alerts reach
  this channel." Without the ping (the owner's decision, 2026-10-10): step 39
  of the bans checklist checks the ping with a real urgent alert.

**Step 3 as built** (2026-10-10), where it adds to or settles what is above:

- **The page's host** (`Admin/OperatorPageHost`) is a second web host in the
  same process, built empty (Kestrel and one request handler, no routing, no
  configuration of its own, so the public `Urls` and `--urls` never reach it),
  logging where the server logs. It has the page alone: `/ws` and `/health`
  are 404 there, and every page path is 404 on the public host. Kestrel's
  `Server` header is off, and its limits are small (16 KB bodies, 64
  connections).
- **The socket** is made by Kestrel in `/run/lookingglass` (the unit's
  `RuntimeDirectory`, mode 0700) and set to 0600 by the server itself on
  Linux, whatever the umask. A socket left by a server that didn't stop
  cleanly is replaced; if something answers on it (another server running),
  the page doesn't start (see "If the page can't start" below). Windows has Unix sockets too, so the tests use a
  real one there; the 0600 is checked on Linux only (by hand: step 45 of the
  bans checklist).
- **Settings checked at start** (`AdminOptions.Problem`): `Listen` is
  `unix:<a full path ending in .sock, in a folder that exists, at most 100
  bytes>` or `http://<127.0.0.1 or ::1>:<port>`, never `localhost`, another
  address, https, or the public server's port (from `Urls`/`--urls`); `Url`
  is required with `Listen` and refused without it: https, a fully qualified
  name of letters, digits and hyphens (so it is safe as it is in alerts), an
  explicit port that isn't 443, 8443 or 10000, nothing after the last `/`;
  `TailscaleLogins` lists at least one login with an `@` and no spaces;
  `TimeZone` is one the machine knows, checked even without the page (as
  this plan says). The startup line says where the page is, and a loopback
  port is a Warning.
- **Who gets in**, in this order: a `Tailscale-Funnel-Request` (any value) is
  refused and calls `ServerAlerts.FunnelRequest()`, even with a listed login;
  then an `X-Forwarded-For` address that isn't the machine's own (see below);
  then exactly one `Tailscale-User-Login`, equal ignoring case to a listed
  login; then exactly one `X-Forwarded-Host`, equal ignoring case to
  `Admin:Url`'s name and port. The audit trail records the login as Tailscale
  sent it, and "from" as the first `X-Forwarded-For` (the device's tailnet
  address). The refusal log (a Warning, at most once a minute, counting the
  rest) gives the check that failed, the method, the path and that address;
  forms refused for their Origin or token go into the same line.
- **Single-use tokens everywhere**, not only on confirmation pages: the main
  form's **Continue...** and **Send a test alert** too (a page reloaded gets
  fresh ones). A token is checked before anything else the form asks, and
  one that doesn't verify (another login's, another action's) isn't used up
  by the attempt. A lift's token names the ban's ID: confirmed after that ban
  was replaced, it lifts nothing, says so, and is in the audit trail as
  refused (`OperatorActions.Lift(..., banId)`). A form with a token that isn't
  good gets 403 and the main page again, with why: out of date (restarted, or
  over 30 minutes), sent already, or not one the page made.
- **Origin** (after the review, 2026-10-11): `Referrer-Policy: same-origin`
  rather than `no-referrer`, since `no-referrer` makes browsers send
  `Origin: null` with the page's own forms (the Fetch standard); with
  `same-origin` they send the real Origin, and a link elsewhere (the
  Lodestone) still carries no referrer (the links have `rel="noreferrer"`
  too). A form's `Origin` must be exactly `Admin:Url`'s scheme, name and port
  (`null`, another case, another port: refused); a `Sec-Fetch-Site` that is
  there must be `same-origin`; only a form without `Origin` may pass on
  `Sec-Fetch-Site: same-origin` alone.
- **Requests from the machine itself are refused** (after the review,
  2026-10-11): `tailscale serve` names a request by the login of the device
  it came from, and a request from the server itself (a local `curl` to the
  ts.net address) comes from the server's own tailnet address, so it carries
  the server's own login, the owner's on an untagged server. The page
  refuses any request whose first `X-Forwarded-For` (the device's address,
  as `tailscale serve` sets it) is one of the machine's own addresses
  (loopback, the unspecified addresses, and every unicast address of every
  interface, read at start and again whenever an address isn't among those
  known, since Tailscale's can come after the start), or that has none, with
  its own reason in the log. Tagging the server's node is stronger: tagged
  nodes' requests carry no login.
- **If the page can't start** where its settings say (something listening
  on the socket, a file or a link in its place, its folder not the service
  user's own or writable by others, the port taken), the chat server runs on
  without it (the owner's default, 2026-10-11): an Error in the log and an
  urgent alert ("the operator web page didn't start"). Settings wrong in
  themselves still stop the server at start. On Linux the socket's folder is
  checked with `statx` (not following links): a folder, owned by the
  service's user, writable by nobody else. Only a socket is ever deleted in
  the socket's place, never a file or a link (which isn't followed). The
  socket is made with the umask 0177 for the moment of listening, then set
  to 0600 again.
- **Errors** the page doesn't expect are answered by the page itself (500,
  with the security headers and no detail); a form it can't read is a 400
  with the headers. Kestrel's own answers to requests it can't parse at all
  (a malformed request line, headers too large) come before the page and
  carry no headers; they have no content either.
- **The page's host** has no lifetime of its own (no second "Application
  started", no signal handlers of its own); the server's host stops it.
- Non-ASCII Tailscale logins (which Tailscale sends MIME-encoded) never match
  a listed login: the page fails closed.
- **Continue...** asks `OperatorActions.CheckBan` (the same checks as `Ban`,
  nothing done, nothing recorded), so a ban the rules refuse comes back to the
  form, filled in as typed, with why, before any confirmation page; the
  confirmed ban is checked again (the hour's 30 may have run out meanwhile).
  The confirmation page carries whom as resolved (the user ID or the address
  as a ban writes it), so a rename between the two pages changes nothing.
- **After an action** the page answers 303 to `/?note=<random>`; the line
  saying what was done is kept in memory 10 minutes for that login (at most
  500), so another site can't put words on the page, and reloading does
  nothing again.
- **Wording settled**: "Banned (name@world) for 7 days." (or "until you lift
  it", ", replacing the ban it had"); "Lifted the ban on (name@world).";
  "(name@world) is an operator again, with keys ..." after a restore, "...
  is an operator now, with keys ..." after a first pin. The mockup's "Its
  plugin sees the Operator section from its next login" is left out until
  step 4 builds the section. An address's confirmation page gives how many
  addresses an IPv4 prefix holds, and an IPv6 one in /64 networks.
- **Times** carry the zone's abbreviation as the system names it: on the
  Linux server (.NET without its globalization data, as the server runs),
  zones are named by their zone files' abbreviations (`GMT`, `BST`); where
  there is none (Windows' long names, or a zone named only by its offset),
  the offset from UTC stands in (`UTC+1`). Windows, without that data, knows
  only Windows' zone names, not IANA ones. Every time shows its date.
- **The layout**: the three last sections (Operators, lifted or ended, the
  audit trail) are `<details>`, closed except Operators while one needs
  pinning or restoring; on screens 700 pixels wide or more they show open
  where the browser supports `::details-content` (current Chrome, Edge and
  Safari), and open with a click elsewhere.
- **Alerts link the page** (`Admin:Url`, only when `Admin:Listen` is set):
  flags ("Look: (page) or `LG --bans`"; an address's "Full address on the
  web page: ..."), automatic blocks, bans made ("Lift: (page) or `LG --unban
  31337`"), the operator keys' alerts, pins ("the web page's audit trail
  (page) or `LG --audit`"), the daily summary (its last line) and a cut
  message's "and N more". Lifts, Funnel requests, the proxy warning, restarts
  and the test have none, as in the mockups.
- **The `tailscale serve` command** in server.md is from Tailscale's
  documentation and hasn't been run on the server's version; the bans
  checklist's step 46 checks it.

**Tests** (automated, in `tests/LookingGlass.Tests`):

- **Policy**: a table of every tool against every action and kind of target
  (character, registered name, unregistered ID, address, prefix, too wide,
  the proxy's address with and without force, an operator character, the
  hour's limit), the same rules from the command line and the service.
- **Audit and schema 12**: the upgrade from 11 (existing bans marked posted,
  their tool "command line" or "automatic"), every attempt recorded, the sweep.
- **Key-pinned operators**: a listed character with matching keys is an
  operator; listed but never pinned is not (no pin on first login); a
  registration with new keys (new computer, reset, recovery) drops it at
  once, a request already in flight included; nothing re-pins by itself;
  `--operator-pin` refuses a fingerprint that isn't the current keys'; a pin
  compares the full hash, not the 25 digits; a pin for an unlisted character
  is deleted at startup and gives nothing; the web page can't make an
  unlisted character an operator.
- **A fake webhook** (an `HttpMessageHandler` that records posts): batching,
  urgent ones at once, `Retry-After`, failures counted and logged once, the
  2,000-character cut, escaping (a reason with `@everyone`, markdown, a
  mention), `allowed_mentions` naming only the role on urgent alerts and
  nothing otherwise, address shortening (IPv4, IPv6, wider prefixes), no
  post when unset, a ban posted once across a restart and across processes,
  the daily summary's hour and quiet day, the clean-stop marker, the "other
  keys" alert once per new set of keys (not at every login) with the ping,
  pins and restores with the ping.
- **Fake Tailscale headers** (the admin host in a test server): no login, an
  unlisted login, a Funnel-marked request (403 and an urgent alert), a wrong
  `X-Forwarded-Host`, then a listed login let in; every admin path 404 on the
  public host, and no `/ws` on the admin host; the startup refusals (a
  non-loopback address, port 443, no logins, an unknown time zone); times
  shown in `Admin:TimeZone` with its abbreviation, across a daylight-saving
  change.
- **CSRF**: no token, another login's token, a confirmation token for
  another action, an expired or reused one, a wrong or missing `Origin`; a
  restore confirmed after the keys changed again (refused: the token names
  the keys shown); the security headers on every answer; names and reasons
  encoded.
- **Plugin protocol**: a non-operator refused (and counted), a listed
  character with unpinned keys refused, a bad signature, an old key's
  signature, a reused or another connection's nonce, another server's
  address, an address ban refused, an operator character refused, the
  hour's limit; `AuthenticateOk.operator_status` only with the capability,
  only to the character's own login, and "not listed" to everyone else; an
  older client and an older server.
- **Words**: `OperatorWords` in both modes, plain, within the "?" limits.

**By hand**: steps added to
[docs/testing/abuse-bans-checklist.md](testing/abuse-bans-checklist.md) on
the tester server, with a private Discord channel (each alert kind, the ping
only on urgent ones), the web page from a phone and from a second tailnet
account ("Not allowed"), and `curl` to the page's port from outside the
tailnet (no answer); and a new `docs/testing/operator-panel-checklist.md` for
the plugin (the section, the menu item on and off an operator, the dialog,
the chat lines, both modes; pinning, then "Reset my identity" on the
operator character: the section gives way to the notice, Discord pings, and
restoring on the web page brings it back at the next login).

## Decided (owner, 2026-10-10)

The questions this plan left open, and the changes asked for since, all
decided by the owner on 2026-10-10:

1. **IPv6 is shortened to a /40** (the recommendation), not a /48. The /48
   was in the planning brief written by the AI, not suggested by the owner.
   A /48 is often one customer's whole allocation, so in Discord it would
   point at one household much as a full address does; a /40 holds 256 of
   those, about what an IPv4 /24 holds.
2. **The plugin acts on characters only**: it may not lift automatic address
   blocks (they end by themselves in 15 minutes), so every address action
   stays on the web page and the command line.
3. **The panel is a section at the end of Settings**, not a window of its
   own; a window can come later if it gets busy.
4. **The daily summary is at 09:00 UTC by default**, a setting
   (`Alerts:DailySummaryHourUtc`).
5. **The web page shows times in the owner's time zone**, a setting
   (`Admin:TimeZone`, an IANA name, UTC by default), each time with the
   zone's abbreviation.
6. **Key-pinned operators**: an operator is a listed character whose current
   keys match the keys pinned for it, so registering the character again
   (which only needs its Lodestone page) gives nobody the panel. The list of
   who may be an operator stays in the configuration; the pins live in the
   database, set and restored only by the operator on the web page or the
   command line, never automatically. A key change drops operator status
   until restored; the operator's own login is told, nobody else, and
   Discord gets an urgent alert. See
   [Key-pinned operators](#key-pinned-operators).

Nothing is left open but the approval of the wording and appearance above.
