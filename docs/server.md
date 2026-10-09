# Running a LookingGlass server

This guide is for people who build LookingGlass, run a server for others, or
test the plugin. For what the server does and why, see [design.md](design.md).

The server is one ASP.NET Core process with one SQLite database file. It runs
on Linux and Windows. It only ever sees encrypted channel names and messages.

## Contents of the repository

| Path | What it is |
| --- | --- |
| `src/LookingGlass.Protocol` | The wire protocol (`Protos/lookingglass.proto`), shared by everything |
| `src/LookingGlass.Core` | Cryptography, the membership log, the client session and the echo bot. No Dalamud dependency |
| `src/LookingGlass.Server` | The server: ASP.NET Core with SQLite |
| `src/LookingGlass.Plugin` | The Dalamud plugin |
| `tools/LookingGlass.DevTool` | `lgdev`: runs an echo bot, or smoke-tests a server |
| `tests/LookingGlass.Tests` | Cryptography, policy, membership log and end-to-end tests, including a malicious in-process server |
| `scripts/` | Running a development server; packing a Linux release |
| `deploy/` | The systemd unit and the Linux installer |
| `Dockerfile` | A container image of the server |
| `.github/` | CI (build, tests, Linux packages) and Dependabot's weekly updates |

## Building and testing

You need the .NET 10 SDK (newer SDKs work too). The plugin also needs
Dalamud's development files, which XIVLauncher installs.

```sh
dotnet build LookingGlass.slnx -c Release
dotnet test LookingGlass.slnx -c Release
```

Without XIVLauncher, unpack Dalamud's
[latest.zip](https://goatcorp.github.io/dalamud-distrib/latest.zip) and set
`DALAMUD_HOME` to its folder. GitHub Actions does that and runs the same two
commands on every push to main and every pull request, and packs both Linux
servers ([.github/workflows/ci.yml](../.github/workflows/ci.yml)).

## Development and production

The server behaves differently depending on its ASP.NET Core environment
(`ASPNETCORE_ENVIRONMENT`).

| | Development | Production (the default) |
| --- | --- | --- |
| Debug accounts | On (`appsettings.Development.json`) | Off |
| Echo bot inside the server | On | Off |
| Announcement | "LookingGlass test server. Debug accounts are enabled." | None |
| `PublicUrls` missing | Starts, with a warning, and trusts each connection's `Host` header | Refuses to start |
| Debug accounts or echo bot turned on | Fine | Refuses to start, unless `Dev:AllowOutsideDevelopment` is also on (then warns) |

Either way the server listens on `127.0.0.1:5180` unless `--urls` (or the
`Urls` setting) says otherwise. The development scripts listen on all
interfaces.

**Only run Development on a private network**, such as your own tailnet.
Anyone who can reach a Development server can register, or take over, any
debug account, including the echo bot, along with its channels.

## Running a test server

### Starting it

On the server machine:

```sh
./scripts/run-dev-server.sh        # Linux or macOS
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-dev-server.ps1   # Windows
```

Windows blocks `.ps1` scripts by default. `-ExecutionPolicy Bypass` applies to
that one run only and changes no system setting.

The scripts build and run the server in Release, in Development mode, on port
5180 on all interfaces. Change the port with `PORT=5181` (shell script) or
`-Port 5181` (PowerShell). Further arguments are passed to the server.

### Where the database lives

The scripts keep the database outside the build output. Rebuilding, cleaning
or running another checkout of the code keeps your registrations and
channels.

| System | Folder |
| --- | --- |
| Windows | `%USERPROFILE%\.lookingglass\dev-server` |
| Linux | `$XDG_DATA_HOME/lookingglass/dev-server`, usually `~/.local/share/lookingglass/dev-server` |

The script prints the folder it uses. To use another one, set
`LookingGlass__DataDirectory` first; the script says when it does.

On Windows the folder isn't under `AppData` on purpose. Windows redirects
`AppData` writes from packaged apps to a private folder, so a tool started
from one (an AI coding assistant, say) would see a different database from
yours.

### Reaching it over Tailscale

1. Allow TCP port 5180 from the tailnet in the machine's firewall.
2. List the server's addresses (see [The server's addresses](#the-servers-addresses)).
3. In game, open `/lookingglass` and click the gear in its title bar (or the
   plugin's settings button in Dalamud's plugin list). Set **Server address** to
   `ws://<machine>:5180/ws`, using the machine's MagicDNS name or its 100.x IP.
   Use exactly one of the addresses you listed: the plugin warns as soon as it
   connects through any other.
4. Register the character as the main window shows (see the
   [README](../README.md#getting-started)).

Tailscale already encrypts traffic between devices, and messages are
end-to-end encrypted anyway. For TLS as well, `tailscale serve` (or
`tailscale funnel`, to reach the server from outside the tailnet) can put
HTTPS in front of port 5180; see `tailscale serve --help` for your version.
The plugin's address then becomes `wss://<machine>.<tailnet>.ts.net/ws`.

### Checking that a server works

From any machine:

```sh
dotnet run --project tools/LookingGlass.DevTool -c Release -- smoke --server ws://<machine>:5180/ws
```

It registers a throwaway debug user, creates a channel, invites the echo bot,
sends a message and waits for the reply. Exit code 0 means the server works.
It needs debug accounts and an echo bot on the server.

The server also answers `GET /health` with `{"status":"ok","version":"…"}`, and
nothing else (not who, or how many, are online).

## Testing alone

- **Debug accounts** (`LookingGlass:Dev:AllowDebugAccounts`) let characters on
  the fake world `Debug` register without the Lodestone. Never enable this on a
  public server; the server logs a warning when it is on.
- **The echo bot.** On a server with debug accounts, invite `Echo Bot` on world
  `Debug`. It accepts, takes part in rekeys and echoes everything. Send
  `!ping`, `!rekey` or `!leave` to exercise those paths. The server runs one
  itself when `LookingGlass:Dev:HostEchoBot` is on (Development turns it on);
  its keys are kept in the data folder.
- **More bots.** Run `lgdev bot --server <address> --name "Another Bot"`. It
  runs until Ctrl+C and keeps its keys in a `<name>-secrets.json` file
  (`--secrets` to choose another).
- **`/lgdebug`** in game shows the connection state, your fingerprint, the
  server's limits, a protocol trace (frame types only, never contents) and
  recent notices. Its buttons ping, reconnect, refresh, forget the account,
  force a rekey, send a test message, or print a simulated incoming message
  locally.

## Configuration

### Where settings come from

Settings live in `appsettings.json` next to the server binary
(`appsettings.Development.json` adds to it in Development). Any setting can be
overridden:

- on the command line: `--LookingGlass:Announcement="Hello"`
- with an environment variable: `LookingGlass__Dev__AllowDebugAccounts=true`
  (a double underscore separates levels; list items are numbered, as in
  `LookingGlass__PublicUrls__0`)

Relative paths, such as the default `data` folder, resolve against the install
folder, however the server is started.

### Settings

All of these are under `LookingGlass`.

| Setting | Default | What it does |
| --- | --- | --- |
| `DataDirectory` | `data` | Folder for the database (`lookingglass.db`) and the hosted echo bot's keys |
| `Announcement` | empty | Shown to every client when it connects |
| `PublicUrls` | empty | Every address clients connect to. Required outside Development: see below |
| `TrustedProxies` | empty | Proxies, besides this machine, whose `X-Forwarded-For` is believed: see below. Like this machine's, their addresses are never flagged or blocked, and banned only with `--force` |
| `Lodestone:BaseUrl` | `https://na.finalfantasyxiv.com` | Where characters are looked up |
| `Lodestone:MinDelaySeconds` | 2 | Least time between Lodestone requests, server-wide |
| `Lodestone:ChallengeMinutes` | 15 | How long a registration code can be used, 1 to 60. The server won't start with anything else |
| `Dev:AllowDebugAccounts` | false | Characters on world `Debug` register without the Lodestone |
| `Dev:HostEchoBot` | false | Runs the echo bot inside the server. Needs `AllowDebugAccounts` |
| `Dev:EchoBotName` | `Echo Bot` | The hosted echo bot's name |
| `Dev:EchoBotServerUrl` | empty | Where the hosted echo bot connects. Empty: worked out from the server's own address |
| `Dev:AllowOutsideDevelopment` | false | Lets `AllowDebugAccounts` and `HostEchoBot` be on outside Development. Without it the server refuses to start with either there |
| `Database:CheckpointMinutes` | 0 | Minutes between explicit (PASSIVE) checkpoints of the write-ahead log; 0 leaves them to SQLite and Litestream: see [Litestream](#replicating-with-litestream) |
| `Messages:KeepDays` | 7 | Days the server keeps each message it relays (encrypted, as relayed), so members who were away get it when they're back: see [Stored messages](#stored-messages). 0 to 365; 0 keeps none |
| `Messages:MaxPerChannel` | 5000 | Messages kept per channel at most; past it the oldest go first. 0 to 100,000; 0 keeps none |
| `Limits:RegistrationsPerHourPerIp` | 10 | Registrations started per IP address (IPv6: per /56) per hour. One whose character the Lodestone doesn't list (or can't be asked about) doesn't count |
| `Limits:RegistrationLookupFailuresPerHourPerIp` | 20 | Lodestone requests per IP address (IPv6: per /56) per hour made by registrations whose character the Lodestone doesn't list (a typo, a new character, a private profile) or that it couldn't answer; a search makes 1 or 2. Past it, nothing more is looked up for that address until the hour is up. 1 to 10,000 |
| `Limits:RefusedRegistrationsPerHourPerIp` | 10 | Registrations refused for naming an address this server doesn't list, logged per IP per hour; refused silently past that |
| `Limits:KeyLoginsPerHourPerIp` | 60 | Key login challenges per IP address per hour |
| `Limits:KeyLoginFailuresPerHourPerIp` | 10 | Failed key logins after which an IP address gets no more challenges for the hour |
| `Limits:ConnectionsPerIp` | 20 | Concurrent connections per IP address (IPv6: per /56) |
| `Limits:NotLoggedInConnectionsPerIp` | 4 | Connections per IP address that haven't logged in yet, at once |
| `Limits:NotLoggedInSeconds` | 180 | How long a connection may stay without logging in, unless it is registering |
| `Limits:MaxConnections` | 10000 | Connections in all; at the cap the oldest not logged in makes room, and only if all have logged in is a new one refused |
| `Limits:ConnectionsPerMinutePerIp` | 60 | New connections per IP address per minute |
| `Limits:RequestsPerSecondPerConnection` | 20 | Requests one connection may make per second, on average; faster ones are slowed down, not refused |
| `Limits:RequestBurstPerConnection` | 200 | Requests one connection may make at once before that applies |
| `Limits:MaxIdentitiesPerRequest` | 500 | Users one identity lookup may ask for |
| `Limits:SendQueueLength` | 256 | Events queued for one connection before it is dropped as too slow |
| `Limits:InviteBurstPerInviter` | 60 | Invites one user may send at once, to anyone. 1 to 10,000 |
| `Limits:InviteIntervalSecondsPerInviter` | 5 | Seconds between their invites once those are spent. 1 to 86,400 |
| `Limits:InviteBurstPerInvitee` | 30 | Invites one user may be sent at once, by everyone together. 1 to 10,000 |
| `Limits:InviteIntervalSecondsPerInvitee` | 10 | Seconds between invites to them once those are spent. 1 to 86,400 |
| `Limits:InviteBurstPerPair` | 20 | Invites one user may send one other at once (one friend to 20 channels in a row). 1 to 10,000, and less than `InviteBurstPerInvitee` |
| `Limits:InviteIntervalSecondsPerPair` | 60 | Seconds between their invites to that one once those are spent. 1 to 86,400, and more than `InviteIntervalSecondsPerInvitee` |
| `Limits:MaxPendingInvitesPerUser` | 50 | Invites one user can have waiting at once, across all channels. 2 to 200 |
| `Limits:MaxPendingInvitesFromOneInviter` | 25 | Of those, how many can be from any one inviter. 1 to one less than `MaxPendingInvitesPerUser` |
| `Limits:LookupBurst` | 60 | Players one user may look up by name at once (each invite by name starts with one). 1 to 10,000 |
| `Limits:LookupIntervalSeconds` | 1 | Seconds between their lookups once those are spent. 1 to 86,400 |
| `Limits:MaxLocalRecipients` | 50 | Players one local chat message may go to (the sender's plugin picks the closest friends). 0 to 200; 0 turns local chat off: see [Local chat](#local-chat) |
| `Limits:LocalMessageBurst` | 5 | Local chat messages one user may send at once, as for channel messages. 1 to 10,000 |
| `Limits:LocalMessageIntervalSeconds` | 1 | Seconds between their local chat messages once those are spent. 1 to 86,400 |
| `Limits:LocalMessagesReceivedBurst` | 120 | Local chat messages one user may be sent at once, by everyone together; past it their copies are dropped (the senders aren't told). 1 to 10,000 |
| `Limits:LocalMessagesReceivedIntervalSeconds` | 1 | Seconds between local chat messages one user may be sent once those are spent. 1 to 86,400 |
| `Limits:LocalMessagesBetweenBurst` | 30 | Local chat messages one user may send one other at once; past it those copies are dropped. 1 to 10,000, and less than `LocalMessagesReceivedBurst` |
| `Limits:LocalMessagesBetweenIntervalSeconds` | 2 | Seconds between them once those are spent. 1 to 86,400, and more than `LocalMessagesReceivedIntervalSeconds` |
| `Abuse:...` | | When an account or address refused by limits again and again is flagged, how bans are picked up, and automatic blocks (on): see [Flags and bans](#flags-and-bans) |

The server won't start with an invite, lookup, registration lookup or local chat setting outside its range: see
[Limits worth knowing](#limits-worth-knowing).

The address the server listens on is the top-level `Urls` setting
(`http://127.0.0.1:5180`), or `--urls` on the command line.

### The server's addresses

`LookingGlass:PublicUrls` lists every address clients use, exactly as typed
into the plugin. For example:

```sh
LookingGlass__PublicUrls__0=wss://chat.example.com/ws
```

or, for a private tailnet server reached directly and through Tailscale
Funnel:

```sh
LookingGlass__PublicUrls__0=ws://<machine>:5180/ws
LookingGlass__PublicUrls__1=ws://100.x.y.z:5180/ws
LookingGlass__PublicUrls__2=wss://<machine>.<tailnet>.ts.net/ws
LookingGlass__PublicUrls__3=ws://127.0.0.1:5180/ws
```

(In PowerShell, `$env:LookingGlass__PublicUrls__0 = '...'`; or a `PublicUrls`
list under `LookingGlass` in `appsettings.json`.)

List **every** address a plugin uses: the MagicDNS name, the 100.x IP if
anyone connects by it, the Funnel or `tailscale serve` name, and
`ws://127.0.0.1:5180/ws` for a plugin on the server machine itself. The scheme,
host and port must match; the path doesn't matter.

**Why the list matters.** Registering, signing in with the identity key (key
login) and "Reset my identity" are each signed for the address the plugin
connected to. The server accepts only its own listed addresses. That stops a
malicious server that a user also connects to from passing these on to this
one: otherwise it could forward a user's registration here and get a login
to their account. Registration codes are made for the address too, and the
server gives out none for an address it doesn't list. The server also tells
plugins its `wss://` addresses, so a plugin can keep its identity when moving
between them.

When a plugin connects through an address that isn't listed, it says so
straight away and names the listed ones. The server refuses its
registrations and key logins, naming both addresses, and logs each refusal as
a warning. At startup, the server logs the addresses it accepts.

**Each listed address must be this server's alone.** The server tells its own
addresses from another server's by name only. An address another server can
also have protects nothing against that server. These can belong to another
server too:

- a short MagicDNS name (a machine on someone else's tailnet can have the same
  name), or a LAN name (`.local`, `.lan` and the like);
- a private, CGNAT (Tailscale's 100.x), loopback or link-local IP;
- any plain `ws://` address (whatever answers at that name on the user's
  network is taken for this server).

On your own tailnet, with testers who only use servers you run, that's fine.
For a server people use alongside servers others run, list and use only
`wss://` addresses with a fully qualified name, such as
`wss://<machine>.<tailnet>.ts.net/ws` or `wss://chat.example.com/ws`. The
server logs a warning at startup for each listed address that may not be its
alone.

**Without a list**, a server outside Development refuses to start and says
what to set. A Development server starts, warns, and accepts whatever address
each connection names in its `Host` header (and its scheme from
`X-Forwarded-Proto` behind a trusted proxy). That is weaker, because a
relaying server chooses the `Host` header. Separate keys per server address
still stop a relayed key login, but nothing stops a relayed registration or a
relayed registration code. Fine for a private test server; list the addresses
anywhere else.

### Behind a reverse proxy

Per-IP limits only work if the server sees real client addresses. It reads
them from `X-Forwarded-For`, but only from a trusted proxy: one on the same
machine (loopback), or one listed in `LookingGlass:TrustedProxies`. That list
takes single addresses (`"10.0.0.5"`) and networks in CIDR form
(`"172.17.0.0/16"`). Without a trusted proxy, every client appears to be the
proxy, and the limits apply to everyone together.

IPv6 clients are counted per /64.

### Limits worth knowing

- **Key logins** are limited per connection (3 challenges), per IP address
  (`KeyLoginsPerHourPerIp` challenges and `KeyLoginFailuresPerHourPerIp`
  failures), and per account from each address (as many challenges as per
  address, and half its failures, rounded up: 5 an hour with the default of
  10). A challenge counts as a failure until it is answered correctly. An
  account asking or failing again from an address that hour doesn't count
  against the address again, so plugins retrying a login the server lost
  (about 20 times an hour each, as connections that don't log in close after
  3 minutes) don't lock their neighbours out. Nothing is limited per account
  alone, so failures from other addresses never stop a user signing in from
  theirs. Users who share an address with an attacker (one NAT, say) share
  its per-address limits. Behind a large shared NAT, raise
  `KeyLoginsPerHourPerIp` and `KeyLoginFailuresPerHourPerIp` together.
- **Devices.** Each user keeps their 20 most recently used devices; older ones
  are dropped as new ones sign in. Players see their devices in Settings (when
  each was added and last used, nothing else), are told when a new one signs
  in, and can **Sign out everywhere else**, which also stops their key signing
  in until they register again through the Lodestone (see
  [design.md](design.md#other-computers-signing-in)). Listing them is limited
  to 20 at once, then 1 every 6 seconds per user, and signing out to 3 at
  once, then 1 a minute; not settings.
- **Connections.** Per IP address (an IPv6 client per /56, the least most
  ISPs give a customer): 20 open at once, 60 new ones a minute, and 4 at once
  that haven't logged in (`ConnectionsPerIp`, `ConnectionsPerMinutePerIp`,
  `NotLoggedInConnectionsPerIp`); past any, the WebSocket upgrade gets HTTP
  429. A connection that hasn't logged in is closed after 3 minutes
  (`NotLoggedInSeconds`; a plugin with a saved login logs in within
  milliseconds, and one without reconnects), unless it is registering: then
  when its registration code expires. A client that doesn't answer the
  server's ping (every 30 seconds) within 60 seconds is dropped.
- **Connections in all.** At most 10,000 (`MaxConnections`). At that cap a new
  connection still gets in: the oldest connection that hasn't logged in (one
  that isn't registering, if there is one; if all are registering, one from
  the address that would hold the most of them) is closed to make room. Only when
  every connection has logged in is a new one refused, with HTTP 503 (and a
  warning in the log, once a minute at most). So connections that never log
  in can't keep out plugins reconnecting. Each connection takes roughly 50 to
  200 KiB of memory, so 10,000 is at most 2 GB: fine on an Oracle Ampere
  machine, but lower it on a small one. The unit allows 65,536 open files.
- **Requests.** Each connection may make 200 requests at once, then 20 a
  second (`RequestBurstPerConnection`, `RequestsPerSecondPerConnection`). A
  faster client is slowed down (its next request is read only when due), never
  refused. Every request type also has its own per-user limits, and a frame
  is at most 128 KiB.
- **Invites.** Each user may send 60 at once, then one every 5 seconds
  (`InviteBurstPerInviter`, `InviteIntervalSecondsPerInviter`); be sent 30
  at once, by everyone together, then one every 10 seconds
  (`InviteBurstPerInvitee`, `InviteIntervalSecondsPerInvitee`); and send any
  one other user 20 at once, then one a minute (`InviteBurstPerPair`,
  `InviteIntervalSecondsPerPair`), enough to invite a friend to all of
  one's channels in one go. The last is checked first, and must stay smaller
  and slower than what a user may be sent, so that one inviter (perhaps one
  they blocked: the server doesn't know whom users block) can't use up all of
  it; the server doesn't start otherwise. A user can have 50 invites waiting
  (`MaxPendingInvitesPerUser`, as many as the channels they can be in), at
  most 25 from any one inviter (`MaxPendingInvitesFromOneInviter`, which must
  be less). A refused invite tells the inviter how long to wait, or that the
  invitee must answer some invites first, and is logged (see [Logs](#logs)).
  Each invite by name starts with a lookup of the player: 60 at once, then one
  a second (`LookupBurst`, `LookupIntervalSeconds`), and the plugin reuses a
  lookup for 10 minutes (until an invite with it fails), so inviting one
  friend to many channels costs one.
- **Local chat.** A local message goes to at most 50 players
  (`MaxLocalRecipients`), and each user may send 5 at once, then one a second
  (`LocalMessageBurst`, `LocalMessageIntervalSeconds`), as channel messages;
  each user may be sent 120 at once, by everyone together, then one a second
  (`LocalMessagesReceivedBurst`, `LocalMessagesReceivedIntervalSeconds`), and
  30 at once from any one sender, then one every 2 seconds
  (`LocalMessagesBetweenBurst`, `LocalMessagesBetweenIntervalSeconds`; checked
  first, and smaller and slower, or the server doesn't start, so a couple of
  accounts can't use up what someone's friends may send them), and
  past that, or while their connection's queue is half full, their copies are
  dropped rather than the connection closed.
  Its friends are looked up by name with the lookup limits above (the plugin
  reuses a lookup, and the answer that someone isn't registered, for 10
  minutes). See [Local chat](#local-chat).
- **The web server** (Kestrel, under `Kestrel:Limits` in `appsettings.json`)
  takes at most 12,000 connections, 12,000 of them WebSockets
  (`MaxConcurrentConnections`, `MaxConcurrentUpgradedConnections`: above
  `MaxConnections`, so the server's own rule decides, and these only back it
  up), plain HTTP request bodies of at most 64 KiB (the server takes none:
  WebSocket frames have their own limit), headers of at most 32 KiB, sent
  within 15 seconds, and closes idle keep-alive connections after a minute.
  An upgrade past its limit is answered 503 too. Behind a reverse proxy these
  count the proxy's connections.
- **Memory.** Per-address counters keep at most 100,000 addresses, and the
  Lodestone cache 10,000 searches (see [design.md](design.md#server-design)).
- **Repeated refusals.** Every refusal by one of these limits is counted, and
  an account or address refused again and again is flagged for you: see
  [Flags and bans](#flags-and-bans).
- **Stored messages** take disk space: see below.
- The full list of protocol limits is in
  [design.md](design.md#abuse-limits).

### Local chat

Local chat (`/lgl` in the plugin) is a `/say`-like chat among friends who
stand near each other in the game (see
[design.md](design.md#local-chat-friends-only)). It is the server capability
`local.v1`, on by default. The server keeps nothing of it and holds no
locations: the sender's plugin names the user IDs of the friends near it, and the server checks the request (each copy signed by the sender for its
recipient, each recipient once, at most `Limits:MaxLocalRecipients`, the
message at most 4 KiB) and passes each copy to its recipient if they are
online with a plugin that knows local chat. Nothing is stored for anyone
offline, so it needs no disk and no database change. It does see who sent to
whom and when, as it sees who is in which channel. And it learns who was
near whom: before sending, the plugin looks up each friend near the sender by
name (each at most once in 10 minutes), so the server sees which friends were
near the sender, and when, even friends who don't use LookingGlass (an open
decision of the owner's: see [design.md](design.md#local-chat-friends-only)).
What one user may be sent by everyone together is limited too, and a
recipient whose connection is slow loses local messages, not the connection.

To turn it off, set `LookingGlass:Limits:MaxLocalRecipients` to 0
(`LookingGlass__Limits__MaxLocalRecipients=0`): the server then doesn't agree
to it, and plugins say local chat isn't available on this server. Older
plugins never offer it and are never sent it.

### Stored messages

The server keeps the messages it relays so that a member who was logged out or
disconnected gets what they missed when they come back (message catch-up; see
[design.md](design.md#message-catch-up)). It keeps them exactly as it relays
them: encrypted, which it can't read, with who sent them, when and in which
channel, which it sees anyway. Each is kept `Messages:KeepDays` days (7), and
each channel keeps at most `Messages:MaxPerChannel` (5,000), the oldest going
first. The server sweeps them when it starts and every ten minutes. A channel
that is disbanded, or whose last member leaves, loses them at once. The
server's log never shows them.

**Disk use.** A stored message takes its ciphertext (at most 4 KiB; a line of
chat is usually 100 to 600 bytes) plus about 300 bytes of envelope (sender,
times, signature) and indexes. So:

| | Typical (about 500 bytes each) | Worst case (4 KiB each) |
| --- | --- | --- |
| One channel at the cap (5,000) | about 4 MB | about 22 MB |
| 1,000 channels at the cap | about 4 GB | about 22 GB |

One user can send at most one message a second after a burst of five, so a
user filling channels alone takes days: one channel's cap in under 1.5 hours,
the 50 channels a user can be in about 3 days, about 1.1 GB at worst. In
practice a channel stores what its members said in the last week, well under
its cap. The sealed channel
keys of epochs that still have stored messages are kept too (up to 64 epochs
back, a copy per member each, about 250 bytes a copy): at most about 8 MB for a
channel of 500 members that was rekeyed 64 times in a week, normally a few KB.

Lower `Messages:KeepDays` or `Messages:MaxPerChannel` (and restart) to use
less; the next sweep, at startup, applies them. `0` for either turns catch-up
off: the server keeps nothing, deletes what it kept, and doesn't offer it to
plugins, which then work as before (messages sent while someone is away don't
reach them). Backups hold the stored messages too, for as long as each backup
is kept (`--keep`). A Litestream replica holds them for Litestream's own
retention (its snapshots and write-ahead log segments, by default a day or more):
a message the server has deleted can still be in the replica until Litestream
drops what held it. Keep Litestream's retention short, and the replica as
private as the database.

Nothing is kept of what someone says alone in a channel: nobody else could ever
fetch it.

## Deploying on Linux with systemd

The first tester server runs this way, behind Tailscale Funnel.

### Building the package

On a machine with the .NET SDK (any OS with PowerShell):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-linux.ps1                         # x64
powershell -ExecutionPolicy Bypass -File .\scripts\publish-linux.ps1 -Runtime linux-arm64    # ARM64
```

It builds a self-contained Linux server for that processor (`linux-x64` by
default, or `linux-arm64`, for example an Oracle Cloud Ampere A1 machine;
`uname -m` on the machine says `x86_64` or `aarch64`), and packs it with the
systemd units and the installer into `lookingglass-server-<runtime>.tar.gz`
(`-Output` to choose another path). The machine needs no .NET: the runtime,
SQLite and everything else come in the package.

### Installing and updating

Copy the archive to the Linux machine, then:

```sh
mkdir lookingglass && tar -xzf lookingglass-server-linux-arm64.tar.gz -C lookingglass
cd lookingglass
sudo sh ./install-linux.sh wss://<machine>.<tailnet>.ts.net/ws [more addresses...]
```

Give it every address clients connect to. The installer:

- creates the `lookingglass` system user if needed;
- stops the service (see [Stopping and restarting](#stopping-and-restarting)),
  and installs the server to `/opt/lookingglass`, keeping the previous install
  as `/opt/lookingglass.old`;
- installs the unit, and writes the addresses to a drop-in
  (`/etc/systemd/system/lookingglass.service.d/public-urls.conf`), so the unit
  itself can be replaced on every update;
- installs the backup units, without enabling them (see
  [Backups and restoring](#backups-and-restoring));
- fixes SELinux labels where `restorecon` exists (Fedora and relatives);
- enables and restarts the service, and shows its status.

Run the same command again to update. The database in `/var/lib/lookingglass`
is kept. Follow the logs with `journalctl -u lookingglass -f`.

### What the unit does

`deploy/lookingglass.service`:

- runs `/opt/lookingglass/LookingGlass.Server` as the `lookingglass` user,
  listening on `127.0.0.1:5180` only;
- keeps the database in `/var/lib/lookingglass/lookingglass.db` (systemd's
  `StateDirectory`, mode 0700), readable only by the service user, with files
  private to it (`UMask=0077`). Root can still read it, so a backup or
  replication tool running as root needs nothing more (for one running as
  another user, see [Litestream](#replicating-with-litestream));
- restarts on failure, gives the server 30 seconds to stop, and runs with
  `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome` and `PrivateTmp`.

For a private Tailscale test server with debug accounts and the echo bot,
change `--urls` to `http://0.0.0.0:5180` and enable the commented-out
`ASPNETCORE_ENVIRONMENT=Development` line. (In Production the server refuses
to start with debug accounts or the echo bot on, unless
`LookingGlass__Dev__AllowOutsideDevelopment=true` says you mean it.)

To install the unit by hand instead of with the installer, see the comments
at the top of the unit file.

### Putting TLS in front

The server listens on localhost only. Put a TLS reverse proxy in front and
give clients a `wss://` address:

- **Tailscale Funnel:** `tailscale funnel --bg 5180` makes the server reachable
  as `wss://<machine>.<tailnet>.ts.net/ws`. Funnel can also use port 8443 or
  10000, when another service has 443:
  `tailscale funnel --bg --https=8443 http://127.0.0.1:5180`, and the address
  is `wss://<machine>.<tailnet>.ts.net:8443/ws`. List it in `PublicUrls`
  exactly so, port included: the port is part of the address registrations
  and key logins are signed for.
- **A reverse proxy** such as Caddy, for a public domain like
  `wss://chat.example.com/ws`:

  ```
  chat.example.com {
      reverse_proxy 127.0.0.1:5180
  }
  ```

Either connects from this machine, so the server believes the client address
it passes in `X-Forwarded-For` (see [Behind a reverse proxy](#behind-a-reverse-proxy)).
To check that the proxy sends it, look at a line in the log that names a
client's address (a refused registration or key login, or, with
`Logging__LogLevel__LookingGlass=Debug` for a while, "Closing connection
from ..."). If every client shows as `127.0.0.1`, the proxy doesn't send the
header, and per-address limits apply to all clients together: raise
`Limits:ConnectionsPerIp`, `Limits:ConnectionsPerMinutePerIp` and the
per-address registration and key login limits to suit.

### A public host

Before giving the address to people you don't know:

- Run in **Production** (the default; the unit doesn't set
  `ASPNETCORE_ENVIRONMENT`). One of the first lines the server logs says how
  it is set up, for example `LookingGlass server 0.2.0 in Production: debug
  accounts off, echo bot off; every address is wss:// with a fully qualified
  name (1); database /var/lib/lookingglass/lookingglass.db`. Check it after
  every update.
- **Debug accounts and the echo bot off.** The server refuses to start with
  either outside Development (see above).
- **Only `wss://` addresses with fully qualified names** in `PublicUrls`. The
  server warns about any other at startup, and the summary line says so.
- **Back the database up**, off the machine: with Litestream, or with the
  backup timer and a copy elsewhere.
- **Keep the machine and the server updated.** Whoever controls the server can
  swap any member's keys (see [design.md](design.md#the-trust-this-needs)).

### Logs

The server logs to the journal (`journalctl -u lookingglass`). It never logs
messages or channel names (it can't read them), device tokens, keys,
registration codes or key login signatures. User IDs (Lodestone character
IDs) appear in lines about registrations, key logins, players signing out
their other devices (with how many) and channel changes,
and in the Information line for an invite refused by one of the invite
limits ("Invite from user 1 to user 2 refused by InviteBurstPerPair", named
as its setting, or `MaxPendingInvitesPerChannel`), which is logged at most
once a minute per inviter. An account or address refused by limits again and
again is flagged with a Warning line (see [Flags and bans](#flags-and-bans)).
At Debug level only, each local chat message is logged with the sender's user
ID and how many copies there were, were delivered and were over their
recipient's limit; never who they were, nor what was said.
Client addresses appear only in lines about registrations and key logins
(refused ones, and key logins that add a device), flags, bans (a banned
account refused, a banned connection closed, an address blocked
automatically) and opened and closed connections (at Debug level only), for
dealing with abuse.

### Flags and bans

Rate limits stop one burst of abuse; the server also notices someone who keeps
hitting them, and the operator can ban them. The design and its reasoning are
in [design.md](design.md#spotting-abuse-and-banning).

**How flags show up.** Every refusal by a limit (invites, messages, lookups,
registrations, key logins, new or open connections from one address, and the
rest) is counted for the account, if it is logged in, and for its address (an
IPv4 address, or an IPv6 /64 and its /56 each). One refused in at least 30
different minutes of the last hour, or by at least 4 different limits within
10 minutes, is flagged, once, with a warning in the log:

```text
Flagged user 31337: refused by limits in 30 of the last 60 minutes, by InviteBurstPerPair, LookupBurst. Repeated refusals by limits
like these are unlikely to be by accident; see it with --bans, and ban it with --ban 31337 if it is abuse. The flag expires 24 hours
after the last refusal.
```

To find them in the journal: `journalctl -u lookingglass | grep Flagged`. A
flag lasts until 24 hours have passed without a refusal; nothing is banned
for it. The limits are named as their settings are (or as the request, for
limits that have no setting; the requests that read a lot share one budget,
`ReadBudget`), and the line holds no names and nothing anyone said. Limits
that others filled (an invitee sent too many invites by everyone together, a
channel's pending invites, unless they sent most of those themselves) don't
count against the one refused. A flagged address may be shared (a household,
a mobile carrier's NAT), and a plugin with a bug can hit a limit too: look
before banning.

**The proxy's address is never flagged.** Refusals from this machine's own
address (127.0.0.1, ::1), the unspecified address, or one of
`TrustedProxies` mean the proxy isn't passing the client's address on
(`X-Forwarded-For`), so every player seems to come from there. That address is
never flagged or blocked; instead the log says:

```text
Refusals by limits from address 127.0.0.1 would flag it (...), but this is the proxy's address: the forwarded client address is
missing, so every player's connections seem to come from there. Check that the proxy passes the client's address on ...
```

Fix the proxy (see [Behind a reverse proxy](#behind-a-reverse-proxy)) rather
than banning anything. Accounts are still counted and flagged as usual.

**Check the address the server sees** before banning any address: turn on
Debug logging for a moment (add `Environment=Logging__LogLevel__LookingGlass=Debug`
in a drop-in, `sudo systemctl edit lookingglass`, and restart), connect, and
look for `journalctl -u lookingglass | grep "Connection from"`. It must show
your public address (an IPv6 one as its /64), not 127.0.0.1, ::1, the
server's own address, or a 100.x Tailscale address. Take the line out again
afterwards.

**The commands.** Run them as the service's user, with the service's
settings (as for `--backup`), while the server runs or not. They start no
server, and the running one picks up a change within 30 seconds
(`Abuse:BanCheckSeconds`), without a restart. The service's settings are the
`Environment=` lines of its unit and drop-ins (the data folder, and any
`LookingGlass__Abuse__...` or `LookingGlass__TrustedProxies__...` you added),
and `appsettings.json` beside the binary, which the commands read anyway.
This shell function (bash) runs a command with the unit's own: `systemctl
show` gives every `Environment=` of the unit and its drop-ins, and only those
the commands use are passed on (so a value with spaces elsewhere, such as an
announcement, does no harm). It doesn't pick up settings from an
`EnvironmentFile=` (the installed unit has none) or from an
environment-specific `appsettings.<Environment>.json` (the commands run in
Production): pass those on the command line, as `--LookingGlass:Abuse:...=`,
if you use them.

```sh
LG() {
    local settings
    mapfile -t settings < <(systemctl show lookingglass -p Environment --value | tr ' ' '\n' |
        grep -E '^LookingGlass__(DataDirectory|Abuse__|TrustedProxies__)')
    sudo -u lookingglass env "${settings[@]}" /opt/lookingglass/LookingGlass.Server "$@"
}

LG --bans                                                  # bans in force, flags, and recent history
LG --ban "Bob Hatter@Lich" --reason "Spamming invites"      # a registered character, until lifted
LG --ban 31337 --days 7                                     # any character by user ID (its Lodestone ID), for 7 days
LG --ban 203.0.113.5                                        # an IPv4 address
LG --ban 203.0.113.0/24 --days 1 --reason "Flooding"        # an IPv4 network, a /16 at the widest
LG --ban 2001:db8:1:2::/64                                  # an IPv6 prefix, /32 to /64 (an address alone means its /64)
LG --unban "Bob Hatter@Lich"                                # lift a ban (an address exactly as --bans lists it)
```

Run them as `lookingglass`, not as root: SQLite may create the database's
`-wal` and `-shm` files, which the service must be able to open. A user ID is
the number in the character's Lodestone address
(`https://na.finalfantasyxiv.com/lodestone/character/31337/`); `--bans` and
the flag lines give it too. `--ban` on someone banned already replaces their
ban. The reason is shown to the player, so write it for them: one line, at
most 300 characters. `--ban` refuses this machine's own address, the
unspecified address, a trusted proxy's, or a prefix holding one: when the
forwarded address is missing that is every player, so it says so and bans
nothing (add `--force` if you really mean it). Exit codes: 0 done, 1 not done
(no such character, no ban to lift, the proxy's address, no database), 2 the
command line was wrong.

`--bans` lists, with names for registered characters:

```text
Bans in force (2):
  user 31337 (Bob Hatter@Lich): since 2026-10-07 12:00 UTC, until lifted. Reason: Spamming invites
  address 203.0.113.0/24: since 2026-10-07 12:05 UTC, until 2026-10-08 12:05 UTC (1 day). Reason: Flooding

Flagged in the last 24 hours (1):
  user 4242 (Carol Queen@Odin): refused by 4 different limits within 10 minutes; flagged 2026-10-07 11:00 UTC, last refused 2026-10-07 11:55 UTC; limits: CreateChannel, InviteBurstPerInviter, LookupBurst, SendMessage

No bans lifted or ended in the last 90 days.
```

**What a ban does.** A banned character can't sign in (its saved logins and
its identity key are refused, and no new login is made) or register again,
with any keys. A banned address can't connect. Their open connections are
closed within 30 seconds. The plugin tells the player the server's operator
blocked them, with the reason and the end if there is one, and tries again
every 5 minutes; a plugin from before bans shows the server's message as a
failed connection and reconnects every 30 seconds at most. Their places in
channels stay, and the other members see them offline; channel admins aren't
told. Banning an address bans everyone behind it.

**History.** Lifted and ended bans are listed for 90 days
(`Abuse:BanHistoryDays`), then deleted. Bans are in the database, so backups
hold them: restoring an older backup brings back its bans and loses later
ones.

**Automatic blocks** are on by default: an address refused
`Abuse:AutoBlockAfterRefusals` times (1,000) within the window is blocked for
`Abuse:AutoBlockMinutes` (15) by itself, with a warning in the log ("Blocked
address ... automatically"), to blunt a flood until you look. No player is
refused that often by accident. Set `AutoBlockMinutes` to 0 to turn them off.
Its connections are refused (HTTP 429) before anything else, rather than let
in to be told why as with your bans. It never blocks an account or the
proxy's address, never lasts longer than those minutes, and never replaces a
ban you made; `--bans` lists it as made automatically, and
`--unban` lifts it early.

**Settings** (under `LookingGlass:Abuse`; the server won't start with one out
of range):

| Setting | Default | What it does |
| --- | --- | --- |
| `WindowMinutes` | 60 | Minutes of refusals looked at, 10 to 1440 |
| `FlagAfterMinutesRefused` | 30 | Flagged once refused in this many different minutes of the window, 1 to `WindowMinutes` |
| `FlagAfterLimits` | 4 | Flagged once refused by this many different limits within `FlagLimitsWithinMinutes`, 2 to 100 |
| `FlagLimitsWithinMinutes` | 10 | The minutes `FlagAfterLimits` counts over, 1 to `WindowMinutes` |
| `FlagExpiresAfterHours` | 24 | Hours a flag lasts after its last refusal, 1 to 8760 |
| `MaxTrackedKeys` | 100000 | Accounts and addresses counted at once, at most (the least recently refused are forgotten past it), 1,000 to 10,000,000 |
| `AutoBlockMinutes` | 15 | Minutes an address is blocked by itself once far past the threshold; 0 never, up to 1440 |
| `AutoBlockAfterRefusals` | 1000 | Refusals within the window that block an address, when `AutoBlockMinutes` is set, 100 to 1,000,000 |
| `BanCheckSeconds` | 30 | Seconds between the server's reads of the bans, 1 to 60: a ban from the command line applies within this |
| `BanHistoryDays` | 90 | Days a lifted or ended ban is kept, 1 to 3650 |

Raise `FlagAfterMinutesRefused` or `FlagAfterLimits` if innocent players get
flagged (behind a large shared NAT, say); lower them to hear sooner.

### Stopping and restarting

`systemctl stop` (or `restart`, or the installer) sends SIGTERM. The server
closes every connection at once, telling clients it is going away (they
reconnect by themselves, waiting longer each time), lets requests in progress
finish, and checkpoints the database. Every change is one SQLite transaction,
so even a server killed outright leaves the database whole; it loses at most
the request it was in the middle of.

### Backups and restoring

The database is one file, `/var/lib/lookingglass/lookingglass.db`, with
`lookingglass.db-wal` and `lookingglass.db-shm` beside it while the server
runs. Don't copy those files while it runs: use one of these.

**Without Litestream**, enable the daily backup:

```sh
sudo systemctl enable --now lookingglass-backup.timer
```

It runs `LookingGlass.Server --backup /var/lib/lookingglass/backups/ --keep 14`
as the service user: SQLite's online backup, safe while the server runs, into
`lookingglass-<UTC time>.db`, keeping the newest 14. Copy that folder off the
machine as well. To make one now:

```sh
sudo systemctl start lookingglass-backup.service
# or by hand, as a user who can read the database:
sudo -u lookingglass env LookingGlass__DataDirectory=/var/lib/lookingglass \
    /opt/lookingglass/LookingGlass.Server --backup /var/lib/lookingglass/backups/manual.db
```

`--backup` takes a file, or a folder (one that exists, or a path ending in
`/`) to write a dated file into; `--keep N` deletes all but the newest N dated
files in that folder, and nothing else. It reads the same settings as the
server (so `LookingGlass__DataDirectory` must name the data folder), starts no
server, and exits with 0 once the copy is written and checked. The copy is a
single file (no `-wal`).

**To restore** a backup:

```sh
sudo systemctl stop lookingglass
sudo systemctl stop litestream    # only if Litestream replicates this database
cd /var/lib/lookingglass
sudo cp backups/lookingglass-20261006-031500-123.db lookingglass.db.restoring
sudo rm -f lookingglass.db-wal lookingglass.db-shm
sudo mv lookingglass.db.restoring lookingglass.db
sudo chown lookingglass:lookingglass lookingglass.db && sudo chmod 600 lookingglass.db
sudo systemctl start litestream   # likewise
sudo systemctl start lookingglass
```

Remove the `-wal` and `-shm` files: they belong to the database being
replaced. If Litestream replicates the database, stop it first and start it
again only once the restored file is in place: otherwise it goes on reading
the old database's log while the file changes under it. (Stopping it pauses
replication of any other database it handles too, for those few seconds.)
With Litestream, restoring from its replica (below) is usually the better
choice anyway: it is newer.

Logins made since the backup no longer work, but plugins sign back in with
their identity keys by themselves (see [design.md](design.md#key-login)).
Anything else since (registrations, channels, invites, new keys) is gone, and
users may need to register again.

### Replicating with Litestream

[Litestream](https://litestream.io) copies the database's write-ahead log to
object storage as it is written, so losing the machine loses seconds, not a
day. The server suits it: the database is in WAL mode, and the server only
ever checkpoints PASSIVE (never waiting for or blocking Litestream's reader),
by default leaving checkpoints to SQLite and to Litestream
(`LookingGlass:Database:CheckpointMinutes` is 0; leave it so). Add to
`/etc/litestream.yml`:

```yaml
dbs:
  - path: /var/lib/lookingglass/lookingglass.db
    replicas:
      - url: s3://<bucket>/lookingglass
        # endpoint, region and credentials as for your other databases
```

and restart Litestream. If it runs as root, the 0700 data folder is no
obstacle. If it runs as another user (check with `systemctl show litestream
-p User`), share the folder with that user through the service's group: a
drop-in such as `/etc/systemd/system/lookingglass.service.d/litestream-access.conf`
with

```ini
[Service]
StateDirectoryMode=0770
UMask=0007
```

then `sudo usermod -aG lookingglass <litestream's user>`, `sudo systemctl
daemon-reload`, stop the server, `sudo chmod 770 /var/lib/lookingglass` and
`sudo chmod 660 /var/lib/lookingglass/lookingglass.db*`, start it, and restart
Litestream (it picks up the new group when it starts). Litestream needs to
write there too: it keeps its own folder, `.lookingglass.db-litestream`, next
to the database. The backup timer isn't needed as well (it does no harm).

To restore from the replica:

```sh
sudo systemctl stop lookingglass
sudo systemctl stop litestream
cd /var/lib/lookingglass
sudo litestream restore -config /etc/litestream.yml -o lookingglass.db.restoring /var/lib/lookingglass/lookingglass.db
sudo rm -f lookingglass.db-wal lookingglass.db-shm
sudo mv lookingglass.db.restoring lookingglass.db
sudo chown lookingglass:lookingglass lookingglass.db && sudo chmod 600 lookingglass.db
sudo systemctl start litestream
sudo systemctl start lookingglass
```

Stop Litestream before putting the restored file in place, and start it again
afterwards (before the server): a running Litestream would go on following the
replaced database's log, and could replicate the wrong file. Stopping it
pauses replication of any other database it handles, for those few seconds.
`litestream restore` itself only reads the replica, and won't write over an
existing file, hence the temporary name. Restoring onto a new machine works
the same, before the server's first start. See Litestream's own
documentation for restoring to a point in time.

## Docker

```sh
docker build -t lookingglass .
docker run -p 127.0.0.1:5180:5180 -v lookingglass-data:/app/data \
    -e LookingGlass__PublicUrls__0=wss://chat.example.com/ws lookingglass
```

The image runs in Production, so `PublicUrls` is required. Put a TLS reverse
proxy in front for anything beyond local testing.

A reverse proxy on the host reaches the container through Docker's bridge
network. Inside the container, the proxy's address is then the bridge gateway
(often `172.17.0.1`), not loopback. Trust the bridge network, for example
`LookingGlass__TrustedProxies__0=172.17.0.0/16` (check yours with
`docker network inspect bridge`). Only do this if nothing untrusted can reach
the container from that network. A proxy in another container on a
user-defined network needs that network trusted instead.

## Upgrading

**Update the server and the plugins together.** The current protocol is
version 3. A plugin from before is asked to update when it connects. The
database is upgraded in place when the server starts.

**From 0.1.** 0.1's channels have no membership log, and nobody can sign one
for them now. So 0.2 refuses to start on a database that has any. It says
which file it is and leaves it unchanged. Stop the server, delete or move that
file (with its `-wal` and `-shm` files, if any), and start again. Everyone
registers again and creates their channels anew. A 0.1 database without
channels is upgraded in place.

**From a server without key recovery.** Places an older server left under
keys their users no longer have stay as they are until those users next
register new keys through the Lodestone, which moves them.

**From a server without message catch-up (schema 8).** The database gains a
table for stored messages; nothing else changes. Messages are kept from the
upgrade on. Plugins from before (0.2.5) keep working: they just don't ask for
what they missed. A plugin with catch-up connecting to an older server doesn't
ask either.

**Local chat.** Nothing to do: no database change and no new setting is
needed (it is on by default; see [Local chat](#local-chat)). A plugin with
local chat connecting to an older server is told local chat isn't available
there when it tries `/lgl`; an older plugin never sees it.

**From a server without bans (schema 9).** The database gains two tables,
for bans and flags; nothing else changes. The new settings (`Abuse:...`, see
[Flags and bans](#flags-and-bans)) all have defaults (automatic blocks are on, at
1,000 refusals an hour for 15 minutes), so nothing needs setting. Restart the service after updating before
using `--ban`: the server running the old version doesn't read bans. Plugins
from before bans keep working; a banned one shows the server's message as a
failed connection.

**From a server without device notices (schema 10).** The `users` table gains
one column, `key_login_off` (0 for everyone); nothing else changes, and there
is no new setting. It is set by a player's **Sign out everywhere else**, and
cleared when they register again. Plugins from before keep working: they
aren't told of new devices, and one that was signed out shows "Login not
recognised".

**Going back to an older version** works with a schema 11 database (an older
server opens it, and ignores what it doesn't know), but an older server
doesn't read bans (everyone banned gets back in until the new version runs
again), nor `key_login_off` (the key of a player who signed out everywhere
else can sign in with a key login again).

## Loading a development build of the plugin

1. Build in Release (see [Building and testing](#building-and-testing)).
2. In Dalamud's settings, open **Experimental**, and under **Dev Plugin
   Locations** add `src/LookingGlass.Plugin/bin/Release/LookingGlass.dll`
   (the full path).
3. Turn the plugin on in the plugin installer.

The plugin keeps its data per character and server address in its config
folder (under XIVLauncher's `pluginConfigs`). See
[design.md](design.md#secrets-on-the-client) for what it stores and how.
