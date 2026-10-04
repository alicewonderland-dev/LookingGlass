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
./scripts/run-dev-server.ps1       # Windows PowerShell
```

This listens on port 5180 on all interfaces in **Development** mode, which
turns on debug accounts and runs an echo bot inside the server. Only do this
on a private network such as your tailnet: anyone who can reach a Development
server can register (or take over) any debug account, including the echo bot.

1. Make sure the machine's firewall allows TCP 5180 from the tailnet.
2. In game, open `/lookingglass`, click the gear at the top right (or the
   plugin's settings button in Dalamud's plugin list), and set the server URL
   to `ws://<machine-name>:5180/ws` (the Tailscale MagicDNS name or 100.x IP).
3. Register your character. The main window walks you through it: get a
   code, paste it into your Lodestone profile, then press **Verify**.

Tailscale already encrypts traffic between devices, and message contents are
end-to-end encrypted regardless. For TLS anyway, `tailscale serve` can put
HTTPS in front of port 5180 (see `tailscale serve --help` for your version);
the plugin URL then becomes `wss://<machine>.<tailnet>.ts.net/ws`.

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

Each character's identity and channel keys, per server, are kept encrypted
in a `secrets-….bin` file in the plugin's config folder (under XIVLauncher's
`pluginConfigs`). Every save keeps the previous version next to it as
`secrets-….bin.bak`, and if the file is missing or damaged the plugin loads
the backup and says so in chat. So to reset a character's identity (you then
register again, other members see that your key changed, and in each of your
channels a moderator must remove you and invite you again), disconnect,
delete **both** the secrets file and its `.bak`, then connect again.

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
`/lgcN` button under the name: if another channel has the number you pick, the
two swap. The list shows which channel has each number. Typing a number with
no channel on it says so.

**Nicknames.** Select a channel, click **+ Nickname** (or its `/lgc <nickname>`
button) next to its number, type one and press **Set** (or **Clear** to
remove it). A nickname is 1 to 16 letters, digits, `-` or `_`,
can't be only digits (so `/lgc 3` is never confused with `/lgc3`), and must
be different from your other channels' nicknames, ignoring case: `/lgc Sky hi`
and `/lgc sky hi` go to the same channel. Problems are shown under the box.
Nicknames, like numbers, are kept per character in the plugin's settings and
are never sent to the server; a channel's nickname goes away when you leave it.

**Colours.** Click the swatch next to a channel's commands to give it one of
the game's own chat colours. Its lines in chat take that colour (or only the
`[LGC]` tag, if you turn that off in Settings), and so does its place in the
channel list. **Default** colours only the tag, as before. Colours are kept
per character like nicknames.

**Unread messages.** The channel list counts messages from others since you
last looked at a channel in the main window or talked in it, and the window's
title shows the total. The counts start again from zero when you log in.

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
  fingerprints over /tell: a member's ... menu, **Compare fingerprints**, then **Mark verified**).
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
- A member who registers again (new keys, say after losing their config) has
  no place in their channels until a moderator removes and invites them
  again. If that member was the admin, nobody can take over the admin role:
  moderators can still invite and remove members, but renames and rank
  changes are no longer possible in that channel.
- The log only grows. Clients fetch just the new entries, but someone new to a
  channel (or invited to it) replays it from the start.
- The server sees metadata (who is in which channel, when messages are sent)
  and can drop or delay anything.
- Debug accounts on a Development server can be taken over by anyone who can
  reach it.

## License

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only). If you
run a modified version of the server for other people, you must offer them its
source code (section 13).
