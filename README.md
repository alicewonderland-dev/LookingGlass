# LookingGlass

End-to-end encrypted, cross-world linkshells for FFXIV: a Dalamud plugin and
a small server. The server relays ciphertext only; channel names and messages
are encrypted between members. This is a clean-room rewrite inspired by the
ideas of ExtraChat; no code is shared with it.

Status: **0.2, authenticated membership** — registration, channels, invites,
automatic rekeying, encrypted messaging, ranks, debug tooling, and a signed
membership log that every client checks for itself. ChatTwo integration and
the import wizard come next (see [docs/design.md](docs/design.md)).

> **Security status.** Message contents are encrypted end to end, and since
> 0.2 clients work out who is in a channel from a signed membership log they
> verify themselves, not from the server's word. A malicious server still
> sees who is in which channel, can drop or delay anything, and can hand you
> its own key the first time you invite someone by name: compare fingerprints
> over /tell (see "Security model" below).

## Layout

| Path | What it is |
| --- | --- |
| `src/LookingGlass.Protocol` | The wire protocol (`Protos/lookingglass.proto`), shared by everything |
| `src/LookingGlass.Core` | Crypto, the membership log, the client session, and the echo bot. No Dalamud dependency |
| `src/LookingGlass.Server` | ASP.NET Core server with SQLite. Runs on Linux and Windows |
| `src/LookingGlass.Plugin` | The Dalamud plugin (`/lookingglass` or `/lg`, `/lgc1`–`/lgc50`, `/lgc <nickname>`, `/lgdebug`) |
| `tools/LookingGlass.DevTool` | `lgdev`: run an echo bot, or smoke-test a server |
| `tests/LookingGlass.Tests` | Crypto, policy, membership log and end-to-end tests, including a malicious in-process server |

## Build and test

Needs the .NET 10 SDK (newer SDKs work too). The plugin also needs Dalamud's
dev files, which XIVLauncher installs.

```sh
dotnet build LookingGlass.slnx -c Release
dotnet test LookingGlass.slnx -c Release
```

## Running a test server (Tailscale)

On the server machine (Linux or Windows):

```sh
./scripts/run-dev-server.sh        # Linux / macOS
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-dev-server.ps1   # Windows
```

(Windows blocks `.ps1` scripts by default; `-ExecutionPolicy Bypass` applies to
that one run only and changes no system setting.)

The database lives outside the build output, so rebuilding, cleaning or running
from another checkout of the code keeps your registrations and channels:
`%USERPROFILE%\.lookingglass\dev-server` on Windows,
`~/.local/share/lookingglass/dev-server` on Linux (the script prints it). Set
`LookingGlass__DataDirectory` first to use another folder; the script says so
when it does. (Not under `AppData` on Windows: Windows redirects packaged apps'
`AppData` writes to a private folder, so a tool started from one, such as an AI
coding assistant, would see a different database than you do.)

This listens on port 5180 on all interfaces in **Development** mode, which
turns on debug accounts and runs an echo bot inside the server. Only do this
on a private network such as your tailnet: anyone who can reach a Development
server can register (or take over) any debug account, including the echo bot.

1. Make sure the machine's firewall allows TCP 5180 from the tailnet.
2. In game, open `/lookingglass`, click the gear in its title bar (or the
   plugin's settings button in Dalamud's plugin list), and set the server URL
   to `ws://<machine-name>:5180/ws` (the Tailscale MagicDNS name or 100.x IP).
3. Register your character. The main window walks you through it: get a
   code, paste it into your Lodestone profile, then press **Verify**.

Tailscale already encrypts traffic between devices, and message contents are
end-to-end encrypted regardless. For TLS anyway, `tailscale serve` (or
`tailscale funnel`, to reach it from outside the tailnet) can put HTTPS in
front of port 5180 (see `tailscale serve --help` for your version); the
plugin URL then becomes `wss://<machine>.<tailnet>.ts.net/ws`.

**List the server's addresses.** Tell the server every address clients use,
so signing in with the identity key works on each of them and plugins can
keep their identity when you switch between them (see "Moving the server" and
"Key login and the server's address" below). For the tailnet plus Funnel case:

```powershell
$env:LookingGlass__PublicUrls__0 = 'ws://<machine-name>:5180/ws'
$env:LookingGlass__PublicUrls__1 = 'wss://<machine-name>.<tailnet>.ts.net/ws'
```

(`export LookingGlass__PublicUrls__0=...` on Linux; or a `PublicUrls` list in
`appsettings.json`.) List an address such as `ws://127.0.0.1:5180/ws` too if
a plugin uses it. Without any, a Development server still signs plugins in
with their key by the address each connection names (weaker, see below) and
says so when it starts; a server not in Development turns key login off.

Check a server from any machine:

```sh
dotnet run --project tools/LookingGlass.DevTool -c Release -- smoke --server ws://<machine-name>:5180/ws
```

It registers a throwaway debug user, creates a channel, invites the echo bot,
sends a message and waits for the reply.

## Testing alone (debug tooling)

- **Echo bot.** On a Development server, invite `Echo Bot` on world `Debug`.
  It accepts, takes part in rekeys, and echoes everything. Send `!ping`,
  `!rekey` or `!leave` to exercise those paths. Run extra bots with
  `lgdev bot --server ... --name "Another Bot"`.
- **`/lgdebug`** in game: connection state, fingerprint, limits, a protocol
  trace (frame types only, never contents), recent notices, and buttons to
  ping, reconnect, refresh, force a rekey, send a test message, or print a
  simulated incoming message locally.
- **Debug accounts** (`LookingGlass:Dev:AllowDebugAccounts`) let characters on
  the fake world `Debug` register without Lodestone. Never enable this on a
  public server.

## Loading the plugin

Build in Release, then in Dalamud settings → Experimental → Dev Plugin
Locations add `src/LookingGlass.Plugin/bin/Release/LookingGlass.dll`.

Each character's identity and channel keys, per server address, are kept
encrypted in a `secrets-<character>-<address hash>.bin` file in the plugin's
config folder (under XIVLauncher's `pluginConfigs`). The file also records
the address it belongs to, and the plugin refuses to use it for any other
address, so one server's keys and login are never sent to another. Every save
keeps the previous version next to it as `secrets-….bin.bak`, and if the file
is missing or damaged the plugin loads the backup and says so in chat.
Earlier versions named these files after a shorter hash (12 hex digits); the
plugin moves each one to its new name the first time its address is used (at
startup for the configured address), and keeps the old file as a backup. It
moves a file only once: if the new file (and its `.bak`) are lost later, the
plugin doesn't go back to the old one by itself, since it may hold an older
login, older channel keys, or a key you have reset since. Instead Settings
says "A backup of your identity from <date> exists. Restore it?", and restores
it only if you confirm. No file is ever deleted.

**Signing in.** The Lodestone proves the character is yours once, when you
register. After that the plugin signs in with the login (a device token) it
was given, and if the server doesn't recognise that (say, it was restored
from a backup), with your identity key: the server checks a signature made
with it and gives this device a new login, with no Lodestone step. Only if
the server doesn't accept the key either (it has never known your account,
you reset your identity elsewhere, the key is gone, or the server has key
login off) does the main window say "Login not recognised". The plugin keeps
your login and tries it again every minute or so, so it works again by itself
once the right server is back; "Retry now" tries the login and the key at
once. Register again through the Lodestone only if your key was lost, or the
server has never known your account (or can't sign you in with your key).
Registering again keeps the identity key the plugin has, so your channels
keep working.

**Reset my identity** (Settings, under "Your identity") is for a key that was
lost or may have been stolen. If you're connected, the plugin first asks the
server to retire the old key (a request signed with that key, for your current
login, so a stolen login alone can't do it): every login made with it stops
working at once, and the key can never sign in or be registered on that server
again. Then it makes new identity keys for the character on this server, and
keeps nothing of the old identity there (its login and channel keys): not in
this address's file, not in the copies a move made for the server's other
addresses, and not in backups (`.bak` files and the old-style file), though
what they hold about others (pinned keys, blocked users, channel positions) is
kept. You then register again through the Lodestone. If you weren't connected,
or the server couldn't be told (an older server doesn't know how), the plugin
says so: the old key and logins then keep working on the server until you have
registered again. Between the two, the account has no working login, as if
the server had lost it, and others still see your old key. You lose your place
in every channel on that server: someone must remove you and invite you again,
and everyone who knows you sees a "key changed" warning. Your identity on
other servers isn't affected.

A key replaced on the server, by "Reset my identity" or by registering again
with new keys, is never accepted there again: registering it says to reset
your identity instead, and it can't sign in.

**Moving the server.** Identities are kept per address, so
`ws://lookingglasschat:5180/ws`, `ws://127.0.0.1:5180/ws` and
`wss://lookingglasschat.<tailnet>.ts.net/ws` count as different servers. When
you change the address in Settings and a character has an identity for the
old address but none for the new one, the plugin first asks the server at the
old address (which you already trust with your login) whether the new address
is one of its own, and then the server at the new address whether the old one
is one of its own. Only if the new address is `wss://` and both list the other
(in `LookingGlass:PublicUrls`) does it offer "This is the same server. Keep
your identity?": yes asks both servers once more (the dialog may have been
open a while) and, if they still agree, copies the character's keys, login and
channels to the new address, so you don't register again and stay in your
channels. Otherwise it says why (the new address isn't `wss://`, the old
server doesn't list the new address, the new one doesn't list the old, one of
them can't be reached, or the server lists no addresses), and the new address
counts as a different server: you'd register there with new keys. A server at
a new address can never get your identity by claiming to be the old one: the
old server has to say so. Only `wss://` counts because the servers vouch for
names, not for whoever answers at them: over plain `ws://`, or a short name the
local network resolves, someone else could answer at the new name, repeat the
real server's addresses, and receive your login in the clear; TLS proves which
server answers. Either way the identity for the old address is kept, so
switching back works. Numbers, nicknames and colours belong to the character
and the channels, so they follow along.

## Commands

| Command | What it does |
| --- | --- |
| `/lookingglass` or `/lg` | Open the main window: register, create and manage channels and invites |
| `/lgc1 <message>` … `/lgc50 <message>` | Send to the channel on that number |
| `/lgc <nickname> <message>` | Send to the channel with that nickname, numbered or not |
| `/lgdebug` | Open the debug window |

Only `/lgc` is listed in Dalamud's command help (`/xlhelp`); the fifty
numbered commands are hidden there to keep the list short. `/lgc` on its own
prints how to use it.

**Numbers.** Each channel you're in gets a number automatically, and keeps it
across restarts until you leave it (or it's disbanded, or you're removed);
the freed number then goes to the next channel without one. To change a
channel's number, select it in the main window's channel list and click its
`/lgcN` tag under the name: if another channel has the number you pick, the
two swap. The list shows which channel has each number. Typing a number with
no channel on it says so.

**Nicknames.** Select a channel, click **+ nickname** (or its `/lgc <nickname>`
tag) next to its number, type one and press **Set** (or **Clear** to
remove it). A nickname is 1 to 16 letters, digits, `-` or `_`,
can't be only digits (so `/lgc 3` is never confused with `/lgc3`), and must
be different from your other channels' nicknames, ignoring case: `/lgc Sky hi`
and `/lgc sky hi` go to the same channel. Problems are shown under the box.
Nicknames, like numbers, are kept per character in the plugin's settings and
are never sent to the server; a channel's nickname goes away when you leave it.
In chat, a channel with a nickname is tagged with it, as in `[sky]`, instead of
its number (`[LGC3]`); turn off **Show nicknames in chat tags** in Settings to
always see numbers. A channel with neither (more than fifty channels, or a
message that arrives before the channel list is in) is tagged `[LGC]`.

**Colours.** In a channel's menu (the ⋮ button next to its name), choose
**Colour...** to give it one of the game's own chat colours, or click the
coloured dot before its name. Its lines in chat take that colour (or only the
tag, if you turn that off in Settings), and so does the bar beside it in the
channel list. **Default** colours only the tag, as before. Colours are kept
per character like nicknames.

**Unread messages.** The channel list counts messages from others since you
last looked at a channel in the main window or talked in it, and the window's
title shows the total. The counts start again from zero when you log in.

**Members.** The icon before each member says whether you've compared
fingerprints with them (a question mark until you have, a check once you
marked them verified, a warning if their key changed); click it to compare.
Its colour says whether they're online: green while they're connected, grey
when they aren't (a warning keeps its orange either way, and invitees stay
grey until they join). Hover over it to see which. Their ⋮ menu has the rest. To remove someone, hold **Ctrl** while choosing
**Remove from channel** (it stays greyed out otherwise); cancelling an invite
happens straight away. Leaving or disbanding a channel (from the channel's ⋮
menu) asks first.

## Server configuration

Settings live in `appsettings.json` next to the server binary, and can be
overridden on the command line (`--LookingGlass:Announcement="Hello"`) or
with environment variables (`LookingGlass__Dev__AllowDebugAccounts=true`).
Relative paths, such as the default `data` folder for the database, resolve
against the install folder.

Production deployment: `deploy/lookingglass.service` (systemd) or the
`Dockerfile`. By default the server only listens on `127.0.0.1:5180`; put a
TLS reverse proxy (for example Caddy) in front and use `wss://` URLs.

**Upgrading from 0.1:** 0.1's channels have no membership log, and nobody can
sign one for them now, so 0.2 refuses to start on a database that has any. It
says which file it is and leaves it unchanged: stop the server, delete or move
that file (with its `-wal` and `-shm` files, if any), and start again.
Everyone registers again and creates their channels anew. A database without
channels is upgraded in place.

Per-IP limits (registrations and concurrent connections) only work if the
server sees real client addresses. It reads them from `X-Forwarded-For`, but
only when the connection comes from a trusted proxy: one on the same machine
(loopback), or one listed in `LookingGlass:TrustedProxies`, which takes
single addresses (`"10.0.0.5"`) and networks in CIDR form
(`"172.17.0.0/16"`). Otherwise every client appears to be the proxy, and the
limits apply to everyone together. IPv6 clients are counted per /64.

**Key login and the server's address.** When a plugin signs in with its
identity key, the signature names the address it connected to, and the
server only accepts one of its own addresses (scheme, host and port; not the
path), so another server you use can't pass your signature on to this one.
"Its own addresses" are the ones listed in `LookingGlass:PublicUrls`; list
every address clients use, for example:

```sh
LookingGlass__PublicUrls__0=wss://chat.example.com/ws
# A tailnet server, reached directly and through Tailscale Funnel:
LookingGlass__PublicUrls__0=ws://<machine-name>:5180/ws
LookingGlass__PublicUrls__1=wss://<machine-name>.<tailnet>.ts.net/ws
```

The server also tells plugins these addresses, which is how a plugin moving
between two of them keeps its identity (see "Moving the server" above), but
only to a `wss://` one. Listing short or plain `ws://` names (a tailnet
machine name, `127.0.0.1`) is still fine for key login on a private network;
plugins just can't move their identity to them.

**Without `PublicUrls`, key login is off** (the server says so when it
starts): plugins whose login it doesn't recognise must register again
through the Lodestone. The exception is a server in Development, which then
accepts the address each connection names in its `Host` header (and scheme,
from `X-Forwarded-Proto` behind a trusted proxy), and warns that this is
weaker: whoever opens a connection chooses its Host header, so a malicious
server relaying your signature simply sends the address you signed for. On
such a server, what protects you is that the plugin keeps separate identity
keys per server address, so the key you sign with for another server isn't
registered on this one (and a key is only carried to another, `wss://`,
address when both addresses' servers list each other). Fine for a private test server;
list the addresses anywhere else.

Key logins are limited per connection (3 challenges), per address
(`KeyLoginsPerHourPerIp` challenges, and `KeyLoginFailuresPerHourPerIp`
failures, under `LookingGlass:Limits`; a challenge counts as a failure until
it is answered correctly, so an address that only asks for challenges is
stopped too) and per account from each address (failed answers only: half
the per-address failure limit, rounded up, so with the default of 10 an
address may fail 5 times an hour for one account). Nothing is limited per
account alone: a signature made with the identity key can't be guessed, so
failures from other addresses never stop you signing in from yours (an
address you share with an attacker, such as one NAT, still shares its
per-address limits). Each user keeps their 20 most recently used devices;
older ones are dropped as new ones are added.

**Docker:** a reverse proxy on the host reaches the container through Docker's
bridge network, so inside the container the proxy's address is the bridge
gateway (often `172.17.0.1`), not loopback. Trust the bridge network, for
example `LookingGlass__TrustedProxies__0=172.17.0.0/16` (check yours with
`docker network inspect bridge`). Only do this if nothing untrusted can
connect to the container from that network; a proxy running in another
container on a user-defined network needs that network trusted instead.

## Security model

What the encryption does today:

- Each character has a long-term Ed25519 signing key and X25519 key. Others
  see a 25-digit fingerprint. Clients pin each user's keys and name on first
  use and show a persistent "key changed" warning when they change. Members
  whose fingerprint you haven't compared show "not compared" (compare
  fingerprints over /tell: click the icon before a member's name, or **Compare
  fingerprints** in their ⋮ menu, then **Mark verified**).
- Who is in a channel, and with what rank, comes from the channel's
  membership log: a hash-chained list of changes, each signed by the member
  who made it. Invites are signed by a moderator or the admin, accepts by the
  exact key the invite named, removals by a moderator or admin ranked above
  the member removed, and rank changes and admin transfers by the admin; the
  admin can't leave while others remain, and an open invite lapses when
  whoever made it is removed, leaves or is demoted. Every client replays
  and checks the log itself, and saves the newest position it has verified,
  so it carries on from there after a restart. The server checks entries
  too, but nothing relies on that: it can't add a member, change a rank, or
  reorder the log, because that takes a member's signature and breaks the
  hash chain. Members are bound to the keys the log admitted them with, so a
  removed member's key signs nothing that counts, and neither does the new
  key of a member who registered again, until they are invited again.
- If a client sees two different, validly signed versions of the log (a
  fork: someone is being shown a different member list), or the server
  shows it an older log than it has already verified (it may be hiding a
  change, such as a removal), it says so, keeps what it verified, and marks
  the channel "check members".
- Each channel has an epoch key. Any join, leave or removal makes a member
  generate a new one, seal it to exactly the members at the log's head (with
  the keys the log has for them), and sign it together with that log
  position and a commitment to the key. The server refuses a rekey unless it
  is for the log's head and every copy carries the same commitment, so a
  member who hands someone a different or unreadable key is named in a
  warning, and that client rekeys. The server stores and forwards the sealed
  copies but can't open them.
- Clients only accept a new epoch key from a member in their verified log,
  only for a newer epoch than they hold, and only if it was made for the
  current membership: a key made before the last join or leave is refused
  (with a warning that the server may be hiding a change), and for a key
  made at a newer position the client fetches and checks the log first.
  They send with the newest key they hold, whatever epoch the server claims,
  and rekey first if that key predates the last join or leave.
- Messages are XChaCha20-Poly1305 encrypted under the epoch key and signed by
  the sender. The server can't read them, alter them, or attribute them to
  someone else. A message is only accepted from a member in the verified log,
  signed with the key the log has for them, and under an older epoch only
  within 2 minutes of the client getting the newer key.
- Replays: clients remember the IDs of recent verified messages (in memory),
  drop messages dated more than 10 minutes from their own clock, and save,
  per channel and sender, the timestamp of the newest message accepted.
  Messages more than 2 minutes older than that are dropped, even after a
  restart. Those timestamps are saved with other changes, on shutdown, and
  while messages arrive at least every 5 minutes, so a crash can lose up to
  about 5 minutes of them. Your own messages aren't recorded this way, so
  after a restart the server could replay one you sent in the last 10
  minutes back to you.
- Channel names carry a signed epoch, revision and log position. Clients only
  accept a name encrypted under the key they use, signed by a member, made for
  the current membership, and never one older than the newest they have
  accepted (remembered across restarts), so a server can't roll a name back,
  whether to a name from an older epoch, an earlier rename, or an older
  membership. Only the admin renames; a rekey carries the name into the new
  epoch, and clients warn if a member's rekey changed it.
- Signing in: the Lodestone check happens once, at registration. After
  that a client signs in with its device token or, if the server no longer
  knows the token, by signing a single-use challenge with its current
  identity key (a key replaced or retired by "Reset my identity" can't, and
  can't be registered again either). The signature
  names the server's address, and the server only accepts its configured
  `PublicUrls`, so a server can't replay it to another (a Development server
  without them goes by the Host header, which doesn't stop this). The plugin
  also keeps separate keys per server address, bound to the address inside
  the file; never follows redirects, so a server can't hand your connection
  and login to another; and only carries an identity to a new address when
  it is `wss://` and the servers at both addresses list each other.
- Clients can block users: their invites are declined unseen and their
  messages hidden. An invite is only shown as verified once the client has
  checked it against the channel's log, and one from someone whose identity
  key changed can't be accepted until it is marked verified.

What it does not do yet (0.2):

- **You trust the keys of the people you invite on first use.** When you
  invite someone by name, the server supplies their key and could substitute
  its own. Each member shows "not compared" until you compare fingerprints
  over /tell and mark them verified. There is no strict mode yet that refuses
  to invite, or seal keys to, anyone not compared.
- **A removal takes effect when the remover's client publishes it.** The
  remover's client rekeys straight away. A server that suppresses that rekey
  stops the channel working for everyone else, and the remover is warned; but
  members who never saw the removal can be shown the old membership, and a
  key they make for it reaches the removed member (anyone who did see the
  removal refuses that key).
- Key commitments rely on the server checking them: a member colluding with
  the server can still give different members different keys, which shows up
  as messages some members can't decrypt.
- Rekeys seal the new key to every member in the log, including one whose
  "key changed" warning you haven't cleared.
- A member with new keys (after losing their config, or "Reset my identity") has
  no place in their channels until a moderator removes and invites them
  again. If that member was the admin, nobody can take over the admin role:
  moderators can still invite and remove members, but renames and rank
  changes are no longer possible in that channel.
- The log only grows. Clients fetch just the new entries, but someone new to a
  channel (or invited to it) replays it from the start.
- The server sees metadata (who is in which channel, when messages are sent)
  and can drop or delay anything.
- Members who share a channel see when each other are online: the server
  tells them when a fellow member connects or disconnects, and when someone
  online joins. Invitees and people you share no channel with aren't told,
  and you aren't shown to them. This comes from the server, which could lie
  about it.
- Debug accounts on a Development server can be taken over by anyone who can
  reach it.

## License

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only). If you
run a modified version of the server for other people, you must offer them its
source code (section 13).
