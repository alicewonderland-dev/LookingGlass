# Operator tools: a plan for approval

Status: a plan, nothing built (2026-10-10). The owner approves the wording and
appearance below before anything is built. The decisions themselves are kept
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
2. **An operator panel in the plugin**, for the owner's own characters.
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
- [Decisions still open](#decisions-still-open)

## One layer for every tool

`OperatorActions`, a server service, does the four things every tool needs:
list the flags, list the bans (in force, and lifted or ended lately), ban,
and lift a ban. The command line (`--bans`, `--ban`, `--unban`), the web page
and the plugin all call it, so the rules are written once:

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
whether the server last stopped cleanly). An older server opens a schema 12
database and ignores what it doesn't know.

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
  WireGuard key, which can't be faked). It sets none for a tagged device, for
  a request from the machine itself, or for a request through Funnel (which
  it marks `Tailscale-Funnel-Request: ?1` instead). The page lets in a
  request only when `Tailscale-User-Login` is exactly one of
  `Admin:TailscaleLogins` (ignoring case), no `Tailscale-Funnel-Request` is
  present, and the address it was asked for (`X-Forwarded-Host`, which
  `tailscale serve` also sets) is the one in `Admin:Url`. Everything else
  gets **403**. (Checked against Tailscale's documentation and its source,
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
- **What operators did**: the last 50 entries of the audit trail.
- **Send a test alert** (when alerts are on), which posts one message to the
  Discord channel.
- Not on the page at first: `--allow-key-login`, settings, anything about
  channels or messages.

It is plain HTML and one small stylesheet, both inside the server: no
JavaScript at all, nothing from another site, no cookies. It works on a
phone browser (one column below about 700 pixels wide; big buttons) and
follows the device's light or dark setting. Times are UTC, as in `--bans`.
It doesn't refresh by itself: a **Refresh** link reloads it.

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
|  What operators did (last 50)                                                                        |
|    2026-10-10 13:20  web page      alice@github from 100.101.102.103   banned 203.0.113.0/24, 1 day   |
|    2026-10-10 12:00  plugin        Alice Wonderland@Lich               banned Carol Queen@Odin, 7 days|
|    2026-10-09 22:10  command line  alice                               lifted the ban on Dan Dodo@... |
|    2026-10-09 22:09  web page      alice@github from 100.101.102.103   refused: 127.0.0.1 is this    |
|                                                                        server's own address           |
|                                                                                                      |
|  [ Send a test alert ]                                                  Times are UTC.               |
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
| | Last 11:55 UTC · Lodestone | |
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
| | Until 2026-10-17 12:00 UTC | |
| | Plugin: Alice Wonderland   | |
| | "Spamming invites"         | |
| | [        Lift...         ] | |
| +----------------------------+ |
|  ...                           |
|--------------------------------|
| Lifted or ended (1)      ▸     |
| What operators did       ▸     |
+--------------------------------+
```

(The last two open with HTML's own `<details>`, which needs no JavaScript.)

### Mockup: confirming

```text
+--------------------------------------------------------------+
| Ban Bob Hatter@Lich?                                         |
|                                                              |
| Character: Bob Hatter@Lich, Lodestone ID 31337 (Lodestone)   |
| For: 7 days, until 2026-10-17 14:02 UTC                      |
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
Queen@Odin? Made 2026-10-10 12:00 UTC in the plugin by Alice
Wonderland@Lich, until 2026-10-17. Reason: Spamming invites. [Cancel] [Lift
the ban]". Afterwards the main page shows "Banned Bob Hatter@Lich for 7
days." or "Lifted the ban on Carol Queen@Odin." at the top.

The refusal page, for anyone not let in: "**Not allowed.** This page is only
for this server's operators, reached through Tailscale from an account they
listed." Nothing more (not which check failed; the server's log says that,
at most once a minute).

### Forms and CSRF

The identity headers come with every request from an allowed device, as a
cookie would, so a page on another site that the owner happens to open could
send a form to the page and the headers would come with it. So:

- **Nothing changes on a GET.**
- **Every form carries a token** the page made: an HMAC, under a key the
  server makes at each start and never stores, of the Tailscale login, the
  time, and, on a confirmation page, the exact action (ban or lift, whom,
  days, the reason's hash). A confirmation token works for that action only,
  for 30 minutes, once. Another site can't read the page, so it can't get
  one. After a restart old forms say "This page is out of date (the server
  restarted). Check and confirm again."
- **`Origin` must be the page's own** (`Admin:Url`), or, without `Origin`,
  `Sec-Fetch-Site` must be `same-origin`; a POST with neither is refused.
- **Headers on every answer:** a Content Security Policy that allows only the
  page's own stylesheet and forms (`default-src 'none'; style-src 'self';
  img-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri
  'none'`), so no script runs and no other site can frame it;
  `X-Content-Type-Options: nosniff`; `Referrer-Policy: no-referrer` (a
  Lodestone link doesn't tell the Lodestone the page's address);
  `Cache-Control: no-store` (full addresses aren't kept in a phone's cache).
- **Everything shown is HTML-encoded**: names, worlds, reasons, limits.

## The plugin's operator panel

### Who sees it

- **Operators are characters** listed by user ID (Lodestone ID) in
  `Operators:Characters`. IDs, not names, since a character can be renamed;
  the server logs the names it found at startup ("Operators: Alice
  Wonderland@Lich (31337)").
- **Only the operator learns they are one.** The server says so in its answer
  to the operator's own login (`AuthenticateOk.operator`), only when both
  sides agreed to the capability `operator.v1`. Nobody else is told who the
  operators are, or that the feature exists for them.
- **The plugin shows an Operator section in Settings** (the last one), and a
  **Ban from server...** item in the right-click menus, only while connected
  and logged in as an operator.

### What the operator's keys can do, and why less than the web page

An operator's character keys become worth more: a copy of the secrets file
would let someone use the panel. The owner already gets told of a new login
(device notices), but it is still worth keeping the panel narrower than the
page:

- **Characters only.** The plugin bans and lifts bans on characters (any
  tool's). Address bans, and lifting them or automatic blocks, are on the web
  page and the command line: an address ban shuts out everyone behind it,
  and needs the full address, which the plugin never gets.
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
| `AuthenticateOk.operator` (bool, field 6) | This login's character is an operator; only with the capability |
| `GetOperatorView` (`ClientFrame` 38) | Asks for the lists; answered with `OperatorView` |
| `OperatorView` (`Response` 25) | Flags, bans in force, lifted or ended lately (characters with names; addresses shortened), the web page's address if set, bans left this hour, and a nonce for one ban or lift |
| `OperatorBan` (`ClientFrame` 39) | A user ID, days (0: until lifted), the reason, the server URL, the nonce, the signature; answered with a fresh `OperatorView` |
| `OperatorUnban` (`ClientFrame` 40) | A user ID, the server URL, the nonce, the signature; answered likewise |
| `OperatorActionProof` (core crypto) | What is signed, with its own context string |

A name@world is turned into a user ID first with the existing `LookupUser`,
so the dialog shows exactly which character the ban is for.

The server checks, in order: logged in; the capability agreed; the character
is an operator (otherwise `FORBIDDEN`, "Only this server's operators can do
that.", counted towards flagging like any refusal); the signature, by the
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

The "?" says: "You see this because the server lists this character as an
operator. Bans you make here are on the character, and are posted to the
alerts channel."

Each list shows at most 10 lines, newest first, with "and 4 more (see the
web page)". The section asks for the lists when Settings opens, after each
action, and on **Refresh**; nothing in the background. Times are the
player's local time, as elsewhere in the plugin.

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
  (Recommended over the /48 first suggested: a /48 is often one customer's
  whole allocation, so it points at one household as a full address does; a
  /40 holds 256 of those, much as a /24 holds 256 IPv4 addresses. See
  [Decisions still open](#decisions-still-open).) The server's own and the
  proxy's address (in the proxy warning) shows in full: it is the server's.
- **Pings:** an optional role (`Alerts:PingRoleId`) is pinged for **urgent
  alerts only**: an address blocked automatically, the proxy warning, and a
  request to the web page through Funnel. Flags and bans never ping.
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

**A copy of an operator character's keys** (its secrets file, on a
compromised PC). The holder can see the lists (addresses shortened) and ban
or lift bans on characters, 10 bans an hour. They can't ban addresses, use
`--force`, ban an operator character, or see full addresses. The owner is
told of the new login (device notices) and of every plugin action (Discord,
naming the character). Undo: lift the bans on the web page, take the
character out of `Operators:Characters` and restart, then sign out
everywhere else or reset the identity.

**A copy of an operator's login alone** (the device token, without the key):
the lists, nothing more; bans and lifts need the key's signature.

**A compromised tailnet device of an allowed account** (a stolen, unlocked
phone, say). The holder has the web page's powers: bans on characters and
addresses, `--force`, lifting. Limits on the damage: 30 bans an hour; every
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
a ban (and the operator character for the plugin's), counts, the server's
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
| `Operators:Characters` | empty | User IDs (Lodestone IDs) of characters that see the Operator section in the plugin. Empty: nobody |
| `Admin:Listen` | empty (no web page) | Where the page listens: `unix:/run/lookingglass/admin.sock` (Linux), or `http://127.0.0.1:<port>` (loopback only; warns). Never the public listener's port |
| `Admin:Url` | empty | The page's address as reached through `tailscale serve`, such as `https://<machine>.<tailnet>.ts.net:8444/`. Required with `Listen`; HTTPS; not port 443, 8443 or 10000. Also the link in alerts and in the plugin |
| `Admin:TailscaleLogins` | empty | Tailscale accounts let in (`alice@github`, `alice@example.com`). Required with `Listen` |
| `Alerts:DiscordWebhookUrl` | empty (no alerts) | The webhook. A secret: in a root-only `EnvironmentFile=`, never in `appsettings.json`. Must be `https://discord.com/api/webhooks/...` |
| `Alerts:PingRoleId` | empty (never ping) | The Discord role pinged by urgent alerts, as its number |
| `Alerts:Restarts` | true | Post when the server starts |
| `Alerts:DailySummaryHourUtc` | 9 | The hour (0 to 23, UTC) the daily summary is posted; -1 for none |

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

The exact `tailscale serve` syntax is checked on the server's Tailscale
version when this is built.

## What changes in server.md

- **Settings**: the `Operators:`, `Admin:` and `Alerts:` rows, pointing to a
  new section.
- **Flags and bans**: "Three ways to ban" (the command line, the web page,
  the plugin), the policy table, `--audit`, who made each ban in `--bans`,
  and the `LG` helper passing `Operators__` on.
- **New section "The operator web page"**: the steps above, why only
  Tailscale, why a socket, and what to do if it says "Not allowed".
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
| 1 | `OperatorActions` and `OperatorPolicy`, schema 12, the command line moved onto them (same words), `--audit` | Server only | Medium: about 600 lines and 500 of tests |
| 2 | Discord alerts: queue, posting, batching, every alert kind, restart and daily summary | Server only | Medium: about 700 lines and 600 of tests |
| 3 | The web page: second host, Unix socket, identity checks, pages, CSRF, stylesheet; unit and docs | Server only | Large: about 900 lines and 700 of tests |
| 4 | The plugin's panel: `operator.v1` in the protocol, server handling, `OperatorActionProof`, `OperatorWords`, the Settings section, the dialog, the menu items | Server and plugin | Large: about 400 server, 400 core, 700 plugin, 600 of tests |

Steps 1 to 3 need no plugin release, and older plugins are unaffected. Step
2 can come before 3 (its alerts then give only commands, and gain the page's
link in 3). Step 4 needs both updated; either alone does nothing new.

**Tests** (automated, in `tests/LookingGlass.Tests`):

- **Policy**: a table of every tool against every action and kind of target
  (character, registered name, unregistered ID, address, prefix, too wide,
  the proxy's address with and without force, an operator character, the
  hour's limit), the same rules from the command line and the service.
- **Audit and schema 12**: the upgrade from 11 (existing bans marked posted,
  their tool "command line" or "automatic"), every attempt recorded, the sweep.
- **A fake webhook** (an `HttpMessageHandler` that records posts): batching,
  urgent ones at once, `Retry-After`, failures counted and logged once, the
  2,000-character cut, escaping (a reason with `@everyone`, markdown, a
  mention), `allowed_mentions` naming only the role on urgent alerts and
  nothing otherwise, address shortening (IPv4, IPv6, wider prefixes), no
  post when unset, a ban posted once across a restart and across processes,
  the daily summary's hour and quiet day, the clean-stop marker.
- **Fake Tailscale headers** (the admin host in a test server): no login, an
  unlisted login, a Funnel-marked request (403 and an urgent alert), a wrong
  `X-Forwarded-Host`, then a listed login let in; every admin path 404 on the
  public host, and no `/ws` on the admin host; the startup refusals (a
  non-loopback address, port 443, no logins).
- **CSRF**: no token, another login's token, a confirmation token for
  another action, an expired or reused one, a wrong or missing `Origin`; the
  security headers on every answer; names and reasons encoded.
- **Plugin protocol**: a non-operator refused (and counted), a bad
  signature, an old key's signature, a reused or another connection's nonce,
  another server's address, an address ban refused, an operator character
  refused, the hour's limit; `AuthenticateOk.operator` only with the
  capability; an older client and an older server.
- **Words**: `OperatorWords` in both modes, plain, within the "?" limits.

**By hand**: steps added to
[docs/testing/abuse-bans-checklist.md](testing/abuse-bans-checklist.md) on
the tester server, with a private Discord channel (each alert kind, the ping
only on urgent ones), the web page from a phone and from a second tailnet
account ("Not allowed"), and `curl` to the page's port from outside the
tailnet (no answer); and a new `docs/testing/operator-panel-checklist.md` for
the plugin (the section, the menu item on and off an operator, the dialog,
the chat lines, both modes).

## Decisions still open

Each with a recommendation; the rest of the plan follows the recommendation
until the owner says otherwise.

1. **IPv6 shortened to a /40, or to a /48?** Recommended: **/40**. A /48 is
   often one customer's whole allocation, so in Discord it would point at
   one household much as a full address does; a /40 holds 256 of those,
   about what an IPv4 /24 holds. The owner's suggestion of a /48 is easy to
   switch to.
2. **May the plugin lift automatic address blocks?** Recommended: **no**,
   characters only. Lifting a block early is rarely urgent (it ends by itself
   in 15 minutes), and keeping every address action on the web page and the
   command line keeps the rule simple: the plugin acts on characters.
3. **Where the panel lives:** a section at the end of Settings
   (recommended), or a window of its own (with a `/lgop` command). The lists
   are short (10 lines each, then "see the web page"), and Settings is where
   the owner was already looking; a window can come later if it gets busy.
4. **The daily summary's hour:** 09:00 UTC by default (a setting). Tell us
   the hour you'd rather read it.
