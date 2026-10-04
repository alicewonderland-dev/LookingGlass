# WonderlandChat

End-to-end encrypted, cross-world linkshells for FFXIV: a Dalamud plugin and
a small server. The server relays ciphertext only; channel names and messages
are encrypted between members. This is a clean-room rewrite inspired by the
ideas of ExtraChat; no code is shared with it.

Status: **0.1, core functionality** — registration, channels, invites,
automatic rekeying, encrypted messaging, ranks, and debug tooling. ChatTwo
integration and the import wizard come next (see the design doc).

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

This listens on port 5180 in **Development** mode, which turns on debug
accounts and runs an echo bot inside the server. Only do this on a private
network such as your tailnet.

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

## Server configuration

Settings live in `appsettings.json` next to the server binary, and can be
overridden on the command line (`--WonderlandChat:Announcement="Hello"`) or
with environment variables (`WonderlandChat__Dev__AllowDebugAccounts=true`).
Relative paths, such as the default `data` folder for the database, resolve
against the install folder.

Production deployment: `deploy/wonderlandchat.service` (systemd) or the
`Dockerfile`. Put a TLS reverse proxy (for example Caddy) in front and use
`wss://` URLs.

## How the encryption works (short version)

- Each character has a long-term Ed25519 signing key and X25519 key. Others
  see a 25-digit fingerprint and are warned if it changes.
- Each channel has an epoch key. Any membership change (join, leave, kick)
  makes a member generate a new one, seal it to every member's X25519 key, and
  sign it. The server stores and forwards the sealed copies but can't open them.
- Messages are XChaCha20-Poly1305 encrypted under the epoch key and signed by
  the sender, so the server can't read, forge or re-attribute them.
