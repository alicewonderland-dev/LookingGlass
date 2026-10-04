# WonderlandChat — Rewrite Design

Exported from the working design document. Diagrams are described in text.

## Summary

WonderlandChat is a from-scratch rewrite of the ExtraChat plugin and server. It keeps the original's core ideas: a server that only relays ciphertext, Lodestone as the identity root, and native in-game chat. It fixes the original's trust, threading and reliability flaws, and adds a versioned, capability-based protocol so new features can be added without breaking existing clients.

### Goals

- **Encryption that holds against the server.** A malicious or compromised server cannot read channels, forge senders or obtain channel keys.
- **Removal means removal.** A member who leaves or is kicked cannot read anything sent afterwards.
- **A reliable client.** No game-object access off the main thread, no requests that hang, and every failure shown to the user.
- **A server you run and can defend.** Rate limits, size limits, and no global lock that can stall everyone.
- **ChatTwo integration kept.** ChatTwo users get channel names, colours and context-menu actions as before.
- **Room to grow.** New features are added as modules, and most can ship as client-only changes.

### Non-goals

- Wire compatibility with the original ExtraChat server or protocol.
- Server-side moderation of message content. The server cannot read it, by design.
- Clients outside the game (web, mobile) in the first release.
- Copying code from the original. This design describes behaviour, and the implementation is written fresh.

## Lessons from the original

The original's principles were sound. Its failures came from trusting the server with key distribution and from unsafe concurrency on both sides.

| Area | Original behaviour | Consequence | Rewrite |
| --- | --- | --- | --- |
| Key exchange | New key pair on every plugin load, relayed by the server and never verified | Server can sit in the middle of any invite and read the channel | Long-term identity keys with fingerprints that users can verify |
| Secret requests | Client encrypts the channel secret to any key the server names, without asking | Server can simply request any channel's secret | Keys only go to verified members, sealed to their identity key |
| Membership changes | No rekeying; invitees get the secret before accepting | Kicked members and declined invitees read the channel forever | New channel key (epoch) on every membership change |
| Sender identity | Server asserts who sent each message | Server can re-attribute or replay messages | Every message signed by the sender's identity key |
| Registration | Challenge not tied to the requesting connection, no expiry on verify | Anyone can claim the key during the verification window | Challenge bound to session and identity key, 15-minute expiry |
| Client threading | Shared dictionaries changed from thread-pool tasks while the UI reads them | Crashes, corrupted state, "Not on main thread" failures | One network actor owns state; UI reads immutable snapshots |
| Server locking | Global lock held across awaits and re-entered | Whole-server deadlock under contention | No lock held across an await; bounded, non-blocking fan-out |
| Requests | No timeouts; waiters dropped on reconnect | UI stuck "busy" forever | Every request completes with a result, an error or a timeout |
| Errors | Swallowed on the client; any error drops the server connection | Silent message loss, reconnect loops | Typed error codes shown to the user; only protocol violations disconnect |
| Limits | No rate, size or count limits | Easy to flood the server and Lodestone | Limits advertised in the handshake and enforced server-side |
| Secret recovery | Serialiser missing two request kinds | Feature never worked | Server stores sealed epoch keys so members can catch up |
| Upkeep | Stale CI, example config that doesn't load, frozen dependencies | Hard to build, hard to trust | CI for both halves, pinned toolchains, automated dependency updates |

## Core principles

### Kept from the original

1. **The server relays and does not read.** It knows who is in which channel. Channel names, messages and keys are opaque to it.
2. **Lodestone is the identity root.** A character proves ownership through its Lodestone profile. The Lodestone ID is the stable account key.
3. **It feels like native chat.** Channels are used through the game's own chat input and appear in the game's own chat log.
4. **One protocol, defined once.** Client and server share one schema instead of hand-written mirrors on each side.
5. **Small enough for one person to run.** A single server process and a single database file are enough.

### New in the rewrite

6. **Trust keys, not the server.** The server distributes public keys; clients verify them and warn when one changes.
7. **Every membership change is a new epoch.** Joining, leaving, kicks and disbanding rotate the channel key.
8. **The main thread owns the game; one actor owns the network.** Nothing else touches either.
9. **Every request completes.** It ends in success, a typed error or a timeout, never silence.
10. **Version and negotiate everything.** Features are capabilities that client and server agree on at connect time.
11. **Everything is bounded.** Message size, rates, queues, channel sizes and pending invites all have limits.

## Architecture overview

The client splits into a game-thread shell, a single network actor and a game-independent core library. The server splits into a gateway, a policy-and-router layer, a Lodestone worker and SQLite. Only ciphertext and public keys cross between them.

Diagram (in words): on the client, the game thread (hooks, chat output, commands, ImGui, player snapshot) sends commands to the network actor and reads snapshots back; the actor uses the core library (protocol, crypto, identity and epoch keys), which stores keys in an encrypted store. ChatTwo talks to the game-thread side over IPC. The actor connects over a TLS WebSocket, carrying only ciphertext, to the server's gateway (handshake, auth, limits), which passes authenticated requests to the policy-and-router layer (one authorization check per request, fan-out to members). The router uses SQLite transactionally and queues Lodestone lookups for a rate-limited worker that talks to the Lodestone over HTTPS.

## Identity and registration

Registration links a Lodestone character to an identity key, and only the session that asked for the challenge can complete it.

1. On first run, the client generates the character's identity key pair.
2. The client asks to register, sending name, home world and its identity public key.
3. The server resolves the Lodestone ID through a queued, cached Lodestone lookup and issues a short challenge code bound to that session and identity key, expiring after 15 minutes.
4. The user pastes the code into their Lodestone profile and clicks Verify on the same session.
5. The server checks the binding and expiry, fetches the profile, confirms the code, records Lodestone ID → identity key and issues a device token.
6. The client stores the token and identity key. The user can delete the code from their profile.

Rules:

- **Re-registering is allowed**, for lost configs and new PCs. It publishes a new identity key, revokes old device tokens, and every contact sees a "key changed" warning.
- **Device tokens** are 256-bit random values, hashed at rest, and revocable.
- **Names and worlds** are keyed by Lodestone ID; name lookups go through an index updated on rename.
- **Lodestone traffic** goes through one worker with a global rate limit and a result cache. Registration is rate-limited per IP, and verification attempts per connection.

## Cryptography and key management

Each character has a long-term identity key that its contacts verify, and each channel has an epoch key that rotates whenever membership changes.

### Identity keys

- One Ed25519 signing key and one X25519 key per character, the latter vouched for by the former. Generated once and kept across sessions.
- **Verification:** every member shows a 25-digit fingerprint. Clients trust a key on first use and pin it with the user's name and world. A changed key, or a name now held by a different account, shows a persistent warning until the user marks it verified.
- Users can compare fingerprints over an in-game /tell, which doesn't pass through the WonderlandChat server.

### Channel keys (epochs)

- Each channel has a 256-bit key per epoch. Epoch 0 is created with the channel.
- On any membership change, a member's client generates the next epoch key, seals one copy to each member's X25519 key, signs each copy, and uploads them. The server requires one key per current member and the next epoch number.
- The server stores sealed copies per member and epoch, so a member who was offline fetches theirs on login.
- Invitees receive the channel name sealed to them, but no key until they accept.
- The channel name is encrypted with the current epoch key and re-encrypted on rotation.

### Messages

- XChaCha20-Poly1305 under the epoch key, with a random 192-bit nonce.
- Associated data binds the channel ID, epoch, sender, message ID and timestamp.
- The sender signs ciphertext and associated data. Clients reject unsigned, mis-signed, duplicate, or far-from-now (more than 10 minutes) messages.

### Storage on the client

- The identity private key, epoch keys and device token are stored encrypted: DPAPI on Windows, or a random local key file where DPAPI is unavailable (Wine/Proton), which guards against accidental sharing rather than a local attacker.

## Authenticated membership (v0.2, proposed)

The code review showed that 0.1 trusts the server's member list, so a malicious server can add a ghost member and receive every new channel key. The fix: every membership is proven by signatures from the members themselves, and clients only seal keys to, and accept keys from, members they can verify. **Status: awaiting approval before implementation.**

### Signed statements

| Statement | Signed by | Covers |
| --- | --- | --- |
| Genesis | Channel creator | Channel ID, creator ID, creator's identity keys |
| Invite | Inviter | Channel ID, invitee ID and identity keys, inviter ID and identity keys, the sealed channel name |
| Accept | Invitee | Channel ID, invitee ID, hash of the exact invite statement and its signature |

A member's **membership proof** is either the genesis statement (for the creator) or an invite plus its acceptance. The server stores every proof, including those of members who later left, and returns them with the channel. It can withhold or delete proofs, but it cannot forge one.

### Which members a client trusts

Each client works out a set of trusted identity keys for the channel, starting from its own key and, if it joined by invite, the inviter key it accepted. It then repeats two rules until nothing changes:

1. **Downward:** if an invite was signed by a trusted key and accepted by the invitee's key, the invitee's key is trusted.
2. **Upward:** if a trusted key accepted an invite, the inviter key named in that invite is trusted, because the acceptance commits to it.

Invites form a tree rooted at the creator, so this reaches every genuine member. A ghost needs either an invite signed by a real member or a real member's acceptance naming it, and the server can forge neither. A member is **verified** when their current identity key is trusted and their proof checks out.

### Rules that use it

- **Sealing:** a rekey seals the new key only to verified current members. If the server's member list contains anyone else, the rekey can't satisfy the server, and the client shows which listed members failed verification.
- **Accepting keys:** an epoch key is accepted only if its author is a verified current member and its epoch is newer than any key held.
- **Names:** a channel name is accepted only if encrypted under the current epoch by a verified member.

### Key changes and removals

- **Re-registration with new keys** invalidates every proof bound to the old key. The server removes that user from their channels, which triggers a rekey, and they must be invited again.
- **Removals are not signed.** A malicious server can falsely remove someone (denial of service) or keep a removed member listed. Every client remembers removals it has seen and refuses to seal to that member until they show a newer proof. A server that hides a removal from every client is a remaining limitation.

### What it does not protect

- Membership metadata, and availability.
- Who your inviter really is: you trust your inviter's key on first use. Compare fingerprints over /tell for certainty.

### Protocol and migration

- New fields: a membership proof on each Member, past proofs on ChannelInfo, invitee keys inside the invite signature, and an accept signature on RespondToInvite. The server checks signatures too, as early rejection.
- Protocol version 1 is unreleased, so v0.2 changes it in place. Test servers need a fresh database.

## Protocol and extensibility

New features fit into two layers. Features the server must take part in are negotiated capabilities. Features that live only inside encrypted messages need no server change at all.

- **Transport:** WebSocket over TLS, one connection per logged-in character, Protocol Buffers frames defined in one schema file.
- **Handshake:** the client sends Hello (protocol versions, client version, capabilities); the server answers Welcome (chosen version, agreed capabilities, limits, announcement); the client then authenticates with its device token.
- **Requests and events:** every request has an ID and a timeout and ends in a result or a typed error. Errors never close the connection; only protocol violations and failed authentication do. Server events carry a per-connection sequence number.

| Layer | Examples | Needs a server change? | How it is added |
| --- | --- | --- | --- |
| Server capability | Presence, message history, channel bans, file attachments | Yes | New capability string and message types; old clients never see them |
| Encrypted content kind | Text, emotes, replies, reactions, polls, typing state | No | New inner `kind` inside the ciphertext; unknown kinds render as "unsupported message" |
| Client-only feature | Chat filters, notifications, colours, sounds | No | Plugin update only |

Versioning: changes within a major version are additive only; removing or changing a field's meaning needs a new major version.

## Client design

The client is a thin Dalamud shell around a core library with no Dalamud dependency.

- **Game thread:** hooks, chat printing, commands, ImGui, and capturing the player snapshot each frame. Nothing else reads game objects.
- **Session:** owns the socket and all session state, publishes an immutable snapshot after every change; the UI reads only snapshots. Anything that must touch the game is queued with `Framework.RunOnFrameworkThread`.
- **Chat:** `/wcl1`–`/wcl8` commands today; a chat-input hook for a sticky channel later. Sending fails closed: an error never falls through to plain game chat. All remote text is sanitised before it reaches the chat log.
- **Game interop:** signatures live in one module; a missing one disables only its feature.
- **ChatTwo:** the original exposed `ExtraChat.ChannelNames`, `ExtraChat.ChannelCommandColours` and `ExtraChat.OverrideChannelColour` to ChatTwo, and added an invite item to ChatTwo's context menu via `ChatTwo.Register`/`Invoke`. WonderlandChat keeps equivalent integration (IPC naming is an open question).

## Server design

A single process with an embedded database, built around three rules: no lock held across an await, one authorization function for every action, and one transaction for every multi-step change.

- **Concurrency:** one task per connection with a bounded outbound queue; a full queue disconnects that client rather than blocking senders.
- **Authorization:** one rank table (Invited < Member < Moderator < Admin) decides every request, with unit tests over every rank × action pair.
- **Storage:** SQLite in WAL mode; conditional updates (epoch, rank) guard against races.
- **Errors:** typed errors map to protocol error codes.

## Abuse limits and operations

| Limit | Value | Why |
| --- | --- | --- |
| Message ciphertext size | 4 KiB | The game's chat input holds about 500 characters |
| Frame size (any request) | 128 KiB | Bounds rekey bundles and list responses; a rekey for 500 members is about 82 KB |
| Messages per user | 5 per second burst, 1 per second sustained | Stops floods without affecting normal chat |
| Rekeys per user | 5 burst, 1 every 2 seconds | Each rekey costs every member's client work |
| Members per channel | 500 | Keeps rekey bundles small |
| Channels per user | 50 | Bounds login and list cost |
| Pending invites per channel | 50 | Stops invite spam |
| Registration attempts | 5 per hour per IP; verify once per 10 s, 10 per challenge | Protects Lodestone and the challenge flow |
| Lodestone requests (server-wide) | 1 every 2 seconds, cached | Avoids being blocked by Lodestone |
| Connections per IP | 20; unauthenticated connections close after 20 minutes | Bounds idle and unauthenticated load |
| Outbound queue per connection | 256 events | A slow client is disconnected, not waited on |

Operations: the server runs on Linux and Windows (.NET 10), listens on localhost by default behind a TLS reverse proxy, and trusts `X-Forwarded-For` only from configured proxies. First deployment is a local VM over Tailscale, then a cloud host.

## Migration from the original

WonderlandChat uses a new server and protocol, so users re-register once and channels are rebuilt through a planned import wizard. It has its own internal name and `/wcl` commands, so it can be installed beside the original.

## Testing and debug tooling

- **Debug accounts:** with `Dev:AllowDebugAccounts` on, characters on the fake world "Debug" register without Lodestone. Never on a public server.
- **Echo bot:** a headless client that accepts invites, takes part in rekeys and echoes messages; runs inside the server (`Dev:HostEchoBot`) or via `wcdev bot`.
- **`/wcdebug`:** connection state, protocol trace, notices, and tools to ping, force a rekey, or simulate an incoming message.
- **Automated tests:** crypto, the policy table, end-to-end flows, and a malicious-server suite that injects forged and replayed events.

## Milestones

Diagram (in words): five milestones with three gates.
M0 Foundations (repo, schema, CI, core library) → gate: crypto spec reviewed → M1 Identity (registration, identity keys, tokens) → M2 Channels and chat (invites, rekeying, signed messages) → gate: two clients chat while a hostile test server tries to read, forge and replay → M3 Integrations and UI (ChatTwo, import wizard, key-verification UI) → M4 Hardening and beta → gate: no open high-severity findings → 1.0 release.

## Open questions

- [x] **Group keys:** sealed epoch keys for v1, behind an interface so MLS can replace them later.
- [x] **Schema format:** Protocol Buffers.
- [x] **Server language:** C# on .NET 10, sharing the core library with the plugin and the echo bot.
- [ ] **Secret storage on Wine/Proton:** is the local key-file fallback enough?
- [ ] **Key-change policy:** warn and continue (current), or block until re-verified?
- [ ] **ChatTwo IPC names:** reuse `ExtraChat.*` or use `WonderlandChat.*` and ask ChatTwo to support them?
- [x] **Command prefix and internal name:** `WonderlandChat`, `/wcl1` to `/wcl8`.
- [ ] **Message history:** in 1.0, or later?
- [ ] **Limits:** confirm after beta load testing.
- [ ] **Public hosting:** who runs it, cost, privacy note, and acceptable Lodestone volume.
