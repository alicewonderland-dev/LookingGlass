# LookingGlass — Rewrite Design

Renamed from WonderlandChat to LookingGlass on 2026-10-04.

Exported from the working design document. Diagrams are described in text.

This document describes the target design. Not all of it is built yet: see the README's "Security model" section for what the current version actually guarantees.

## Summary

LookingGlass is a from-scratch rewrite of the ExtraChat plugin and server. It keeps the original's core ideas: a server that only relays ciphertext, Lodestone as the identity root, and native in-game chat. It fixes the original's trust, threading and reliability flaws, and adds a versioned, capability-based protocol so new features can be added without breaking existing clients.

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
2. The client asks to register, sending name, home world, its identity public key, the server URL it connected to and a fresh 32-byte random client nonce.
3. The server checks the URL's origin as for key login (not for debug accounts), resolves the Lodestone ID through a queued, cached Lodestone lookup, and issues a 32-byte random nonce bound to that session and identity key, expiring after 15 minutes (`Lodestone:ChallengeMinutes`, 1 to 60: the server doesn't start with anything else), and the code derived from them: `LGC-` + Crockford base32 of the first 100 bits of SHA-256(`SigningPayload("lookingglass/lodestone-code/v1")` + origin (`wss://host:port`, as `ServerOrigin` normalises it) + identity signing key + nonce + client nonce + Lodestone ID), in five groups of four (`LGC-XXXX-XXXX-XXXX-XXXX-XXXX`). Debug accounts get no code. A StartRegistration without the URL or the client nonce (an older plugin) is refused, asking to update; a client nonce of any other length is an invalid request.
4. The client recomputes the code from the origin of its own server URL, its own identity key, the nonce it was sent, its own client nonce and the Lodestone ID it was sent, and refuses to show any other (see "Relay protection").
5. The user pastes the code into their Lodestone profile and clicks Verify on the same session. The client signs the nonce, the Lodestone ID and the server URL it connected to with the identity signing key (`lookingglass/registration/v1`).
6. The server checks the binding and expiry, the signature against the key being registered, which is the key the code was derived for (and, as for key login, the signed URL, which must have the origin registering was started for, the one the code was made for; not for debug accounts), fetches the profile, confirms the code (case-insensitive, O read as 0 and I or L as 1, from a literal `LGC-`; ASCII only, so no other character counts as one of the code's, even one whose upper case is ASCII), records Lodestone ID → identity key and issues a device token.
7. The client stores the token and identity key. The user can delete the code from their profile.

Rules:

- **Registering proves holding the key.** A user's public identity bundle (binding signature included) is served to everyone sharing a channel with them, so without the signature in step 5 anyone could register another user's key for their own character, then register again with new keys and so have the server retire it. A request without one (a plugin from before 0.2) is refused, asking to update; the protocol version is unchanged. A failed check stores nothing and leaves the registration open on the connection.
- **One account per key.** A signing key that is another account's current key is refused, when registering starts (once the account is known) and in the transaction that registers it. With the proof above only the key's owner could try it, by registering a second character with one character's keys, which the plugin never does. Not repaired: a database from before registrations were signed may still hold one key for two accounts (or, from schema 5, a key retired for one account that another holds); the upgrade to schema 6 leaves them and logs each at Warning (the accounts and a short hash of the key), since the server can't tell which account owns the key. Until an operator removes the wrong registration, neither account can register that key again (the owner resets their identity instead), and a schema 5 retirement dropped as a duplicate is lost.
- **Re-registering is allowed**, and revokes old device tokens. It keeps the key the client has (memberships survive); only a client without one (lost config, new PC) publishes a new key. A signing key the account replaces is retired for that account: it can't sign in to it, and registering it for it again is refused (the user is told to reset their identity), so a stale copy can't take the account back from its new key. Retirement is per account (`retired_keys` is keyed by user ID and key, schema 6), so nothing one account does with a key affects another. Keys replaced before the server kept this aren't known.
- **Reset my identity** (Settings), only for a lost or stolen key: new keys, nothing of the old identity kept for that server (login, channel keys), registering again through the Lodestone. That publishes the new key, revokes old tokens, stops the old key signing in, leaves the old memberships bound to the old key (a moderator must re-invite), and contacts see "key changed".
  - Before that, never while the user is the admin of a channel (by their verified logs): they hand admin on (Transfer admin, which makes them a moderator), disband it, or leave it as its only member, themselves, so no channel is lost without a choice. Then, while logged in with the complete channel list, the client leaves every channel it is a member of with a Leave signed by the old key, and declines every invite; failures are reported and the reset goes on (what is left over can be removed with `ForgetChannel`, see "Authenticated membership"). The rule lives in `IdentityResetPlan` (Core), so it is unit-tested; offline, or with a login the server doesn't accept, nothing can be checked, and the reset only goes ahead on the user's explicit say-so (the server may be gone for good).
  - First, while logged in, the client sends `RetireIdentity`, signed by the current key over the user ID, the hash of the connection's device token and the server URL it connected to (so a stolen token alone can't wreck the identity, and a signature made for one login, or for another server, doesn't work here: user IDs are Lodestone IDs, the same on every server; the URL is checked as for key login; no challenge is needed, since it only ever retires the key that signed it). The server retires the key and deletes every device of the account in one transaction, and logs the connection out. Until the new keys are registered the account has no working login (Lodestone needed), and others still see the old key. If the client isn't logged in, or the server refuses or is too old, the reset goes on locally, and the user is told the old key and login work until they register again.
  - Locally, the old identity is removed from every file of the character whose signing key is the one being reset: the address's own, other addresses it was carried to by a move, the old-style file, and every `.bak` (each rewritten twice, so the backup holds the new contents too). Those files keep what they hold about others and the channels (pins, blocks, verified log positions), so a vouched move can carry the new identity there later.
- **Device tokens** are 256-bit random values, hashed at rest, and revocable.
- **A refused device token is kept.** A server that doesn't recognise it may be the wrong one, or one reset or restored from a backup, so the client keeps the token, stays connected (to allow registering again), and tries it again: on that connection with a backoff up to a minute apart, and on every reconnect. Only registering again, or "Forget account" in the debug window, replaces it. Login and registration requests go through one gate, and the old token isn't tried while a registration challenge is pending, so a try of it never races registering again.
- **Key login.** The Lodestone is the initial proof of identity, and the fallback only when the key is lost (or the server has never seen the user); otherwise the client signs in with its identity key. When the server refuses its device token, the client signs a fresh challenge (single use, bound to the connection, 60 s) together with its user ID and the server URL it connected to, under the account's current key, and gets a new device token, which replaces the refused one; other devices keep theirs. Key logins are rate-limited per connection, per address (challenges; and failures, a challenge counting as one until answered correctly) and per account and address (failed answers only, half the address's allowance). Nothing is limited per account alone: a signature can't be guessed, so the limits only stop spam, and failures from other addresses must never lock an account out of key login from its own. Every answer that reaches the account check verifies one signature (unknown accounts against a key nobody holds), so timing doesn't reveal accounts. A user keeps their 20 most recently used devices.
- **Relay protection.** A malicious server the user also connects to could fetch another server's challenge, have the user sign it, and replay the signature there. For a registration that is an account takeover: the target registers whatever key signed it (the user's key for the relay, so separate keys per address don't help) and hands the relay the new device token. Key logins, registrations through the Lodestone and retirements all name the URL, and the signed URL stops this only against the server's configured `PublicUrls`, so outside Development the server refuses to start without them (logging what to set), and a handler built without them accepts no signed URL. Since an honest client connected through an unlisted address (say the tailnet IP, with only the machine name listed) is refused too, the client warns as soon as it connects when Welcome lists addresses (`public_urls`) without its own, naming them; the server's refusal names the address used and the accepted ones (public anyway), and logs it at Warning. The checks tell servers apart by address alone, so each listed address must be this server's only: a single-label name (a short MagicDNS or LAN name), a local-network suffix (`.local`, `.lan`, ...), a private, CGNAT, loopback or link-local IP, or plain `ws://` (whoever answers at the name) can be another server's too, and a malicious server a user reaches under the same address can relay to this one. That is fine on a private network the operator controls; elsewhere, list `wss://` addresses with fully qualified names. The server logs a Warning at startup for each listed address that may not be its alone (`RequestHandler.WhyNotUnique`). A Development server without them accepts the connection's scheme and Host header, which a relay sets to whatever the user signed for: there, the plugin's separate keys per server address still stop a relayed key login (the key a user signs with for the relay isn't registered on the target), but nothing stops a relayed registration; it is for private test servers. The Lodestone code is bound too: a malicious server M could otherwise start a registration on another server B for the user's character with M's own key, show the user B's code as its own, and complete it on B (signing the proof itself, for B's address) once the user put the code in their profile. The code is derived from the server's origin, the key being registered, the nonce and the Lodestone ID (step 3), B only issues one for an origin it accepts and only lets the key it was derived for complete, and the client recomputes it from its own origin and key (step 4): B's code for M's key and B's origin isn't the code for the user's key and M's origin, so the client refuses it, shows no code, drops the challenge, logs a warning (naming the origin and the character, never the code) and tells the user that the server "sent a registration code that doesn't belong to it" and not to put it in their profile. Nor can M write a code into its words ("LGC-… isn't in your Lodestone profile yet", an announcement, a user name) for the user to paste: the client replaces every code in what a server says (errors, announcements, status texts, notices, names, its log, and chat text through `TextSanitizer`) with "[code removed]", reading codes as the Lodestone check does and through invisible characters, except the code it checked itself (`LodestoneCode.Redact`); the server's own "not found" message names no code. A StartRegistration without the URL (an older plugin) is refused, asking to update; the protocol version is unchanged. M can only get a relayed code accepted by finding its own nonce and ID whose truncated hash, with the user's client nonce, equals one B issued (M can't choose B's nonce): a second preimage of 100 bits, against however many of B's codes M holds open at once (one per connection, a few an hour per IP, with IPv6 /64s plentiful; each for at most an hour, the longest `ChallengeMinutes` allows). The client nonce is fresh and random for each StartRegistration, so M can only start once the user's client asks, and must answer before the client stops waiting (60 s): nothing can be precomputed, although the user's key is stable per server (so M may know it). Even 2^20 codes held leave 2^80 hashes in a minute (about 2*10^22 a second, more than all Bitcoin mining). At 80 bits that would be 2^60 hashes in a minute, possible for a large operation, so 100 bits it is, five characters more. (Without the client nonce, M could have ground for the user's key beforehand, for as long as it liked, and needed only to hold B's codes against a table.) Profile matching starts only at a literal `LGC-`, and no code contains an L, so a pasted code can't be read as another starting inside it. In Development without `PublicUrls`, B accepts whatever origin the Host header names, so M can have B derive the code for M's origin and the user's key and relay the whole registration (as for signatures above): private test servers only. The plugin binds each secrets file to its address (stored inside, 128-bit file name hash) and never follows redirects, so one server's keys and login are never used with another. An old-style file (48-bit name) is moved to its new name only if it names no address yet, as read from the file itself (never its `.bak`, which a move leaves holding the unstamped original); once moved (and stamped), or if it can't be read as it is, it is a backup, offered in Settings and restored only when the user confirms, never by itself.
- **Moving address.** Identities are per address. When the user changes the server address, the plugin copies an identity to the new address only if the new address is `wss://`, the server at the old address (already trusted with the login) lists the new one in `PublicUrls` (sent in Welcome), the server at the new address lists the old one, and the user confirms; the new address's word alone never counts. The servers vouch for names, and only TLS proves who answers at one: over plain `ws://` (or a name the local network resolves), whoever answers at the new name could confirm the old one, receive the copied login in the clear and relay key login challenges. A server key in Welcome wouldn't help without channel binding, since a relay forwards its proofs unchanged. The check is made again when the user confirms, and a copy needs one at most a minute old. The old address's identity is kept.
- **Names and worlds** are keyed by Lodestone ID; name lookups go through an index updated on rename.
- **Lodestone traffic** goes through one worker with a global rate limit and a result cache. Registration is rate-limited per IP, and verification attempts per connection. A registration refused for naming an address that isn't the server's costs neither, but logs a warning, so those are counted per IP on their own (`RefusedRegistrationsPerHourPerIp`, 10 an hour) and refused unlogged past that; names and worlds that aren't plain text (control, format or unassigned characters, line or paragraph separators) are refused before anything is logged.

## Cryptography and key management

Each character has a long-term identity key that its contacts verify, and each channel has an epoch key that rotates whenever membership changes.

### Identity keys

- One Ed25519 signing key and one X25519 key per character, the latter vouched for by the former. Generated once and kept across sessions.
- **Verification:** every member shows a 25-digit fingerprint. Clients trust a key on first use and pin it with the user's name and world. A changed key, or a name now held by a different account, shows a persistent warning until the user marks it verified.
- Users can compare fingerprints over an in-game /tell, which doesn't pass through the LookingGlass server.

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

## Authenticated membership (v0.2, revised)

Every membership change becomes a signed entry in a hash-chained log for the channel, and clients replay that log to work out who the members are. Every epoch key commits to the log position it was made for. The second code review showed the first version of this design was unsound: trust only ever grew, and ranks and removals weren't signed, so a removed member's key could keep vouching for new ghosts, and any member could add one. **Status: implemented on 2026-10-04 (revised design approved on 2026-10-03), as an interim step before MLS (see below).**

### The membership log

Each channel has an append-only log. Every entry carries the channel ID, a sequence number, the hash of the previous entry, its kind, the actor (user ID and key fingerprint), the subject (user ID and identity keys), a rank where relevant, and a timestamp. It is signed by the actor.

| Entry | Signed by | Valid only if, at that point in the log |
| --- | --- | --- |
| Genesis | Creator | It is entry 0; the creator becomes admin |
| Invite | Inviter | The inviter is a moderator or admin; the invitee isn't a member or invited |
| Accept | Invitee | It names the exact invite entry, and is signed by the exact key that invite names |
| Decline / Cancel invite | Invitee / a moderator or admin | There is an open invite |
| Remove | Moderator or admin | The subject's rank is below the actor's |
| Leave | The leaving member | They are a member and not the last admin of a non-empty channel |
| Set rank / Transfer admin | Admin | The subject is a member |

The server stores and serves the log and checks each entry before appending it, but clients never rely on that check. The server can't forge an entry, because that needs a member's signature, and it can't reorder entries, because of the hash chain. A removed member's key can't sign anything that counts after their removal. A re-registered user's new key isn't a member until a fresh invite and accept.

**Stale places and "Remove from my list".** A place under keys the user no longer has can't be left (only the old key can sign a leave), so the client offers `ForgetChannel` instead. It is not a log entry: the server marks the account's member and invite rows for the channel `forgotten` (schema 7), only if none of them is under the account's current keys (checked in the transaction). The rows stay, because they are the log's state: entries are checked against them, and `ApplyRekey` still requires a key for every member at the head, so clients (who seal to every member of their verified log, the old key included) keep rekeying. A forgotten row isn't listed or counted towards the user's limits, has no rank for any request (so it can't fetch, send, rename or disband), isn't sent the channel's events or presence, and is never asked to rekey (nor is any member whose row keys aren't their current ones). It goes when the log removes the place (Remove or Cancel invite); a re-invite of the new key then works as usual. Other members still see the old key as a member, as before.

### Freshness and rekeys

- Each client keeps, and persists, the newest log position (sequence number and hash) it has verified for each channel.
- Every sealed epoch key and every channel name signs the log position it was made for. A client rejects a key or name made for an older position than its own; for a newer one, it fetches and verifies the log first.
- A rekey seals to exactly the members at that position, and the server checks that the position is the current head. A key commitment (one shared hash of the key in every copy) stops a member sealing different or junk keys to different people.

### What this stops

| Attack (from the review) | Result |
| --- | --- |
| Server inserts a ghost member | No valid invite and accept chain, so it isn't a member, and nobody seals to it |
| Former member signs invites for ghosts | Invalid: the inviter isn't a member at that point in the log |
| Ordinary member invites ghosts | Invalid: rank is part of the signed log |
| Server hides a removal | The remover's client, and everyone who saw the removal, reject keys made for the older position, and warn that the server may be hiding a change |
| Server shows different member lists to different clients | Needs a member to sign two different entries at the same position; a client that sees both reports a fork |
| Old key reused after re-registration | The old key stopped being a member when they were removed |
| Old channel name replayed | Names are bound to the log position and a revision counter |

### Remaining limits (documented, not fixed)

- **You trust the keys of the people you invite on first use.** When you invite "Bob" by name, the server supplies Bob's key and could substitute its own. Each member therefore shows "fingerprint not compared" until you compare over /tell and confirm. A strict mode can refuse to invite, or seal to, anyone not yet compared.
- **A removal takes effect when the remover's client publishes it.** The remover's client rekeys immediately. A server that suppresses that rekey stops the channel working for everyone else, but can't hide the removal from the remover, who is warned.
- **Metadata and availability.** The server still sees who is in which channel and can drop or delay anything.

### MLS

MLS (RFC 9420) solves the same problems with an audited standard, and scales better. There is no mature C# implementation, so adopting it would mean shipping a Rust library (OpenMLS) through native interop in both the plugin and the server.

**Decision (2026-10-03): build the log design for v0.2 as an interim step, then move to MLS once core functionality is confirmed in real use.** The group-key and membership layers stay behind interfaces so the switch replaces them without touching chat, UI or server routing.

### Protocol and tests

- New protocol pieces: a membership entry message; channel info carries the log (or the part after the client's position); a log-fetch request; invite, accept, decline, remove, leave and rank requests each carry their signed entry; epoch keys and names carry the log position. These changes moved the unreleased protocol to version 2, so a 0.1 plugin is told to update instead of failing later. `ForgetChannel` ("Remove from my list") was added later without a version change; an older server answers "Unknown request.".
- Acceptance tests:
    1. A server-inserted ghost never receives a key.
    2. A former member's invite is rejected.
    3. A non-moderator's invite is rejected.
    4. A hidden removal is detected by the remover.
    5. A forked log is reported.
    6. All existing malicious-server tests still pass.

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
- **Chat:** `/lgc1`–`/lgc50` commands today; a chat-input hook for a sticky channel later. Sending fails closed: an error never falls through to plain game chat. All remote text is sanitised before it reaches the chat log.
- **Game interop:** signatures live in one module; a missing one disables only its feature.
- **ChatTwo:** the original exposed `ExtraChat.ChannelNames`, `ExtraChat.ChannelCommandColours` and `ExtraChat.OverrideChannelColour` to ChatTwo, and added an invite item to ChatTwo's context menu via `ChatTwo.Register`/`Invoke`. LookingGlass keeps equivalent integration (IPC naming is an open question).

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
| Frame size (any request) | 128 KiB | Bounds rekey bundles and list responses; a rekey for 500 members is about 100 KB |
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

LookingGlass uses a new server and protocol, so users re-register once and channels are rebuilt through a planned import wizard. It has its own internal name and `/lgc` commands, so it can be installed beside the original.

## Testing and debug tooling

- **Debug accounts:** with `Dev:AllowDebugAccounts` on, characters on the fake world "Debug" register without Lodestone. Never on a public server.
- **Echo bot:** a headless client that accepts invites, takes part in rekeys and echoes messages; runs inside the server (`Dev:HostEchoBot`) or via `lgdev bot`.
- **`/lgdebug`:** connection state, protocol trace, notices, and tools to ping, force a rekey, or simulate an incoming message.
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
- [ ] **ChatTwo IPC names:** reuse `ExtraChat.*` or use `LookingGlass.*` and ask ChatTwo to support them?
- [x] **Command prefix and internal name:** `LookingGlass`, `/lgc1` to `/lgc50`.
- [ ] **Message history:** in 1.0, or later?
- [ ] **Limits:** confirm after beta load testing.
- [ ] **Public hosting:** who runs it, cost, privacy note, and acceptable Lodestone volume.
- [ ] **Move to MLS (RFC 9420)** once core functionality is confirmed in real use. The v0.2 signed membership log is the interim design; plan the switch as its own milestone, including how existing channels migrate.
