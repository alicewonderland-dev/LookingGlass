# How LookingGlass works

This document explains the design of LookingGlass: what each part does, what
the encryption protects, and why things are the way they are. It is for
technical readers. To use the plugin, see the [README](../README.md). To run a
server, see [server.md](server.md).

## Overview

LookingGlass adds cross-world chat channels to Final Fantasy XIV. It is a
clean-room rewrite of the ExtraChat plugin and server: it keeps ExtraChat's
core ideas, shares no code with it, and doesn't talk to ExtraChat's servers.

### The parts

| Part | Path | What it does |
| --- | --- | --- |
| Plugin | `src/LookingGlass.Plugin` | The Dalamud plugin: commands, chat output and windows. A thin shell around the core library |
| Core library | `src/LookingGlass.Core` | Cryptography, the membership log and the client session. It has no Dalamud dependency, so the echo bot, `lgdev` and the tests use it too |
| Server | `src/LookingGlass.Server` | Relays encrypted messages, stores the membership logs and sealed keys, checks every request, and looks characters up on the Lodestone. ASP.NET Core with SQLite |
| Protocol | `src/LookingGlass.Protocol` | One Protocol Buffers schema (`Protos/lookingglass.proto`) that every part shares |

### The trust model in brief

- **The Lodestone proves who you are, once.** Registering shows that a
  character is yours. After that, the plugin signs in with keys it holds.
- **Each character has long-term identity keys** on the player's computer.
  Other players see a fingerprint of them and can compare it over /tell.
- **Only members can read a channel.** Messages and channel names are
  encrypted with a channel key that only members hold. The server stores and
  relays ciphertext it can't read.
- **Members decide who is a member.** Membership comes from a log of changes
  that members sign. Every client checks the log itself, so the server can't
  add members or change ranks.
- **The server is trusted for identity recovery.** When a player re-verifies
  their character through the Lodestone with new keys, the server moves their
  places in channels to the new keys. A malicious server could use this to
  swap any member's keys for its own. It can't do it quietly: every member of
  the channel is told. This trade-off was chosen so that losing your keys
  doesn't lose your channels.
- **The server sees metadata.** It knows who is in which channel, when
  messages are sent and who is online, and it can drop or delay anything. It
  keeps each message, encrypted, for a week, so members who were away get it.

### Status

The current version is 0.2. It has registration, key login, identity recovery,
channels, invites, ranks, automatic rekeys, encrypted messages, the signed
membership log, online indicators, blocking, channel windows (pop-out chat),
message catch-up (what was sent while you were away), an opt-in chat log on the
player's computer, local chat with friends near you (`/lgl`), flags for abuse
and the operator's bans, and debug tooling. ChatTwo integration and the import
wizard come next. LookingGlass isn't moving to MLS (see [MLS](#mls)).

## Glossary

| Term | Meaning |
| --- | --- |
| Account | A character on one server, identified by its Lodestone ID |
| Identity keys | A character's long-term keys for one server address: an Ed25519 signing key and an X25519 key for receiving sealed keys |
| Fingerprint | A 25-digit number made from a user's identity keys, for comparing over /tell |
| Pinning | Remembering a user's keys the first time they're seen, and warning if they change (trust on first use) |
| Device token | The login a server gives one device after registering or key login |
| Key login | Signing in by signing a server challenge with the identity key, when the device token is refused |
| Membership log | A channel's append-only, hash-chained list of signed membership changes |
| Log position, head | An entry's sequence number and hash; the head is the newest entry |
| Place | A user's member or invite row in a channel: their rank or invite, under particular keys |
| Epoch, epoch key | The channel key for one stretch between membership changes |
| Rekey | Making the next epoch key and sealing a copy to each member |
| Catch-up | A member coming back gets the messages sent while they were disconnected, which the server keeps for a while (see [Message catch-up](#message-catch-up)) |
| First epoch | The first epoch whose key was sealed to a member's place: they may only fetch stored messages from it on |
| Sealing | Encrypting something to one recipient's X25519 key, so only they can open it |
| Key recovered entry | A log entry that moves a user's place to new keys after they re-verify through the Lodestone |
| Origin | The scheme, host and port of a server address (`wss://host:port`) |
| `PublicUrls` | The server's configured list of its own addresses |
| Development server | A server in ASP.NET Core's Development mode, with debug accounts and the echo bot |

## Goals and non-goals

### Goals

- **Encryption that holds against the server, short of swapping keys.** A
  malicious or compromised server can't read channels, forge senders or get
  channel keys without it showing. The one way in is replacing a member's keys
  through identity recovery, which every member is told about.
- **Removal means removal.** A member who leaves or is removed can't read
  anything sent afterwards.
- **A reliable client.** No game-object access off the main thread, no
  requests that hang, and every failure shown to the user.
- **A server one person can run and defend.** Rate limits, size limits, and no
  global lock that can stall everyone.
- **ChatTwo integration.** ChatTwo users get channel names, colours and
  context-menu actions, as with ExtraChat.
- **Room to grow.** New features are added as modules, and most can ship as
  client-only changes.

### Non-goals

- Wire compatibility with ExtraChat's server or protocol.
- Server-side moderation of message content. The server can't read it, by
  design.
- Clients outside the game (web, mobile) in the first release.
- Copying code from ExtraChat. This design describes behaviour; the
  implementation is written fresh.

## Lessons from ExtraChat

ExtraChat's principles were sound. Its failures came from trusting the server
with key distribution, and from unsafe concurrency on both sides.

| Area | ExtraChat's behaviour | Consequence | LookingGlass |
| --- | --- | --- | --- |
| Key exchange | New key pair on every plugin load, relayed by the server and never verified | The server can sit in the middle of any invite and read the channel | Long-term identity keys with fingerprints users can compare |
| Secret requests | The client encrypts the channel secret to any key the server names, without asking | The server can simply ask for any channel's secret | Keys only go to members in the verified log, sealed to their identity keys |
| Membership changes | No rekeying; invitees get the secret before accepting | Removed members and declined invitees read the channel forever | A new channel key (epoch) on every membership change |
| Sender identity | The server says who sent each message | The server can re-attribute or replay messages | Every message is signed by its sender |
| Registration | Challenge not tied to the requesting connection; no expiry on verify | Anyone can claim the key during the verification window | Challenge bound to the connection and identity key, with an expiry |
| Client threading | Shared dictionaries changed from thread-pool tasks while the UI reads them | Crashes, corrupted state, "Not on main thread" failures | One session owns the state; the UI reads immutable snapshots |
| Server locking | A global lock held across awaits and re-entered | Whole-server deadlock under contention | No lock held across an await; bounded, non-blocking fan-out |
| Requests | No timeouts; waiters dropped on reconnect | UI stuck "busy" forever | Every request ends in a result, an error or a timeout |
| Errors | Swallowed on the client; any error drops the server connection | Silent message loss, reconnect loops | Typed error codes shown to the user; only protocol violations disconnect |
| Limits | No rate, size or count limits | Easy to flood the server and the Lodestone | Limits advertised in the handshake and enforced by the server |
| Secret recovery | The serialiser was missing two request kinds | The feature never worked | The server stores sealed epoch keys, so members can catch up |
| Disconnects | Messages are only relayed to whoever is connected | Messages sent during an untimely disconnect are lost | The server keeps the encrypted messages for a week, and a returning member catches up (see [Message catch-up](#message-catch-up)) |
| Upkeep | Stale CI, an example config that doesn't load, frozen dependencies | Hard to build, hard to trust | The plugin's package versions are locked; CI builds and tests every push and pull request, and Dependabot proposes dependency updates weekly |

## Core principles

Kept from ExtraChat:

1. **The server relays and doesn't read.** It knows who is in which channel.
   Channel names, messages and keys are opaque to it.
2. **The Lodestone is the identity root.** A character proves ownership
   through its Lodestone profile. The Lodestone ID is the account's stable
   key.
3. **It feels like native chat.** Channels are used through the game's own
   chat input and appear in the game's own chat log.
4. **One protocol, defined once.** Client and server share one schema instead
   of hand-written copies on each side.
5. **Small enough for one person to run.** One server process and one
   database file.

New in LookingGlass:

6. **Trust keys, not the server.** The server hands out public keys. Clients
   verify them and warn when one changes.
7. **Every membership change is a new epoch.** Joins, leaves, removals and
   recoveries rotate the channel key.
8. **The main thread owns the game; one session owns the network.** Nothing
   else touches either.
9. **Every request completes.** It ends in success, a typed error or a
   timeout, never silence.
10. **Version and negotiate everything.** Features are capabilities that
    client and server agree on when they connect.
11. **Everything is bounded.** Message size, rates, queues, channel sizes and
    pending invites all have limits.

## Architecture

The client has three layers:

- **The game thread** runs hooks, chat output, commands, the ImGui windows,
  and captures a snapshot of the player each frame.
- **The session** (one per connection) owns the socket and all session state.
  The game thread sends it commands and reads its snapshots.
- **The core library** does the protocol, cryptography and key handling. It
  keeps keys in an encrypted store on disk.

ChatTwo will talk to the game-thread side over IPC.

The server has four:

- **The gateway** handles the WebSocket, the handshake, logging in and limits.
- **The policy and router layer** makes one authorization check per request
  and fans events out to members.
- **SQLite** stores everything, with each multi-step change in one
  transaction.
- **A Lodestone worker** makes rate-limited, cached Lodestone lookups over
  HTTPS.

The client and server talk over one WebSocket (TLS in production). Only
ciphertext and public keys cross it.

## Identity

### Identity keys and fingerprints

Each character has one Ed25519 signing key and one X25519 key. The signing
key vouches for the X25519 key with a binding signature. The keys are made
once and kept across sessions.

The plugin makes separate keys for each character and each server address. A
key is registered to at most one account on a server.

Others see a 25-digit fingerprint of a user's keys. Clients pin each user's
keys, name and world the first time they see them:

| Shown for a member | Meaning |
| --- | --- |
| Question mark, "not compared" | Pinned on first use; you haven't compared fingerprints yet |
| Check mark | You compared fingerprints and marked them verified |
| Circling arrow, "New key" | They re-verified their character through the Lodestone; you haven't compared the new key yet |
| Warning, "key changed" | Their key changed without explanation, or their name now belongs to a different account |

The warning stays until the user marks the new key verified. To compare,
users click the icon before a member's name (or choose **Compare
fingerprints...** in the member's menu), compare over an in-game /tell, which
doesn't pass through the LookingGlass server, then press **Mark verified**.
This is advanced mode; simple mode, the default, says the same in everyday
words (see [Simple and advanced mode](#simple-and-advanced-mode)).

### Registering through the Lodestone

Registering links a Lodestone character to identity keys. Only the connection
that asked for the challenge can complete it.

1. The plugin makes the character's identity keys, if it has none for this
   server.
2. It asks to register (`StartRegistration`), sending the character's name and
   home world, its identity public keys, the server URL it connected to, and a
   fresh 32-byte random client nonce.
3. The server:
   - checks that the URL's origin is one of its own (see
     [Server addresses and relay protection](#server-addresses-and-relay-protection));
     debug accounts skip this;
   - looks up the Lodestone ID, through a queued, cached lookup;
   - issues a 32-byte random nonce, bound to this connection and identity key;
   - works out the registration code (see [The registration code](#the-registration-code)).

   The challenge expires after `Lodestone:ChallengeMinutes` (15 by default; the
   server only starts with 1 to 60). Debug accounts get no code.
4. The plugin works out the code again from its own server address, its own
   keys, the nonce, its client nonce and the Lodestone ID it was sent. It
   refuses to show any other code.
5. The user pastes the code into their Lodestone profile and presses
   **Verify**, on the same connection. The plugin signs the nonce, the
   Lodestone ID and the server URL with the identity signing key
   (`lookingglass/registration/v1`).
6. The server checks:
   - the challenge belongs to this connection and hasn't expired;
   - the signature is made by the key being registered, which is the key the
     code was made for;
   - the signed URL has the origin the registration was started for (not for
     debug accounts);
   - the code is in the Lodestone profile.

   It then records Lodestone ID → identity keys and issues a device token.
7. The plugin stores the token and keys. The user can delete the code from
   their profile.

A failed check stores nothing, and the registration stays open on the
connection.

Requests from older plugins are refused with a request to update: a
`StartRegistration` without the URL or the client nonce, or a registration
without the signature (a plugin from before 0.2). A client nonce of any other
length is an invalid request. None of these changed the protocol version.

### The registration code

The code is `LGC-` and five groups of four characters
(`LGC-XXXX-XXXX-XXXX-XXXX-XXXX`). It is the first 100 bits, in Crockford
base32, of SHA-256 over:

- `SigningPayload("lookingglass/lodestone-code/v1")`;
- the server's origin (`wss://host:port`, as `ServerOrigin` normalises it);
- the identity signing key being registered;
- the server's nonce and the client's nonce;
- the Lodestone ID.

When the server reads the profile:

- matching starts only at a literal `LGC-`;
- case doesn't matter, and O is read as 0, I and L as 1, so a code typed by
  hand works;
- only ASCII counts: no other character counts as one of the code's, even one
  whose upper case is ASCII;
- no code contains an L, so a pasted code can't be read as another one
  starting inside it.

Why the code is bound to the address and key is explained under
[The code is bound too](#the-code-is-bound-too).

### Registration rules

- **Registering proves you hold the key.** A user's public identity keys,
  binding signature included, are served to everyone who shares a channel
  with them. Without the signature in step 5, anyone could register another
  user's keys for their own character. They could then register again with
  new keys, and so have the server retire the victim's key.
- **One account per key.** A signing key that is another account's current
  key is refused. This is checked when registering starts (once the account
  is known) and again in the transaction that registers it. With the proof
  above, only the key's owner could try this, by registering a second
  character with one character's keys. The plugin never does that.
- **Registering again is allowed.** It revokes the account's old device
  tokens. It keeps the key the plugin has. Only a plugin without keys (a lost
  file, a new computer) publishes new keys, and then the account's places
  move to them (see [Recovering an identity](#recovering-an-identity)).
- **A replaced key is retired.** When an account's signing key is replaced, by
  registering new keys or by [Reset my identity](#reset-my-identity), the old
  key is retired for that account. It can't sign in, and registering it again
  is refused with advice to reset the identity instead. So a stale copy can't
  take the account back from its new key.
- **Retirement is per account.** `retired_keys` is keyed by user ID and key
  (schema 6). Nothing one account does with a key affects another. Keys
  replaced before the server kept this list aren't known.
- **Names and worlds** are keyed by Lodestone ID. Name lookups go through an
  index that is updated on rename.

**Old databases.** A database from before registrations were signed may hold
one key for two accounts, or (from schema 5) a key retired for one account
that another holds. The upgrade to schema 6 leaves these, and logs each at
Warning with the accounts and a short hash of the key, since the server can't
tell which account owns the key. Until an operator removes the wrong
registration, neither account can register that key again (the owner resets
their identity instead). A schema 5 retirement dropped as a duplicate is
lost.

## Signing in

### Device tokens

- A device token is a 256-bit random value. The server stores only its hash,
  and can revoke it.
- Each user keeps their 20 most recently used devices. Older ones are dropped
  as new ones are added.
- **A refused token is kept.** A server that doesn't recognise it may be the
  wrong server, or one that was reset or restored from a backup. So the
  plugin keeps the token, stays connected (so the user can register again),
  and tries it again: on the same connection, backing off to once a minute,
  and on every reconnect.
- Only registering again, or **Forget account** in the debug window, replaces
  the token.
- Logging in and registering go through one gate. The old token isn't tried
  while a registration challenge is pending, so a retry never races
  registering again.

### Key login

The Lodestone is the first proof of identity. After that it is only the
fallback for a lost key, or a server that has never seen the user. Otherwise
the plugin signs in with its identity key:

1. The server refuses the device token.
2. The plugin asks for a challenge. It is single use, bound to the
   connection, and lasts 60 seconds.
3. The plugin signs the challenge, its user ID and the server URL it connected
   to, with the account's current key.
4. The server checks the signature and issues a new device token, which
   replaces the refused one. Other devices keep theirs.

A retired key can't key-login. Every answer that reaches the account check
verifies one signature (unknown accounts against a key nobody holds), so the
timing doesn't reveal which accounts exist.

**Limits.** Key logins are limited:

- per connection: 3 challenges;
- per IP address: challenges (60 an hour), and failures (10 an hour; a
  challenge counts as a failure until it is answered correctly, so an address
  that only asks for challenges is stopped too);
- per account from each address: challenges (60 an hour), and failed answers,
  half the address's allowance, rounded up (5 an hour).

An account asking again, or failing again, from an address within the hour
doesn't count against the address again: its own allowance there limits it.
A plugin whose login the server no longer knows (after a reset, say) tries
key login on every connection, and the server closes connections that don't
log in after 3 minutes, so it asks about 20 times an hour. Without this, a
few of them behind one address (a household, a shared NAT) used up its
allowance, and nobody there could sign in with their key. Now it takes 10
different failing accounts (or 60 asking).

Nothing is limited per account alone. A signature can't be guessed, so the
limits only stop spam, and failures from other addresses must never lock an
account out of key login from its own. An address shared with an attacker
(one NAT, say) still shares its per-address limits: they can keep its key
logins refused, an hour at a time (see [Known limitations](#known-limitations)).
The settings are in [server.md](server.md#limits-worth-knowing).

**What the user sees.** "Login not recognised" appears only when the server
refuses both the token and the key. That happens when:

- the server has never known the account;
- the identity was reset elsewhere, or re-verified on another computer;
- the key is gone;
- the plugin connects through an address the server doesn't list (the plugin
  warns about this separately).

The plugin keeps trying the login every minute or so, so it works again by
itself once the right server is back. **Retry now** tries the token and the
key at once. The message also says that if the character was re-verified on
another computer and it wasn't the user, **Reset my identity** takes the
channels back.

## Server addresses and relay protection

### The threat

A user may use more than one server. A malicious server M could fetch a
challenge from another server B, have the user sign it, and replay the
signature to B.

For key login, that would give M a login on B. For a registration, it is an
account takeover: B registers whatever key signed (the user's key for M, so
separate keys per address don't help) and hands M the new device token.

### The defence: signed addresses

Key logins, registrations through the Lodestone and identity retirements all
sign the server URL the plugin connected to. A server only accepts a
signature for one of its own origins, as listed in `PublicUrls`.

- **Outside Development, `PublicUrls` is required.** The server refuses to
  start without it, and logs what to set. A request handler built without it
  accepts no signed URL at all.
- **Plugins are told the list.** An honest plugin connected through an
  unlisted address (say, the tailnet IP when only the machine name is listed)
  would be refused too. So the server's `Welcome` lists its addresses
  (`public_urls`), and the plugin warns as soon as it connects if its own
  address isn't among them, naming the listed ones.
- **Refusals say why.** The server's refusal names the address used and the
  accepted ones (they're public anyway), and the server logs it at Warning.

### Each address must be the server's alone

The checks tell servers apart by address only. An address that another server
can also have protects nothing against that server:

- a single-label name (a short MagicDNS or LAN name);
- a local-network suffix (`.local`, `.lan` and the like);
- a private, CGNAT, loopback or link-local IP;
- any plain `ws://` address (whoever answers at the name).

A malicious server that a user reaches under the same address can relay to
this one. That is fine on a private network the operator controls. Elsewhere,
list `wss://` addresses with fully qualified names. The server logs a Warning
at startup for each listed address that may not be its alone
(`RequestHandler.WhyNotUnique`).

### Development servers without `PublicUrls`

A Development server without `PublicUrls` accepts the scheme and `Host`
header of each connection. A relay sets these to whatever the user signed
for. There:

- the plugin's separate keys per server address still stop a relayed key
  login (the key the user signs with for the relay isn't registered on the
  target);
- nothing stops a relayed registration, which registers whatever key signed
  it;
- nothing stops a relayed registration code, since the server makes the code
  for whatever origin the `Host` header names.

So this is for private test servers only.

### The code is bound too

**The attack.** Without binding, a malicious server M could start a
registration on another server B for the user's character, with M's own key.
It would show the user B's code as its own. Once the user put the code in
their profile, M would complete the registration on B, signing the proof
itself for B's address.

**The defence.**

- The code is made from the server's origin, the key being registered, both
  nonces and the Lodestone ID.
- B only issues a code for an origin it accepts, and only lets the key it was
  made for finish.
- The plugin works the code out from its own server address and its own key.
  B's code for M's key and B's origin isn't the code for the user's key and
  M's origin.

So the plugin refuses it. It shows no code, drops the challenge, and logs a
warning naming the origin and the character, never the code. It tells the
user: "This server sent a registration code that doesn't belong to it. It may
be passing on another server's code. Don't put it in your Lodestone profile."

**Codes in the server's words.** M can't slip a code into what it says either
(an error like "LGC-… isn't in your Lodestone profile yet", an announcement, a
user name). The plugin replaces every code in server text with "[code
removed]", except the code it checked itself (`LodestoneCode.Redact`). This
covers errors, announcements, status texts, notices, names, the plugin's log,
and chat text through `TextSanitizer`. It reads codes the way the Lodestone
check does, and sees through invisible characters. The server's own "not
found" message names no code.

**Why 100 bits.** To get a relayed code accepted, M must find its own nonce
and Lodestone ID whose truncated hash, with the user's client nonce, equals a
code B issued. M can't choose B's nonce. That is a 100-bit second preimage
against however many of B's codes M holds open at once:

- one per connection, a few an hour per IP (IPv6 /64s are plentiful);
- each for at most an hour, the longest `ChallengeMinutes` allows.

The client nonce is fresh and random for each `StartRegistration`. So M can
only start once the user's plugin asks, and must answer before the plugin
stops waiting (60 seconds). Nothing can be precomputed, even though the user's
key is stable per server and M may know it.

Even with 2^20 codes held, M would need 2^80 hashes in a minute: about
2×10^22 a second, more than all Bitcoin mining. At 80 bits it would be 2^60
hashes in a minute, possible for a large operation. So 100 bits it is, five
characters more. Without the client nonce, M could grind for the user's key
in advance, for as long as it liked, and only need to hold B's codes against
a table.

**Older plugins.** Servers that check codes ask a plugin from before codes
were checked to update. Such a plugin is **not protected** against a
malicious server: it shows any code it is sent.

## Secrets on the client

Each character's secrets for one server address live in one file,
`secrets-<character>-<address hash>.bin`, in the plugin's config folder
(under XIVLauncher's `pluginConfigs`). The file holds:

- the identity private keys and the device token;
- channel epoch keys;
- what the plugin knows about others and the channels: pinned keys, blocked
  users, the log positions it has verified, the newest channel names it has
  accepted, and the newest message times per sender (see
  [Replay protection](#replay-protection)).

**Bound to its address.** The file records the address it belongs to, and
its name holds a 128-bit hash of it. The plugin refuses to use a file for any
other address, so one server's keys and login are never sent to another. The
plugin also never follows redirects, so a server can't hand the connection
and login to another.

**Encryption.** The file is encrypted with Windows DPAPI, which doesn't move
between machines. Where DPAPI is unavailable (Wine, Proton) it uses a random
local key file instead, which guards against accidental sharing rather than a
local attacker. Settings shows which is in use. An optional passphrase for
the key file is planned (see
[A passphrase for the key file on Wine and Proton](#a-passphrase-for-the-key-file-on-wine-and-proton)).

**Backups.** Every save keeps the previous version next to it as
`secrets-….bin.bak`. If the file is missing or damaged, the plugin loads the
backup and says so in chat. No file is ever deleted.

**Old file names.** Earlier versions named these files after a 48-bit hash
(12 hex digits). The plugin moves each one to its new name the first time its
address is used (at startup, for the configured address), and keeps the old
file as a backup.

- It moves a file only if the file names no address yet, as read from the
  file itself, never from its `.bak` (which a move leaves holding the
  unstamped original).
- It moves a file only once. If the new file and its `.bak` are lost later,
  the plugin doesn't go back to the old file by itself: it may hold an older
  login, older channel keys, or a key reset since.
- Instead, Settings says "A backup of your identity from <date> exists.
  Restore it?", and restores it only if the user confirms. An old file that
  can't be read as it is is treated the same way.

Channel numbers, nicknames and colours are plugin settings, kept per
character, and never sent to the server.

**The chat log**, if the player keeps one, is kept apart from the secrets
file, per character and address too, under a key of its own protected the same
way (see [Chat log on this computer](#chat-log-on-this-computer)).

## Moving to a new server address

Identities are kept per address. So `ws://<machine>:5180/ws`,
`ws://127.0.0.1:5180/ws` and `wss://<machine>.<tailnet>.ts.net/ws` count as
different servers.

When the user changes the address in Settings, and a character has an
identity for the old address but none for the new one, the plugin:

1. asks the server at the old address (already trusted with the login)
   whether the new address is one of its own;
2. asks the server at the new address whether the old one is one of its own;
3. if the new address is `wss://` and both servers list the other in
   `PublicUrls`, offers "This is the same server. Keep your identity?";
4. if the user agrees, asks both servers once more (the dialog may have been
   open a while; a copy needs a check at most a minute old) and, if they still
   agree, copies the character's keys, login and channels to the new address.

Otherwise it says why: the new address isn't `wss://`, one server doesn't
list the other, one of them can't be reached, or the server lists no
addresses. The new address then counts as a different server, where the user
registers with new keys.

Either way, the identity for the old address is kept, so switching back
works. Channel numbers, nicknames and colours belong to the character and the
channels, so they follow along. A carried identity's chat log, if the player
keeps one, moves with it (see
[Chat log on this computer](#chat-log-on-this-computer)).

**Why both servers, and why only `wss://`.**

- The new address's word alone never counts. A server at a new address can
  never get an identity by claiming to be the old one: the old server has to
  say so.
- The servers vouch for names, not for whoever answers at them. Over plain
  `ws://`, or a short name the local network resolves, someone else could
  answer at the new name, repeat the real server's addresses, receive the
  copied login in the clear and relay key login challenges. Only TLS proves
  which server answers.
- A server key in `Welcome` wouldn't help without channel binding, since a
  relay forwards its proofs unchanged.

## Lodestone traffic

- All Lodestone requests go through one worker, with a server-wide rate limit
  (one every 2 seconds, at most 20 waiting) and a cache of the characters
  found. A search that finds nobody isn't cached, so someone who fixes a
  typo, or makes their profile public, is looked up again at once.
- Nothing is asked of the Lodestone for a name the game wouldn't allow (a
  first and last name, each 2 to 15 letters, apostrophes or hyphens): it is
  refused at once, saying what to fix, and costs nothing. The world isn't
  checked, as the plugin sends the game's own name for the character's home
  world, and a list in the server could lack a new one; the server's list of
  public worlds only spells a world on it as the game does. A search is for
  the exact name on one world, and reads at most 2 pages of results, so a
  world that doesn't exist costs at most that (counted as below).
- Registration is rate-limited per address (`RegistrationsPerHourPerIp`, 10
  an hour), and verification attempts per connection (once every 10 seconds,
  10 per challenge). Each Verify reads the character's page afresh (asking
  for no cached copy), in the same queue. Addresses here are IPv4 addresses
  and IPv6 /56s, as for connections, so one customer's many /64s count once.
- A registration whose character the Lodestone doesn't list, or that the
  Lodestone can't be asked about (an error, or no answer within HttpClient's
  20-second timeout), gives its registration back: the user fixes it and
  tries again. Each request such a lookup made of the Lodestone counts per
  address on its own (`RegistrationLookupFailuresPerHourPerIp`, 20 an hour,
  so at least 10 lookups that fail), checked before anything is asked of the
  Lodestone. So an address can make at most about 30 searches an hour of
  the queue, and no one address can fill it. A refusal because the queue is
  full costs nothing.
- A Verify the Lodestone couldn't answer isn't counted against the
  challenge, but only the first 3 times: past those it counts, so one
  pending registration can't keep taking turns of the queue.
- What the user is told says what to do. A character not listed: check the
  name and home world; a new character can take a while to show up, and one
  whose profile is private may not show up at all, so make it public in the
  character's privacy settings on the Lodestone, wait a moment and try again.
  A private profile when verifying: make it public, wait a moment, press
  Verify again. A profile is taken as private when its page has no profile
  text and says the profile is private ("profile" then "private" in one
  clause). That is a guess at the real page, not yet checked against one;
  any other page without profile text gets a general message that also says
  to make the profile public. A limit reached says how long to wait ("Too
  many registrations from your address; try again in about 40 minutes.").
- A registration refused for naming an address that isn't the server's costs
  neither of those, but logs a warning. So those refusals are counted per IP
  on their own (`RefusedRegistrationsPerHourPerIp`, 10 an hour), and refused
  without logging past that.
- Names and worlds that aren't plain text (control, format or unassigned
  characters, line or paragraph separators) are refused before anything is
  logged.

## Recovering an identity

**Re-verifying a character through the Lodestone with new identity keys
restores everything.** The account keeps its places in every channel, rank
included (admin too), and its open invites, under the new keys. There is no
per-channel opt-out, delay or veto. The other members are told, in plain
words, that the person re-verified their character and has a new key.

This covers a lost secrets file, a new computer (DPAPI-protected files don't
move between Windows machines) and [Reset my identity](#reset-my-identity)
alike.

### What the server does

A registration completes when the Lodestone code is found (or, for a debug
account, straight away). If the account then has places under keys other
than the ones just registered, the server does this in one transaction:

1. It replaces the account's keys, retires the old signing key for the
   account, and deletes every device of the account. The old key can't sign in
   or be registered again, and every login made with it stops working.
2. It deletes the channel keys sealed to the account.
3. For every channel where the account has a member or invite row that isn't
   forgotten ("Remove from my list") and isn't under the new keys, it checks a
   key recovered entry with the same rules clients use, appends it to the log,
   and moves the row to the new keys, keeping the rank or invite. This
   includes places still under older keys, from before recovery existed: they
   are the same account. An entry the rules refuse is logged, and the row is
   left as it was.
4. A moved member row is marked as waiting for a key (`awaiting_key`, schema
   8), and its channel needs a rekey. A moved invite row waits too, and that
   carries over when the invite is accepted.

Then the server:

- disconnects the account's other sessions;
- sends each channel's members the new entry;
- asks a member who is online to rekey. This is never a member who is waiting
  for a key: they hold none for the channel, so they don't know the name to
  carry into the new epoch. The exception is when nobody who holds the key is
  left (see [When nobody holds the key](#when-nobody-holds-the-key)).

That rekey gives the new keys the channel's key, and the old ones nothing
more. The waiting flag clears with the next rekey.
`RegistrationComplete.places_restored` says how many places moved, and the
plugin tells the user. The main window says before registering that this will
happen.

### When nobody holds the key

Waiting for a member who holds the key only works while one exists. Everyone
who held it may have re-verified: Alice recovers while Bob is offline, then
Bob recovers on a new computer before his old one comes back. Or the only
others may be places under old keys, which can't rekey.

**The rule.** If no member row exists, online or not, that:

- isn't forgotten,
- is under its owner's current keys, and
- isn't waiting for a key,

then the server asks a member who is waiting instead, and says so:
`RekeyNeeded.no_key_holder`, and `ChannelInfo.no_key_holder` with
`rekey_pending` for a member who comes online later. That member's client
doesn't know the channel's name, so it makes the new key under the placeholder
name "Restored channel".

The rule is the same everywhere a rekey is asked for: a registration moving
places, an accept, a leave, a removal, a member coming online. So every mix of
members (waiting, under old keys, forgotten) gets a key, and a channel only
waits while someone who holds the key may still come back.

**Details.**

- If a member who holds the key joins or comes back before that rekey (say,
  accepting an invite made before everyone recovered), they are asked
  instead, with no flag. A placeholder rekey made for the earlier position is
  then refused as stale.
- The flag is the server's word, like the rest of rekey scheduling. A server
  that lies can only rename the channel to the placeholder, which every member
  sees.
- Once a placeholder rekey has happened, the channel's real name is gone for
  good. Later keys carry the placeholder over. A member who still knew the
  real name, if one came back, would see it replaced, and is told who replaced
  it (as for any rekey that changes a name it knew; a rekey naming the channel
  anew carries no name over). The admin can rename the channel.
- Only a member holding no key for the channel names it anew. One holding a
  key whose name it can't show (a server can garble it, and claim nobody
  holds the key) refuses, and waits for the name.
- A member who holds the key but never comes back keeps the channel waiting.
  An admin who is back with a new key can remove them. The new key then comes
  from whoever is left, under the placeholder if nobody left holds it.

### The key recovered entry

`MEMBERSHIP_ENTRY_KIND_KEY_RECOVERED` (protocol version 3):

| Field | Value |
| --- | --- |
| Actor | The subject |
| `actor_key_hash` | The hash of the new keys |
| `subject` | The keys the place had |
| `new_keys` | The keys the place moves to |
| Timestamp | The server's |
| Signature | The new keys' signature over `lookingglass/key-recovery/v1`, the user ID and both new public keys (`KeyRecoveryProof`) |

The signature isn't over the entry. The plugin makes it once, with the
registration (`CompleteRegistration.recovery_signature`), and the server puts
it into every channel's entry. It only proves that whoever holds the new keys
agreed to be that user. So the server can't bind keys someone else holds (taken
from a channel they share, say) to another user's place.

A registration that would move places without the signature is refused, with
a request to update. One signed by other keys, or for another account,
registers nothing.

The new keys are in the entry's signed payload, so the entry's hash covers
them, and it is chained like any other entry. (They are added only when
present, so every other kind of entry signs and hashes as before.)

**How clients check it.** A client replaying the log accepts a key recovered
entry only if, at that point in the log:

- the subject is a member or invitee under exactly the keys it names;
- the new keys are well formed and can be sealed to;
- the new keys aren't the place's keys, and aren't anyone else's in the
  channel (member or invitee);
- the actor is the subject, `actor_key_hash` is the new keys' hash, and the
  signature is theirs over the user ID and those keys;
- it has no rank or invite field.

**Applying it** moves the place. A member keeps their rank; an invitee keeps
their invite (its position and inviter). A member's move is a membership
change: keys and names made before it are for another membership, so the
channel is rekeyed before anyone sends. An invitee's move isn't.

Each client remembers each user's last four moves (`KeysAt`). So a name or key
a member signed before moving is checked against the keys they had when they
signed it. Without that, a member who restarted before the rekey couldn't
show the channel's name, and so couldn't make the new key.

### What other members see

Each other member gets a line in the channel: "Alice@World re-verified their
character and has a new key." It appears:

- whenever the entry follows a log position they had verified, live or when
  catching up after being offline;
- whenever it changes what the client held for that user, even in a log
  replayed from the start (an invite's, say). A key pinned before, from
  another channel, is replaced; if the user had compared it, the line adds
  that the comparison was for the old key. Or a "key changed" warning is
  explained.

A log replayed from the start to someone who never saw the user before says
nothing: a new member reads history, not news.

The new key is pinned as expected, not as an unexplained change. There is no
"key changed" warning, but the member shows "New key", not compared, until the
user compares fingerprints over /tell and marks them verified. A "key changed"
warning raised first from the server's identity lookup turns into "New key"
once the log explains it.

### What the member whose keys moved sees

An honest server disconnects every session of the old keys before it tells
anyone, and they can't sign in again. So the old computer only sees "Login
not recognised". That message says that a character re-verified on another
computer has had its key replaced and its channels moved. It adds that if it
wasn't them, **Reset my identity** (Settings) re-verifies them through the
Lodestone and takes the channels back. Registering the old key again is
refused with the same advice.

If a client does see a key recovered entry moving its own place away from the
keys it holds (a server that swaps keys without shutting the old ones out), it
says the same once, as a warning. Every such channel shows "Moved to another
key" instead of the old key's place.

On the new keys:

- A moved invite shows without its channel name (that was sealed to the old
  key) until it is accepted and a member shares the channel's key.
- Until a member gives the channel a new key, the user can already remove
  members and change ranks (as their rank allows), but can't read, send,
  invite or rename.
- A recovered member alone in a channel, or one asked to rekey where nobody
  holds the key, makes the new key themselves, under the placeholder name
  "Restored channel". The admin can rename it.

### The trust this needs

Clients can't check the Lodestone, so a key recovered entry is the server's
word. A malicious or compromised server can therefore:

- replace any member's keys with keys it holds, in any channel, whenever it
  likes;
- after the next rekey, read that channel's messages and act as that member,
  with their rank (as admin, say);
- show different members different recoveries, since these entries carry no
  member's signature. A client only notices by comparing the whole log, not
  from a single entry.

The new keys' signature stops it binding someone else's keys, not its own.

The only defence is visibility:

- every member is told in the channel;
- the member shows "New key" until fingerprints are compared over /tell, which
  doesn't pass through the server;
- the member whose keys were swapped is warned if their client sees the entry
  or finds its login refused, with what to do. A server can hide that from
  them, but not from everyone else.

Debug accounts, which anyone can register on a Development server, take their
channels along too.

This means an honest server is only as safe as its operation. Whoever breaks
into it can swap any member's keys. So the server's host, its updates and its
hardening protect every account's channels, not only their availability.

### Places still under old keys

A place can still belong to keys the account no longer has:

- one from before recovery existed (the account registered again on an older
  server);
- one whose entry the rules refused;
- one removed from the user's list (a forgotten row is never moved).

The account's next registration through the Lodestone with new keys moves the
first kind, and tries the second again. Meanwhile the place stays with the
old key until a moderator removes it. "Remove from my list" removes any of
them from the user's list (see
[Stale places and "Remove from my list"](#stale-places-and-remove-from-my-list)).

## Reset my identity

**Reset my identity** (Settings, under "Your identity") is for a key that was
lost or may have been stolen. It makes new keys, keeps nothing of the old
identity for that server, and then the user registers again through the
Lodestone. Registering publishes the new keys, revokes old tokens, stops the
old key signing in, and moves the account's places (ranks, admin included, and
invites) to the new keys, as any registration with new keys does. Nothing is
left or declined first, and the user doesn't need to hand admin on: the
channels stay theirs.

**1. Retiring the old key on the server.** While logged in, the plugin sends
`RetireIdentity`, signed by the current key over:

- the user ID;
- the hash of the connection's device token;
- the server URL it connected to (checked as for key login).

So a stolen token alone can't wreck the identity, and a signature made for
one login, or for another server, doesn't work here. (User IDs are Lodestone
IDs, the same on every server.) No challenge is needed, since a retirement
only ever retires the key that signed it.

The server retires the key and deletes every device of the account in one
transaction, then logs the connection out. Until the new keys are registered,
the account has no working login (the Lodestone is needed), and others still
see the old key.

If the plugin isn't logged in, or the server refuses or is too old to know
how, the reset goes on locally. The user is told that the old key and its
logins keep working on the server until they register again, which retires
the old key then.

**2. Removing the old identity locally.** The plugin removes the old identity
(login and channel keys) from every file of the character whose signing key
is the one being reset:

- the address's own file;
- copies a move made for the server's other addresses;
- the old-style file;
- every `.bak`. Each file is rewritten twice, so the backup holds the new
  contents too.

Those files keep what they hold about others and the channels (pinned keys,
blocked users, verified log positions). So a vouched move can carry the new
identity there later, and the channels carry on from the positions verified.

The user's identity on other servers isn't affected.

## Channels and the membership log

Every membership change is a signed entry in a hash-chained log for the
channel. Clients replay the log to work out who the members are and what rank
each has. Every epoch key and channel name commits to the log position it
was made for.

### Ranks

Ranks are ordered Invited < Member < Moderator < Admin. One table on the
server (`Policy`) decides every channel request, and is unit-tested over every
rank and action.

| Action | Who may |
| --- | --- |
| Read the membership log | Anyone with a place, invitees included (to check an invite before answering it) |
| Send, fetch epoch keys, rekey | Members and above |
| Fetch stored messages (catch-up) | Members and above, only those sent under epochs from their first one on (see [Message catch-up](#message-catch-up)) |
| Leave | Members and above, except the admin while others remain |
| Invite, cancel an invite | Moderators and above |
| Remove a member | Moderators and above, for members ranked strictly below them |
| Change ranks, hand over admin | The admin |
| Rename, disband | The admin |

A place under keys the account no longer has may only read the log (see
[An old key's place has no say](#an-old-keys-place-has-no-say)).

### Entries

Each channel has an append-only log. Every entry carries:

- the channel ID and a sequence number;
- the hash of the previous entry;
- its kind;
- the actor (user ID and key fingerprint);
- the subject (user ID and identity keys);
- a rank, where relevant;
- a timestamp.

It is signed by the actor.

| Entry | Signed by | Valid only if, at that point in the log |
| --- | --- | --- |
| Genesis | The creator | It is entry 0. The creator becomes admin |
| Invite | The inviter | The inviter is a moderator or admin; the invitee isn't a member or invited |
| Accept | The invitee | It names the exact invite entry, and is signed by the exact key that invite names |
| Decline | The invitee | There is an open invite |
| Cancel invite | A moderator or admin | There is an open invite |
| Remove | A moderator or admin | The subject's rank is below the actor's |
| Leave | The leaving member | They are a member, and not the admin of a channel with others in it |
| Set rank, transfer admin | The admin | The subject is a member |
| Key recovered (protocol version 3) | Appended by the server; signed by the new keys over the user ID and those keys only | See [The key recovered entry](#the-key-recovered-entry) |

An open invite lapses when whoever made it is removed, leaves or is demoted.

### What clients rely on

The server stores and serves the log, and checks each entry before appending
it. Clients never rely on that check:

- The server can't forge an entry: that needs a member's signature.
- It can't reorder entries: that breaks the hash chain.
- Members are bound to the keys the log admitted them with. A removed
  member's key signs nothing that counts after the removal.
- A new key of a member's counts for nothing until a key recovered entry moves
  their place to it, or a fresh invite and accept admits it.

The exception is the key recovered entry, which is the server's word that a
user re-verified their character (see
[The trust this needs](#the-trust-this-needs)).

### Freshness

- Each client keeps, and saves, the newest log position (sequence number and
  hash) it has verified for each channel. It carries on from there after a
  restart, and fetches only new entries.
- Every sealed epoch key and every channel name signs the log position it was
  made for. A client rejects a key or name made for an older position than its
  own. For a newer one, it fetches and verifies the log first.
- **Forks.** If a client sees two different, validly signed versions of the
  log (someone is being shown a different member list), it says so, keeps
  what it verified, and marks the channel "check members".
- **Rollbacks.** If the server shows a client an older log than it has already
  verified (it may be hiding a change, such as a removal), the client does the
  same.

### Stale places and "Remove from my list"

A place under keys the user no longer has can't be left: only the old key can
sign a leave. Such a place comes from before recovery existed, or is one
recovery couldn't move (see [Places still under old keys](#places-still-under-old-keys)).
The plugin explains this in plain words ("Your old key's place"), and the
channel's menu offers **Remove from my list...** instead of Leave and Disband.
That sends `ForgetChannel`.

`ForgetChannel` isn't a log entry. The server marks the account's member and
invite rows for the channel `forgotten` (schema 7), but only if none of them is
under the account's current keys (checked in the transaction). The plugin
forgets the channel's key, number, nickname and colour. Declining an invite
made for an old key removes it the same way.

The rows stay, because they are the log's state. Entries are checked against
them, and a rekey still needs a key for every member at the head. Clients seal
to every member of their verified log, the old key included, so rekeys go on
working for the others.

A forgotten row:

- isn't listed, and doesn't count towards the user's limits;
- has no rank for any request, so it can't fetch, send, rename or disband;
- gets nothing about the channel: not its events (messages, log entries,
  renames, rekey requests, new epoch keys), not its members' presence (coming
  online, going offline, joining) unless they share another channel, and not
  its end (removal, a cancelled invite, an invite that goes with its inviter,
  disbanding, the last member leaving);
- is never asked to rekey (nor is any member whose row isn't under their
  current keys);
- is never moved by a recovery: the user chose to drop it.

The other members still see the old key as a member, and may still see its
presence, which follows the log's members. The forgotten row goes when the log
removes the place (Remove or Cancel invite), and a fresh invite of the new key
then works as usual. When the last member whose place isn't forgotten leaves,
the channel is deleted, as when the last member leaves: nobody would ever see
it again. `ForgetChannel` is charged to the per-user read budget.

### An old key's place has no say

A member row whose keys aren't the account's current ones
(`MemberRow.CurrentKeys`) can't send, rekey, fetch epoch keys, rename or
disband, whatever its rank. None of that would be signed by the key the log
knows it by, and a disband would end the channel for everyone.

- The server's single authorization check (`RequireAllowed`) refuses every
  channel action through such a place except reading the log, which is how the
  plugin sees whose place it is. The refusal carries the message the plugin
  turns into plain words.
- `ForgetChannel` doesn't go through that check, so a stale place can always
  be removed.
- Log entries made through such a place fail anyway: the actor key isn't the
  log's.
- The place must also be under the keys the connection signed in with. A
  registration with new keys disconnects the old keys' sessions before it
  tells anyone. Should one outlive that, the place (which moved to the new
  keys, rank and all) is no more its own than an old key's place is.

### Attacks the log stops

| Attack | Result |
| --- | --- |
| The server inserts a ghost member | No valid invite and accept chain, so it isn't a member, and nobody seals to it |
| A former member signs invites for ghosts | Invalid: the inviter isn't a member at that point in the log |
| An ordinary member invites ghosts | Invalid: rank is part of the signed log |
| The server hides a removal | The remover's client, and everyone who saw the removal, reject keys made for the older position, and warn that the server may be hiding a change |
| The server shows different member lists to different clients | Needs a member to sign two different entries at the same position; a client that sees both reports a fork. Key recovered entries are the exception: the server can show different ones to different clients, which only comparing whole logs reveals |
| An old key is reused after registering again | The old key stopped being a member when it was removed, or when a key recovered entry moved the place to the new key |
| An old channel name is replayed | Names are bound to the log position and a revision counter |

## Channel keys and messages

### Epoch keys

- Each channel has a 256-bit key per epoch. Epoch 0 is created with the
  channel.
- Any join, leave, removal or member's key move makes a member's client
  generate the next epoch key. It seals one copy to each member at the log's
  head (to the X25519 keys the log has for them), and signs each copy together
  with that log position and a commitment to the key (one hash of the key,
  the same in every copy).
- The server accepts a rekey only for the next epoch number and the log's
  head, with one copy per current member, all carrying the same commitment.
  A member who hands someone a different or unreadable key is named in a
  warning by the client that got it, and that client rekeys.
- The server stores the sealed copies per member and epoch, so a member who
  was offline fetches theirs on login. It can't open them.
- Invitees receive the channel name sealed to them, but no key until they
  accept.

**Who rekeys.** The server asks a member who is online (`RekeyNeeded`), or the
first to come online. It never asks a forgotten place, a place under old keys,
or a member waiting for a key, unless nobody holds the key (see
[When nobody holds the key](#when-nobody-holds-the-key)). A remover's client
rekeys straight away. A client remembers the epoch the channel was at when a
rekey was asked for, and only a key for a later epoch settles it: one made
before the request (its own rekey whose answer arrives after the request, or
someone else's key arriving late) doesn't.

**What clients accept.** A client accepts a new epoch key only:

- from a member in its verified log;
- for a newer epoch than it holds;
- made for the current membership. A key made before the last join or leave
  is refused, with a warning that the server may be hiding a change. For a key
  made at a newer position, the client fetches and checks the log first.

Clients send with the newest key they hold, whatever epoch the server claims,
and rekey first if that key predates the last join or leave.

### Keys have a maximum age

Built at the owner's request (2026-10-09, before the public release). A channel
whose members don't change used to keep one epoch key for ever, so one key that
leaked (a crash dump, a debug log, someone reading the game's memory) read every
message sent from then on. Now a key is replaced once it is **7 days** old
(`ClientSession.EpochMaxAge`), which bounds a leaked key to about a week of new
messages.

**Who makes it.** As for any automatic rekey, a member who is online: while
connected, a client looks at its channels' keys once its login's catch-up is
over, and every 10 minutes after. (Not before: live messages held back during
the catch-up are taken under an older key only within 2 minutes of a newer one
arriving, so a key made at login could cut them off.) For a channel whose newest key is older than 7 days, it makes the next one if
it could make any automatic rekey: a member under the keys it has (not a place
under old keys, not a forgotten place), holding the channel's current key and
name (not waiting for a key), with no rekey for a membership change waiting
(that one goes as it always does). Not in a channel it is alone in: nobody else
could read what a leaked key opens, and anyone joining brings a new key anyway.
The server isn't asked to choose, so it needs no change.

**How old a key is.** By the time its maker signed into it
(`SealedEpochKey.created_unix_ms`, `epoch-key-created/v1`), never the server's
word: a server that changes the time breaks the signature, and the time isn't
believed. Capped by when this client got the key (it was made before then), so
a time ahead can't keep a key in use longer. A key that doesn't say (an older
plugin made it, or its time didn't check out) counts from when this client got
it (`KeyPosition.HeldSinceMs`, saved). Keys kept by an earlier version have no
such time: those from builds since 2026-10-07 saved their maker's signed time,
and count from it; older ones saved neither, and count from the update, so
updating doesn't make every channel's key look old at once.

**No rekey storm.** Each client waits a random time of up to 10 minutes before
trying, then checks again that the key is still old and it still may: members
online together pick different waits, so the first one makes the key and the
others hold it by the time their wait ends, and stop. Two that try at once are
settled as any rekeys at once: the server takes only the next epoch, so the
first wins, and the other is refused ("no longer at that epoch"), fetches the new
key and gives up. While the plugin runs, a client tries at most once per
channel an hour, whatever happens (a refusal, a lost connection); this isn't
saved, so reloading the plugin or restarting the game allows a try at once.
The server's rekey rate limit (5, then 1 every 2 seconds) still applies. If a
membership change arrives during the wait, the try makes the rekey it needs,
and a failure of that one is told as any failed rekey is.

**Nothing else changes.** It is an ordinary rekey: the new key is sealed to
every member at the log's head, signed with that position and the time it was
made, and carries the channel's name over. It is silent, like every automatic
rekey: no notice, nothing in game chat, the channel windows or the chat log on
this computer; the diagnostic log notes it at Debug, without names. Messages in
flight under the old key are taken for 2 minutes, as after any rekey. Members
who were away fetch the new key at login, as for any rekey; the stored
messages under the old key (up to 7 days of them) stay readable to those who
were sealed it, by the rules of [Message catch-up](#message-catch-up) (the
new key's signed time ends the old epoch, and nobody's first epoch changes).
On their own, age rekeys put at most two epochs within the 7 days of stored
messages, well inside the 64 the server keeps keys for.

**Decisions.**

- **A client constant, not a server setting.** The server sends limits in
  `Welcome`, but it isn't trusted with this: a client would have to refuse any
  value longer than 7 days, so a server setting could only shorten it. Shorter
  means more rekeys (each costs every member's client work) and more epochs
  within the 7 days of stored messages, for little gain. If a shorter age is
  wanted later, it can be added as an advertised value the client accepts
  between a day and 7 days.
- **Jitter in the client, not a server choice.** The server already picks a
  rekeyer for membership changes, but the age is the client's to judge (by the
  signed time), and the existing refusal of a second rekey for one epoch makes
  concurrent tries harmless.
- **Compatibility.** No protocol change and no new capability. A plugin from
  before never starts one; it takes the new key as it takes any rekey (a member
  could always "Force rekey"). A channel whose members online all run older
  plugins keeps its key as before.

**Limits.** A server can keep an old key in use by refusing every new key, or
not passing them on (it can withhold, not forge); a refusing server costs each
client one try per channel an hour while the plugin runs, silently. Nobody
makes a key while no member who could is online. A member whose clock is days slow sees keys as
younger, and leaves it to another; one whose clock is days fast has its keys
refused by the server for their time, as any rekey of theirs. Not testable in
game (it takes a week): automated tests (`EpochMaxAgeTests`) cover it with a
moved clock.

### Channel names

- The name is encrypted with the current epoch key, and re-encrypted on each
  rekey, which carries it into the new epoch.
- Each name carries a signed epoch, revision and log position.
- A client accepts a name only if it is encrypted under the key it uses,
  signed by a member, made for the current membership, and not older than the
  newest name it has accepted (remembered across restarts). So a server can't
  roll a name back to one from an older epoch, an earlier rename or an older
  membership.
- Only the admin renames. Clients warn if a member's rekey changed the name.

### Messages

- Messages are encrypted with XChaCha20-Poly1305 under the epoch key, with a
  random 192-bit nonce.
- The associated data binds the channel ID, epoch, sender, message ID and
  timestamp.
- The sender signs the ciphertext and associated data. The server can't read
  messages, alter them, or attribute them to someone else.
- A message is accepted only from a member in the verified log, signed with
  the key the log has for them. A message under an older epoch is accepted
  only within 2 minutes of the client getting the newer key.
- Inside the ciphertext, each message has a content kind (text today). A
  client shows a kind it doesn't know as an unsupported message.
- The plugin sanitises all remote text before it reaches the chat log.

#### Links in messages

An item, a map flag or a status linked in a message reaches the other members
as the game's own interactive link, where it was in the sentence: hovering an
item or a status shows its tooltip, clicking a map flag opens the map there,
in the game's chat log and in ChatTwo. The format and the rules are in the
core library (`MessageContent`, `ChatLinks`, `LinkText`), and unit tested;
the plugin's `GameLinks` reads and builds the game's side, and is checked in
game with
[docs/testing/chat-links-checklist.md](testing/chat-links-checklist.md).

- **Format.** A text message keeps its content kind and its text, which is
  the whole message as plain text with each link as its name in square
  brackets: "look [Potion]". Links are an added, repeated field of
  `TextContent` (`TextLink`): where the link's "[name]" is in the text
  (start and length, in UTF-16 code units) and what it points at, one of an
  item (the game's raw item id: an `Item` row, +500,000 for a collectable,
  +1,000,000 for high quality; from 2,000,000 an `EventItem` row), a map flag
  (`TerritoryType` and `Map` rows, and world coordinates times 1,000, as the
  game's map links carry them) or a status (a `Status` row). Ids and numbers
  only: no name, no game bytes.
- **Compatibility.** Older clients skip the unknown field and show the text,
  "look [Potion]", as they always showed links. Messages without links are
  exactly as before. Nothing about it reaches the server, which needed no
  change: the field is inside the encrypted, signed plaintext, under the same
  4 KiB ciphertext limit (five links with the longest names add well under
  1 KiB).
- **Checks on receipt.** A link is shown as one only if it passes every check;
  otherwise the sender's "[name]" shows as plain text, sanitised like all
  remote text ("[unknown link]" if nothing is left of it). A message with more
  than five links shows as text only (a LookingGlass client never sends more).
  Each link must stand over a bracketed name in the text, in order and not
  overlapping, and be of a known kind with ids and coordinates in range. Then,
  against the recipient's own game data: an item must be an `Item` row with a
  name (high quality only if it can be, a collectable only if it is one) or an
  `EventItem` row; a map flag's territory and map must exist, and the map be
  the territory's default map (`TerritoryType.Map`) or belong to the territory
  (`Map.TerritoryType`): nearly half the territories (duties, trials, raids,
  instanced copies of open-world zones, the Diadem, seasonal zones) use
  another territory's map, so either is enough; the position must be on that
  map (inside its square at its size factor and offset, give or take a
  little), and the territory have a place name; a status must be a `Status`
  row with a name.
- **Rebuilt, never copied.** A link that passes is built afresh from its ids
  with Dalamud's own link builders (`SeString.CreateItemLink`,
  `CreateMapLink`; a status as `StatusPayload`, the link arrow, its name and
  the link terminator), so it looks as the game's links do (an item in its
  rarity's colour, with the high-quality mark). The name shown is the
  recipient's own game's, in their language, never the sender's text. No byte
  from the network reaches the chat log except as sanitised text, and a
  message with links is held to the same 1,000-character limit as one
  without, across all its pieces (a link counting as its "[name]").
- **Sending.** A link goes as a link only if the sender's own game data shows
  it (the same checks); up to five per message, any more go as their names.
  How a typed line's links are found is under
  [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc)
  (*Links*); `/lgcN` and `/lgc <nickname>` find them the same way.

#### Text commands in messages

In game chat, the game replaces its text commands before a line is sent:
`<t>` becomes the target's name, `<me>` the player's own. The owner's in-game
test showed LookingGlass sending them as typed: the game replaces them only
while it runs the line, and a channel message is taken from the line before
that (from the gate's copy, or kept from the game while sticky). So the
sender's plugin replaces them itself, just before the message is encrypted,
in `/lgcN`, `/lgc <nickname>` and sticky lines alike (`ChannelSender.Send`, on
the game thread, in the same frame the line was typed). The rules are in the
core library (`TextCommands`) and unit tested; the plugin's
`GameTextCommands` asks the game.

- **Which.** `<t>` (target), `<tt>` (target's target), `<f>` (focus target),
  `<me>` (the player), `<mo>` (mouseover), `<lt>` (last target), `<1>` to
  `<8>` (party members), `<r>` (last tell partner) and `<pos>` (the player's
  position). Anything else between angle brackets (`<se.1>`, `<hp>`,
  `<wait.3>`) is text, sent as typed. The link placeholders (`<item>`,
  `<flag>`, `<status>`) are links, as above.
- **How: the game's own expander.** Each text command in the line, on its
  own, goes through `PronounModule.ProcessString` (FFXIVClientStructs; the
  function the game runs on a chat line, and the one ChatTwo uses for its
  tells and its echo): first encoding it, which turns it into the game's fixed
  macro (a player and their world, a map position), then decoding that into
  what the chat log would show. What is shown is read as plain text: a player
  as their name, and one from another world with the game's cross-world mark
  and their world, as the game shows them; `<pos>` as the place and
  coordinates, without the map link's arrow. The result is the module's own
  buffer, copied at once. Each is asked once per line, as typed, so the game's
  own rules on upper and lower case apply.
- **Not replaced: as typed.** A text command the game hands back unchanged or
  empty (nothing targeted, no such party member, no tell yet), or that can't be
  read, stays as typed. Whether the game itself sends `<t>` with no target as
  typed or as nothing is to be checked in game (the checklist has it).
- **Plain text, never a link.** A name goes as text, cleaned like a link's
  name: no game formatting, no line breaks, nothing that reads as a link's
  marker, and `<` and `>` become round brackets, so what a text command stood
  for is never read as a placeholder or a text command again. The game's own
  icons (private-use characters, the cross-world mark) stay.
- **Only on the sender's side.** A received message is never looked at for
  them: `<t>` from an older client (or one typed where nothing was targeted)
  shows as `<t>`. Only `ChannelSender.Send` calls `TextCommands.Resolve`.
- **The diagnostic log** counts the text commands replaced in a sticky line's
  "sending" entry, and never says what they stood for.

[docs/testing/placeholders-gagspeak-checklist.md](testing/placeholders-gagspeak-checklist.md)
has the checks to make in game.

### Replay protection

- Clients remember the IDs of recent verified messages (in memory) and drop
  duplicates.
- They drop messages dated more than 10 minutes from their own clock.
- They save, per channel and sender, the timestamp (and message ID) of the
  newest message accepted, and drop messages more than 2 minutes older than
  that, even after a restart.
- Those timestamps (with the IDs of the newest messages, and each channel's
  catch-up position) are saved within 30 seconds of changing, with other
  changes, on shutdown, and after each channel's catch-up. A crash can lose up
  to about 30 seconds of them.
- A live message that is one of the newest already accepted from its sender
  (same time and ID) is dropped too, even after a restart.
- Your own messages aren't recorded this way. After a restart, the server
  could replay one you sent in the last 10 minutes back to you.
- Messages caught up from while you were away are older than the first two
  rules allow, so they have a rule of their own (see
  [Message catch-up](#message-catch-up)): each must be newer than everything
  already accepted from its sender in the channel. A caught-up message replayed
  later, live or caught up again, fails that rule or the seen-set like any other.

### Message catch-up

Built at the owner's request (2026-10-07; with the
[chat log on this computer](#chat-log-on-this-computer), the answer to lost
messages): messages sent while a member was disconnected
reach them when they come back. Testers lost messages to untimely disconnects
with ExtraChat; this fixes that. The server keeps recent messages for **7
days**, and at most **5,000 per channel** (the oldest go first), both operator
settings (`Messages:KeepDays`, `Messages:MaxPerChannel`, see
[server.md](server.md#stored-messages)). Capability `history.v1`.

**What the server keeps.** Each channel message exactly as it relays it: the
ciphertext and the envelope it sees anyway (channel, epoch, sender, message ID,
the sender's signed time, the signature), plus the time it relayed it and a
number per channel (`server_id`, schema 9). Nothing it can read. It numbers and
fans out a channel's messages under one lock (one of 64, by channel), so every
member is sent them in number order, and stores one only while the channel is
still at its epoch with no rekey pending (checked in the same transaction). It
keeps nothing someone says alone in a channel: nobody else could ever fetch it
(a member joining later gets nothing from before they joined). The cap is kept
as each is stored; the rest (age, a lowered cap) is swept at startup
and every ten minutes. Disbanding a channel, or its last member leaving, deletes
its messages with it. The sealed keys of epochs that still have stored messages
are kept too (normally only the newest 4 epochs' are), up to 64 epochs back;
messages under older epochs go with their keys.

**Who may fetch what.** `FetchMessages` goes through the single authorization
check like every channel request: a member (rank Member and above), through a
place under the account's current keys and the keys the connection signed in
with. So an invitee, a place removed from the user's list (forgotten), a place
under keys the account no longer has, and anyone removed or gone get nothing.
Then each member row has a **first epoch**:

- the channel's creator: epoch 0;
- a member who joins (an accept): the epoch after the one the channel is at;
- a member whose place moves to new keys (a key recovered entry): likewise, the
  next epoch.

A member gets only messages under their first epoch or later, never their own.

Why this is exactly "sent while they were a member, under keys they could
hold": the server stores a message under epoch E only while the channel is at E
with no rekey pending; a join, a leave, a removal or a move sets a rekey
pending, and the next epoch's key is sealed to exactly the members at the log's
head (the server refuses a rekey that isn't). So a member row that exists, with
first epoch F, was at the head when every epoch from F on was made, and was
sealed each of those keys; and no epoch before F was sealed to its keys. A row
goes when the member leaves or is removed (a rejoin is a new row, with a new
first epoch), and a move to new keys moves the first epoch on, so a member
waiting for a key across a recovery gets nothing until the rekey that gives
their new keys one, and nothing from before. Rows from before schema 9 start at
the channel's current epoch (the next one if a rekey is pending): nothing older
is stored. Clients never rely on any of this: they check each message
themselves (below), and couldn't open one under a key they weren't given.

**Asking.** At every login (a reconnect too), the client asks per channel for
the messages after the last `server_id` it has had from it, live or caught up
(saved per character and server address with the message times). With none (the
first login with this version, a new computer, a channel just joined) it asks
for what was stored in the last hour: those it already received live are
recognised and left out, and a new computer's new keys can't read older ones
anyway. Pages hold at most 200 messages and 96 KiB of ciphertext (well within
the 128 KiB frame); the client asks again while there are more, waiting and
retrying if told to slow down. Each page is charged to a per-user budget of its
own (100 at once, then 4 a second). `StoredMessages.latest_id` lets a client
skip what it may not read (its own, from before it joined) once nothing more
is to come; and if it is lower than the client's position (the server's
database was restored from a backup), the client carries on from it. Only what
a page says comes after the position, in order and of that channel, is looked
at.

**Order, and live messages meanwhile.** The server relays to a connection as
soon as it is logged in, so from before the login the client holds back live
messages. Each channel's caught-up messages are taken first, page by page, then
that channel's held live messages. All of it runs in the session's one inbox,
in turn with the server's events, so nothing races. A message both relayed live
and in a page is taken once (by its signed message ID). One the server leaves
out of the pages but relays live is taken, after them. Nothing is reordered
within a channel: the server relays in number order, and a sender's messages in
the order sent.

**When a catch-up fails.** A channel whose catch-up fails (a timeout, an error,
a key that couldn't be fetched) is tried again twice, from where it got to,
waiting a little longer each time, while its live messages stay held. If the
connection closed, its held live messages are let go: the server has them, and
the next login fetches them after what was missed, in order. If it still fails
on a working connection, its live messages are taken, and the channel is
marked as having a gap: its position doesn't move on with live messages, the
times (and IDs) already had from each sender are kept as they were, and the
messages accepted since are remembered. So the next catch-up fetches from
before the gap, judges the missed messages against what was had before it (a
sender who has spoken live since doesn't make them look old), and leaves out
what was already shown. A catch-up that completes ends the gap. More live
messages than the client holds back (2,000) are handled the same way: taken as
they come, with the channel marked.

**Checks.** A caught-up message is checked as a live one, with these changes:

- **Who could send it.** The sender must have held the key of its epoch: it is
  checked against the keys the log had for them where that key was made (a
  member's moves are followed back, as for names). Someone who has left (or was
  removed) since counts if that key was made before they left: the client
  remembers, for 8 days, who left each channel and with which keys.
- **Not after its key was replaced.** Live, a message under an older key is
  taken only within 2 minutes of the newer one arriving. Caught up, a message
  under epoch E must be dated no later than:
  - 10 minutes after E+1's key was made, which every new key states, signed
    by whoever made it (`SealedEpochKey.created_unix_ms`, a signature of its
    own so older clients still take the key): a member at that point of the
    log, under the keys they had then, so not the server, not someone who left,
    and not a place's replaced keys. 10 minutes, as much as a live message may
    be from the clock, so a member whose clock is a few minutes slow doesn't
    make others lose what was sent just before. The server refuses a new key
    whose stated time is more than 10 minutes from its own clock, saying the
    maker's clock is off; keys from older clients state none;
  - 2 minutes after the first membership change (a join, a leave, a removal, a member's place
    moving to new keys) after where E's key was made, as the client dates it:
    as the entry says, but never later than when the client verified it, and
    no later than any later entry signed by someone other than the change's
    subject. A key recovered entry's time is the server's (nothing signs it),
    and a leave's or a join's is signed by its own subject, so on their own
    they only count up to when the client saw them.
  - And if the sender's own keys stopped after E's key was made (they left,
    were removed, or their place moved to new keys: a stolen computer's keys
    after its owner re-verified), a time by which that had happened that
    neither they nor the server chose: when E+1's key was made, if someone else
    made it before this client came back; an entry someone else signed after
    it; or when this client saw the change happen, connected (with the same
    10 or 2 minutes). Without one, nothing of those keys is caught up: how long
    they spoke can't be told. That isn't a failed check, so it isn't a warning:
    one blue line per channel and login says it plainly ("Some messages Carol
    sent before leaving couldn't be confirmed, so they weren't restored."), and
    the diagnostic log notes it without who or what. It happens when nobody
    else was there to make the next key or sign an entry (a two-member channel
    the other left), and when the next key was made by an older plugin, which
    states no time: members should update together.

  A membership change dated more than 10 minutes ahead of the clock when it is
  verified is misdated (by its signer, or the server for a re-verification):
  the user is told, once per channel and session (members who see it happen
  see the lie), and it counts as dated when it was seen.

  The client remembers each channel's membership changes for 8 days; a message
  under a key from before the changes it knows of (or before its channel's
  last membership change when it started recording them) is dropped, unless
  E+1's key says when it was made.
- **When.** The live rules (within 10 minutes of the clock, and not more than 2
  minutes older than the newest from the sender) would refuse an older message.
  Instead, a caught-up message must be newer than the newest message already
  accepted from its sender in the channel (by its signed time; at the same
  time, a different message from those accepted then: the IDs of the newest
  few are kept), saved across restarts, and not dated further in the future
  than a live message may be (10 minutes). (One dated ahead, which only its
  sender can sign, can make their own next live messages look like replays
  for a while, as live.)

So the server can't pass an old message off as new: a caught-up one is shown as
such, with the time it was sent, and is accepted at most once, whether the
server sends it again in a page, out of order, or live, before or after a
restart. One it shows twice or out of order is dropped quietly (nothing was
forged or hidden). One under a key the member never held (from before they
joined), from someone who couldn't have sent it, not really the sender's, dated
after its key was replaced, or dated in the future is dropped, and each
channel's drops are told in one notice
("3 messages from while you were away … aren't shown").

**Keys missed while away.** A channel rekeyed while a member was away has
epochs whose keys they never fetched (a client fetches only the newest two on
login). For a caught-up message under one, the client fetches its keys from that
epoch on, and keeps those older than its newest key in memory, for reading only:
each sealed to it and signed by a member (or someone who left since, for a key
from before they left) for a position of the log it verified. It never sends
with one, and the key it sends with is unchanged. A message's authenticity comes
from its sender's signature, not from the key.

**Showing them.** The session raises each channel's caught-up messages once
(`MessagesCaughtUp`), not as live messages. The plugin puts every one in the
channel's window history, after a dimmed line ("12 messages were sent while you
were away:"), each with the time it was sent (with the day, if not today), and
counts them as unread. In game chat, unless the channel is turned off there, one
blue line with the channel's tag says how many, then the last 50 follow, each
with its time after the tag (`[sky] [14:05] <Bob@Lich> hi`); if there were more,
the line says the earlier ones are in the channel's window. The rules are in the
core library (`CatchUpChat`, `ChannelHistory.AddCaughtUp`) and unit tested.

**Compatibility.** Additive, no new protocol version: a new capability string,
a request and response, `ChatMessage.server_id`, and two limits in `Welcome`.
A plugin from before (0.2.5) doesn't offer the capability and never asks: it
works as before, and skips `server_id`. A plugin with catch-up asks only a
server that agreed, so against an older server (or one keeping nothing) it works
as before too.

**The trade-off.** See [Known limitations](#known-limitations): a stolen
identity key now reads up to a week of a channel's stored messages, not only
new ones, and the server keeps the metadata it saw (who sent when, in which
channel) for as long as it keeps the message.

### Online status

Members who share a channel see when each other are online. The server tells
them when a fellow member connects (their first connection) or disconnects
(their last), and when someone online joins. Invitees and people you share no
channel with aren't told, and you aren't shown to them. This is the server's
word, and it could lie.

The server decides each change, and fills in the online flags of a channel
list, under one lock, so a client never sees someone online (or offline)
twice in a row. Whom to tell is looked up before taking it, so a slow lookup
holds up nobody else's login; a change that a join may have overtaken looks
again.

### Blocking and invites

- A user can block others. Their invites are declined unseen, and their
  messages hidden.
- An invite is shown as verified only once the client has checked it against
  the channel's log.
- An invite from someone whose identity key changed can't be accepted until
  the user marks them verified. One not signed by the inviter's current key
  can't be accepted at all.

## Security model

### What the encryption protects

- The server can't read channel names or messages, including the messages it
  keeps for members who were away.
- The server can't forge, alter or re-attribute messages, or pass an old one
  off as new (a caught-up message is shown as such, with its time, once).
- The server can't add members, change ranks or reorder the membership log.
- Removed members and declined invitees get no later keys, so they can't read
  anything sent afterwards.
- The server can't roll back channel names or replay old keys without the
  client noticing.
- A malicious server can't pass on registrations, key logins, retirements or
  registration codes from another server, as long as both list only addresses
  that are theirs alone.

### Known limitations

- **You trust the keys of the people you invite on first use.** When you
  invite someone by name, the server supplies their key and could substitute
  its own. Each member shows "not compared" until you compare fingerprints
  over /tell and mark them verified. There is no strict mode yet that refuses
  to invite, or seal keys to, anyone not compared (planned after the public
  release; see
  [Strict mode for keys not compared](#strict-mode-for-keys-not-compared)).
- **The server vouches for re-verified keys.** See
  [The trust this needs](#the-trust-this-needs).
- **A removal takes effect when the remover's client publishes it.** The
  remover's client rekeys straight away. A server that suppresses that rekey
  stops the channel working for everyone else, and the remover is warned. But
  members who never saw the removal can be shown the old membership, and a key
  they make for it reaches the removed member. Anyone who did see the removal
  refuses that key.
- **Key commitments rely on the server checking them.** A member colluding
  with the server can still give different members different keys. That shows
  up as messages some members can't decrypt.
- **Rekeys seal to every member in the log**, including one whose "key
  changed" warning you haven't cleared.
- **A server can keep an old key in use.** Keys are replaced once they are a
  week old, but a server that refuses (or doesn't pass on) every new key keeps
  the old one in use, and with it whatever a leaked copy reads. It can't make
  a key look old to have it replaced over and over: the age is signed by the
  key's maker (see [Keys have a maximum age](#keys-have-a-maximum-age)).
- **Old places linger.** A place under a key its owner no longer has stays
  with that key until its owner registers new keys again (which moves it) or
  a moderator removes it. Its owner can remove it from their own list
  meanwhile.
- **The log only grows.** Clients fetch just the new entries, but someone new
  to a channel (or invited to it) replays it from the start.
- **Metadata and availability.** The server sees who is in which channel, when
  messages are sent and who is online, and can drop or delay anything.
- **Stored messages widen what a stolen key reads** (message catch-up, owner's
  decision 2026-10-07). The server keeps each channel's messages, encrypted, for
  7 days (an operator setting). Someone who steals a member's identity keys can
  sign in as them and fetch, and read, up to a week of that channel's stored
  messages from the epochs that member held, not only what is sent from then
  on. Before, nothing was kept to fetch. The server still can't read them, and a
  member who leaves or is removed fetches nothing more. "Reset my identity"
  stops the stolen keys signing in, and their place moves to the new keys.
- **No forward secrecy or post-compromise security.** Epoch keys are sealed
  to each member's long-term X25519 key, and the server keeps them, sealed,
  while it keeps messages under them. Someone who copies a member's identity
  keys can open every epoch key sealed to that member, old ones the server
  still holds and new ones as they are made, until the member resets their
  identity; nothing locks them out on its own. 1.0 promises neither property
  (owner, 2026-10-09; see [MLS](#mls)).
- **Metadata is kept, not only seen.** With each stored message the server
  keeps what it saw when relaying it: who sent it, when, in which channel, under
  which epoch, and its size, for as long as it keeps the message (7 days by
  default). So do its backups and Litestream replicas. A server operator could
  always have logged this as it went by; now it is in the database for a week.
  Setting `Messages:KeepDays` to 0 turns catch-up (and this) off.
- **A sender and a malicious server together can backdate.** A member (or
  someone who has left since) can sign a message now with an earlier time,
  under a key they held then, and a server can show it to the others as one
  missed while they were away, if it is newer than anything they already have
  from that sender. It must be dated no later than 10 minutes after the next
  key was made (as its maker signed) and 2 minutes after the next membership
  change; and
  for keys that stopped being theirs (they left, or their place moved), after
  a time neither they nor the server chose (see
  [Message catch-up](#message-catch-up)), or nothing of them is caught up.
  What is left: a server can hold back the next key (by not asking anyone to
  make it) and the evidence of a change, so the window lasts until a member
  makes the key, someone else signs an entry, or the reader sees the change
  happen; until then a sender can post under a key they still hold as a
  member. A test pins this window
  (`KnownLimitTheWindowLastsUntilSomeoneElseMakesTheNextKey`): a
  re-verification the server dates ahead, with the next key made only when
  another member comes back later, lets the old keys post up to then. Within the 10 minutes' grace
  after the next key, anyone who held a key can post under it, dated before.
  Keys made by older clients don't say when they were made, and then only the
  change times count (and the fail-closed rule drops what can't be dated). (An honest server
  stores only what members send, as they send it.)
- **Catch-up has limits.** A client with no position in a channel (the first
  login with this version) catches up only the last hour. A channel rekeyed
  more than 64 times within the 7 days keeps only its newest 64 epochs'
  messages. Up to about 30 seconds of messages accepted just before a crash can
  show again as missed after it (the message times weren't saved yet). If more
  than 5,000 live messages arrive in a channel while its catch-up keeps failing
  (it has a gap), the oldest of them could show again once it catches up. A sender whose clock is
  more than 2 minutes fast can lose messages they sent under a key just before
  it was replaced. Messages from someone whose keys stopped while you were
  away, before anyone else made a new key or signed an entry, aren't caught up. Right
  after updating to this version, messages under a key from before the
  channel's last membership change before the update can't be dated, so they
  aren't caught up.
- **Shared addresses share limits.** Per-address limits (registrations, key
  login, connections) can't tell apart the people behind one address (a
  shared NAT, a mobile carrier's CGNAT, one IPv6 /64). Someone there can use
  them up for everyone else, an hour at a time. Logins with a device token,
  the usual way in, aren't limited like this.
- **Connections can be crowded out, not shut out.** Someone with many
  addresses (many IPv6 /56s, many IPv4 addresses) can open connections that
  never log in, 4 per address, each for 3 minutes. At the server's cap they
  are the ones closed to make room, so plugins that log in still get in, but
  someone registering (whose connection must stay open while they edit their
  Lodestone profile) can have theirs closed and must start again. Connections
  registering go last, and then first from the address that would hold the
  most of them (counting the new connection), so someone registering from
  several connections an address, or connecting again from one, pushes out
  their own first. Only once every such address holds one does the oldest go:
  that takes about as many addresses as the server's cap, 10,000, registering
  at once (and registering is limited to 10 an hour per address). Only 10,000
  logged-in connections fill the server for good.
- **Replays of your own messages** within 10 minutes of a restart, and up to 30
  seconds of replay timestamps lost in a crash (see
  [Replay protection](#replay-protection)).
- **Debug accounts** on a Development server can be taken over by anyone who
  can reach it, channels and all.
- **The local key file** used where DPAPI is unavailable guards only against
  accidental sharing.
- **Local chat trusts keys on first use and shows metadata.** A friend's key
  comes from the server the first time (a lookup, or with their first local
  message shown), as for invites; a local message never changes it. The server
  sees who sent local messages to whom and when, which says they were
  together, and from the lookups which friends were near the sender, even ones
  who don't use LookingGlass.
  See [Local chat (friends only)](#local-chat-friends-only).

## Protocol and extensibility

### Transport and requests

- **Transport.** A WebSocket (TLS in production), one connection per
  logged-in character, carrying Protocol Buffers frames defined in one schema
  file.
- **Handshake.** The client sends `Hello` (protocol versions, client version,
  capabilities). The server answers `Welcome` (the chosen version, agreed
  capabilities, limits, an announcement, whether debug accounts are on, and
  its `public_urls`). The client then logs in with its device token, a key
  login or a registration.
- **Requests and events.** Every request has an ID and a timeout, and ends in
  a result or a typed error. Errors never close the connection; only protocol
  violations, failed authentication and a block by the operator (see
  [Spotting abuse, and banning](#spotting-abuse-and-banning)) do. Server
  events carry a per-connection sequence number.

### Versions

The current protocol version is 3. Plugin and server are always updated
together, and an older plugin is told to update at `Hello`.

| Version | What it added |
| --- | --- |
| 1 | 0.1: registration, channels, invites, epoch keys, messages |
| 2 | The signed membership log: a membership entry message; channel info carries the log (or the part after the client's position); a log-fetch request; invite, accept, decline, remove, leave and rank requests carry their signed entry; epoch keys and names carry the log position |
| 3 | Key recovered entries, and `no_key_holder` on `RekeyNeeded` and `ChannelInfo`. A version 2 plugin would refuse the new entry kind, and with it every later entry of the channel's log |

Some additions needed no new version:

- `ForgetChannel` ("Remove from my list"). An older server answers "Unknown
  request.".
- Message catch-up: the capability `history.v1`, `FetchMessages` and
  `StoredMessages`, `ChatMessage.server_id`, and `Limits.message_keep_days` and
  `max_stored_messages_per_channel`. Each new epoch key states when it was made
  (`SealedEpochKey.created_unix_ms` and `created_signature`, a signature of its
  own, so older clients still verify the key as before). Agreed in `Hello`/`Welcome` (see
  [Message catch-up](#message-catch-up)), so neither side uses it with one that
  doesn't know it.
- Signed registrations, the registration client nonce and signed URLs. Older
  plugins are refused with a request to update.
- Local chat: the capability `local.v1`, `SendLocalMessage` and the
  `LocalMessage` event, and `Limits.max_local_recipients` (see
  [Local chat (friends only)](#local-chat-friends-only)). A server agrees to it
  only with a client that offers it, and sends `LocalMessage` only to such a
  connection, so an older plugin never sees one; an older server never agrees.
- Blocks: `ERROR_CODE_BLOCKED`, and `Error.block` (the operator's reason, when
  it ends, and whether it is on the address). An older plugin shows the
  server's message, which says it all, as a failed connection.

### Adding features

New features fit into one of three layers. Features the server must take part
in are negotiated capabilities. Features that live only inside encrypted
messages need no server change at all.

| Layer | Examples | Server change? | How it is added |
| --- | --- | --- | --- |
| Server capability | Message history, channel bans, file attachments, local chat | Yes | A new capability string and message types; old clients never see them |
| Encrypted content kind | Emotes, replies, reactions, polls, typing state | No | A new content kind inside the ciphertext; older clients show "unsupported message" |
| Encrypted field of a kind | Links in text messages | No | A new field of an existing kind, with a fallback in the old fields; older clients skip it and show the fallback (the text) |
| Client-only feature | Chat filters, notifications, colours, sounds | No | A plugin update only |

Within a major version, changes are additive only. Removing a field, or
changing its meaning, needs a new major version.

## Client design

The client is a thin Dalamud shell around the core library, which has no
Dalamud dependency.

- **Game thread.** Hooks, chat output, commands, ImGui, and capturing the
  player snapshot each frame. Nothing else reads game objects.
- **Session.** Owns the socket and all session state, and publishes an
  immutable snapshot after every change. The UI reads only snapshots.
  Anything that must touch the game is queued with
  `Framework.RunOnFrameworkThread`.
- **Sending.** `/lgc1` to `/lgc50` and `/lgc <nickname>`, or, after `/lgc3`
  or `/lgc sky` with no message, plain text typed in the chat box (see
  [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc)), or
  a channel window's own input box (see [Channel windows](#channel-windows)).
  Sending fails closed: an error never falls through to ordinary game chat.
  `/lgl <message>` talks to the friends near the player, and `/lgl` alone
  talks in local chat as `/lgc3` alone does in a channel (see
  [Local chat (friends only)](#local-chat-friends-only)).
- **Game interop.** Signatures live in one module (`ChatInterop`), and come
  from FFXIVClientStructs. A missing one disables only its feature.
- **ChatTwo.** ChatTwo's input sends through the same game function the chat
  box does, which sticky mode hooks, and LookingGlass names its channel in
  ChatTwo's input through `ExtraChat.OverrideChannelColour` (see below).
  Right-clicking a name in ChatTwo offers **Invite to LookingGlass**, through
  ChatTwo's context menu IPC (`ChatTwo.Register`, `ChatTwo.Invoke`,
  `ChatTwo.Unregister`, `ChatTwo.Available`; see
  [Context menu invites](#context-menu-invites)). ExtraChat also exposed
  `ExtraChat.ChannelNames` and `ExtraChat.ChannelCommandColours`;
  LookingGlass has no equivalent of those yet, and their names are an open
  question.
- **Context menus.** "Invite to LookingGlass" in the game's own right-click
  menus on a player, through Dalamud's `IContextMenu` (see
  [Context menu invites](#context-menu-invites)).

### Channel numbers, nicknames and colours

These are plugin settings, kept per character, and never sent to the server.

- **Numbers.** Each channel gets a number from 1 to 50 automatically, and
  keeps it across restarts until the user leaves it (or it is disbanded, or
  they are removed). The freed number then goes to the next channel without
  one. Choosing a number another channel has swaps the two. Typing a number
  with no channel on it says so.
- **Connecting by itself.** With "Connect automatically" on (the default), a
  session starts when a character logs in, and also whenever a logged-in
  player has none for any other reason: the owner found a plugin update left
  them on "Not connected" (2026-10-09). Each frame the plugin checks
  (`AutoConnect`): logged in, setting on, no session and none on its way, the
  player didn't press Disconnect (until they connect, log in again or change
  the server address), and the last start it made by itself was at least 30
  seconds ago. A start that fails at once (the keys can't be read) is said
  once and not retried until the player acts. The log records each start and
  stop and why, never a name.
- **Commands.** Of the channel commands only `/lgc` is listed in Dalamud's
  command help (`/lgl` is too); the fifty numbered commands are hidden to keep
  the list short. `/lgc` on its own explains how to use it.
- **Nicknames.** 1 to 16 letters, digits, `-` or `_`. A nickname can't be only
  digits, so `/lgc 3` is never confused with `/lgc3`. It must differ from the
  user's other nicknames, ignoring case. It can't be `Local` (in any case):
  its tag would be local chat's `[Local]`, so one saved before that rule is
  tagged by its number instead. A channel's nickname goes away when the user
  leaves it.
- **Chat tags.** A channel with a nickname is tagged with it, as in `[sky]`,
  unless **Use nicknames in tags** is off; otherwise with its number, as
  in `[LGC3]`. A channel with neither (more than fifty channels, or a message
  that arrives before the channel list is in) is tagged `[LGC]`.
- **Colours.** One of 40 of the game's own chat colours, or a custom colour,
  any RGB value (see [Custom colours](#custom-colours)). The channel's lines
  take the colour, or only the tag if **Colour the whole line** is off. The
  bar beside the channel in the list takes it too. **Default** colours only
  the tag.
- **Chat channel.** Messages appear in one of the game's chat channels, chosen
  in Settings (**Show messages in**), so chat tabs can show or hide them.
- **LookingGlass's own lines.** Everything LookingGlass itself says in the
  chat log starts with "[LookingGlass]" in LookingGlass blue, and is in one of
  three colours (rows of the game's UIColor sheet, chosen in one place,
  `NoticeColours`, and tested). A notice about one of the player's channels
  also names it: its tag goes in front, in the channel's colour ("[sky]
  Dropped a message from Bob…"), unless the words hold it already ("Now
  talking in [sky]."); since 0.2.12, after a tester couldn't tell which
  channel a warning in game chat was about (`ColouredText.WithChannelTag`):

  | Tone | Colour | What |
  |------|--------|------|
  | Information | LookingGlass blue (UIColor 37, 0x0099FF, the default `[LGC]` tag's) | Status and replies: "Now talking in", "Stopped talking in", every "Not sent", "A link in it couldn't be read", "Not connected", refusals to start, `/lgc` usage, "No channel has the nickname", the ChatTwo note, a right-click invite's "Invited" and "Couldn't invite", server announcements, "Joined", other notices at Info level |
  | Warning | light red (UIColor 508, 0xFF8080) | Every notice at Warning or Error level that isn't critical: a key changed, a name now another account, ExtraChat is on, the server not showing a membership (`MembershipHidden`), a stale key offered, a bad channel key, dropped messages, couldn't load keys, "something went wrong" (a line kept) |
  | Critical | dark red (UIColor 534, 0xAE0000) | By kind, whatever the level: a forked membership (`MembershipForked`), members shown different memberships (`MembersShownDifferently`), a removal not in effect, so a removed member may still read (`RemovalNotInEffect`), the server refusing a key and hiding a change (`ServerRefusesKey`), a relayed registration code (`RelayedRegistrationCode`) |

  A line about a channel ("Now talking in [sky].") shows the tag in the
  channel's own colour within the blue.
- **Unread counts.** The channel list counts messages from others since the
  user last looked at a channel in the main window (or in a channel window
  that has the focus) or talked in it. The window's title shows the total. The
  counts start from zero at each login.
- **Member icons.** Besides the fingerprint state in advanced mode (see
  [Identity keys and fingerprints](#identity-keys-and-fingerprints)), the
  icon's colour shows presence: green while connected, grey when not. A
  warning keeps its orange either way, and invitees stay grey until they join.
- **Confirmations.** Removing a member needs **Ctrl** held. Leaving or
  disbanding asks first. Cancelling an invite happens straight away.

### Custom colours

A channel's colour is either a row of the game's UIColor sheet (one of the 40
swatches, as every colour was before) or a custom colour, any RGB value. The
owner's decision (2026-10-07): try arbitrary colours; if they don't show
correctly in the game, offer the game's whole UIColor table instead. So custom
colours are an addition, the swatches stay, and both can be checked in game
([docs/testing/custom-colours-checklist.md](testing/custom-colours-checklist.md)).

- **Kept.** Per character, never sent to the server, like the rest. Rows stay
  in `ChannelColours` (channel ID → row), unchanged, so settings saved before
  read the same; custom colours are a second map, `CustomChannelColours`
  (channel ID → 0xRRGGBB). A channel is in at most one (choosing either clears
  the other); a hand-edited file with both shows the custom colour. Both are
  dropped when the channel is gone. An older version of the plugin ignores the
  second map: those channels show the default colour there. The rules are the
  core library's `ChannelColour` and `ChannelColours`, unit tested.
- **Picking one.** The colour menu keeps its swatches and **Default**, and adds
  **Custom...**: a colour wheel (ImGui's hue wheel) and a colour code field
  (`#RRGGBB`; the `#` may be left out, either case), kept in step both ways. A
  code that isn't `#` and six hex digits is refused with a plain line, and
  **Use this colour** is greyed out until it is fixed. Under them: a preview
  of a chat line on a dark background like the chat's (the tag, and the whole
  line if that setting is on), the closest game colour (see below) as a
  swatch, and, for a colour too dark to read (WCAG contrast below 2:1 against a
  typical chat background, `#1E1E1E`: navy, maroon, dark grey, pure blue), the
  plain line "This colour is very dark, so it may be hard to read in chat. You
  can still use it." A channel with a custom colour shows it as an outlined
  swatch beside **Custom...**. The picker's words are the same in both modes
  and checked for jargon.
- **The closest game colour.** For each custom colour, the UIColor row that
  looks closest: the smallest distance in CIELAB (ΔE*76, sRGB with a D65
  white), not in raw RGB, which misjudges by eye (pure green is nearer a
  darker green in RGB but looks nearer a lighter one). Only fully opaque rows
  count, row 0 never, and ties go to the lowest row. It is read from the whole
  sheet (its Dark column, the one ImGui and ChatTwo use), not only the swatches.
- **In the game's text: layered.** Chat lines (the tag, and the whole line if
  that's on), LookingGlass's lines that name the channel's tag, the server
  info bar and the game's right-click menu are SeStrings. A row is the
  UIForeground macro, as always. A custom colour is two macros, nested:
  `UIForeground(closest row) → Color(exact) → text → Color off → UIForeground
  off`. Whatever shows the Color macro shows the exact colour, the innermost;
  whatever ignores it still shows the closest row instead of no colour. The
  order of every coloured line is decided in `ColouredText` (core, tested:
  every push popped, in the opposite order); the plugin's `GameText` writes
  it. Dalamud's SeString has no payload for the Color macro, so it is written
  with Lumina's `SeStringBuilder.PushColorRgba` and `PopColor` and read back
  with `SeString.Parse`, which keeps it byte for byte as a `RawPayload`: for
  0x123456, `02 13 06 FE FF 12 34 56 03`; the pop, `02 13 02 EC 03`
  (`stackcolor`).
- **ChatTwo** (from its public source, 2026-09-27): it reads each chat line's
  payloads into one stack of colours. UIForeground pushes the row's Dark
  colour and its "off" pops; a `RawPayload` that is a Color macro (0x13) with
  a literal colour pushes that exact colour, and `stackcolor` pops. So ChatTwo
  shows the exact colour (the inner push is on top), and the text after the
  tag goes back to the chat channel's colour once both are popped. ChatTwo's
  input label (`ExtraChat.OverrideChannelColour`) takes 0xRRGGBBAA directly,
  so it gets the exact colour too.
- **Everywhere else: exact.** ImGui takes any colour, so the channel list's
  bar, the dot before the channel's name, channel windows' tabs, their "add a
  channel" list and their senders' names (unless the sender has a
  [name colour](#name-colours)), and ChatTwo's right-click menu show the
  custom colour itself.
- **What is checked in game.** Whether the game's chat log, the server info
  bar and the game's menus show the Color macro (we believe they do: it is
  the game's own formatting), what ChatTwo shows, and whether text after an
  item link inside a custom-coloured line keeps the exact colour (the game
  keeps UIForeground and Color as separate stacks, so this is the one case the
  layering could show the closest row instead). To compare without changing
  any channel, the debug window's **Colour test** (or `/lgdebug colours
  #RRGGBB ...`) prints, for each colour, a line layered as a channel's is,
  one in only its closest row, and one in only the exact colour.

### Name colours

A person's name can have a colour of its own, so people can be told apart at a
glance in a busy channel. The owner's decision (2026-10-07): one colour per
person, everywhere.

- **What and where it shows.** One custom colour (any RGB value) per person,
  on their name only: in every channel's lines in game chat and in channel
  windows, and in the channel member list in the main window. The rest of the
  line keeps the channel's colour, as before (the whole line or only the tag,
  as the setting says). Your own name can have one too.
- **Setting it.** Right-click a name in a channel's member list (the main
  window), or a message in a channel window, and choose **Name colour...**
  (it is also in a member's ⋮ menu). It opens the same colour wheel and colour
  code field as a channel's **Custom...** (`ColourWheel`, shared), with a
  preview of a chat line, **Use this colour**, **Default** (back to the line's
  colour) and **Cancel**. The words are `NameColourWords`, checked for jargon.
  It isn't offered for user ID 0 (a sender not known), and works with or
  without a session, since it belongs to no one character.
- **Kept.** On this computer only, never sent to the server: `NameColours` at
  the top of the plugin's settings, a map from a person's key to 0xRRGGBB.
  Unlike channel colours, it isn't per character: one colour per person for
  every character played on this computer. Settings saved before have none. A
  colour isn't dropped when the person leaves a channel (they may be in
  another, or come back); **Default** removes it.
- **Keyed by who they are, not by name.** The key is the person's user ID,
  which is their Lodestone character ID: it stays through a name change or a
  world transfer, and it is the same character on every server, so the colour
  follows them across channels and servers. A test server's made-up accounts
  are the exception: their negative IDs are made from the name alone (a hash),
  the same on every server, but anyone can register any such name on any test
  server, so the same ID on two servers needn't be the same person. Those keys
  carry the server's address too (`-1@wss://…`), written the same however it
  was typed (scheme and host in lower case, no default port, no trailing
  slash), so tidying the address setting keeps them. User ID 0 (a sender not
  known) never gets a colour. The rules are the core library's `NameColours`,
  unit tested.
- **In game chat: one colour at a time.** The sender shows as
  `<Name@World> `; only `Name@World` takes the colour, layered like a custom
  channel colour (closest UIColor row outside, the exact colour inside). When
  the whole line is in the channel's colour, that colour isn't left open around
  the name: it is closed before the name and opened again after it, so no
  colour is nested inside another, and the message after the name (item links
  and all) sits in the channel's colour exactly as it would without a name
  colour, whatever a renderer does with nested colours:
  `channel → "<" → off`, `name → "Name@World" → off`,
  `channel → "> " + message → off`. Without a name colour the line is what it
  was before, part for part, so byte for byte. The order is decided in
  `ColouredText.Message` and tested.
- **Elsewhere: exact.** Channel windows draw the sender's name in its colour
  (else the channel's, as before), and the member list draws the name in it.
  Both read it every frame, so a change shows at once.
- **Logs.** Nothing is logged about name colours.
- **Checked in game:** [docs/testing/name-colours-checklist.md](testing/name-colours-checklist.md).

### Talking in a channel without /lgc

`/lgc3` or `/lgc sky` with no message makes the chat box talk in that channel
(a "sticky" channel): from then on, plain text typed in the chat box goes to
the channel, as `/lgc3 <message>` would send it, and never to game chat.
Commands still work, and so do the game's short channel commands as one-off
modifiers, as in FFXIV: `/p brb` talks in Party once, and talking in the
channel goes on (with one ChatTwo exception, below). `/lgc` alone still
explains itself, and `/lgc 3` is a nickname, never channel number 3. `/lgl`
alone talks in local chat the same way, held as one more channel (see
*Talking in local chat* under
[Local chat (friends only)](#local-chat-friends-only)). The rules
live in the core library (`StickyChannel`, `StickyRoute`, `ShortCommandRule`,
`ChatTwoLine`, `ChatChannelPrefixes`, `LinkText`, `ChatBoxGate`), with no game
types, and are unit tested; the plugin's `StickyMode` feeds them and acts on
them, on the game thread only. The checks to make in game are in
[docs/testing/sticky-channel-checklist.md](testing/sticky-channel-checklist.md).

**Starting.** Only in a channel the player is a member of (any rank, under
their current setup) on the server they're connected to now, once the channel
list is in. Otherwise it is refused with one plain line: not connected, still
loading, not in that channel, or not available (a hook is missing, or the
game's chat channel can't be read). With ChatTwo it is also refused while
ChatTwo's main input is on a /tell (see below). `/lgcM` while talking in
another channel moves to that one. If ExtraChat (or a fork of it) is loaded
too, one more line warns that it watches the same chat box and ChatTwo label,
and to turn it off while doing this. The same warning comes (once) if ExtraChat
is turned on while already talking in a channel: the loaded plugins are looked
at every few seconds (the owner's third retest turned ExtraChat on after
`/lgc1`, and saw no warning).

**What it says.** Short lines, in LookingGlass blue with the tag in the
channel's colour (see LookingGlass's own lines, above), in the chat channel
chosen in Settings (the same one for every line, so a ChatTwo tab that shows
"Now talking in" shows "Stopped" too): "Now talking in [sky]." and "Stopped
talking in [sky]." with a few words of reason where they help (": you logged
out.", ": disconnected.", ": you're no longer in it.", ": LookingGlass was
turned off.", ": the connection started over."). Most of these only with
**Say when I start or stop talking in a channel** on (Settings, under Chat;
called "Verbose channel messages" before 2026-10-09; `VerboseChannelMessages`,
off by default, also for settings saved before it existed): off, "Now talking
in" isn't said, nor "Stopped talking in" when the player chose the stop (see
Leaving). A player who switches between LookingGlass and game channels often
found these lines noise (the owner's request, October 2026). It is safe because
the line was never the only sign: the server info bar entry and the chat box
labels (the game's and ChatTwo's) show the tag exactly while typing goes to the
channel, kept in step every frame and taken down the moment it ends (see The
indicator), and the owner found them reliable. Everything else is always said:
a stop the player didn't choose, refusals to start ("No channel is on /lgc3"),
the ChatTwo note, every "Not sent", the ExtraChat warning and every other
warning. The diagnostic log has every start and stop either way. The first time ever that it
starts with ChatTwo loaded, one more sentence follows (a saved setting,
`ChatTwoLabelNoteShown`; a new name, so players who saw an earlier round's
note see the new one once): ChatTwo's "(Warning: …)" names its own channel,
typing still goes to the channel, and a short command like `/p hi` talks in
that game channel once. (Until 0.2.10 it said ChatTwo's own channel's short
command went to the channel, and to use `/party hi`.) A message kept
from the game says "Not sent to [sky] or game chat: *reason*", in blue: it is
information, nothing went where it shouldn't. Only "Not sent … something went
wrong" (deciding threw) is a warning.

**The hooks.** Two required game functions, by the addresses
FFXIVClientStructs gives (Dalamud resolves them at startup), hooked with
`IGameInteropProvider` in `ChatInterop`:

- `ShellCommandModule.ExecuteCommandInner`, the gate: where the game runs a
  chat line, a command (Dalamud's included) or plain text for the current
  channel. Every line goes through it, whichever way it came:
  - the game's own chat box. Enter goes from the ChatLog addon, through the
    UI's external interface handler 19 (`UIModule`), which adds the line to the
    input's history and calls this function directly. The game's code never
    calls `UIModule.ProcessChatBoxEntry` (a reviewer's cross-reference scan of
    the 2026.09.15 game build: no callers). The first version of sticky mode
    gated `ProcessChatBoxEntry`, so the game's own chat box went past it
    entirely: plain text, links and `/s test` all reached game chat while the
    label showed the tag. That was the owner's B and C;
  - `UIModule.ProcessChatBoxEntry`, which ChatTwo (and other plugins) call, and
    which ends here too;
  - about twenty other callers in the game: macro lines, gear sets, battle
    mode, general actions, joining the novice network. Their commands pass
    through unchanged, short channel commands with text included: a raid
    macro's `/p Pull in 5` goes to Party while sticky (the owner saw this in the
    second retest and wants it). A macro's plain text while sticky goes to the
    LookingGlass channel instead of game chat: private, so it fails safe.

*Lines run inside a line (a reviewer's reading of the game's code).* Two of
the game's command handlers run the gate's function again while their own
line is running. The reply command (`/r`, `/reply`, `ShellCommandChatReply`)
sets the tell target, then runs only the text after "/r" that way. Judged,
that text is plain text, so a private reply would go to the whole channel and
the tell would never be sent. So a line run directly inside a reply the gate
let through goes to the game unjudged (`NestedLines`; English names and the
client's own from the `TextCommand` sheet, found by the English name), and the
log says so. Every other line run inside another is judged as usual: a
plugin command that submits plain text while it runs, and the game's command
that runs a stored line (`ShellCommandCommand`, contents unknown; low risk,
and judged means kept from game chat). The game's other chat commands (Say,
Party, Tell, FC, the linkshells, Alliance, Novice Network, PvP team) send
directly, with nothing run inside.
- `RaptureShellModule.ChangeChatChannel`, which switches the game's chat
  channel (`/s`, `/p`, `/l1`, ChatTwo's channel picker and tabs). Some of the
  game's own commands set the channel through another function that
  FFXIVClientStructs doesn't name (`RaptureShellModule.SetChatChannel` in the
  reviewer's scan, about nine shell-command sites), so not every switch passes
  this hook. Those are still seen: a typed channel command on its own is caught
  at the gate, and the channel is read once a frame and before each draw of the
  chat log.

Both or neither: if either address is missing, neither is hooked and sticky
mode refuses to start, rather than catch lines without seeing switches. Both
stay enabled while the plugin is loaded; when no channel is sticky, the
detours only call the game (and count a running line, below). Three more are
optional: `UIModule.ProcessChatBoxEntry` (a pass-through that notes a line came
from a plugin and, while sticky, asks ChatTwo what its main input holds as the
line arrives; see Where a line goes), `AgentChatLog.ChangeChannelName` (to see
a switch sooner) and `AgentChatLog.InsertTextCommandParam` (the diagnostic log
only). Without the `ProcessChatBoxEntry` hook no line can be told to be the
game's own, so every line is held to the strict short-command rule (the way
in "unknown"): sticky mode stays safe, and `/p brb` goes to the channel.

*Plugin commands (checked in Dalamud's source).* Dalamud dispatches plugin
commands, `/lgc` included, from its own hook on
`ShellCommands.TryInvokeDebugCommand` (`Dalamud/Game/Command/CommandManager.cs`):
it calls the game's function first and, if the game doesn't know the command,
runs the plugin's handler right there, synchronously. The game calls that
function while running a command, inside `ExecuteCommandInner`, so the gate
sees `/lgc3` first, lets it through like any command, and the handler runs
inside the gate's call to the game (which is also why a line counts as
running, below). The two hooks are on different functions, so their order
doesn't matter. Another plugin hooking `ExecuteCommandInner` too is chained by
Dalamud: if it runs first and changes the text, the gate decides the changed
text, which still goes to the channel. (GagSpeak, on the owner's machine,
rewrites gagged speech in a hook of its own on the game's chat input, found by
its own signature, `ProcessChatInput`, and only for the game channels its user
picked; see [Garbled speech with GagSpeak](#garbled-speech-with-gagspeak).)

**Where a line goes** (`StickyRoute.For`). It is decided from the line as the
gate got it (`ChatBoxLine`): its bytes, an SeString in which links
and auto-translate phrases are payloads starting with the byte 2, and its text
(`SeString.TextValue`, where those become their text). Not sticky: the game.
Sticky:

- A channel command on its own, with nothing after it (`/s`, `/say`, `/p`,
  `/party`, `/l1`, `/linkshell1`, `/cwl1`, `/cwlinkshell1` and the rest, in
  English and in the client's language from the game's `TextCommand` sheet):
  the player switching back. Sticky mode ends right there, with its line and
  the labels taken down, and then the command goes on to the game, which
  switches its channel (`StickyRoute.Leave`). This doesn't depend on whether,
  or when, the game calls its channel switch for the command; for the channel
  already on it may not (see Leaving). If the game then refuses the switch
  (`/l3` without a third linkshell), sticky mode has still ended, and the label
  shows the game's channel, which is where typing goes.
- A short channel command (`/s`, `/p`, `/a`, `/y`, `/sh`, `/fc`, `/pt`, `/b`,
  `/l1` to `/l8`, `/cwl1` to `/cwl8`) followed by *anything* (text, a link's
  payload bytes, a link placeholder, an auto-translate phrase) is the player's
  one-off and goes to the game, which talks in that channel once; sticky mode
  goes on. That is FFXIV's own rule, and what players type (almost nobody
  types `/party`); the owner chose it after the second retest, because
  sending `/p brb` to the LookingGlass channel could also put something meant
  for Party where they didn't expect it. The exception is ChatTwo's typing:
  ChatTwo sends plain text typed in it as "*its channel's short command*
  *text*" ("hi" in an input on Party is sent as "/p hi"; below). Which short
  commands stand for plain text is decided for each line by the way it came
  in (`ShortCommandRule`):

  | The line came from | Short command with text | Plain text |
  |--------------------|-------------------------|------------|
  | the game itself (its own chat box, a macro line, a gear set) | the game, once | LookingGlass |
  | ChatTwo's main input, on Party (its current tab's channel, or its one-off channel) | typed as it is (`/p hi`, `/s hi`, `/fc hi`): the game, once | LookingGlass (ChatTwo sends "hi" as `/p hi`, while its input holds only "hi") |
  | ChatTwo's main input on echo (no channel), a tell or an ExtraChat channel | the game, once | (ChatTwo sends it as `/e …`, a tell or `/ecl…`: not game chat) |
  | a plugin, but not ChatTwo's main input (a ChatTwo pop-out with its own input, ChatTwo's web interface, another plugin), or ChatTwo didn't answer, or named a channel LookingGlass doesn't know | LookingGlass (the strict rule: it may be ChatTwo's typing in an input whose channel isn't known) | LookingGlass |
  | unknown (the `ProcessChatBoxEntry` hook is missing) | LookingGlass (strict) | LookingGlass |

  With ChatTwo on Party, `/p hi` typed and "hi" typed are sent as the same
  line, but the main input's text tells them apart: the whole line for the
  first, only "hi" for the second. Its length alone can't ("hi" and three
  spaces is as long as "/p hi", and ChatTwo trims the spaces off), so the
  plugin reads the text itself (under ChatTwo, *Sending*, below); when it
  can't, ChatTwo's own channel's command goes to the LookingGlass channel, as
  before. Until 0.2.10 that was always so (an accepted edge); a tester on Say
  kept hitting it with `/s`.
  A short command whose line goes to LookingGlass is sent without it, like
  plain text. Only the bare command is a switch (above). The command ends at
  the first space or control byte, so a payload straight after it still
  counts. The long forms (`/party hi`) always go to the game.
- Any other line starting with `/` is a command and goes to the game
  untouched, `/lgc` commands included. Only a `/` at the very start counts: a
  line with a space before it goes to the channel, never the game. (Except
  in ChatTwo, which trims its input before sending: there "  /s hi" is sent
  as "/s hi" and goes to Say once, as ChatTwo itself would send it.)
- Anything else goes to the channel, trimmed, as text and links. A line with
  only a link goes too (it used to be kept from the game, "not sent", while
  links couldn't be sent): that is a line with only links (payloads) or only
  link placeholders (`<item>`, `<flag>`, `<status>`: what the chat input holds
  for a link until the line is sent, put there by
  `AgentChatLog.InsertTextCommandParam`; the game makes them links only while
  running the line, after the gate, and ChatTwo's input holds them the same
  way; the owner's log shows the placeholder in both chat boxes). A short
  command whose line goes to LookingGlass (above) with only a link goes the
  same way. A line with something in it but nothing LookingGlass can send (a
  payload that is neither text nor an item, map or status link) is kept from
  the game, with "Not sent to [sky] or game chat: nothing in it can be sent to
  a channel." A blank line goes nowhere, quietly.
- *Links* (`LinkText`, and the plugin's `GameLinks`). A link goes as its name
  in square brackets in the text, "look `<item>`" sent as "look [Potion]"
  (what older clients show), and over that as the link itself (see
  [Links in messages](#links-in-messages)). The plugin reads the line's bytes
  with Dalamud (`SeString.Parse`): an `ItemPayload`, `MapLinkPayload` or
  `StatusPayload` and the text up to its link terminator become one link (its
  raw item id, territory, map and coordinates, or status id), held in the
  line's text as a marker (a Unicode noncharacter, U+FDD0 on, which never
  stands for anything in game text and is taken out of anything typed) so the
  routing rules above see "text" and "links" apart and can cut the command off
  without losing them. Any other link (a player, a quest) stays as its text.
  A placeholder is resolved where the game keeps what it stands for, as
  ChatTwo's input preview does (`Message.cs`, `DecodeTextParam`, 1.40.9):
  `<item>` the item the chat log agent holds as linked
  (`AgentChatLog.LinkedItem`: its `ItemId` and `Flags` fields, read rather than
  calling the game, unless it is a symbolic item; high quality and collectable
  from the id's offsets or the flags; `LinkedItemName` if the sheet has no
  name), `<status>` its `ContextStatusId` (`ContextStatusName` if the sheet has
  no name), `<flag>` the map flag (`AgentMap`, the first flag marker: world
  coordinates to a thousandth, as the game and ChatTwo make a map link of it).
  The order for each: what it points at, if the player's own game data shows
  it (the checks in Links in messages), else name only; the name from the
  player's sheet (in their language), else the game's text for it; neither:
  left out. Each kind is asked once per line. A name is plain text (no game
  formatting or icons; `<`, `>`, `[` and `]` become brackets). A link with a
  name but nothing known about what it points at (a symbolic item) goes as
  its name only. A link nothing at all is known about is taken out, with the
  spaces around it, the rest is sent, and once it has been sent one blue line
  says "A link in it couldn't be read, so it was left out." If nothing is
  left, "Not sent to [sky] or game chat: the link couldn't be read." A message
  is never both sent and an error. In a short command line going to the game
  (the player's one-off `/p look <item>`), links are left to the game, as
  before. A map flag the player's own game data can't show as a link
  still goes, as its place name or "[flag]". A name is cut to fit what a
  link may stand over (65 characters, 67 with its brackets), between
  characters, never inside a surrogate pair.
- *Links in `/lgcN` and `/lgc <nickname>`.* Dalamud gives a command handler
  its arguments as a string, in which a link's bytes would be garbled. So the
  gate (`ExecuteCommandInner`, below) reads every line starting with `/lgc`
  as above, placeholders resolved right then, and keeps it while the game runs
  the line; the handler, which Dalamud runs inside that call, takes its
  message from it, so a `/lgc` message is now the gate's copy of the line:
  what the game's chat box or ChatTwo handed the game, before the game runs
  it. Two consequences to check in game (the checklist has them): the game's
  own text commands (`<t>`, `<me>`) are expanded only while the line runs, so
  they reached the gate's copy as typed (the owner saw them sent that way;
  LookingGlass now replaces them itself, see
  [Text commands in messages](#text-commands-in-messages)); and a plugin that rewrites lines in
  the same hook after LookingGlass (GagSpeak) would not have its rewrite
  sent, where one that runs first would. The command ends at any space,
  a full-width one too. A `/lgc` command not run through the gate (another
  plugin calling it, or the gate's hooks missing) is sent from Dalamud's
  string: each payload in it is skipped by its own length (replacement
  characters counted as the byte they stand for), or to the next byte 3 if
  its length can't be read, and placeholders are resolved when it is sent.

**Fail closed.** While sticky, a line never reaches game chat unless it is a
command:

- Not connected (or reconnecting): kept from the game, and "Not sent to [sky]
  or game chat: not connected to LookingGlass." A connection that drops on its
  own keeps its session, which reconnects, so sticky mode stays on, and a
  reconnect doesn't drop the player into public chat. Pressing **Disconnect**
  stops the session: that ends sticky mode, with "Stopped talking in [sky]:
  disconnected." (`StickyEnd.Disconnected`), said whatever the verbose setting.
- The send fails (no channel key yet, rate limited, refused, timed out): the
  same line with the reason, in the mode's words (`PlainMessages.MessageOf`).
- Deciding throws: the line is kept, and the player is told it wasn't sent.
  `ChatBoxGate.KeepFromGame` holds this rule, and a test fails if its catch
  lets the line through. An exception never reaches the game.
- Hooks missing: sticky mode can't be turned on at all.
- Unloading: sticky mode ends first (with a line), the chat input's channel
  name is put back, then the hooks come off, before anything else is disposed.

A line kept from the game is still in the chat input's history, so the up
arrow brings back a message that wasn't sent: the game's own chat box adds the
line to its history before running it, and ChatTwo keeps its own. (The first
version added kept lines itself, which would now add the game's twice.)

**Leaving.** It ends when any of these happens. With verbose channel messages
on, one line says so every time. Off, the line is said only for a stop the
player didn't choose (`StickyMessages.ChosenByThePlayer`, a unit test lists
every `StickyEnd`, so a new one has to be put on one side on purpose, and one
it doesn't know is said). The player's own, marked *(own)* below
(`ChannelSwitched`, `ChatBoxSwitched`, `Stopped`), are quiet; the rest
(`LoggedOut`, `Disconnected`, `SessionEnded`, `NotInChannel`, `ChannelUnknown`,
`Unloading`) are always said, as the player can't otherwise tell why the tag
went away. Pressing Disconnect counts as not chosen: the player chose to
disconnect, not to stop talking in the channel. Moving to another LookingGlass
channel with `/lgcM` doesn't end it at all (see below).

- *(own)* the game's chat channel changes: read once a frame
  (`RaptureShellModule.ChatType`, which names the linkshell too), and after
  every `ChangeChatChannel` call, against the channel it started in
  (Tab-cycling, ChatTwo's picker, a ChatTwo tab with another channel, another
  plugin);
- *(own)* a channel command is typed on its own, even for the channel already
  on (`/s` while in Say): decided from the line itself, before the game runs it
  (see Where a line goes), and also any `ChangeChatChannel` call made while a
  line from the chat box is being run (`StickyChannel.ChannelSwitchCalled`),
  such as `/t Bob`;
- *(own)* the player clicks the server info bar entry;
- they log out or another character logs in;
- the session is stopped: **Disconnect** pressed (or a server change waiting
  to connect), "disconnected";
- the session is replaced: the server address changed, or the identity reset
  or restored (a new session object), "the connection started over";
- they're no longer in the channel (left, removed, disbanded, "Remove from my
  list", or now only a place under old keys), checked against the complete
  channel list only;
- *(own)* the game makes a one-off switch, saving the channel it is on to go
  back to (`StickyEnd.ChatBoxSwitched`, below);
- the game's chat channel can't be read any more;
- the plugin is turned off or updated.

**One-off switches (the reviewer's reading of the game's code).** Besides its
channel (`ChatType`), the shell keeps `RaptureShellModule.TempChatType` and
`TempChatCommand` (FFXIVClientStructs), with a saved tell target: the channel
to go back to. For a one-off switch, a tell from a menu ("Send Tell",
`SetContextTellTargetInForay`) or a channel for one line, the game saves the
channel it is on there, then calls `ChangeChatChannel` with the new one without
making it its channel (`setChatType` false), and sets the saved type to -2 if
that fails. (The previous round's docs had this backwards, as a channel typed
for one line.) Sticky mode reads it, with the chat log agent's channel and
label (`AgentChatLog.CurrentChannel`, `ChannelLabel`, the label only as a
hash), as a `ChatBoxState`: once a frame, right before each draw of the chat
log, and when the game renames its input's channel
(`AgentChatLog.ChangeChannelName`, hooked; if that hook is missing, the other
two still hold). Measured against the state when sticky mode started:

- a channel saved now, where it changed: a one-off switch; sticky mode ends,
  with its line, and the label is the game's again (a tell from a menu ends it
  too, which is accepted);
- any other change of it (only its type, or cleared): the tag is held back
  from the label, so the game's own name for the channel shows, while what is
  typed still goes to the LookingGlass channel (the safe way round); the info
  bar still shows the tag;
- the agent's channel or label alone: logged, nothing else.

A line let through to the game (a command, `/party hi`) may set and reset the
saved channel while it runs, so the state is measured again once it has run
(`StickyChannel.LinePassed`) and changes while it runs don't count.

A `ChangeChatChannel` call that neither changes the channel nor comes from a
typed line doesn't end it: ChatTwo makes one with the channel it is already on
at every tab switch, and when its input loses focus or Escape is pressed after
a one-off channel. The cost: picking the current channel again in a picker
doesn't end it either.

With verbose channel messages on, the line is printed for a channel switch
too, though the player usually made it. Before the setting it always was, as
the line told them their typing goes to game chat again; the owner's testing
showed the labels already tell them that, so it is off by default. Moving to
another LookingGlass channel says "Now talking in" instead (verbose on), or
nothing (off): the labels change to the new tag in the same frame.

**The indicator.** While sticky, the channel's tag (`[sky]` or `[LGC3]`)
shows in three places:

- the game chat input's channel name, where it says "Say" or "Party"
  (`AddonChatLog.CurrentChannelTextNode`), set just before the chat log is
  drawn (`IAddonLifecycle` `PreDraw`). When sticky mode ends, the node gets
  the game's own name for its current channel
  (`AgentChatLog.ChannelLabel`), not the one saved when it started, so after a
  switch it never shows the old channel;
- ChatTwo's main input (below);
- the server info bar (`IDtrBar`), as "LG [sky]" in the channel's colour, with
  a tooltip; clicking it stops. This one works whatever chat window is in use.

They never disagree with where typing goes. Once a frame (`SyncIndicators`),
whatever happened, ChatTwo's label and the info bar are made to match the
state: set while sticky (ChatTwo's sent again every second, in case it missed
it, `LabelKeeper`), and taken down on the first check after it ends, once,
whatever ended it. ChatTwo's is cleared only if LookingGlass set it, so
another plugin's label is left alone. When sticky mode ends, the labels come
down first and the line is printed after, each even if the other fails.

**ChatTwo.** ChatTwo replaces the game's chat window, and it is the owner's
default. What its public source (github.com/Infiziert90/ChatTwo, EUPL-1.2,
read for its behaviour only; version 1.40.9) shows, and what LookingGlass does
about it:

- *Sending (verified).* ChatTwo sends what is typed with
  `UIModule.ProcessChatBoxEntry`, which ends in the gate, but puts its input's
  channel command in front of plain text first: "hello" typed in an input on
  Party is sent as "/p hello" (`SendHandler.SendChatBox`,
  `InputChannelExt.Prefix`); a line starting with `/` is sent as typed,
  trimmed. Every input has its own channel: the main window's (its current
  tab's, `Plugin.CurrentTab.CurrentChannel`, or the one-off channel a keybind
  set there), and each pop-out with its own input (a tab with **Pop out** and
  **Supports input** on, and **No input** off; `Popout.cs`), whose picker or
  fixed channel changes only that pop-out, without the game knowing.
- *Which input sent a line (verified in ChatTwo's source; the timing is
  inferred).* `ChatTwo.GetChatInputState` returns `(InputVisible,
  InputFocused, HasText, IsTyping, TextLength, ChannelType)`
  (`Ipc/TypingIpc.cs`): for the main window only, `ChannelType` is the channel
  a line typed there now goes to (the current tab's `UsedChannel`, its one-off
  channel if set: the very field `SendChatBox` reads), in ChatTwo's own
  `ChatType` numbering (`Code/ChatType.cs`: Say 10, Shout 11, a tell 12, Party
  14, Alliance 15, linkshells 16 to 23, FC 24, Novice Network 27, Yell 30, PvP
  team 36, cross-world linkshell 1 37 and 2 to 8 101 to 107, echo 56,
  ExtraChat's 1001 to 1008; a ushort enum, which Dalamud converts to the
  ushort LookingGlass asks for), and `TextLength` the main input's length as
  typed. There is no IPC for a pop-out's input, and ChatTwo's settings say
  only which tabs *could* have one, so LookingGlass doesn't read them (reading
  another plugin's config file from `pluginConfigs` would be fragile, and is
  out of bounds). Instead it recognises the main input by what it still holds:
  `SendChatBox` empties the input only after `ProcessChatBoxEntry` returns, so
  while the main input's line is on its way, the input holds it as typed (a
  command, "/s hi", the same length as the line) or without the command
  ChatTwo put in front (plain text: "hi", sent as "/p hi", the line's length
  less "/p "). ChatTwo sends the input trimmed but reports its length as
  typed, so up to two spaces more (`ChatTwoLine.MostTrimmed`, before and
  after together) still count as the line: "/s hi " is the main input's, and
  goes to Say once. (Before 0.2.9 the lengths had to match exactly, and a
  stray space sent "/s hi " to the channel.) Since 0.2.10 it also reads the
  input's text: ImGui keeps the text of the input typed in last (its input
  text state, until another input is typed in), every plugin's windows share
  Dalamud's ImGui context, and ChatTwo sends its main input's line as that
  input lets go. Text of the main input's length is that input's: trimmed, it
  is either the line (a command as typed: every short command goes to the
  game once, ChatTwo's own channel's too) or the line without ChatTwo's
  command (plain text: the channel), and anything else (an auto-translate
  phrase, sent as something else) is held to the strict rule. Text of another
  length means another input was typed in last (a pop-out's): not the main
  input's line, strict. Only when the text can't be read does the length
  alone decide, as before, with ChatTwo's own channel's command as text. The
  text is never logged; the log says whether it decided ("by its text"). The
  plugin reads the IPC (and the text) in its `ProcessChatBoxEntry` hook, so
  before any other plugin's hook on the gate (GagSpeak's, on the owner's
  machine) can change the line, and keeps it for that line only, not for
  lines run inside it (`ChatTwoLine`). A line from a pop-out, the web
  interface or another plugin finds the main input empty, or holding a draft
  of another length, and is held to the strict rule (every short command is
  text); with the text unreadable, a draft of that length or up to two
  characters longer is the one way to mistake it (with the text read, the
  pop-out's text is the last typed, so it isn't). A main-input line whose
  length doesn't match (more than two spaces
  around a command, an auto-translate phrase, another plugin changing it on
  the way in) also gets the strict rule: it goes to the channel, never to game
  chat. The diagnostic log then gives both lengths ("not ChatTwo's main input
  (it holds 14 characters, the line 5)"). So with ChatTwo: plain text goes to
  the LookingGlass channel; a short command typed in the main input goes to
  the game once, its own channel's included; in a pop-out,
  every short command goes to the LookingGlass channel (use the long form
  there). Three of ChatTwo's prefixes are never text: `/t` (it sends tells to a
  known player itself, below), `/e` (echo, for an input with no channel, seen
  only by the player) and `/ecl1` to `/ecl8` (ExtraChat's, not game chat).
- *Links (verified).* ChatTwo's input is plain text: a link put in it (its own
  "Link" menu item calls `AgentChatLog.LinkItem`) arrives through the game's
  chat log refresh event as a string ChatTwo adds to its input
  (`Chat.ChatLogRefreshDetour`, `ChatLog.Activated` with `AddIfNotPresent`),
  and its preview turns `<item>`, `<flag>` and `<status>` back into links
  (`Message.cs`, `TextParamRegex`; `InputPreview.cs` counts a link as
  `"<item>".Length`), so the input holds the placeholder; the status link is
  added as " <status>" directly (`PayloadHandler.cs`). A link alone in an
  input on a cross-world linkshell is therefore sent as "/cwl1 <item>"
  (`SendHandler.SendChatBox`). Whether the game's string is always the
  placeholder, or sometimes the link's own bytes, isn't visible from ChatTwo's
  side, so both are handled (Where a line goes). The preview reads what a
  placeholder stands for from the game (`AgentChatLog.LinkedItem.ItemId`,
  `ContextStatusId`, `AgentMap`'s flag markers), which is where LookingGlass
  reads what a link points at too (*Links*, above).
- *Links received (from ChatTwo's public source, 2026-10-06).* ChatTwo shows
  what the game's chat log is given: it takes every printed message from
  Dalamud's `ChatMessageUnhandled` (`MessageManager.cs`) and cuts it into
  chunks by its Dalamud payloads (`ChunkUtil.ToChunks`): an `ItemPayload`,
  `MapLinkPayload` or `StatusPayload` makes the text after it a link until
  the link terminator (`RawPayload.LinkTerminator`, which Lumina's `PopLink`
  and Dalamud's map link both end with), and `UIForeground` and `UIGlow` set
  its colours. Hovering an item or a status shows ChatTwo's tooltip, clicking
  a map link opens the map (`PayloadHandler.cs`: `HoverItem`, `HoverStatus`,
  `GameGui.OpenMapWithMapLink`). So a link LookingGlass prints with those
  payloads is clickable in ChatTwo too, with nothing ChatTwo-specific.
- *A channel command on its own (verified on ChatTwo's side).* Typed in
  ChatTwo, `/s` is sent as it is through `ProcessChatBoxEntry`, like any line
  starting with `/` (`SendHandler.SendChatBox`); ChatTwo doesn't act on it
  itself. Its label then follows the game's channel when the game renames its
  own (`Chat.ChangeChannelNameDetour`). So it is caught by the rule for a
  channel command on its own.
- *Tells (verified).* ChatTwo sends a tell to a known player straight to the
  server, without running a chat line in the game, so it can't be caught. Sticky mode
  therefore refuses to start while the game's channel or ChatTwo's main input
  is on a /tell, and switching the game to a tell ends it. A ChatTwo tab or
  pop-out set to a tell, or a one-off tell (its reply keybind, "Send Tell" in
  a menu), goes as a tell, as that input shows (and as the README says).
- *Switching (verified).* ChatTwo's channel picker, its tab switches and its
  keybinds for a lasting switch call `RaptureShellModule.ChangeChatChannel`
  (`SetChannelWithExtraChat`); see Leaving for which calls end sticky mode.
- *The label (verified).* ChatTwo names another plugin's channel in its input
  only through the IPC message `ExtraChat.OverrideChannelColour` (made for
  ExtraChat): `{ Channel, UiColour, Rgba }`, with a null `Channel` to stop.
  LookingGlass sends "LookingGlass [sky]" in the channel's colour while sticky,
  and null when it ends (and again when `ChatTwo.Available` says ChatTwo
  reloaded). Dalamud matches the struct's fields by name. ChatTwo shows it in
  its main input only when no one-off channel, tell or tab channel takes
  precedence; pop-outs show their own channel. It changes only the label and
  the input's colour, never where ChatTwo sends. ExtraChat sends on the same
  IPC, hence the warning when it is loaded.
- *"(Warning: Party)" (verified, can't be avoided today).* ChatTwo adds
  "(Warning: *its channel*)" to an override unless its own input channel is one
  of ExtraChat's (its `ExtraChatLinkshell1` to `8`). Its input can only be put
  on one of those through its own channel picker, which offers them only while
  `/ecl1` to `/ecl8` are registered Dalamud commands. LookingGlass doesn't
  register ExtraChat's commands, so ChatTwo shows "LookingGlass [sky] (Warning:
  Party)". The ChatTwo sentence said once at a start (after "Now talking in"
  with verbose channel messages on, on its own with them off) says what
  it means: ChatTwo's own channel underneath; what is typed still goes to the
  LookingGlass channel (from any ChatTwo input not set to a tell), and a
  short command typed in the main input talks in that game channel once.
  Making the label go away needs a change in ChatTwo, such as an override
  that names a command to send plain text with (`/lgc3`) and no warning.
- *Why ExtraChat's sticky channel was unreliable with ChatTwo (inferred from
  ChatTwo's side only).* Typing `/ecl1` in ChatTwo sends the command, and the
  override renames ChatTwo's label, but ChatTwo's own input channel stays
  where it was (Party in the owner's screenshot, hence "(Warning: Party)").
  ChatTwo then sends plain text as "/p text", so whether it reached ExtraChat
  or party chat depended entirely on whether the other plugin's hook claimed a
  "/p" line as its own. Picking the ExtraChat channel in ChatTwo's picker put
  ChatTwo's input on it properly, but any game channel change, and every tab
  switch, puts ChatTwo's input back on the game's channel. LookingGlass reads
  ChatTwo's main input's channel for each line as it is sent, and holds every
  other input to the strict rule, so no input's typing reaches game chat.
- *Tabs (verified).* A ChatTwo tab either has a channel of its own (its
  **Input channel** setting) or none. Switching to a tab with a channel of its
  own puts the game on that channel (`SetChannelWithExtraChat`), so if that is
  another channel than the one sticky mode started in, sticky mode ends, with
  its line (the owner's second retest: Say to Party, ended), and switching
  back doesn't start it again: type `/lgc1` again. Switching to a tab with no
  channel of its own, or one whose channel is the game's already, calls the
  switch with the channel already on, which doesn't end it. Tabs don't
  remember sticky mode: the owner decided (2026-10-06) that ending it on a
  tab switch that changes the channel is fine.

**Limits.** Only lines the game runs through the gate are caught: ChatTwo's
tells to a known player (above), which it sends to the server itself, go as
tells. Everything else that runs a chat line goes through the gate, so while a
channel is sticky, plain text from a macro, from another plugin, or from
ChatTwo's special tells in Eureka and Bozja (`ExecuteCommandInner` with the
message alone) is sent to the channel instead (private, so it fails safe), and
so is a short channel command with text from another plugin (the strict
rule). A plugin that calls `ExecuteCommandInner` itself with "/p text" looks
like the game, and goes to Party: it chose the command. A channel command on its own that isn't
known by name (neither English nor the client's language) ends sticky mode
only if the game calls its channel switch while the line runs, or the channel
changes.

**The diagnostic log.** So an in-game test can be read back without the
player copying anything, sticky mode writes one Information line to Dalamud's
log (`dalamud.log`, tagged `[LookingGlass] [sticky]`, built by
`StickyDiagnostics`) for every line the gate sees while sticky (saying
whether it came from the game, from a plugin through `ProcessChatBoxEntry`, or
an unknown way when that hook is missing), every
start (and refusal) and every end, and every `ChangeChatChannel` call while
sticky. A line's entry has the channel's tag, whether ChatTwo is loaded,
the leading command if it is a known one (otherwise "(text)", "(payload)",
"(link placeholder)", "(blank)" or "(other command)"), its size in bytes,
whether it held payloads, the decision with a reason in fixed words ("short
command, to the game once", "short command with text", "plain text" …), and
the short-command rule used ("rule: typed in the game: short commands go to
the game once", "rule: ChatTwo's main input on /p, a command as typed: short
commands go to the game once", "rule: ChatTwo's main input on /p, plain text
sent as /p: only /p is text", "rule: not ChatTwo's main input: short
commands are text (it holds 14 characters, the line 5)", and so on: a known
command and lengths at most). A message sent
adds a "sending" entry: sizes and counts only (bytes typed, characters
sent, how many links, whether one was left out, how many text commands were
replaced), never a name or an id. A
switch's has the chat type before and after the call, and whether a typed line
was in flight. Never what was typed, a link's contents, or an unknown command's
name (it could be a message typed after a "/"); tests check this. When sticky
mode is off, the hook doesn't look at lines at all, and channel switches are
written at Debug only. Also logged while sticky: every change of the shell's
saved channel (`ChatBoxState`: numbers, and its command only if known),
where it was seen (a frame, a draw, the game renaming its channel, after a
line), and whether it counted as a one-off switch; the tag being held back from the
label and shown again; a line run inside a reply passed to the game unjudged
(its size only); a link placeholder put in the chat input
(`AgentChatLog.InsertTextCommandParam`, hooked for the log only: just its
number); and the chat box state at start and end. A line typed while sticky
that reached game chat with no `[sticky] line` entry at that time went past the
gate.

**What the owner's game test showed, and what changed (October 2026).** The
owner turned ChatTwo off after step 1, so B and C happened in the game's own
chat box: `/s` typed while sticky printed nothing and left the tag on the
label while the next line, `test`, went to Say; and a link alone reached the
cross-world linkshell. The cause (found by a reviewer, verified in the
2026.09.15 game build): the game's own chat box never calls
`UIModule.ProcessChatBoxEntry`, which was the gate then, but runs its lines
through `ShellCommandModule.ExecuteCommandInner` directly (see The hooks). So
no line from it was ever seen: not `/s` (and its switch went through
`SetChatChannel`, past the `ChangeChatChannel` hook, so nothing ended), not
`test`, not the link, while the label showed the tag from sticky mode's state.
Earlier "plain text works" results without a second character to check Say
were seeing only the label. ChatTwo's lines did reach the old gate. What
changed: the gate is on `ExecuteCommandInner`, where both chat boxes, macros
and the game's other callers meet; a channel command on its own ends sticky
mode from the line itself; the short-command rule holds for every line; the
saved channel of a one-off switch is watched; and the diagnostic log says
which way each line came. (A guess in the round before, that the game's chat
box has a "one-line channel" typed as "/s ", was wrong: the `Temp` fields hold
the channel to go back to, see One-off switches.)

**What the owner's second retest showed, and what changed (October 2026).**
Everything worked and nothing leaked, in the game's own chat box (ChatTwo
off) and in ChatTwo. Four changes came from it:

- *Short commands are one-offs again* (the owner's decision): `/s hi`, `/p
  brb`, `/cwl1 hi` talk in that game channel once, as in FFXIV, and a macro's
  `/p Pull in 5` goes to Party. The one rule for every line had sent them to
  the LookingGlass channel, which players don't expect, and which could put
  something meant only for Party in a channel. Only ChatTwo's own channel's
  command stays the channel's there (Where a line goes).
- *Colours*: "Not sent" was red like a warning; it is information, in
  LookingGlass blue, with light red kept for warnings and dark red for
  critical ones (LookingGlass's own lines).
- *Text and a link* (ChatTwo step 35c): "look " and a linked item were sent to
  the channel as "look `<item>`", the placeholder as typed, and the owner saw
  an error with it (not in the log: no LookingGlass error line was written
  then). The link now goes as its name, or is left out with one blue line
  saying so, never both sent and an error.
- *ChatTwo tabs* (step 36): the owner's tabs have channels of their own, so
  switching tabs moved the game from Say to Party and ended sticky mode, as
  designed, and switching back didn't start it again. The owner decided that
  is fine (see ChatTwo, *Tabs*).

### Channel windows

Built at the owner's request (2026-10-06): a channel can be read and written
in an instant-messenger-style window of its own, apart from the game's chat
log and ChatTwo. Several windows can be open at once, each with a tab for each
of its channels. What is typed in a tab goes only to that tab's channel. Why:

- **No leak path through game chat.** The window's input box is the plugin's
  own (ImGui), so typed text never passes through the game's chat input or
  ChatTwo, and never reaches a game channel, whatever state the game or
  ChatTwo is in.
- **No wrong-channel mistakes.** With ExtraChat, heavy users of several
  channels sometimes sent sensitive or embarrassing messages to the wrong one.
  The tab, the window's title and the input box's hint ("Message sky") all
  name the channel the box sends to.

The rules that need no game are in the core library (`ChannelHistory`,
`ChannelWindowLayouts`, `GameChatChannels`, `UnreadCounter`) and unit tested;
the plugin's `ChannelWindows` opens and remembers the windows and
`ChannelWindow` draws one. The checks to make in game are in
[docs/testing/channel-windows-checklist.md](testing/channel-windows-checklist.md).

- **Opening.** Right-click a channel in the main window's channel list:
  **Open in new window** (always a new window, with that channel as its only
  tab); **Add to window ▸**, listing the open windows by their tabs' names
  (ticked where the channel is a tab already: choosing it shows it there); and
  **Show its window** when one has it. The channel's ⋮ menu has **Open in new
  window** too. A place from the user's old keys (see *Stale places*) has none
  of these.
- **A window** is a Dalamud window like the main window, so Dalamud's own
  settings for its transparency, pinning and click-through apply, and its
  sizes follow the global scale. It can be resized and closed; Escape never
  closes it. Its title is the selected tab's channel. Along the top, a tab per
  channel: its nickname or (shortened) name in the channel's colour, and on a
  tab not selected, the number of messages from others since it was last
  shown; its tooltip has the full name and the channel's commands. A tab
  closes with its ×, or **Close tab** in its right-click menu, and closing the
  last closes the window. Tabs can be dragged into another order. The **+**
  after them lists the channels the window doesn't have, in the channel list's
  order; choosing one adds it as a tab and selects it.
- **A tab** shows the channel's warnings at the top as the channel pane does
  (the membership warning, and why nothing can be sent yet: an old key's
  place, a new key pending, waiting for a member), then its lines, oldest at
  the top, following the newest unless scrolled up (then **New messages**
  takes it back down), then the input box.
- **A line** is the time it arrived (HH:mm, by the computer's clock; the
  plugin has no setting for the server's time), the sender's name in the
  channel's colour (name and world in its tooltip), and the text, wrapped
  under the name. A link is its name in brackets, coloured by kind: hovering
  an item shows its name (and high quality, collectable or key item), a
  status its name, and clicking a map flag opens the map there (Dalamud's
  `OpenMapWithMapLink`, on the game thread), as the game's own links do. A
  link passes the same checks as in chat (`ChatLinks.Check`, against the
  player's own sheets) and shows the player's own game's name for it; one that
  doesn't shows the sender's "[name]" as text. All remote text is sanitised as
  for chat (`TextSanitizer`, `LinkedText.ShownParts`), and a "##" in a name
  can't end a title or label early. LookingGlass's lines about the channel
  (someone was invited, joined, left or was removed, set up LookingGlass
  again; the channel's new name; a message that was dropped) are dimmed, and
  warnings are light or dark red as in chat. Right-click a line to copy it.
- **The input box** sends to its tab's channel only, through the same path as
  `/lgc` (`ChannelSender.Send`): the same link placeholders, text commands,
  rate limits, rekey waits and errors. Enter sends and keeps the keyboard in
  the box; Enter on an empty box, or Escape, gives the keyboard back to the
  game (Escape keeps what was typed, which ImGui would otherwise undo). Up to
  500 characters, as in the game's chat box: the box itself stops there as
  text is typed or pasted (an input callback), with a counter from 400, so
  nothing is cut unseen. "Not sent: …" shows as a line in the tab, in
  LookingGlass blue, never in game chat (and not at all if the session it was
  typed in has ended), and what was typed goes back into the box to send
  again, once the box is empty, into the box's own text if it is being typed
  in (the same callback), so what it shows is what Enter sends.
- **Links typed in the box.** `<item>`, `<flag>` and `<status>` resolve as
  they do in chat, from what the game holds now (the item last linked, the
  map flag, the status). The game's own ways of linking (an item's **Link**,
  or a shortcut) insert the link into the game's chat box, not into this
  window, so a link made that way goes to game chat unless it is cleared
  there. No supported way to route it to the window is known, and nothing is
  hooked for it; typing `<item>` in the window after linking it in game uses
  the item the game holds as linked.
- **Messages since login** (`ChannelHistory`). Each channel's last 500 lines,
  kept with the session, not the windows, so closing and opening a window
  loses nothing: messages from others, the user's own (as the server accepts
  them, the same event that shows them in chat, so nothing shows that wasn't
  sent, and nothing twice), notices about the channel, and the window's
  feedback. In memory only, nothing written to disk, like the game's own chat
  log (unless the player keeps a chat log, below). Emptied whenever a session
  stops or starts (logging out, another character or server; a reconnect
  keeps it), and what an old session still
  delivers is dropped (a generation number). A message delivered twice is
  held once. A channel's lines go when the user is no longer in it. The owner
  accepted showing only what came since login (2026-10-05). Messages sent
  while the user was away (logged out or disconnected) are added when they
  come back, after a dimmed line saying how many, each with the time it was
  sent (see [Message catch-up](#message-catch-up)).
- **Older lines** (`EarlierLines`), if the player keeps a chat log on this
  computer (see [Chat log on this computer](#chat-log-on-this-computer)):
  above a tab's lines since login, a page at a time.
- **Show in game chat.** Per channel, kept per character like its colour
  (`CharacterSettings.GameChatOff`), in the channel's ⋮ menu and a tab's
  right-click menu, with a green check while on and a red cross while off (a
  plain checkbox was hard to read, testers said); on unless turned off, as
  before. Off, the channel's messages and its notices go only to its history
  and windows (`GameChatChannels`), except warnings, which go to game chat
  too, so a
  warning is never kept from it. Turning it off while no window has the
  channel opens one, and a channel off game chat is never left shown nowhere:
  once no window has it (its last tab or window was closed, or none came back
  at login), it goes back to game chat, with one blue line there ("[sky] shows
  in game chat again, since no window shows it.", `GameChatChannels.ShownNowhere`).
  Unread counts don't change. Where a message goes is read before the session
  is checked, and a session that stops starts the history's next generation
  before its channel settings are let go, so a message caught in a logout is
  dropped rather than printed in game chat as if its channel weren't off.
- **Unread.** A tab selected in the window that has the focus reads its
  channel for the channel list too: `UnreadCounter` takes a viewer per window
  (the main window, and each channel window while it has the focus), and a
  channel any of them shows is read. A window without the focus doesn't read
  its tab, so what arrives meanwhile counts in the channel list.
- **Remembered** per character and server address
  (`CharacterSettings.ChannelWindows`; the rules in `ChannelWindowLayouts`):
  each window's tabs, their order (read off where ImGui shows the tabs once the
  mouse is let go after a drag), the selected tab, and its position and size
  in pixels (saved once a move or resize is over; not in ImGui's own settings
  file). What a hand-edited settings file holds where a window or its tabs
  should be counts as nothing, and a fault there is logged once without taking
  the UI down; no empty list is kept for a server. They open again at login
  once the channel list is in, without the channels the user is no longer in
  (a window left with none isn't opened), where they were, and without taking
  the focus from the game. They close without being forgotten at logout, when
  the session changes, and when the plugin unloads; only closing one forgets
  it. Windows are added to and taken from Dalamud's window system only between
  frames (`ChannelWindows.Update`), never while it draws.
- **Sticky mode is separate.** Talking in a channel from the chat box
  (`/lgc3` with no message) works as before, and windows don't change it. The
  window is the leak-proof way to talk in a channel: nothing the game, ChatTwo
  or another plugin does can send what is typed there to game chat.
- **Simple and advanced mode** apply as everywhere: the warnings and notices
  in the mode's words, switching at once; nothing technical in simple mode.

### Windows only, never game chat

Built at the owner's request (tester request, accepted 2026-10-07; built
2026-10-07). For heavy users who want the game's own chat kept clean: one
setting moves every channel into channel windows. The rules are in the core
library (`WindowsOnly`, `PendingWindows`) and unit tested; the plugin's
`SessionManager` asks for windows and `ChannelWindows` opens them. The checks
to make in game are in
[docs/testing/windows-only-checklist.md](testing/windows-only-checklist.md).

- **One setting, "Show LookingGlass only in windows"** ("Show LookingGlass
  messages only in windows" before 2026-10-09), in Settings under Chat
  (`Configuration.MessagesOnlyInWindows`), with a "?" saying what it does (see
  [The Settings window](#the-settings-window)); off by default, also for
  settings saved before it.
  While on, no channel's messages, and none of its information lines (someone
  was invited, joined, left or was removed; a catch-up's "N messages while you
  were away"), go to game chat, whatever each
  channel's "Show in game chat" says. They go to the channel's history and
  windows as always. The player's own messages come back from the server the
  same way as everyone else's, so they follow the same rule: no echo in game
  chat while it is on.
- **What still goes to game chat** (owner decisions):
  - *Warnings and critical lines*, light and dark red, as for a channel kept
    out of game chat, so none is ever hidden. Decided by the line's colour
    (`NoticeColours.ToneOf`), so a critical kind at the information level goes
    too (the per-channel rule still decides by level only).
  - *Lines no window could show*: about no channel in particular (the
    connection, your identity, "You left sky"), about a channel the player
    isn't in ("Bob invited you to sky"), or about a place from their old keys.
  - *Answers to what the player did in the game itself*, where they are
    looking: a `/lgc` command's usage, "Not connected" and "Not sent: …";
    sticky mode's own lines ("Now talking in", "Stopped talking in", its "Not
    sent", the ChatTwo and ExtraChat notes); a right-click invite's "Invited
    Bob@Lich to [sky]."; and `/lgdebug colours`. These are printed by the
    plugin directly, never through the channel rules, so the setting can't
    reach them. What a channel window's input box says ("Not sent: …") stays
    in the window, as before.
  - When in doubt about a line about a channel, it goes to windows only.
- **A channel no window shows opens in one.** A line kept out of game chat
  asks for a window (`PendingWindows`); if no channel window has the channel,
  a second setting (`Configuration.WindowOpening`, **New channels open** in
  Settings) chooses where it goes:
  - **As a tab** (the default; "Add it as a tab to the window used last"
    before 2026-10-09), in the window used last: the channel
    window that last had the focus, or, if it has been closed or none has had
    the focus this session, the one opened last. "Used last" is kept for the
    session only; nothing new is saved. The tab is added at the end, not
    selected, so the tab the player is reading stays where it was, and the new
    one shows its count of new messages (from others since login), as a tab
    reopened at login behind another does. A tab opened by an information line
    alone (someone joined) shows no count: only messages are counted.
  - **In a new window** ("Open a new window each time" before 2026-10-09): a
    new window with the channel as its only tab. Except for channels with
    messages from while the player was away (message catch-up, mostly at
    login): those share one new window, the first opening it and the others
    added as tabs behind it, for the rest of the session while it is open, so
    a login never opens a window for every channel.
  - With no window open, a new one either way; several channels at once share
    the window opened for the first (or get one each, with a new window each
    time). Each new window opens a step (30 pixels, scaled) below and right of
    the window opened this way before it, or if none was this session, of the
    window used last, or else near the top left of the screen
    (`WindowsOnly.NextPlace`). Past the bottom of the game's screen it goes back
    to the top, past the right back to the left, and it never lands off it, so
    no new window hides the one before it. The settings are saved once for all
    the windows opened in a frame.
  - Neither takes the keyboard from the game: a window opened this way doesn't
    take the focus (as windows reopened at login), and adding a tab doesn't
    bring its window to the front. Windows opened this way are remembered like
    any other.
  - The main window's channel pane doesn't count as showing a channel: only
    channel windows do.
  - Only channels in the complete channel list open (not a place from old
    keys); a channel left meanwhile is dropped. They wait for the windows to
    come back at login, so a channel already in a remembered window isn't
    opened twice.
  - Information lines that arrive at login before their channel is in the
    published channel list go to game chat, as no window could show them yet
    (the same rule as an invite to a channel the player isn't in). Accepted:
    a few lines at most, and nothing is hidden.
- **In combat, a cutscene or a loading screen** (Dalamud's `ICondition`:
  `InCombat`, `OccupiedInCutSceneEvent`, `WatchingCutscene`,
  `WatchingCutscene78`, `BetweenAreas`, `BetweenAreas51`), no window opens and
  no tab is added (owner decision: wait until after). The channels wait, once
  each and in the order they asked, and open once it is over. Nothing is lost
  meanwhile: the history keeps every line since login.
- **Each channel's "Show in game chat"** can't be changed while the setting is
  on: in the channel's ⋮ menu and a tab's right-click menu it shows a red cross
  (no channel shows in game chat now, whatever its own choice), greyed out,
  with one tooltip saying that only windows show messages now and to change
  it in Settings, under Chat; a tab's tooltip says the same. It is kept as it was.
  While the setting is on, a channel off game chat that no window shows isn't
  put back in game chat (the usual "shows in game chat again" rule): the next
  line for it opens a window anyway.
- **Turning it off** restores the usual behaviour at once (the next line goes
  where its channel's own setting says), and nothing about each channel's
  choice is lost. A channel still waiting for a window whose own setting shows
  it in game chat needs none and is dropped; one kept out of game chat on its
  own still gets its window. So does a channel off game chat on its own that no
  window shows (as the setting for opening says), so its choice is kept rather
  than undone by the "shows in game chat again" rule, which leaves channels
  waiting for a window alone. These wait for combat, a cutscene or a loading
  screen to end like any other, and in the meantime their lines are kept in
  the history, to show when the window opens: no line ends up shown nowhere.
  Turned off while logged out, that happens at the next login, once the
  channel list is in.

### Chat log on this computer

Built at the owner's request (decided 2026-10-06 and 2026-10-07). Testers asked
for it: with ExtraChat, losing messages to an untimely disconnect was common.
[Message catch-up](#message-catch-up) covers what was sent while a player was
away; this covers reading again what they saw, after a crash, a relog or the
next day. ChatTwo keeps its own log, but many players use the game's chat,
which keeps nothing, so LookingGlass offers it itself. The rules are in the
core library (`ChatLog`, `ChatLogStore`, `EarlierLines`, `ChatLogKeeper`,
`ChatLogWords`) and unit tested (`ChatLogTests`); the checks to make in game
are in [docs/testing/chat-log-checklist.md](testing/chat-log-checklist.md).

**The owner's decisions.**

- **Opt-in**, off by default: one setting, **Keep a chat log on this
  computer** (simple mode: **Keep chat history on this computer**, in a
  section of its own; its "?" says what it does), logs **every channel**, with
  no per-channel choice.
- **A size limit, not an age**: 50 MB by default, from 5 MB to 1 GB (the
  **Size limit** slider in Settings while it's on). When the log would pass it, the oldest messages
  go first. The limit applies to each character's log on each server.
- **Shown in channel windows**, above the lines since login. **No export.**
- **Nothing readable leaves the player's computer**: the log is never sent,
  uploaded or shared, and is encrypted on disk.

**What is kept.** Every line a channel's history (`ChannelHistory`) holds, as
it holds it, except two kinds: messages, others' and the player's own, live
and caught up, each with when it arrived, the sender's name, world and ID, the
signed time, its text and its links (as the message's content encoding, so
they come back through the same checks as a received message's, and show as
links again); and LookingGlass's information lines about the channel, in both
modes' words (joined, left, invited, set up LookingGlass again, the channel's
new name, "12 messages were sent while you were away"). Not kept: warnings
(light and dark red: they are about that moment, and were shown when it
happened; a warning about a channel shown again days later, out of context,
would mislead), and "Not sent" feedback (about what was typed then). The
history hands each line to its recorder inside its lock, so the log has them
in order; the recorder only queues it.

**Where.** One folder per character and server address,
`chatlog-<content ID>-<128 bits of the address's hash>`, beside the secrets
files in the plugin's config folder and named the same way, so another
character or server never reads or adds to it. "Delete my chat log" and the
size shown cover every folder.

**Format: segment files of encrypted records.** Chosen over SQLite with
encrypted rows: it needs no native library in the plugin, appends never
rewrite anything, and every byte on disk is either a fixed header or
ciphertext (SQLite would keep row IDs, timestamps and page structure in the
clear, and its own journal). A folder holds `chatlog.key` and numbered
segments (`0000000001.lgl`, …). A segment is an 8-byte header (`LGCL`, a
version) and records, each a 4-byte length, a 24-byte random nonce and the
line's bytes sealed with XChaCha20-Poly1305 under the log's key, with the
segment's number and the record's offset as associated data, so a record
can't be moved (to another place or another segment) or swapped unnoticed,
and every record has a nonce of its own. What is in the clear: the headers,
each record's 4-byte length, and the files' sizes and times. So someone with
the files can count the lines and tell roughly how long each is (a message
from a notice, a short line from a long one), and when they were logged; not
what they say, who said them, in which channel, or the channel's name.

**Encryption at rest.** Each log has its own random 256-bit key, kept in
`chatlog.key` protected exactly as the secrets file is (`LocalProtection`):
Windows DPAPI for the current user, or, where DPAPI is unavailable (Wine,
Proton), the same local key file (`local.key`), which guards against
accidentally sharing the files rather than a local attacker. The chat log's
key uses its own DPAPI entropy and associated data, so its protected bytes
can't stand in for the secrets file's. A log whose key can't be unlocked here
(copied from another computer or Windows account, its key file deleted or
damaged) is **unreadable**: nothing is added to it, nothing of it is changed,
and Settings says so and offers to delete it. A key file that can't be read
*just now* (in use, access denied) is not that: the log counts as failed, is
tried again with the next line, and the key is never replaced.

**Crash safety.** A record is appended in one write, and the writer hands
each batch to the operating system, so a crash of the game loses at most the
record being written. Opening a log cuts its last segment back to the end of
its last record that opens, so what is added afterwards can be read; only an
unreadable tail goes. A damaged record (its length field too) fails its check
and is skipped on its own: the reader looks for the next whole record after it,
byte by byte (a record only opens at its own place). A last segment without
its header is deleted. A segment is one of the log's only once its file holds
its header, and a number whose file is already there (left by a deletion that
failed) is skipped, so a file that couldn't be made, or wasn't deleted, never
stops logging.

**The size limit** counts every file in the folder. Segments are started once
one reaches a sixteenth of the limit (at least 4 KiB, at most 16 MiB, so one
is quick to read back), and whole oldest segments are deleted until a new
record fits. Lowering the limit deletes the oldest straight away.

**Never in the way.** Everything that touches the disk runs on one background
task per log (`ChatLog`), in order: recording a line only queues it, so the
game never waits on the disk. A failure (a full disk, a folder that can't be
made) is written once to the diagnostic log, with the kind of failure and the
file system's words, never what was said, who said it or a channel's name,
and it never keeps a line from being shown or reaches whoever recorded it.

**In channel windows.** A tab shows **Show older messages** above its lines
since login while the log is on; it, or scrolling up with the wheel at the
top, reads the 200 lines before (from before this session: this session's are
in the window already) on the log's task. They go above, under a dimmed
"Earlier: Tuesday 6 October 2026" line for each day and a dimmed "Since you
logged in" line before this session's, each line with its time and, if not
today, its day, drawn as lines since login are (links, notices in the mode's
words, the copy menu). The line at the top keeps its place on screen as older
lines arrive, and the tab doesn't jump to new lines while the player reads
old ones (**New messages** takes it back down). Once the start is reached:
"That's everything in your chat log for this channel." Opening a window
reads nothing. **A message is shown once**: one the session holds too (caught
up again, say) shows as the session's, and one the log holds twice (shown in
two sessions) once; so does an information line the session holds too (the
same time and words: the log turned off and on again in one session). Messages
from someone the player has blocked since aren't shown, as live ones aren't,
and show again if they are unblocked. Older lines never count as unread.
Windows show only the channels the player is in now.

**Lifecycle.**

- **Turning it on** keeps the current session's lines from then on (not those
  already shown) in this character's log for this server, and every session's
  after.
- **Turning it off** stops adding to the log at once (what was queued is
  still written), and asks, in plain words, whether to delete what was kept;
  Cancel keeps it. Windows no longer show older lines.
- **"Delete my chat log"** is in Settings whenever any log exists, on or off,
  with how much room they take. It deletes every character's log on every
  server, after a confirmation. While on, logging goes on afterwards in a new
  log with nothing older.
- **Leaving a channel** (or being removed, or a disband) keeps its lines in the
  log, the player's own record, until the size limit pushes them out or the
  log is deleted. No window shows a channel the player isn't in; rejoining the
  same channel shows them again.
- **Reset my identity** keeps the log: it is the player's own record, under
  its own key, not the identity's.
- **Another character or server** has its own log; logging out closes it.
- **Moving to a new server address** with the identity (see
  [Moving to a new server address](#moving-to-a-new-server-address)) moves
  each carried character's log with it, once its log has closed: the folder is
  renamed, so the log goes on at the new address. One the new address has
  already is left as it is (never merged or written over), and the old
  address's stays. Starting afresh at the new address moves nothing.

**Left for later.** A channel with more than 500 lines in one session (the
window's in-memory cap) shows the ones that fell out of memory only after the
next login. There is no search, and no per-channel choice or deletion.

### Context menu invites

Right-clicking a player in the game's own menus (a name in the chat log, the
party list, a target, the friend list, a linkshell's or Free Company's member
list, the party finder) or a name in ChatTwo's chat shows **Invite to
LookingGlass ▸**, a submenu of the channels the player can be invited to,
each as its tag in the channel's colour and its name (`[sky] Tea party`).
Picking one sends the invite, the same request as the channel's **Invite**
button (`ClientSession.InviteAsync`), and LookingGlass says how it went in
LookingGlass blue, with the tag in the channel's colour: "Invited Bob
Hatter@Lich to [sky]." or "Couldn't invite Bob Hatter@Lich to [sky]: " and
why, in the mode's words (`PlainMessages.MessageOf`, as the Invite button
shows it: what a server said comes without its error code in simple mode,
there as everywhere).
The invite runs off the game thread; the line is printed on it. Nothing is
logged about whom.

What is offered (`ContextInvites`, in the core, tested):

- **Channels.** Those where the user is a moderator or the admin under their
  current keys, by number, then by name, at most 24 (the game's menus hold 32
  lines in all). An old key's place has no rank, and a forgotten one isn't
  listed, so neither is offered.
- **Greyed out, with why.** A channel the player is already in, or invited to
  ("already a member", "already invited"), one whose name isn't known yet (an
  invite carries it: "not ready yet"), and every channel if the user blocked
  them ("you blocked them"). Showing these, rather than leaving them out,
  says why a channel is missing, which a shorter list wouldn't. Who is in a
  channel comes from its verified log, matched by name and world, ignoring
  case.
- **Nothing at all** (no menu item) while not connected and registered, for
  the user themselves, when no channel can be invited to, and for anything
  that isn't a player with a home world: a name that isn't a forename and a
  surname (NPCs, minions, retainers), a game object right-clicked in the
  world that isn't a player character (or isn't the one the menu names, by
  name and home world), or a home world that is missing or
  not a public world.
- **Worlds.** The world's name comes from the game's World sheet, as the
  server knows players by, so players from any world or data centre can be
  invited, as anywhere else in LookingGlass.

**The game's menus** come through Dalamud's `IContextMenu` (`OnMenuOpened`,
`MenuTargetDefault`: `TargetName`, `TargetHomeWorld`, `TargetContentId`,
`TargetObject`), with no hooks of LookingGlass's own. Only the default menu
type, and only from windows whose menus are about a player (`ChatLog`,
`_PartyList`, `PartyMemberList`, `FriendList`, `SocialList`, `ContactList`,
`FreeCompany`, `LinkShell`, `CrossWorldLinkshell`, `ContentMemberList`,
`BeginnerChatList`, `LookingForGroup`, the target bars, and the world itself),
since a menu about something else can still hold the last player's name. The
item has a boxed "L" in LookingGlass blue in front, as Dalamud asks of
plugins' items, and opens a Dalamud submenu; the channels' items are greyed
out with Dalamud's own `IsEnabled`.

**ChatTwo's menu** comes through its context menu IPC (its `ipc.md` and
`IpcManager.cs`, verified in its public source): `ChatTwo.Register` returns an
ID, `ChatTwo.Unregister` drops it, `ChatTwo.Invoke` (the ID, the message's
sender as a `PlayerPayload`, its content ID, the payload right-clicked, and the
sender's and the message's text) asks each registered plugin to draw its items
inside ChatTwo's **Integrations** submenu, and `ChatTwo.Available` says
ChatTwo (re)loaded, which forgets every ID, so LookingGlass registers again
(dropping any ID it still holds first, so the item never shows twice).
The item shows only on a name (the payload right-clicked is a `PlayerPayload`),
for that player, as an ImGui submenu with each channel in its colour.

### Local chat (friends only)

Built at the owner's request (decided 2026-10-05, built 2026-10-07, security
review fixes 2026-10-07): a `/say`-like chat for players who stand near each
other and both use the plugin. Nobody without the plugin sees it, and only
players on the sender's in-game friends list can read it. `/lgl <message>`
sends it; it shows in game chat tagged `[Local]`. The rules are in the core
library (`LocalChat`, `LocalChatWords`, `LocalCrypto`,
`ClientSession.SendLocalAsync`) and unit tested; the plugin's `LocalChatGame`
reads the game and `LocalSender` sends. The checks to make in game are in
[docs/testing/local-chat-checklist.md](testing/local-chat-checklist.md).

**The idea.** The sender's plugin picks the recipients and the receiving
plugin checks again, so the server can't add anyone and can't forge a
message:

- **The sender's plugin picks the recipients.** It takes the players near the
  sender in the game (the object table, within 20 yalms, about `/say` range),
  keeps those on the sender's friends list, and looks up their LookingGlass
  keys (the same lookup as inviting by name, using pinned keys). It encrypts
  the message to each of them, signs each copy with the sender's identity
  key, and asks the server to deliver the copies to those accounts.
- **The receiving plugin checks too.** It shows a message only if it opens,
  its signature is the sender's known key, the sender is on this player's
  friends list, and the sender's character is near them. FFXIV friendships are
  mutual, and both checks run in the players' own plugins.
- **The server holds no locations, but learns who was near.** It holds no
  zones, instances or rooms, and stores nothing; it only delivers sealed copies
  to the user IDs the sender named. But it isn't blind to where people are: the
  copies say which friends were near the sender when they sent, and the lookups
  before them (see *Looking them up*) say which friends were near, including
  friends who don't use LookingGlass.
- **A new server capability**, `local.v1`, with its own message types. It
  doesn't touch channels, the membership log or epochs, and an old client
  never sees it.

**Sending.** `/lgl <message>` (`/lgl` alone talks in local chat from then on,
see *Talking in local chat* below; a link alone is sent, as with `/lgc3`):

- **Who is near and a friend**, read in the frame the line was typed, on the
  game thread: every player in the object table (`IObjectTable.PlayerObjects`)
  within `LocalChat.SayRange`, 20 yalms of the player (straight-line distance),
  who is a friend. A player is a friend if the game marks them so (Dalamud's
  `StatusFlags.Friend` on the character) or the game's friends list
  (`InfoProxyFriendList`, FFXIVClientStructs) has them, by content ID or by name
  and home world; a friend request still waiting for an answer doesn't count.
  Closest first, each name and world once, at most 50 (`LocalChat.MaxRecipients`,
  and no more than the server allows).
- **Nobody to send to** is said plainly, in LookingGlass blue: nobody near
  enough; nobody near on the friends list; or, when only players not marked as
  friends are near and the friends list is empty, that the game may not have
  loaded it yet, and to open it once (Social menu, Friend List). The game may
  only fill the list once the Friends window has been opened in a session; the
  plugin can't tell an empty list from one not filled in yet, so it says "may".
- **Looking them up.** Each friend is looked up by name and home world with
  the lookup an invite uses (`LookupUser`), and trusted on first use and
  pinned as any lookup is (a change of keys is warned about). A lookup is
  reused for 10 minutes (`LookupMemory`), and so is the answer that nobody is
  registered by that name, so a friend who doesn't use LookingGlass isn't
  looked up again with every message. The lookups' rate limit applies (60 at
  once, then one a second); a friend who can't be looked up just now is left
  out of this message, and the player is told how many. The player themselves
  and anyone they blocked are left out. If none of them uses LookingGlass on
  this server, nothing is sent, and the player is told. **What this tells the
  server:** every `/lgl` looks up every friend near the sender by name (unless
  looked up in the last 10 minutes), so the server learns which friends were
  near the sender, and when, whether or not those friends use LookingGlass.
- **Links and text commands** work as in a channel message: the gate reads a
  `/lgl` line's links as it reads `/lgc`'s, and `<t>`, `<me>` and the like are
  replaced as the game would, as plain text.
- **Sealing.** The content (the same `Content` as a channel message, links
  and all) is encrypted once with XChaCha20-Poly1305 under a random 256-bit key
  made for this message alone; that key is sealed to each recipient's identity
  agreement key (an X25519 sealed box, as epoch keys are sealed to members),
  and each copy is signed with the sender's identity key over the sender, the
  recipient, the message ID, the time, a hash of the ciphertext, a commitment
  to the message's key and the sealed key. The design first said "encrypt the
  message separately to each recipient"; sealing one key per recipient instead
  keeps that property (each copy is for one recipient's identity key, and
  signed for them alone) at a fraction of the cost: a copy is about 170 bytes,
  so fifty copies of the longest message are about 10 KB, and even 200 fit
  within the 128 KiB frame, where fifty whole copies would not. A recipient
  learns nothing of the others: its copy's signature names only it. The
  associated data and contexts bind the sender, message ID and time, so nothing
  can be moved between messages, recipients or senders, and a recipient (who
  holds the message's key) can't make a copy for anyone else, as only the
  sender can sign one.
- **Key commitment.** As every copy of an epoch key carries a commitment to it,
  every local message carries one to its key (SHA-256 over its own domain, the
  sender, the message ID and the key; `key_commitment`), signed in every copy.
  A recipient whose key doesn't match it drops the message, so a sender can't
  give different friends different keys, or a ciphertext that opens to two
  messages under two keys. The signing domains are `v2`: the first version,
  without the commitment, was never released.
- **The sender's own line** is printed once the server takes it, as in a
  channel. The server never says who got a copy (see below), so the line
  doesn't mean anyone read it.

**Receiving.** The session opens a `LocalMessage` only from a server that
agreed to `local.v1` on this connection, never from the player themselves or
anyone they blocked, and only if:

- its copy opens with the player's identity key, its key matches the signed
  commitment, and it is signed for them by the key held for the sender. The
  server sends the sender's identity with each message; it is used only if no
  key is held for them yet (trust on first use, as for a lookup).
- **A local message never changes the keys held for anyone.** One under other
  keys than those held is dropped, and nothing else happens: the keys held
  for someone change only through a lookup (sending them a local message, or
  inviting them) or a channel, which warn as they always have. The security
  review found that taking the server's other keys here, with a warning, let a
  malicious server swap any pinned user's keys (and clear "compared" in
  advanced mode) just by sending a local message, so the next `/lgl` was
  sealed to its key.
- **A held sender under another name isn't shown.** If the sender's keys are
  held under another name or world than the server gives now (a rename or a
  world transfer, or a server passing one friend off as another, or as someone
  standing near), the message is shown under neither name.
- it is dated within 10 minutes of the player's clock, and isn't one already
  had: not in the seen-set channel messages use (apart from them), nor more
  than 2 minutes older than the newest shown from that sender, nor one of the
  newest shown (same time and ID). The newest times and IDs are kept with the
  channels' (`NewestMessageTimes`, under the key `local`, which no channel ID
  can be) and saved with them, so a replay after a restart is refused too. Only
  messages shown are recorded there, so strangers' messages (never shown)
  can't make the saved file grow.

Then the plugin, on the game thread, as the game shows things when it
arrives (`LocalChat.Judge`): the sender (by the name and home world the
session gives) must be within `LocalChat.ReceiveRange`, 30 yalms (a little
more than the sender's 20, as either may have moved while it travelled), and a
friend, as above. Only once it passes does the plugin ask the session
(`ClientSession.ConfirmLocalSender`), which records the message against
replays and pins a sender seen for the first time, so the keys of someone who
isn't near or isn't a friend are never kept. It refuses (the message isn't
shown) if it was shown already, if other keys were pinned for the sender
meanwhile (another first message, shown first), or if **another account is
held under the name it gives**: a malicious server could otherwise send from a
new account named as a held friend, and if that friend stood near, the message
would show as theirs (pinning would only have warned that the name now belongs
to another account). Nothing is pinned then. The same check covers a sender held with no
name (a channel's membership log can pin someone so, and then no rename could
be noticed): the name its message gives mustn't be another held account's,
and is held with their keys from then on. Anything that fails is dropped
silently: the diagnostic log counts drops by reason, never who or what.

**Hints, for a friend near.** A message under other keys than those held, from
a held sender under another name, or refused for a name held by another
account, never shows and changes nothing, but it isn't dropped without a word
if it may well be a friend's: the session passes it on without its content
(`LocalMessageUnchecked`: who the server says sent it, and why), and if, as
the game shows it, that sender is near and a friend, one information line (in
LookingGlass blue; `LocalHints`, `LocalChatWords.Unchecked`) says what may
have happened and what to do. Strangers, and anyone not near, get nothing.
The name in a hint is the server's word, so hints are remembered by name (one
per name a session, whichever account it came from) and capped at 5 a session.

- **Other keys:** "Bob sent you a local message that couldn't be checked: they
  may have set up LookingGlass again, or someone else may be using their name.
  Check with them over /tell before you trust it." A server can fake this
  (a held account, other keys, named as a friend standing near), so the hint
  never says the player will be warned of new keys, or that accepting them is
  the fix: a server swapping keys produces exactly that warning. The verification
  review found the first wording did, and that the lookup memory was cleared
  as the message arrived, priming the swap. Now nothing is forgotten on
  arrival; only once the plugin has judged the sender near and a friend is
  what was looked up for them forgotten (`ClientSession.ForgetLookupAfterHint`:
  the positive lookup only, never the answer that nobody is registered by a
  name), and only if the server named the account by the name held for it, so
  a faked hint naming someone else forgets nothing. Their next `/lgl` then
  looks them up afresh, which changes nothing unless their keys did.
- **Another name or world:** "...they may have changed their name or world.
  Talk to them with /lgl, or share a channel, to update it." (The message was
  signed by the keys held for that account, so it is theirs.)
- **A name held by another account:** that LookingGlass knows someone else by
  that name, and to check with them over /tell.

The other exception: if the sender was near but not marked as a friend and the
friends list is empty, one line a session says that a player near sent a
local message and to open the friends list once to see local messages from
friends.

**Shown in game chat** as a channel's message is (`ChatOutput.LocalMessage`):
the tag `[Local]`, then `<Name@World>` and the message, sanitised, with links
rebuilt from the player's own game data; the sender's name colour if they have
one. Its colour is a setting of its own (Settings, under Local chat,
**Colour**: the channel colour menu's swatches, **Default** and **Custom...**;
`LocalChatColourRow` and `LocalChatCustomColour`, none by default, also for
settings saved before; one for every character), used for the tag, or the
whole line as **Colour the whole line** says. It goes to
the chat channel chosen in Settings, like every LookingGlass line.

**Talking in local chat** (the owner's decisions, built 2026-10-08). `/lgl`
with no message talks in local chat as `/lgc3` with no message talks in a
channel (see [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc)):
from then on, plain text typed in the chat box or ChatTwo's main input is sent
as if typed after `/lgl`, by the same `LocalSender`, to the friends near the
player as they are when the line is typed, and never to game chat. The first
version left this out (`/lgl` alone explained itself), as sticky mode was
built around channels; it is now built into sticky mode rather than beside it:

- **One more channel, never a channel.** `StickyChannel` holds local chat as
  the channel ID `StickyChannel.LocalId` (`local`, which no channel ID can be:
  those are 32 hex digits). So starting, every way of leaving, where a line
  goes (`StickyRoute`, the short-command rule, ChatTwo's prefix and its typed
  text, lines run inside a reply), the chat box labels, the server info bar
  and ExtraChat's warning are the channels' own code. The ID never leaves
  sticky mode: `StickyMode` tags it `[Local]` (`LocalChat.Tag`, in local chat's
  colour, `Configuration.LocalChatColour`) without looking it up as a channel,
  and sends its lines with `LocalSender`, never `ChannelSender`, so nothing
  that holds channels (windows, unread counts, the chat log, slots, nicknames,
  colours, snapshots) is ever given it. Checking membership skips it, so it is
  never ended for not being in a channel.
- **What it shows.** "Now talking in [Local]." (with **Verbose channel
  messages** on, as for a channel), "LG [Local]" in the server info bar (its
  tooltip names the friends near rather than a channel) and "LookingGlass
  [Local]" in ChatTwo's input, both in local chat's colour, and `[Local]`
  where the game's chat input names its channel, uncoloured, as a channel's
  tag is there (`ChatInterop.SetChannelLabel` writes plain text). The ChatTwo
  note is the channels' (once ever, whichever is talked in first). No channel
  may be nicknamed "Local" (any case; `ChannelNicknames.Reserved`), so no
  channel's tag is `[Local]`: one saved so before is tagged by its number.
- **Starting needs what `/lgl` needs, not membership.** In order: the hooks
  (else "Talking in local chat without /lgl doesn't work in this game version
  yet"); the privacy notice accepted (else, as the first `/lgl <message>`,
  nothing starts: game chat says local chat first asks to accept, and the
  privacy window opens through the same callback); connected and logged in
  ("Can't switch to [Local]: not connected to LookingGlass."); a server that
  offers local chat (`LocalChatWords.NotOnThisServer`); then, as for a
  channel, the game's channel readable and ChatTwo not on a tell. The channel
  list doesn't matter, loaded or not. Once the notice is accepted, game chat
  says what to type again for what asked (`LocalChatWords.PrivacyAcceptedFor`):
  after `/lgl <message>`, to send it again with `/lgl <message>`; after `/lgl`
  alone, to type `/lgl` again to talk in local chat; while already talking in
  local chat, only to type the message again, never `/lgl`; from Settings,
  nothing.
- **Switching and ending** are a channel's: a channel command on its own
  (`/s`) ends it, `/lgc3` alone moves to that channel and `/lgl` alone moves
  back (or, already in local chat, says "Now talking in [Local]." again),
  and a channel switch in the game's UI, a one-off switch, a logout,
  **Disconnect**, a new session and unloading end it with the channels' lines
  ("Stopped talking in [Local]: disconnected."). `/lgl <message>` while talking
  in a channel sends once and the channel goes on; `/lgc3 <message>` while
  talking in local chat sends to the channel once and local chat goes on (both
  are commands, which go to the game, so their handlers run). Which sender a
  line goes to, and the tag and colour, are decided in one place,
  `StickyTarget`, so local chat's ID can't reach `ChannelSender`.
- **Each line is checked as `/lgl <message>` is**, and every refusal says it
  didn't go to game chat either, as for a channel (`LocalChatWords.Refusal`):
  "Not sent to [Local] or game chat: nobody is near enough to hear you…", and
  so for no friend near, the friends list not loaded, none of them using
  LookingGlass, not being able to see who is near, not connected, an
  unreadable link, a failed send and a server that doesn't offer local chat.
  A line with nothing to send says "…: nothing in it can be sent.". A server
  that stops offering local chat after a reconnect doesn't end it: each line
  says local chat isn't available, and nothing reaches game chat.
- **The privacy notice withdrawn ends it** (reviewed 2026-10-08; Settings, or
  the privacy window's **Withdraw**). The notice is read once a frame with
  the rest of what sticky mode checks, and withdrawn it ends
  (`StickyEnd.PrivacyWithdrawn`): "Stopped talking in [Local]: you withdrew
  the privacy notice.", always said, as the player chose to withdraw, not to
  switch channel. The first version kept talking and refused each line, which
  opened the notice again for every line. As a backstop, a line typed before
  the frame's check sees it (`StickySendTo.LocalNotAccepted`) is refused
  ("Not sent to [Local] or game chat: local chat first asks you to accept…"),
  kept from game chat, ends talking in local chat at once and opens the notice:
  once, as nothing is talked in after it. Nothing is looked up meanwhile.
- **How to use it stays findable.** `/lgl` alone no longer prints the usage,
  so the usage (`LocalChatWords.Usage`: Dalamud's command help; Settings,
  under Local chat, says it more briefly in the "?" of **Colour**) says both
  forms and how to stop, and the first time ever that talking in local chat
  starts, one more line says where typing goes and to type `/s` (or another
  channel) on its own to stop (a saved setting, `LocalChatTalkNoteShown`),
  whatever **Say when I start or stop talking in a channel** says.
- **The diagnostic log** is the channels' (`[sticky]` lines), tagged
  `[Local]`; `/lgl` is a known command in it. Never what was typed, nor who is
  near.

The checks to make in game are in
[docs/testing/local-chat-checklist.md](testing/local-chat-checklist.md),
*Talking in local chat*.

**What it doesn't do (choices made when building it):**

- **Not in channel windows, and always in game chat.** It isn't a channel, so
  no channel window shows it, and **Show LookingGlass messages only in
  windows** doesn't move it: like the other lines no window could show, it
  stays in game chat (the setting's tooltip says so). There is no "Show in
  game chat" for it. A tab for local chat could come later.
- **Not kept.** Not in the chat log on this computer, not caught up (the
  server stores nothing), not counted as unread.

**The server** (`RequestHandler.SendLocalMessage`) agrees to `local.v1` only
if the client offers it and local chat isn't turned off, and only such a
connection may send one or is sent one. It checks the message ID (16 bytes),
the key commitment (32 bytes), the ciphertext (at most
`Limits.max_message_bytes`, as a channel message), at least one and at most
`Limits:MaxLocalRecipients` copies (50; 0 turns local chat off; at most 200,
so a request fits in a frame), each recipient named once and never the sender,
and every copy's signature against the sender's registered key, hashing the
ciphertext once (garbage, or a copy readdressed to someone else, is refused
before anything is passed on). Local messages are rate limited per sender as
channel messages are (`Limits:LocalMessageBurst`, 5 at once, then one every
`Limits:LocalMessageIntervalSeconds`, 1), and per recipient, by everyone
together, so many senders can't flood one person
(`Limits:LocalMessagesReceivedBurst`, 120 at once, then one every
`Limits:LocalMessagesReceivedIntervalSeconds`, 1), and per sender and
recipient, checked first and smaller, so a couple of accounts can't use that up
and silence someone's friends (`Limits:LocalMessagesBetweenBurst`, 30 at once,
then one every `Limits:LocalMessagesBetweenIntervalSeconds`, 2, enough for a
busy roleplay scene; at most 100,000 pairs remembered, the least recently
used forgotten past that; the server
doesn't start unless they are smaller and slower). Both are spent only by
copies that would reach them. Each copy goes to its recipient if they are online on a
connection that agreed, with the sender's identity; nothing is stored, and a
recipient who is offline never gets it. A copy past the recipient's limit, or
for a connection whose queue is half full or more, is dropped rather than
queued: a slow connection loses local messages, never the connection itself
(and local messages never fill the room channel events need). The answer is
the same whoever got a copy, so naming user IDs can't be used to see who is
online (presence is otherwise only shown to people who share a channel). It
logs, at debug level, the sender's user ID and how many copies there were,
were delivered and were over their recipient's limit: never names, never
content.

What it costs, all accepted by the owner:

- **No meeting strangers.** It is chat among friends who use the plugin. An
  open, signed-only local chat is out of scope.
- **Metadata.** The server sees who sent to whom and when, which implies those
  players were together; and, from the lookups, which of the sender's friends
  were near them and when, including friends who don't use LookingGlass (see
  *What the lookups tell the server* below).
- **The friends list must be loaded.** The game may only fill it in once the
  Friends window has been opened in a session. If so, the plugin says plainly
  to open it once. Check in game.
- **Crowds.** One sealed key per recipient is fine for a crowd of friends.
  Recipients per message are capped (50), and messages rate limited like
  channel messages.

And, found while building it:

- **Lookups show who uses LookingGlass.** Looking a friend up by name tells the
  sender whether that friend is registered on the server, as an invite by name
  always has.
- **Keys are trusted on first use**, from the server, as for invites: a server
  could hand out its own key for someone neither side has seen before. Once
  held, a local message can't change them.
- **A friend who registered again** with new keys isn't shown in local chat
  until this player's keys for them are refreshed by a lookup (sending them a
  `/lgl` after the 10 minutes a lookup is reused, or inviting them) or a
  channel, with the usual warning; their messages meanwhile aren't shown, but
  the player is told once a session (see *Hints, for a friend near*).

**What the lookups tell the server, and asking first.** Every `/lgl` looks
up the friends near the sender (each at most once in 10 minutes), so the
server learns who was near whom, and when, even for friends who don't use
LookingGlass. The owner chose to keep this (2026-10-08): for friends who use
LookingGlass the copies already say as much, so the lookups add only friends
who don't, at most once in 10 minutes each, and only while the player uses
local chat. Looking up the whole friends list once a session instead was
turned down: it would hand the server every friend's name each session, use
up the lookups invites need, and miss friends who join mid-session.

Instead, the player is told, and asked: the first `/lgl` sends nothing and
looks nobody up. It opens a window (`LocalChatPrivacyWindow`, words in
`LocalChatWords.PrivacyNotice`) saying which names the server learns (friends
near, those who don't use LookingGlass too, and who was sent to when), that it
never sees what is said, that LookingGlass's server keeps no record of the
lookups (another operator could change that), and that receiving needs none
of it, with **Accept and use local chat** and **Not now**. The message typed
isn't kept: once accepted, game chat says to send it again. The choice is one
setting for every character (`LocalChatPrivacyAccepted`, off by default, also
for settings saved before it), shown in Settings under Local chat as
**Privacy notice accepted** (or **not accepted yet**), with a "?", **Read it**
(the window) and **Withdraw** (then `/lgl` asks again, and talking in local
chat ends; the window has a **Withdraw** too, once accepted).

**What is checked in game** (the checklist has it): that `/lgl` is free (no
game command and no common plugin uses it); that 20 yalms is about `/say`'s
reach; that `StatusFlags.Friend` is set for friends near the player before the
friends list is loaded (if so, a friend near is found without opening it);
that `InfoProxyFriendList` holds nobody until the Friends window is opened
(and what it holds while only partly loaded), and its content IDs and home
worlds match the characters'; and that the names and home worlds the game
shows match what the server has (the Lodestone's).

### Simple and advanced mode

Most players don't want to think about keys, so the plugin starts in **simple
mode**. **Advanced mode** (a setting, under "Your identity") shows everything
described in this document: fingerprints, **Compare fingerprints** and **Mark
verified**, key numbers, and the technical wording of every warning.

**Simple mode never hides a warning.** It only says it differently: what
happened, in everyday words, and what to do, nearly always "check with them
over /tell". Someone's key changing without explanation becomes "Bob set up
LookingGlass again (new computer or reset), or someone else may be using
their name"; a key recovered entry becomes "Bob set up LookingGlass again (new
computer or reset)"; a fork or a hidden membership change becomes "the server
is showing different member lists". A member with a warning still shows the
warning icon, and one with a key recovered entry the circling arrow. **It's
really them** clears either, for the keys shown when the user opened the
check, but isn't a comparison: advanced mode still shows those keys as not
compared. Otherwise a member's icon only shows whether they're online.

How it works:

- Everything the core tells the user carries both wordings (`Wording`:
  `Technical` and `Plain`) and a `NoticeKind`. `PlainMessages` holds them all.
  Notices (`SessionNotice.TextFor`), snapshot texts (`StatusFor`,
  `WarningFor`, `AddressNotListedFor`) and errors (`PlainMessages.Failure`,
  `MessageOf`) give the plugin both, and it picks one when it shows them. So
  switching modes takes effect at once, and a missing plain wording falls back
  to the technical one rather than to nothing.
- Tests check that every kind has a plain wording without jargon (a list of
  banned words: key, fingerprint, epoch, rekey, fork, log, pinned, signature,
  encrypted, and the like), and that every notice raised anywhere in the test
  suite is shown in both modes.
- The debug window (`/lgdebug`) always shows the technical details.

### The Settings window

Opened from the main window's gear, or Dalamud's plugin settings button
(`SettingsWindow`). Redesigned at the owner's request (approved 2026-10-09):
testers skimmed past the long tooltips and dimmed paragraphs under each
setting. Now every setting has a **short label**, and only a setting its label
doesn't explain has a small round **"?"** right after the label (or after the
checkbox or button). Clicking the "?" opens a small bubble beside it, a few
sentences about 24 em wide, to its right or, with no room on the screen
there, to its left. It isn't a hover tooltip and doesn't push the settings
below it down. A click anywhere else (the "?" too) or Escape closes it.

The sections, in order, with the settings that have a "?" marked (?):

- **Server**: **Server address** (?) with **Apply**, the "Not saved yet"
  warning and the line while the address change is checked (they are state,
  not explanations); **Connect automatically**, with **Connect now** or
  **Disconnect** at the right of the same line if it fits.
- **Chat**: **Show messages in** (?) the game's chat channel; **Colour the
  whole line**; **Use nicknames in tags**; **Say when I start or stop talking
  in a channel** (`VerboseChannelMessages`); **Show LookingGlass only in
  windows** (?), and under it **New channels open** **As a tab** / **In a new
  window**, greyed out while it is off.
- **Local chat**: **Colour** (?, about `/lgl`), the swatch opening the colour
  menu; **Privacy notice accepted** (or **not accepted yet**) (?), with **Read
  it** (the privacy window) and, once accepted, **Withdraw**, each beside what
  is before it if it fits, else under it.
- **Chat history** ("Chat log" in advanced mode): **Keep chat history on this
  computer** (?, naming the protection in advanced mode), **Size limit**, how
  much room it takes, and **Delete my chat history**.
- **Your identity**: **Advanced mode** (?), the fingerprint (advanced mode
  only), **Reset my identity...** (?), and the backup offer if there is one.
- **Blocked users**: the list, or "Nobody blocked. Block someone from a
  member's menu in a channel."

The words are in the core library, so they are tested like every other: the
labels and each "?"'s words in `SettingsWords` (by `SettingHelp`, in both
modes' words), windows only's in `WindowsOnly`, the chat history's in
`ChatLogWords`. Tests (`SettingsWordsTests`) check that every "?" has words in
both modes, at most 180 characters and three sentences, plain in simple mode
(Advanced mode's own "?" is the one exception: it names what advanced mode
shows), and that labels are short and plain. The plugin draws a label and its
"?" with `Widgets.Label` and `Widgets.Help`, for every "?". Tooltips stay only
where they say a state (the local chat colour swatch: default or not). The
checks to make in game are in
[docs/testing/settings-checklist.md](testing/settings-checklist.md).

## Server design

The server is one process with an embedded database, built around three rules:
no lock held across an await, one authorization function for every action, and
one transaction for every multi-step change.

- **Concurrency.** One task per connection, with a bounded outbound queue. A
  full queue disconnects that client rather than blocking senders.
- **Authorization.** One rank table decides every request (see
  [Ranks](#ranks)). A place under keys the account no longer has may only read
  the log. A place just moved to new keys isn't asked to rekey until a rekey
  gives it the channel's key, unless nobody else holds it.
- **Storage.** SQLite in WAL mode. Conditional updates (on epoch and rank)
  guard against races. The schema is upgraded in place at startup; the
  current schema version is 10 (bans and flags; 9 brought stored messages for
  catch-up).
- **Stored messages.** Kept as relayed, numbered per channel, swept at startup
  and every ten minutes (see [Message catch-up](#message-catch-up)).
- **Memory.** Nothing kept per address, user or name grows without bound.
  Per-address counters drop addresses whose window has passed, and keep at
  most 100,000 (past that, the least recently seen are forgotten and start
  afresh). Per-user and per-pair rate limits drop keys unused for an hour
  (or, for slower settings, for as long as their allowance takes to refill).
  Characters found on the Lodestone are cached for an hour (searches that find
  nobody aren't cached); the cache is swept every ten minutes, and at most
  10,000 are kept. Refusals counted towards flagging keep at most 100,000
  accounts and addresses (see [Noticing](#noticing)).
- **Errors.** Typed errors map to protocol error codes.
- **Addresses.** The server refuses to start outside Development without
  `PublicUrls`, and with a `ChallengeMinutes` outside 1 to 60.

## Abuse limits

| Limit | Value | Why |
| --- | --- | --- |
| Message ciphertext size | 4 KiB | The game's chat input holds about 500 characters |
| Frame size (any request) | 128 KiB | Bounds rekey bundles and list responses; a rekey for 500 members is about 100 KB |
| Messages per user | 5 per second burst, 1 per second sustained | Stops floods without affecting normal chat |
| Rekeys per user | 5 burst, 1 every 2 seconds | Each rekey costs every member's client work |
| Members per channel | 500, counting pending invites | Keeps rekey bundles small |
| Channels per user | 50 | Bounds login and list cost |
| Pending invites per channel | 50 | Stops invite spam |
| Pending invites per user | 50 (as many as the channels they can be in), at most 25 of them from any one inviter | Stops one person being flooded, or one inviter filling them all; operator settings |
| Invites sent per user | 60 at once, then 1 every 5 seconds | Stops one person spamming many; operator settings |
| Invites received per user | 30 at once, then 1 every 10 seconds | Stops many inviters together flooding one person; operator settings |
| Lookups by name per user | 60 at once, then 1 a second; the plugin reuses one for 10 minutes (until an invite with it fails) | Each invite by name starts with one, so inviting a friend to many channels isn't stopped here first; bounds enumerating players; operator settings |
| Invites from one person to another | 20 at once, then 1 a minute; checked first | Someone can invite a friend to all their channels in one go, but one inviter (blocked or not) can't use up someone's invites; operator settings |
| Registration attempts | 10 per hour per IP (IPv6 per /56; a household's players and alts); verify once per 10 seconds, 10 per challenge (3 more the Lodestone couldn't answer) | Protects the Lodestone and the challenge flow; operator setting |
| Registrations whose character the Lodestone doesn't list (or can't be asked about) | Cost no registration; each Lodestone request they made counts, 20 per hour per IP (IPv6 per /56), past which nothing more is looked up for that address; a search reads at most 2 pages; names the game wouldn't allow are refused without asking | Someone fixing a typo or a private profile isn't locked out, while names that aren't there can't fill the server-wide Lodestone queue; operator settings |
| Lodestone requests (server-wide) | 1 every 2 seconds, cached | Avoids being blocked by the Lodestone |
| Connections per IP (IPv6 per /56) | 20 open, 60 new a minute, 4 not logged in; one not logged in closes after 3 minutes (registering: when its code expires); no answer to a ping within 60 seconds closes one | Bounds idle, unauthenticated and churning load |
| Requests per connection | 200 at once, then 20 a second; faster ones are slowed, not refused | Bounds the work one connection makes |
| Connections in all | 10,000; at the cap the oldest not logged in is closed for a new one (one registering only if all are, then from the address that would hold the most), and only when all have logged in is one refused (503) | Connections that never log in can't keep plugins out, nor someone crowding the server push out others' registrations first; about 2 GB at most |
| Outbound queue per connection | 256 events | A slow client is disconnected, not waited on |
| Stored messages (catch-up) | 7 days, 5,000 per channel; the oldest go first | Bounds the disk a channel can take (about 22 MB at worst); operator settings |
| Pages of stored messages | 200 messages or 96 KiB each; 100 pages per user at once, then 4 a second | A returning client asks once per channel; within the frame limit |
| Local messages | 50 recipients each (0 to 200, 0 turns local chat off); 5 per user at once, then 1 a second; 120 to one user at once, by everyone together, then 1 a second, and 30 from one sender to one user, then 1 every 2 seconds (past either, and to a connection whose queue is half full, dropped); the message as large as a channel message | Crowds of friends stay cheap and within the frame limit; floods are stopped as in channels; operator settings |
| Devices per user | 20 most recently used | Bounds stored logins |

Channel creation, renames, disbands, identity lookups and heavy reads have
their own per-user rate limits. The server doesn't know whom a user blocked
(their client declines those invites unseen), so the limits between one
inviter and one invitee are what stop a blocked inviter using up the
invitee's allowance. They are checked first, so an invite past them spends
nothing of the invitee's, and they are smaller and slower than the
invitee's (20 at once and 1 a minute, against 30 and 1 every 10 seconds;
the server doesn't start with settings that aren't), so one inviter always
leaves some for everyone else, as the 25 pending invites one inviter may
have leave 25 of the 50. Bringing channels over from elsewhere, someone
inviting the same friend to each of 20 channels in a row is never stopped
by these; the 21st waits about a minute. Several inviters together can
still use up someone's allowance, up to the per-user limits. An invite the server
refuses for its log entry (made before someone else's change landed, which
the client fetches and tries again after) gives back what it took from
every one of these. A refused invite says how long to wait ("You've sent a
lot of invites to Bob Hatter@Lich recently; try again in about a minute."),
or, at a cap on pending invites, that the invitee must answer some first;
and the server logs it, with the limit and the user IDs, at most once a
minute per inviter. Key login limits
are under [Key login](#key-login). Operators can change some of these (see
[server.md](server.md#settings)). Every refusal by a limit is also counted
towards flagging (see [Spotting abuse, and banning](#spotting-abuse-and-banning)).

## Spotting abuse, and banning

Rate limits stop one burst of abuse, but someone who keeps hitting them, hour
after hour, is unlikely to be doing so by accident. The server notices that,
tells the operator, and lets the operator ban them from connecting. The owner
made this a requirement before public release (2026-10-07); it was built the
same day, with the defaults below, which the owner confirmed (2026-10-08). How an operator
uses it is in [server.md](server.md#flags-and-bans).

### Noticing

- **What counts.** Every refusal by a limit: invites, messages, lookups,
  registrations, key logins, connections (too many new, open, or not logged
  in, from one address), and the other per-user limits. A refusal that isn't
  the client's doing doesn't count: the server-wide Lodestone queue is busy,
  the server is full, or a limit others filled (the invitee has been sent too
  many invites, by everyone together, or has as many waiting as they may; the
  channel has as many invites waiting as it may, unless the inviter sent most
  of them). The requests that read a lot share one budget, which a plugin
  reconnecting with many channels spends across several kinds of request, so
  they are one limit (`ReadBudget`), not one per kind. It counts for the account, if the connection is logged in
  (never an account a request only names, as a key login does), and for the
  address: an IPv4 address, and an IPv6 /64 and its /56 each.
- **Not the proxy's address.** Refusals from this machine's own address
  (loopback), the unspecified address, or a trusted proxy's mean the proxy
  isn't passing the client's address on, so every player seems to come from
  there: that address is never flagged or blocked, and the warning in the log
  says the forwarded client address is missing instead of suggesting a ban.
  The accounts are still counted.
- **Flagged.** Over a sliding window of 60 minutes, an account or address
  refused in at least 30 different minutes, or by at least 4 different limits
  within 10 minutes, is flagged: one warning in the server's log (the limits'
  names and the user ID or address, never a name or anything said), and an
  entry in the database that `--bans` lists. Being refused in most minutes of
  an hour is what a script or a determined person does. A household behind
  one address, or a plugin with a bug, is refused now and then: a plugin
  retrying a login the server lost is refused every third minute at most.
  Many different limits at once is someone trying them out.
- **Expiry.** A flag expires by itself 24 hours after the last refusal. A
  server started again meanwhile doesn't announce one still in force again.
- **Memory.** At most 100,000 accounts and addresses are counted at once, each
  with a count per minute of the window and when each limit last refused it;
  past that the least recently refused are forgotten (their flags stay in the
  database). The thresholds, the window, the expiry and the cap are operator
  settings.

### Banning is the operator's decision

- **No automatic permanent ban.** A ban is made from the server's command
  line (the server runs as a service, and has no admin interface yet), safely
  while the service runs, as `--backup` is: `--ban <name@world | user ID |
  address or prefix> [--days N] [--reason "..."]`, `--unban <the same>`, and
  `--bans`, which lists the bans in force, the flags, and the bans lifted or
  ended lately.
- **Kept in the database**, so bans survive restarts and backups (restoring
  an older backup brings back its bans, and loses later ones).
- **Picked up while running.** The server reads the bans every 30 seconds
  (and at once when it starts), so one made from the command line applies
  within that, without a restart: it closes the connections it covers, and
  refuses them from then on.
- **On the character, not the keys.** A ban on an account is on the
  character's user ID (its Lodestone ID), so registering again with new keys
  doesn't get round it, and a character can be banned before it ever
  registers (by its ID). A ban on an address covers an IPv4 address or
  network (a /16 at the widest), or an IPv6 prefix from a /64 (an address
  alone stands for its /64, as one client usually has a whole /64) to a /32.
  Never the server's own or its proxy's address, nor a prefix holding one,
  unless the operator adds `--force`: that would shut out every player.
- **An automatic temporary block**, on by default (the owner, 2026-10-08: no
  player is refused that often): an address refused 1,000 times within the
  window is blocked for 15 minutes (0 turns it off), to blunt a flood until
  the operator looks. Only addresses,
  never accounts or the proxy's address, and never over a ban already
  covering the address. Its connections are refused at once (HTTP 429), before
  any WebSocket is opened, rather than let in to be told why.

### What a ban does

- **No way in.** A banned account can't sign in: its saved logins and a key
  login are refused (no new login is made), and so is registering the
  character again, with any keys. A banned address can't connect: the first
  request on a connection from it is refused. Each refusal says it is a
  block, with the operator's reason if they gave one and when it ends, and the
  server then closes the connection. Only someone who proves they are the
  character (its login, its key, or the Lodestone) learns of its ban: asking
  for a key login challenge, which anyone can do for any account, tells
  nothing.
- **Told plainly.** The plugin tells the player once that the server's
  operator has blocked them (or their internet address, which others may
  share), why if a reason was given, and until when; shows it as the
  connection's status; and tries again only every 5 minutes (or once the block
  ends, if sooner, but never sooner than 30 seconds, in case its clock is
  ahead; **Try again now** at once), not every 30 seconds.
- **Older plugins** show the server's message ("This server's operator has
  blocked this character, so you can't use it. The reason they gave: ...") as
  a failed connection, and reconnect as after any failure.
- **Places stay.** A banned player's places in channels stay (admins can
  remove them as usual); while banned they can't send or receive, as they
  can't connect, and the other members see them offline. Admins aren't told:
  bans are the operator's matter.
- **History.** A ban lifted or ended is kept 90 days (listed by `--bans`),
  then deleted.

### Chosen defaults (confirmed by the owner, 2026-10-08)

- Window 60 minutes; flagged if refused by limits in at least 30 different
  minutes of it, or by at least 4 different limits within 10 minutes.
- Flags expire after 24 hours without new refusals.
- Channel admins aren't told a member was banned; members just see them
  offline.
- Ban history: a lifted or ended ban is kept 90 days, then deleted.
- Flags are listed by `--bans`, with the bans, rather than by a separate
  `--flags`.
- The automatic temporary block is on by default: an address refused 1,000
  times within the window is blocked for 15 minutes (the owner turned it on,
  2026-10-08).
- A blocked plugin tries again every 5 minutes, or once the block ends if
  that is sooner (but at least 30 seconds apart).
- Address bans cover an IPv4 /16 to /32, or an IPv6 /32 to /64, and never the
  server's own or its proxy's address without `--force`.
- Limits others filled (the invitee's, the channel's pending invites) don't
  count towards flagging the one refused; a channel's pending invites do when
  the one refused sent most of them.

## Operations

- The server runs on Linux (x64 and ARM64) and Windows (.NET 10). A release
  is a self-contained package, so the machine needs no .NET.
- It listens on localhost by default, behind a TLS reverse proxy, and trusts
  `X-Forwarded-For` only from proxies on the same machine or configured ones.
- Outside Development it refuses to start without `PublicUrls`, or with debug
  accounts or the echo bot on (unless told it is meant), and warns about any
  listed address that isn't `wss://` with a fully qualified name. One line at
  startup says how it is set up.
- `/health` says only that it is up, and its version.
- The database is one SQLite file in WAL mode. The server only checkpoints
  PASSIVE, so Litestream can replicate it; `LookingGlass.Server --backup`
  makes an online backup for hosts without it.
- On SIGTERM it closes every connection (clients reconnect later), lets
  requests finish and checkpoints the database. Every change is one
  transaction, so a crash leaves the database whole.
- It never logs messages, channel names, tokens, keys or registration codes;
  client addresses only where abuse handling needs them. It keeps relayed
  messages, encrypted, for catch-up (7 days by default), and so do its backups.
- The first tester server runs as a systemd service on a Linux machine behind
  Tailscale Funnel. The public one is an ARM64 cloud machine, also behind
  Funnel, with its database replicated by Litestream.

How to build, configure, deploy, back up and restore a server is in
[server.md](server.md).

## Testing and debug tooling

- **Debug accounts.** With `Dev:AllowDebugAccounts` on, characters on the fake
  world "Debug" register without the Lodestone. Never on a public server.
- **Echo bot.** A headless client that accepts invites, takes part in rekeys
  and echoes messages. It runs inside the server (`Dev:HostEchoBot`) or
  through `lgdev bot`.
- **`lgdev smoke`.** Checks a server end to end with a throwaway debug user
  and the echo bot.
- **`/lgdebug`.** Connection state, protocol trace, notices, and tools to ping,
  force a rekey, or simulate an incoming message.
- **Automated tests.** Cryptography, the policy table, the membership log and
  its rules, key login, recovery, identity resets, server moves, secrets
  files, limits, message catch-up (storage, sweeps, who may fetch what, paging,
  and a server that repeats, reorders or forges what it sends back), flags and
  bans (the thresholds, the commands, and what a ban refuses),
  end-to-end flows, local chat (who gets it, what the server checks and what
  a receiver shows), keys' maximum age (with a moved clock: a week is too long
  to wait in game), and malicious-server and malicious-member suites that
  inject forged and replayed events.

Acceptance tests for the membership log:

1. A server-inserted ghost never receives a key.
2. A former member's invite is rejected.
3. A non-moderator's invite is rejected.
4. A hidden removal is detected by the remover.
5. A forked log is reported.
6. All earlier malicious-server tests still pass.
7. A key recovered entry that doesn't check out is refused (someone not in the
   channel, keys their place isn't under, keys they or someone else already
   have, not signed by the new keys). One that does is shown to every member.

## Migration from ExtraChat

LookingGlass uses a new server and protocol, so users register once, and
channels are rebuilt through a planned import wizard. It has its own internal
name and `/lgc` commands, so it can be installed beside ExtraChat.

## Planned features

### Garbled speech with GagSpeak

Status: not possible at this time; nothing built, and no request made. The
owner decided (2026-10-06) not to ask GagSpeak for it while GagSpeak's notes
say IPC isn't a focus and it is busy with features and fixes; revisit if
GagSpeak adds a garbling IPC. Suggested by the owner (2026-10-06): an option in a channel's options, shown only while GagSpeak is
loaded and set per channel, to garble the player's own speech there as
GagSpeak garbles it in the game channels its user picks (LookingGlass's
messages appear in a chat channel GagSpeak doesn't offer, such as Debug, and
are sent from a copy of the line GagSpeak may not have rewritten).

**What GagSpeak offers today** (its public source,
github.com/Project-GagSpeak/client, Apache-2.0, read for its behaviour and
public IPC only; version 2.2.2.1, commit `f82b6926`, 2026-09-15). Its only IPC
provider (`Interop/Ipc/IpcProvider.cs`, `GagSpeakApiVersion = 2`) has six
gates:

| Gate | Type | What it is |
|------|------|------------|
| `GagSpeak.GetApiVersion` | `ICallGateProvider<int>` | returns 2 |
| `GagSpeak.Ready` | `ICallGateProvider<object>` | sent when it has started |
| `GagSpeak.Disposing` | `ICallGateProvider<object>` | sent when it stops |
| `GagSpeak.PairRendered` | `ICallGateProvider<nint, object>` | a paired player's game object came into view |
| `GagSpeak.PairUnrendered` | `ICallGateProvider<nint, object>` | that player went out of view |
| `GagSpeak.GetAllRendered` | `ICallGateProvider<List<nint>>` | the paired players in view |

None garbles a text, and none says whether the player is gagged. Its
separate API repository (github.com/Project-GagSpeak/api) holds its server's
contract only, with no Dalamud IPC, and there is no package on NuGet. The
garbler itself is internal (`MufflerService.GarbleMessage`), run from its own
hook on the game's chat input (`ProcessChatInput`, by its own signature) when
its garbler is on, a gag is applied, and the line is for a channel its user
allows. Its manifest's internal name is `ProjectGagSpeak`.

**So it isn't viable now**, without hooking GagSpeak or copying its garbler,
which LookingGlass won't do: the garbling is GagSpeak's, follows its user's
gags and settings, and changes with it. LookingGlass waits for a supported
way.

**The IPC to ask GagSpeak for** (a proposal; names are GagSpeak's to choose):

- `GagSpeak.GarbleText`, `ICallGateProvider<string, string>`: the text,
  garbled for the player's gags now exactly as GagSpeak would garble a chat
  line it allows (with its own length limit; the text unchanged when no gag
  is applied or its garbler is off). Called on the game thread, synchronously,
  for each piece of typed text.
- `GagSpeak.IsGarbling`, `ICallGateProvider<bool>` (optional): whether a gag is
  applied and its garbler is on, so the option can say so.
- The API version raised, so LookingGlass can tell the gate is there.

**How LookingGlass would use it.** A per-channel setting, **Garble my speech
with GagSpeak** (kept per character in its settings, like a channel's colour
and nickname, and never sent anywhere), in the channel's menu, shown only
while GagSpeak is loaded (`ProjectGagSpeak` in Dalamud's loaded plugins) and
its version has the gate. While on, the sender passes the typed text through
the gate before the message is encrypted, only the typed pieces, never a link
or what a text command stood for (the core library cuts the line into pieces
already, `TextCommands.Split`), so links keep their places. If the gate fails,
or GagSpeak is gone, the message is sent as typed (it is cosmetic), with a
warning in the log that never holds the text. The same words in simple and
advanced mode.

### Alerts to a Discord channel

Status: planned, not started (owner, 2026-10-08), for when the server is
public and the plugin has a Discord with an admin channel. Flags and automatic
blocks (see [Spotting abuse, and banning](#spotting-abuse-and-banning)) today
show only in the log and in `--bans`, so the operator has to remember to look.

- **A webhook, set by the operator.** One setting,
  `LookingGlass:Alerts:DiscordWebhookUrl` (empty: no alerts, the default).
  The URL is a secret (anyone holding it can post in the channel), so it goes
  in a root-only drop-in or `EnvironmentFile=`, never in `appsettings.json` or
  the repository, and is never logged; the startup line says only "Discord
  alerts on". A `--alert-test` command posts one test message, to check it.
- **What is posted:** someone flagged (account or address, the limits, how
  often), an address blocked automatically, a ban made or lifted with the
  command line (the server posts it when it picks the change up, so the
  command needs no network), and the proxy warning (every player seeming to
  come from 127.0.0.1: worth knowing at once). Each says what to do next, as
  a command to copy (`LG --ban 4242 --days 7 --reason "..."`, `LG --bans`),
  and links the character's Lodestone page.
- **Never in the way of the server.** Alerts go through a bounded queue on
  their own task: a slow or failing Discord never delays a request. Discord
  limits a webhook to a few messages a second, so alerts within a minute are
  batched into one message, a flood is summed up ("and 37 more"), a `429`'s
  `Retry-After` is honoured, and a failure is logged at most once every ten
  minutes, without the URL.
- **Safe to post.** `allowed_mentions` is empty, so no character name or ban
  reason can ping `@everyone` or anyone; names and reasons are escaped so they
  can't format the message. Nothing anyone said is ever posted (the server
  never has it).
- **Built and tested like the rest:** a fake webhook in the tests (batching,
  limits, retries, escaping, nothing posted when unset), and steps in the bans
  checklist with a private test channel.

**To decide when it's built:**

- **How much to post to Discord**, a third party that keeps what it's sent:
  character names (public on the Lodestone anyway) or only user IDs with the
  Lodestone link; and addresses in full, shortened (`203.0.113.x`), or left
  out (they're in `--bans` on the server).
- **Whether to ping a role** for the urgent ones (an automatic block, the
  proxy warning), or never ping.
- **Anything else worth an alert:** the server restarting, a daily summary
  ("3 flagged, 1 blocked, nothing else"), or nothing more.

### A chat history kept unencrypted

Status: planned, not started (tester request, accepted by the owner
2026-10-07). The chat history stays encrypted by default; a player may opt
out.

- **A setting, "Keep my chat history unencrypted"** (off by default, only
  shown while the history is on), with a plain warning: anyone or anything
  that can read the player's files can read it, including backup and
  cloud-sync tools.
- **To decide when it's built:** the format (plain text that other tools can
  open, which makes it in effect an export, a decision the owner made against
  for the encrypted history; or the same format without encryption), what
  happens to what is already stored when the setting changes (convert it, or
  keep the old part as it was), and how the size cap and deletion work across
  both.

### Strict mode for keys not compared

Status: planned, after the public release (owner, 2026-10-09; H4 in
[the MLS evaluation](mls-evaluation.md)). An optional setting, for a player or
a channel, that refuses to invite, or seal keys to, anyone whose fingerprint
hasn't been compared (see [Known limitations](#known-limitations)). Warn and
continue stays the default: most key changes in a game community are new
computers, and blocking by default would teach players to click through.

### A passphrase for the key file on Wine and Proton

Status: planned, after the public release (owner, 2026-10-09; H5 in
[the MLS evaluation](mls-evaluation.md)). Where DPAPI is unavailable, the
secrets file and the chat log are protected by a local key file in the same
folder, so a copied plugin folder gives away everything. An optional
passphrase would protect that key file. Off by default, since a forgotten
passphrase means re-verifying through the Lodestone with new keys and losing
the chat log. The details are to decide when it's built.

### MLS

Status: not moving to MLS (owner, 2026-10-09). The full evaluation, with its
sources, is in [mls-evaluation.md](mls-evaluation.md).

MLS (RFC 9420) would replace only the epoch-key layer, the smallest and
simplest part of LookingGlass's cryptography. The membership log (ranks,
invites, removals), recovery through the Lodestone, message catch-up and local
chat would all stay custom, with a new layer binding them to MLS, and two
sources of truth (the log and MLS's tree) to keep in lockstep. Contrary to
what this section used to say, the switch wouldn't stay behind
`IGroupKeyProvider` and `IMembershipProvider`: those are shaped around sealed
keys, the log stays, and the server's routing would change (it would have to
order and keep commits, store each channel's GroupInfo, and accept frames over
today's limit). What MLS adds, forward secrecy and post-compromise security,
is worth little while a stolen secrets file also lets the thief sign in and
read a week of stored messages, and it would cost a native Rust library in
the game process and in the server. Instead, smaller hardening comes first: a
notice when the identity signs in from another device, with a list of devices
and **Sign out everywhere else** (H1), members' log heads gossiped inside
messages (H2), and a maximum epoch age of about 7 days (H3), all in progress
for the public release; then the two features above after it. 1.0 promises
no post-compromise security.

Look at MLS again if:

- clients outside the game, or several devices per character, become goals;
- channels need to grow well beyond 500 members;
- forward secrecy and post-compromise security are to become promised
  properties;
- the adversarial reviews find structural problems in the epoch-key layer
  that are better fixed by replacing it than by patching it;
- a mature, maintained, audited MLS library with a stable C interface or a
  managed .NET binding appears.

## Milestones

| Milestone | Contents | Gate after it |
| --- | --- | --- |
| M0 Foundations | Repository, schema, CI, core library | |
| M1 Identity | Registration, identity keys, tokens | |
| M2 Channels and chat | Invites, rekeying, signed messages | Two clients chat while a hostile test server tries to read, forge and replay |
| M3 Integrations and UI | ChatTwo, import wizard, key-verification UI | |
| M4 Hardening and beta | Hardening (H1 to H3 in [MLS](#mls)), beta testing | Adversarial reviews by several models of the finalized design and its implementation, with no open high-severity findings, then the 1.0 release |

Version 0.2 covers M1 and M2 and the key-verification UI of M3. Of M3's
ChatTwo integration, sticky mode's (sending through ChatTwo's input, and
naming the channel in it) and the invite item in its right-click menu are
built, as is M0's CI. The rest of the ChatTwo integration (channel names and
colours for ChatTwo's own use) and the import wizard aren't built yet.

M0's gate used to be "the cryptography spec is reviewed". The review now comes
once the design is finalized, as M4's gate (decided 2026-10-09): a compact
protocol specification is written for the reviewers, and the design and code
are run past adversarial reviews by several different AI models, rather than
a commissioned review (see
[How the review is done](mls-evaluation.md#how-the-review-is-done)). MLS is no
longer a milestone.

## Decisions

The owner's decisions, and why.

- **Sealed epoch keys now, MLS later (2026-10-03; the MLS part superseded on
  2026-10-09, see the decision not to move to MLS below).** Build the signed
  membership log for v0.2 as an interim step, then move to MLS once core
  functionality is confirmed in real use. The second code review had shown the
  first design unsound: trust only ever grew, and ranks and removals weren't
  signed, so a removed member's key could keep vouching for new ghosts, and
  any member could add one. The revised log design was approved on
  2026-10-03 and built on 2026-10-04.
- **Protocol Buffers** for the schema.
- **C# on .NET 10** for the server, sharing the core library with the plugin
  and the echo bot.
- **Name and commands.** `LookingGlass` (renamed from WonderlandChat on
  2026-10-04), with `/lgc1` to `/lgc50`.
- **Recovery restores everything (2026-10-05).** Re-verifying through the
  Lodestone with new keys is proof of identity, and brings back every place,
  rank (admin too) and invite. There is no opt-out, delay or veto. This is the
  trade-off Signal and WhatsApp make: the server vouches for the key change,
  and the other members are told. In the owner's words, at some point a user
  has to trust whatever server they connect to, and someone whose account was
  hacked likely has bigger issues. Players are expected to trust the server
  they connect to (the default one is run by the project), and someone who
  controls a player's Lodestone page already has their game account. Before
  recovery, a re-registered key had no place until a moderator invited it
  again, so the server couldn't swap keys at all (it could, and still can,
  hand out its own key when someone is invited by name).
- **Reset keeps your channels (2026-10-05).** "Reset my identity" used to
  leave the user's channels first (with leaves signed by the old key), and
  refuse while they were the admin of any, since a place under the old key
  could never be used or cleared again. Recovery made that unnecessary, and it
  was removed.
- **Stale places can be removed from your list.** A place under an old key
  can't be left, so `ForgetChannel` takes it off the user's list without
  touching the log.
- **Sticky channel (2026-10-05).** As in ExtraChat, a channel command with no
  message switches the chat box to that channel, by number (`/lgc3`) or, new,
  by nickname (`/lgc sky`). See
  [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc).
  ChatTwo is the owner's default chat window, so it is designed for, not
  only tolerated. After the second in-game retest the owner chose that short
  channel commands (`/p brb`) stay FFXIV's one-off modifiers while talking in
  a channel, except the one ChatTwo sends its own typing with.
- **Links are interactive, as in ExtraChat (2026-10-06).** An item, a map
  flag or a status linked in a channel message reaches the others as the
  game's own clickable link, in the game's chat log and in ChatTwo, and a line
  with only a link is sent. The owner confirmed ExtraChat did this with
  ChatTwo on and off, and wants LookingGlass to be a strict upgrade. Links
  travel as ids inside the encrypted message, beside a plain-text "[name]"
  older clients show, and are rebuilt from the recipient's own game data
  after checks, never from bytes off the network (see
  [Links in messages](#links-in-messages)). No server change.
- **Channel windows (2026-10-06).** In the owner's words, several windows can
  be open at a time, each with tabs for several channels: right-click a
  channel in the list to make a window for it (or add it to one), and a "+"
  after the tabs to add another channel. They show only what came since login,
  like the game's chat log (accepted 2026-10-05), and since 2026-10-07 what
  was sent while away (see [Message catch-up](#message-catch-up)), and older
  lines from the player's chat log if they keep one (see
  [Chat log on this computer](#chat-log-on-this-computer)). Game chat
  stays optional per channel, warnings excepted. See
  [Channel windows](#channel-windows).
- **Message catch-up (2026-10-07).** Messages sent while a member was
  disconnected must reach them when they reconnect: testers lost messages to
  untimely disconnects with ExtraChat. The server keeps recent messages for 7
  days, plus a per-channel cap (about 5,000, oldest dropped first), both
  operator settings. It costs a wider reach for a stolen key and a week of kept
  metadata (see [Known limitations](#known-limitations)). See
  [Message catch-up](#message-catch-up).
- **Chat log on this computer (2026-10-06, 2026-10-07).** Opt-in, one setting
  for every channel; a size limit (50 MB by default, 5 MB to 1 GB), oldest
  first; shown in channel windows above the lines since login; no export;
  encrypted with the secrets file's protection and never uploaded. Deleted
  only by the player (Settings, or when turning it off), not on leaving a
  channel or resetting the identity. See
  [Chat log on this computer](#chat-log-on-this-computer).
- **Quiet start and stop lines (2026-10-07).** "Now talking in" and the
  "Stopped talking in" lines for stops the player chose are off by default
  (Settings, **Say when I start or stop talking in a channel**, once called
  "Verbose channel messages"): someone who moves between
  LookingGlass and game channels often found them a bother, and the server
  info bar and chat box labels have proven reliable. Stops the player didn't
  choose are always said. See
  [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc).
- **Context menu invites (2026-10-07).** Right-clicking a player in the
  game's menus, or a name in ChatTwo, offers **Invite to LookingGlass ▸** with
  the channels the user can invite to, each in its tag and colour; picking one
  sends the invite and says how it went in chat. Hidden where it can't work
  (not connected or registered, yourself, not a player, no home world, no
  channel to invite to). A channel the player is already in or invited to is
  shown greyed out with why, rather than left out, which was the clearer of
  the two options. Through Dalamud's `IContextMenu` and ChatTwo's context menu
  IPC, with no game hooks, no protocol change and no server change. See
  [Context menu invites](#context-menu-invites).
- **Windows only (2026-10-07).** One setting, "Show LookingGlass only in
  windows", keeps every channel's messages and information lines out
  of game chat; warnings and critical lines still go there, as do answers to
  what the player typed in the chat box. A channel no window shows gets a tab
  in the window used last, or a new window, as a second setting says, without
  taking the keyboard; in combat, a cutscene or a loading screen it waits
  until that is over. See [Windows only, never game chat](#windows-only-never-game-chat).
- **Spotting abuse, and banning (2026-10-07).** Required before public
  release. The server flags accounts and addresses that limits refuse again and
  again; banning is the operator's decision, from the command line, never an
  automatic permanent ban; a ban is on the character, not its keys. Built with
  defaults the owner confirmed on 2026-10-08 (see
  [Chosen defaults](#chosen-defaults-confirmed-by-the-owner-2026-10-08)).
- **Friends-only local chat (2026-10-05).** No party or Free Company option,
  since those can include people a player doesn't trust. Built 2026-10-07, with
  every cost the design listed accepted; see
  [Local chat (friends only)](#local-chat-friends-only).
- **Key-change policy for re-verified keys.** Keys re-verified through the
  Lodestone take over the user's places, and members are told. Other key
  changes warn and continue (still open, below).
- **Signed addresses are required.** Outside Development, the server won't
  start without `PublicUrls`, because signed addresses are what stop relayed
  registrations.
- **Identity moves need `wss://` and both servers' word**, because only TLS
  proves who answers at a name.
- **100-bit registration codes**, with a client nonce, so a relayed code
  can't be found in time (see [The code is bound too](#the-code-is-bound-too)).
- **First deployment.** The first tester server runs as a systemd service on
  a Linux machine behind Tailscale Funnel; a cloud host comes later.
- **Keys have a maximum age (2026-10-09).** A channel's key is replaced once it
  is 7 days old, by a member online, silently; a client constant, not a server
  setting (see [Keys have a maximum age](#keys-have-a-maximum-age)).
- **Local chat looks up the friends near, and asks first (2026-10-08).** The
  server learning friends near the sender, those who don't use LookingGlass
  too, is accepted; the player is told what the server learns and accepts it
  before the first `/lgl` (see
  [Local chat (friends only)](#local-chat-friends-only)).
- **Not moving to MLS; reviewed by models, not a paid firm (2026-10-09).**
  After an evaluation ([mls-evaluation.md](mls-evaluation.md)), the owner
  decided: no move to MLS and no MLS milestone or spike, with the conditions
  for looking again recorded under [MLS](#mls); no commissioned, paid review
  ("we're making an FFXIV plugin, not Signal"), but adversarial reviews by
  several AI models of the finalized design and its implementation; the
  hardening H1 to H3 (sign-in notices and a device list, log heads in
  messages, a maximum epoch age) before the public release, and H4 (strict
  mode) and H5 (a passphrase on Wine and Proton) after it; and no promise of
  post-compromise security in 1.0.
- **Settings: short labels, and a "?" only where needed (2026-10-09).** Long
  tooltips and dimmed paragraphs gave way to short labels; a setting its label
  doesn't explain gets a "?" that opens a small bubble on click, not on hover.
  Local chat and the chat history have sections of their own. See
  [The Settings window](#the-settings-window).

## Open questions

- **Secret storage on Wine and Proton:** is the local key-file fallback
  enough? Answered (2026-10-09): not on its own; an optional passphrase comes
  after the public release (see
  [A passphrase for the key file on Wine and Proton](#a-passphrase-for-the-key-file-on-wine-and-proton)).
- **Key-change policy:** warn and continue (current), or block until
  re-verified? (Decided for keys re-verified through the Lodestone; see
  Decisions.) Answered (2026-10-09): warn and continue stays the default, and
  an optional strict mode comes after the public release (see
  [Strict mode for keys not compared](#strict-mode-for-keys-not-compared)).
- **ChatTwo IPC names:** reuse `ExtraChat.*`, or use `LookingGlass.*` and ask
  ChatTwo to support them? Sticky mode already sends on
  `ExtraChat.OverrideChannelColour`, the only one ChatTwo listens to for its
  input's label; asking ChatTwo for a neutral override (one that names the
  command to send plain text with, and adds no "(Warning: ...)") would make
  the label exact.
- **Limits:** confirm after beta load testing.
- **Public hosting:** who runs it, the cost, a privacy note, and an acceptable
  Lodestone volume.
- **Moving to MLS:** decided (2026-10-09): not moving, and no longer a
  milestone (see [MLS](#mls)).
