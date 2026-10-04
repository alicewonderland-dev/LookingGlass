# WonderlandChat

End-to-end encrypted, cross-world linkshells for FFXIV: a Dalamud plugin and
a small server. The server relays ciphertext only; channel names and messages
are encrypted between members. This is a clean-room rewrite inspired by the
ideas of ExtraChat; no code is shared with it.

Status: **0.1, core functionality** — registration, channels, invites,
automatic rekeying, encrypted messaging, ranks, and debug tooling. ChatTwo
integration and the import wizard come next (see [docs/design.md](docs/design.md)).

> **Security status.** Message contents are encrypted end to end, but in 0.1
> the server's member list is still trusted: a malicious server could add a
> hidden member and receive channel keys. Only use servers run by someone you
> trust. Version 0.2 replaces this with signed, verifiable membership (see
> "Security model" below).

## Layout

| Path | What it is |
| --- | --- |
| `src/WonderlandChat.Protocol` | The wire protocol (`Protos/wonderlandchat.proto`), shared by everything |
| `src/WonderlandChat.Core` | Crypto, the client session, and the echo bot. No Dalamud dependency |
| `src/WonderlandChat.Server` | ASP.NET Core server with SQLite. Runs on Linux and Windows |
| `src/WonderlandChat.Plugin` | The Dalamud plugin (`/wcl1`–`/wcl8`, `/wonderlandchat`, `/wcdebug`) |
| `tools/WonderlandChat.DevTool` | `wcdev`: run an echo bot, or smoke-test a server |
| `tests/WonderlandChat.Tests` | Crypto, policy and end-to-end tests against an in-process server |

## Build and test

Needs the .NET 10 SDK (newer SDKs work too). The plugin also needs Dalamud's
dev files, which XIVLauncher installs.

```sh
dotnet build WonderlandChat.slnx -c Release
dotnet test WonderlandChat.slnx -c Release
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
2. In game, open `/wonderlandchat`, expand **Settings**, and set the server URL
   to `ws://<machine-name>:5180/ws` (the Tailscale MagicDNS name or 100.x IP).
3. Register your character. The plugin shows a code to paste into your
   Lodestone profile; then press **Verify**.

Tailscale already encrypts traffic between devices, and message contents are
end-to-end encrypted regardless. For TLS anyway, `tailscale serve` can put
HTTPS in front of port 5180 (see `tailscale serve --help` for your version);
the plugin URL then becomes `wss://<machine>.<tailnet>.ts.net/ws`.

Check a server from any machine:

```sh
dotnet run --project tools/WonderlandChat.DevTool -c Release -- smoke --server ws://<machine-name>:5180/ws
```

It registers a throwaway debug user, creates a channel, invites the echo bot,
sends a message and waits for the reply.

## Testing alone (debug tooling)

- **Echo bot.** On a Development server, invite `Echo Bot` on world `Debug`.
  It accepts, takes part in rekeys, and echoes everything. Send `!ping`,
  `!rekey` or `!leave` to exercise those paths. Run extra bots with
  `wcdev bot --server ... --name "Another Bot"`.
- **`/wcdebug`** in game: connection state, fingerprint, limits, a protocol
  trace (frame types only, never contents), recent notices, and buttons to
  ping, reconnect, refresh, force a rekey, send a test message, or print a
  simulated incoming message locally.
- **Debug accounts** (`WonderlandChat:Dev:AllowDebugAccounts`) let characters on
  the fake world `Debug` register without Lodestone. Never enable this on a
  public server.

## Loading the plugin

Build in Release, then in Dalamud settings → Experimental → Dev Plugin
Locations add `src/WonderlandChat.Plugin/bin/Release/WonderlandChat.dll`.

Each character's identity and channel keys, per server, are kept encrypted
in a `secrets-….bin` file in the plugin's config folder (under XIVLauncher's
`pluginConfigs`). Every save keeps the previous version next to it as
`secrets-….bin.bak`, and if the file is missing or damaged the plugin loads
the backup and says so in chat. So to reset a character's identity (you then
register again, and other members see that your key changed), disconnect,
delete **both** the secrets file and its `.bak`, then connect again.

## Server configuration

Settings live in `appsettings.json` next to the server binary, and can be
overridden on the command line (`--WonderlandChat:Announcement="Hello"`) or
with environment variables (`WonderlandChat__Dev__AllowDebugAccounts=true`).
Relative paths, such as the default `data` folder for the database, resolve
against the install folder.

Production deployment: `deploy/wonderlandchat.service` (systemd) or the
`Dockerfile`. By default the server only listens on `127.0.0.1:5180`; put a
TLS reverse proxy (for example Caddy) in front and use `wss://` URLs.

Per-IP limits (registrations and concurrent connections) only work if the
server sees real client addresses. It reads them from `X-Forwarded-For`, but
only when the connection comes from a trusted proxy: one on the same machine
(loopback), or one listed in `WonderlandChat:TrustedProxies`, which takes
single addresses (`"10.0.0.5"`) and networks in CIDR form
(`"172.17.0.0/16"`). Otherwise every client appears to be the proxy, and the
limits apply to everyone together. IPv6 clients are counted per /64.

**Docker:** a reverse proxy on the host reaches the container through Docker's
bridge network, so inside the container the proxy's address is the bridge
gateway (often `172.17.0.1`), not loopback. Trust the bridge network, for
example `WonderlandChat__TrustedProxies__0=172.17.0.0/16` (check yours with
`docker network inspect bridge`). Only do this if nothing untrusted can
connect to the container from that network; a proxy running in another
container on a user-defined network needs that network trusted instead.

## Security model

What the encryption does today:

- Each character has a long-term Ed25519 signing key and X25519 key. Others
  see a 25-digit fingerprint. Clients pin each user's keys and name on first
  use and show a persistent "key changed" warning when they change (compare
  fingerprints over /tell, then press "Mark verified").
- Each channel has an epoch key. Any membership change (join, leave, kick,
  re-registration) makes a member generate a new one, seal it to every
  member's X25519 key, and sign it together with a commitment to the key.
  The server refuses a rekey unless every copy carries the same commitment,
  so a member who hands someone a different or unreadable key is named in a
  warning, and that client rekeys. This relies on the server checking: one
  that colludes with the member can give each recipient a different
  commitment, and nobody notices (the v0.2 design fixes this). The server
  stores and forwards the sealed copies but can't open them.
- Messages are XChaCha20-Poly1305 encrypted under the epoch key and signed by
  the sender. The server can't read them, alter them, or attribute them to
  someone else.
- Replays: clients remember the IDs of recent verified messages (in memory),
  drop messages dated more than 10 minutes from their own clock, and save,
  per channel and sender, the timestamp of the newest message accepted.
  Messages more than 2 minutes older than that are dropped, even after a
  restart. Those timestamps are saved with other changes, on shutdown, and
  while messages arrive at least every 5 minutes, so a crash can lose up to
  about 5 minutes of them. Your own messages aren't recorded this way, so
  after a restart the server could replay one you sent in the last 10
  minutes back to you. A message is only
  accepted from a current member, and under an older epoch only within
  2 minutes of the client getting the newer key.
- Clients only accept a new epoch key from a member according to the
  server's member list (verifiable membership is planned for v0.2), and only
  for a newer epoch than they hold. They send with the newest key they hold,
  whatever epoch the server claims.
- Channel names carry a signed epoch and revision. Clients only accept a
  name encrypted under the key they use, and never one older than the newest
  they have accepted (remembered across restarts), so a server can't roll a
  name back, whether to a name from an older epoch or an earlier rename.
  Only the admin renames; a rekey carries the name into the new epoch, and
  clients warn if a member's rekey changed it.
- Clients can block users: their invites are declined unseen and their
  messages hidden. An invite from someone whose identity key changed can't
  be accepted until it is marked verified.

What it does not do yet (0.1):

- **The member list is trusted.** A malicious server can list an extra,
  hidden member; honest clients will then seal new keys to it. Fixing this is
  the v0.2 "authenticated membership" design in [docs/design.md](docs/design.md).
- **Ranks and removals aren't signed.** Clients take the server's word for
  who is admin or moderator and who was removed.
- **Invitees' keys are trusted on first use.** An invite is sealed to
  whatever identity key the server returns for that name the first time you
  look them up; compare fingerprints over /tell to be sure.
- Rekeys still seal the new key to a member whose identity key changed,
  even while the "key changed" warning is showing.
- The server sees metadata (who is in which channel, when messages are sent)
  and can drop or delay anything.
- Debug accounts on a Development server can be taken over by anyone who can
  reach it.

## License

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only). If you
run a modified version of the server for other people, you must offer them its
source code (section 13).
