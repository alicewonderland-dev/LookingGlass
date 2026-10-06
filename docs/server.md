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

## Building and testing

You need the .NET 10 SDK (newer SDKs work too). The plugin also needs
Dalamud's development files, which XIVLauncher installs.

```sh
dotnet build LookingGlass.slnx -c Release
dotnet test LookingGlass.slnx -c Release
```

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
   plugin's settings button in Dalamud's plugin list). Set **Server URL** to
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
| `TrustedProxies` | empty | Proxies, besides this machine, whose `X-Forwarded-For` is believed: see below |
| `Lodestone:BaseUrl` | `https://na.finalfantasyxiv.com` | Where characters are looked up |
| `Lodestone:MinDelaySeconds` | 2 | Least time between Lodestone requests, server-wide |
| `Lodestone:ChallengeMinutes` | 15 | How long a registration code can be used, 1 to 60. The server won't start with anything else |
| `Dev:AllowDebugAccounts` | false | Characters on world `Debug` register without the Lodestone |
| `Dev:HostEchoBot` | false | Runs the echo bot inside the server. Needs `AllowDebugAccounts` |
| `Dev:EchoBotName` | `Echo Bot` | The hosted echo bot's name |
| `Dev:EchoBotServerUrl` | empty | Where the hosted echo bot connects. Empty: worked out from the server's own address |
| `Dev:AllowOutsideDevelopment` | false | Lets `AllowDebugAccounts` and `HostEchoBot` be on outside Development. Without it the server refuses to start with either there |
| `Database:CheckpointMinutes` | 0 | Minutes between explicit (PASSIVE) checkpoints of the write-ahead log; 0 leaves them to SQLite and Litestream: see [Litestream](#replicating-with-litestream) |
| `Limits:RegistrationsPerHourPerIp` | 5 | Registrations started per IP address per hour |
| `Limits:RefusedRegistrationsPerHourPerIp` | 10 | Registrations refused for naming an address this server doesn't list, logged per IP per hour; refused silently past that |
| `Limits:KeyLoginsPerHourPerIp` | 60 | Key login challenges per IP address per hour |
| `Limits:KeyLoginFailuresPerHourPerIp` | 10 | Failed key logins after which an IP address gets no more challenges for the hour |
| `Limits:ConnectionsPerIp` | 20 | Concurrent connections per IP address |
| `Limits:ConnectionsPerMinutePerIp` | 60 | New connections per IP address per minute |
| `Limits:RequestsPerSecondPerConnection` | 20 | Requests one connection may make per second, on average; faster ones are slowed down, not refused |
| `Limits:RequestBurstPerConnection` | 200 | Requests one connection may make at once before that applies |
| `Limits:MaxIdentitiesPerRequest` | 500 | Users one identity lookup may ask for |
| `Limits:SendQueueLength` | 256 | Events queued for one connection before it is dropped as too slow |

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
  failures), and per account from each address (failures only: half the
  per-address allowance, rounded up, so 5 an hour with the default of 10). A
  challenge counts as a failure until it is answered correctly, and an account
  that already failed from an address that hour doesn't count against it
  again, so plugins retrying a login the server lost (about three times an
  hour each) don't lock their neighbours out. Nothing is limited per account
  alone, so failures from other addresses never stop a user signing in from
  theirs. Users who share an address with an attacker (one NAT, say) share
  its per-address limits. Behind a large shared NAT, raise
  `KeyLoginsPerHourPerIp` and `KeyLoginFailuresPerHourPerIp` together.
- **Devices.** Each user keeps their 20 most recently used devices; older ones
  are dropped as new ones sign in.
- **Connections.** 20 open at once and 60 new ones a minute per IP address
  (`ConnectionsPerIp`, `ConnectionsPerMinutePerIp`); past either, the
  WebSocket upgrade gets HTTP 429. Unauthenticated connections close after 20
  minutes, and a client that doesn't answer the server's ping (every 30
  seconds) within 60 seconds is dropped.
- **Requests.** Each connection may make 200 requests at once, then 20 a
  second (`RequestBurstPerConnection`, `RequestsPerSecondPerConnection`). A
  faster client is slowed down (its next request is read only when due), never
  refused. Every request type also has its own per-user limits, and a frame
  is at most 128 KiB.
- **The web server** (Kestrel, under `Kestrel:Limits` in `appsettings.json`)
  takes at most 2,000 connections, 2,000 of them WebSockets
  (`MaxConcurrentConnections`, `MaxConcurrentUpgradedConnections`), plain HTTP
  request bodies of at most 64 KiB (the server takes none: WebSocket frames
  have their own limit), headers of at most 32 KiB, sent within 15 seconds,
  and closes idle keep-alive connections after a minute. Behind a reverse
  proxy these count the proxy's connections; a small server needs no more.
- **Memory.** Per-address counters keep at most 100,000 addresses, and the
  Lodestone cache 10,000 searches (see [design.md](design.md#server-design)).
- The full list of protocol limits is in
  [design.md](design.md#abuse-limits).

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
  replication tool running as root (Litestream, say) needs nothing more;
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
IDs) appear in lines about registrations, key logins and channel changes.
Client addresses appear only in lines about registrations and key logins
(refused ones, and key logins that add a device) and about closed connections
(at Debug level only), for dealing with abuse.

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

and restart Litestream. It runs as root, so the 0700 data folder is no
obstacle. The backup timer isn't needed as well (it does no harm).

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

## Loading a development build of the plugin

1. Build in Release (see [Building and testing](#building-and-testing)).
2. In Dalamud's settings, open **Experimental**, and under **Dev Plugin
   Locations** add `src/LookingGlass.Plugin/bin/Release/LookingGlass.dll`
   (the full path).
3. Turn the plugin on in the plugin installer.

The plugin keeps its data per character and server address in its config
folder (under XIVLauncher's `pluginConfigs`). See
[design.md](design.md#secrets-on-the-client) for what it stores and how.
