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
  messages are sent and who is online, and it can drop or delay anything.

### Status

The current version is 0.2. It has registration, key login, identity recovery,
channels, invites, ranks, automatic rekeys, encrypted messages, the signed
membership log, online indicators, blocking and debug tooling. ChatTwo
integration and the import wizard come next. Local chat and a move to MLS are
planned (see [Planned features](#planned-features)).

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
| Upkeep | Stale CI, an example config that doesn't load, frozen dependencies | Hard to build, hard to trust | The plugin's package versions are locked; CI and automated dependency updates are planned |

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
- per IP address: challenges, and failures (a challenge counts as a failure
  until it is answered correctly, so an address that only asks for challenges
  is stopped too);
- per account from each address: failed answers only, half the address's
  allowance, rounded up.

Nothing is limited per account alone. A signature can't be guessed, so the
limits only stop spam, and failures from other addresses must never lock an
account out of key login from its own. An address shared with an attacker
(one NAT, say) still shares its per-address limits. The numbers are in
[server.md](server.md#limits-worth-knowing).

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
local attacker. Settings shows which is in use.

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
channels, so they follow along.

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
  and a result cache.
- Registration is rate-limited per IP address, and verification attempts per
  connection (once every 10 seconds, 10 per challenge).
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
  real name, if one came back, would see it replaced. The admin can rename the
  channel.
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
rekeys straight away.

**What clients accept.** A client accepts a new epoch key only:

- from a member in its verified log;
- for a newer epoch than it holds;
- made for the current membership. A key made before the last join or leave
  is refused, with a warning that the server may be hiding a change. For a key
  made at a newer position, the client fetches and checks the log first.

Clients send with the newest key they hold, whatever epoch the server claims,
and rekey first if that key predates the last join or leave.

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

### Replay protection

- Clients remember the IDs of recent verified messages (in memory) and drop
  duplicates.
- They drop messages dated more than 10 minutes from their own clock.
- They save, per channel and sender, the timestamp of the newest message
  accepted, and drop messages more than 2 minutes older than that, even after
  a restart.
- Those timestamps are saved with other changes, on shutdown, and while
  messages keep arriving, at least every 5 minutes. A crash can lose up to
  about 5 minutes of them.
- Your own messages aren't recorded this way. After a restart, the server
  could replay one you sent in the last 10 minutes back to you.

### Online status

Members who share a channel see when each other are online. The server tells
them when a fellow member connects (their first connection) or disconnects
(their last), and when someone online joins. Invitees and people you share no
channel with aren't told, and you aren't shown to them. This is the server's
word, and it could lie.

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

- The server can't read channel names or messages.
- The server can't forge, alter or re-attribute messages.
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
  to invite, or seal keys to, anyone not compared.
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
- **Old places linger.** A place under a key its owner no longer has stays
  with that key until its owner registers new keys again (which moves it) or
  a moderator removes it. Its owner can remove it from their own list
  meanwhile.
- **The log only grows.** Clients fetch just the new entries, but someone new
  to a channel (or invited to it) replays it from the start.
- **Metadata and availability.** The server sees who is in which channel, when
  messages are sent and who is online, and can drop or delay anything.
- **Replays of your own messages** within 10 minutes of a restart, and up to 5
  minutes of replay timestamps lost in a crash (see
  [Replay protection](#replay-protection)).
- **Debug accounts** on a Development server can be taken over by anyone who
  can reach it, channels and all.
- **The local key file** used where DPAPI is unavailable guards only against
  accidental sharing.

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
  violations and failed authentication do. Server events carry a
  per-connection sequence number.

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
- Signed registrations, the registration client nonce and signed URLs. Older
  plugins are refused with a request to update.

### Adding features

New features fit into one of three layers. Features the server must take part
in are negotiated capabilities. Features that live only inside encrypted
messages need no server change at all.

| Layer | Examples | Server change? | How it is added |
| --- | --- | --- | --- |
| Server capability | Message history, channel bans, file attachments, local chat | Yes | A new capability string and message types; old clients never see them |
| Encrypted content kind | Emotes, replies, reactions, polls, typing state | No | A new content kind inside the ciphertext; older clients show "unsupported message" |
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
  [Talking in a channel without /lgc](#talking-in-a-channel-without-lgc)).
  Sending fails closed: an error never falls through to ordinary game chat.
- **Game interop.** Signatures live in one module (`ChatInterop`), and come
  from FFXIVClientStructs. A missing one disables only its feature.
- **ChatTwo.** ChatTwo's input sends through the same game function the chat
  box does, which sticky mode hooks, and LookingGlass names its channel in
  ChatTwo's input through `ExtraChat.OverrideChannelColour` (see below).
  ExtraChat also exposed `ExtraChat.ChannelNames` and
  `ExtraChat.ChannelCommandColours`, and added an invite item to ChatTwo's
  context menu through `ChatTwo.Register` and `Invoke`. LookingGlass will keep
  equivalent integration; the IPC names are an open question.

### Channel numbers, nicknames and colours

These are plugin settings, kept per character, and never sent to the server.

- **Numbers.** Each channel gets a number from 1 to 50 automatically, and
  keeps it across restarts until the user leaves it (or it is disbanded, or
  they are removed). The freed number then goes to the next channel without
  one. Choosing a number another channel has swaps the two. Typing a number
  with no channel on it says so.
- **Commands.** Only `/lgc` is listed in Dalamud's command help; the fifty
  numbered commands are hidden to keep the list short. `/lgc` on its own
  explains how to use it.
- **Nicknames.** 1 to 16 letters, digits, `-` or `_`. A nickname can't be only
  digits, so `/lgc 3` is never confused with `/lgc3`. It must differ from the
  user's other nicknames, ignoring case. A channel's nickname goes away when
  the user leaves it.
- **Chat tags.** A channel with a nickname is tagged with it, as in `[sky]`,
  unless **Show nicknames in chat tags** is off; otherwise with its number, as
  in `[LGC3]`. A channel with neither (more than fifty channels, or a message
  that arrives before the channel list is in) is tagged `[LGC]`.
- **Colours.** Any of the game's own chat colours. The channel's lines take
  the colour, or only the tag if **Colour the whole line in a channel's
  colour** is off. The bar beside the channel in the list takes it too.
  **Default** colours only the tag.
- **Chat channel.** Messages appear in one of the game's chat channels, chosen
  in Settings, so chat tabs can show or hide them.
- **Unread counts.** The channel list counts messages from others since the
  user last looked at a channel in the main window or talked in it. The
  window's title shows the total. The counts start from zero at each login.
- **Member icons.** Besides the fingerprint state in advanced mode (see
  [Identity keys and fingerprints](#identity-keys-and-fingerprints)), the
  icon's colour shows presence: green while connected, grey when not. A
  warning keeps its orange either way, and invitees stay grey until they join.
- **Confirmations.** Removing a member needs **Ctrl** held. Leaving or
  disbanding asks first. Cancelling an invite happens straight away.

### Talking in a channel without /lgc

`/lgc3` or `/lgc sky` with no message makes the chat box talk in that channel
(a "sticky" channel): from then on, plain text typed in the chat box goes to
the channel, as `/lgc3 <message>` would send it, and never to game chat.
Commands still work. `/lgc` alone still explains itself, and `/lgc 3` is a
nickname, never channel number 3. The rules live in the core library
(`StickyChannel`, `StickyRoute`, `ChatChannelPrefixes`, `ChatBoxGate`), with
no game types, and are unit tested; the plugin's `StickyMode` feeds them and
acts on them, on the game thread only. The checks to make in game are in
[docs/testing/sticky-channel-checklist.md](testing/sticky-channel-checklist.md).

**Starting.** Only in a channel the player is a member of (any rank, under
their current setup) on the server they're connected to now, once the channel
list is in. Otherwise it is refused with one plain line: not connected, still
loading, not in that channel, or not available (a hook is missing, or the
game's chat channel can't be read). With ChatTwo it is also refused while
ChatTwo's main input is on a /tell (see below). `/lgcM` while talking in
another channel moves to that one. If ExtraChat (or a fork of it) is loaded
too, one more line warns that it watches the same chat box and ChatTwo label,
and to turn it off while doing this.

**What it says.** Short lines, in the channel's colour, in the chat channel
chosen in Settings (the same one for every line, so a ChatTwo tab that shows
"Now talking in" shows "Stopped" too): "Now talking in [sky]." and "Stopped
talking in [sky]." with a few words of reason where they help (": you logged
out.", ": disconnected.", ": you're no longer in it.", ": LookingGlass was
turned off.", ": the connection started over."). The first time ever that it
starts with ChatTwo loaded, one more sentence follows (a saved setting,
`ChatTwoStickyNoteShown`): ChatTwo's "(Warning: …)" only names the game
channel underneath, messages still go only to the channel, and the long form
(`/party hi`) talks in a game channel once. A message kept from the game says
"Not sent to [sky] or game chat: *reason*".

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
    through unchanged. A macro's plain text (or `/p text`) while sticky goes to
    the LookingGlass channel instead of game chat: private, so it fails safe. A
    raid macro's `/p Pull in 5` therefore goes to the channel while sticky; macros
    meant for Party should use the long form, `/party`.

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
optional, for the diagnostic log and to see a switch sooner:
`UIModule.ProcessChatBoxEntry` (a pass-through that only notes a line came from
a plugin), `AgentChatLog.ChangeChannelName` and
`AgentChatLog.InsertTextCommandParam`.

*Plugin commands (checked in Dalamud's source).* Dalamud dispatches plugin
commands, `/lgc` included, from its own hook on
`ShellCommands.TryInvokeDebugCommand` (`Dalamud/Game/Command/CommandManager.cs`):
it calls the game's function first and, if the game doesn't know the command,
runs the plugin's handler right there, synchronously. The game calls that
function while running a command, inside `ExecuteCommandInner`, so the gate
sees `/lgc3` first, lets it through like any command, and the handler runs
inside the gate's call to the game (which is also why a line counts as
running, below). The two hooks are on different functions, so their order
doesn't matter. Another plugin hooking `ExecuteCommandInner` too (GagSpeak, on
the owner's machine) is chained by Dalamud: if it runs first and changes the
text (gagged speech), the gate decides the changed text, which still goes to
the channel.

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
  payload bytes, a link placeholder, an auto-translate phrase) is the
  channel's, like plain text, in both chat boxes. ChatTwo sends what was typed
  in it that way (below), and a typed `/p hi` can't be told apart from that, so
  one rule holds for every line. Only the bare
  command is a switch (above). The command ends at the first space or control
  byte, so a payload straight after it still counts. The long forms (`/party
  hi`) are the way to talk in a game channel once.
- Any other line starting with `/` is a command and goes to the game
  untouched, `/lgc` commands included. Only a `/` at the very start counts: a
  line with a space before it goes to the channel, never the game.
- Anything else goes to the channel, trimmed, as text.
- A line with nothing to send as text is kept from the game, with "Not sent to
  [sky] or game chat: no text (links can't be sent)." That is a line with only
  links (payloads) or only link placeholders (`<item>`, `<flag>`, `<status>`:
  what the chat input holds for a link until the line is sent, put there by
  `AgentChatLog.InsertTextCommandParam`; the game makes them links only while
  running the line, after the gate, and ChatTwo's input holds them the same
  way). In a line with text, a placeholder is sent as typed. A blank
  line goes nowhere, quietly.

**Fail closed.** While sticky, a line never reaches game chat unless it is a
command:

- Not connected (or reconnecting): kept from the game, and "Not sent to [sky]
  or game chat: not connected to LookingGlass." A connection that drops on its
  own keeps its session, which reconnects, so sticky mode stays on, and a
  reconnect doesn't drop the player into public chat. Pressing **Disconnect**
  stops the session: that ends sticky mode, with "Stopped talking in [sky]:
  disconnected." (`StickyEnd.Disconnected`), as the player chose to stop.
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

**Leaving.** It ends, with one line saying so, when:

- the game's chat channel changes: read once a frame
  (`RaptureShellModule.ChatType`, which names the linkshell too), and after
  every `ChangeChatChannel` call, against the channel it started in
  (Tab-cycling, ChatTwo's picker, a ChatTwo tab with another channel, another
  plugin);
- a channel command is typed on its own, even for the channel already on (`/s`
  while in Say): decided from the line itself, before the game runs it (see
  Where a line goes), and also any `ChangeChatChannel` call made while a line
  from the chat box is being run (`StickyChannel.ChannelSwitchCalled`), such
  as `/t Bob`;
- the player clicks the server info bar entry;
- they log out or another character logs in;
- the session is stopped: **Disconnect** pressed (or a server change waiting
  to connect), "disconnected";
- the session is replaced: the server address changed, or the identity reset
  or restored (a new session object), "the connection started over";
- they're no longer in the channel (left, removed, disbanded, "Remove from my
  list", or now only a place under old keys), checked against the complete
  channel list only;
- the game makes a one-off switch, saving the channel it is on to go back to
  (`StickyEnd.ChatBoxSwitched`, below);
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

The line is printed for a channel switch too, though the player usually made
it: the line is what tells them their typing goes to game chat again. Moving
to another LookingGlass channel says "Now talking in" instead.

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
  `InputChannelExt.Prefix`). Every input has its own channel: the main window,
  each tab (a tab can have a fixed channel), and each pop-out with input,
  whose picker changes only that pop-out (`Popout.cs`), without the game
  knowing. `ChatTwo.GetChatInputState` reports the main window's only. So,
  while sticky with ChatTwo loaded, every short channel command ChatTwo can
  send plain text with, followed by text, goes to the LookingGlass channel,
  whatever channel the game or any input is on (`ChatChannelPrefixes.ChatTwo`):
  `/s`, `/p`, `/a`, `/y`, `/sh`, `/fc`, `/pt`, `/b`, `/l1` to `/l8` and
  `/cwl1` to `/cwl8`. Three of ChatTwo's are left to the game: `/t` (it sends
  tells to a known player itself, below, and a `/t` line names the player
  first), `/e` (echo, for an input with no channel, seen only by the player)
  and `/ecl1` to `/ecl8` (ExtraChat's, not game chat). The cost, fail safe: a
  one-off `/p hi` typed in ChatTwo while sticky goes to the LookingGlass
  channel too. The long commands (`/party hi`, `/say`, `/shout`,
  `/linkshell1`, `/cwlinkshell1` and so on) are never sent by ChatTwo, so they
  still reach the game; the ChatTwo sentence after "Now talking in" says to
  use them. The rule doesn't depend on ChatTwo being detected: it holds in
  the game's own chat box too (Where a line goes).
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
  side, so both are handled (Where a line goes).
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
  Party)". The ChatTwo sentence after "Now talking in" (shown once) says what
  it means: the game's channel underneath; what is typed still goes to the
  LookingGlass channel (from any ChatTwo input not set to a tell). Making it go away needs a change in
  ChatTwo, such as an override that names a command to send plain text with
  (`/lgc3`) and no warning.
- *Why ExtraChat's sticky channel was unreliable with ChatTwo (inferred from
  ChatTwo's side only).* Typing `/ecl1` in ChatTwo sends the command, and the
  override renames ChatTwo's label, but ChatTwo's own input channel stays
  where it was (Party in the owner's screenshot, hence "(Warning: Party)").
  ChatTwo then sends plain text as "/p text", so whether it reached ExtraChat
  or party chat depended entirely on whether the other plugin's hook claimed a
  "/p" line as its own. Picking the ExtraChat channel in ChatTwo's picker put
  ChatTwo's input on it properly, but any game channel change, and every tab
  switch, puts ChatTwo's input back on the game's channel. LookingGlass doesn't
  depend on ChatTwo's input channel at all: the prefix rule catches every
  input.

**Limits.** Only lines the game runs through the gate are caught: ChatTwo's
tells to a known player (above), which it sends to the server itself, go as
tells. Everything else that runs a chat line goes through the gate, so while a
channel is sticky, plain text (or a short channel command with text) from a
macro, from another plugin, or from ChatTwo's special tells in Eureka and
Bozja (`ExecuteCommandInner` with the message alone) is sent to the channel
instead (private, so it fails safe). A channel command on its own that isn't
known by name (neither English nor the client's language) ends sticky mode
only if the game calls its channel switch while the line runs, or the channel
changes.

**The diagnostic log.** So an in-game test can be read back without the
player copying anything, sticky mode writes one Information line to Dalamud's
log (`dalamud.log`, tagged `[LookingGlass] [sticky]`, built by
`StickyDiagnostics`) for every line the gate sees while sticky (saying
whether it came from the game, or from a plugin through `ProcessChatBoxEntry`), every
start (and refusal) and every end, and every `ChangeChatChannel` call while
sticky. A line's entry has the channel's tag, whether ChatTwo is loaded,
the leading command if it is a known one (otherwise "(text)", "(payload)",
"(link placeholder)", "(blank)" or "(other command)"), its size in bytes,
whether it held payloads, and the decision with a reason in fixed words. A
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
  current schema version is 8.
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
| Pending invites per user | 20 | Stops one person being flooded |
| Registration attempts | 5 per hour per IP; verify once per 10 seconds, 10 per challenge | Protects the Lodestone and the challenge flow |
| Lodestone requests (server-wide) | 1 every 2 seconds, cached | Avoids being blocked by the Lodestone |
| Connections per IP | 20; unauthenticated connections close after 20 minutes | Bounds idle and unauthenticated load |
| Outbound queue per connection | 256 events | A slow client is disconnected, not waited on |
| Devices per user | 20 most recently used | Bounds stored logins |

Invites sent and received, channel creation, renames, disbands, identity
lookups and heavy reads have their own per-user rate limits. Key login limits
are under [Key login](#key-login). Operators can change some of these (see
[server.md](server.md#settings)).

## Operations

- The server runs on Linux and Windows (.NET 10).
- It listens on localhost by default, behind a TLS reverse proxy, and trusts
  `X-Forwarded-For` only from proxies on the same machine or configured ones.
- The first tester server runs as a systemd service on a Linux machine behind
  Tailscale Funnel. A cloud host comes later.

How to build, configure and deploy a server is in [server.md](server.md).

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
  files, limits, end-to-end flows, and malicious-server and malicious-member
  suites that inject forged and replayed events.

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

### Local chat (friends only)

Status: planned, not started.

A `/say`-like chat for players who stand near each other and both use the
plugin. Nobody without the plugin sees it, and only players on the sender's
in-game friends list can read it.

- **The sender's plugin picks the recipients.** It takes the players near the
  sender in the game (the object table, at about `/say` range), keeps those on
  the sender's friends list, and looks up their LookingGlass keys (the same
  lookup as inviting by name, using pinned keys). It encrypts the message
  separately to each recipient's identity key, as epoch keys are sealed today,
  signs it with the sender's identity key, and asks the server to deliver the
  copies to those accounts.
- **The receiving plugin checks too.** It shows a message only if it decrypts,
  its signature is the sender's known key, the sender is on this player's
  friends list, and the sender's character is near them. FFXIV friendships are
  mutual, and both checks run in the players' own plugins, so the server can't
  add anyone and can't forge a message.
- **The server never learns locations.** It holds no zones, instances or
  rooms. It only delivers sealed copies to the user IDs the sender named, and
  stores nothing.
- **Shown in game chat** with its own tag, command (for example `/lgl`; check
  in game that it's free) and colour, like a channel.
- **A new server capability** (`local`) with its own message types. It
  doesn't touch channels, the membership log or epochs, and an old client
  never sees it.

What it costs, all accepted:

- **No meeting strangers.** It is chat among friends who use the plugin. An
  open, signed-only local chat is out of scope.
- **Metadata.** The server sees who sent to whom and when, which implies those
  players were together.
- **The friends list must be loaded.** The game may only fill it in once the
  Friends window has been opened in a session. If so, the plugin says plainly
  to open it once. Check in game.
- **Crowds.** One copy per recipient is fine for a handful of friends nearby.
  Cap recipients per message (about 50), and rate-limit like channel messages.

### Channel windows (pop-out chat)

Status: planned, not started. Requested by the owner (2026-10-05) for after
the sticky channel is settled.

A channel can be opened in its own instant-messenger-style window, apart from
the game's chat log and ChatTwo. What you type there goes only to that
channel.

Why:

- **No leak path through game chat.** The window's input box is the plugin's
  own (ImGui), so typed text never passes through the game's chat input or
  ChatTwo, and never reaches a game channel, whatever state the game or
  ChatTwo is in.
- **No wrong-channel mistakes.** Each window belongs to one channel, shown in
  its title, colour and input box. With ExtraChat, heavy users of several
  channels sometimes sent sensitive or embarrassing messages to the wrong one;
  separate windows make the target obvious.

How it could work:

- **Opening:** from the channel's menu in the main window ("Open in window"),
  and later perhaps a command. Several windows can be open at once; which
  ones are open, and where, is remembered.
- **Each window shows:** the channel's name and colour, its messages with
  sender names and times, an input box, and optionally its members. Its own
  unread count, and a mark in the main window's channel list.
- **Game chat stays optional.** A per-channel setting decides whether that
  channel's messages also appear in game chat (as today) or only in its
  window.
- **Messages it can show** are those received since the player logged in,
  like the game's own chat log, which keeps nothing between logins either.
  The owner accepted this (2026-10-05). Stored history isn't planned, but
  isn't ruled out: if it comes later, it would be opt-in, kept on the
  player's computer and encrypted like the secrets file (see the open
  question on message history).
- **Typing in the window** takes keyboard focus from the game, as other
  plugin windows do; pressing Escape or clicking away gives it back.
- **The same send path** as `/lgc`: the same rate limits, "not sent" errors,
  rekey waits and plain-language warnings. The window shows the channel's
  warnings at the top, as the channel pane does.
- **Simple and advanced mode** apply as everywhere else.

To decide when it's built: whether windows can be docked together as tabs
(depends on what Dalamud's ImGui allows), and how the window looks with
Dalamud's transparency.

### MLS

MLS (RFC 9420) solves the same problems as the membership log and epoch keys,
with an audited standard, and scales better. There is no mature C#
implementation, so adopting it means shipping a Rust library (OpenMLS)
through native interop in both the plugin and the server.

The group-key and membership layers sit behind interfaces
(`IGroupKeyProvider`, `IMembershipProvider`), so the switch replaces them
without touching chat, UI or server routing.

## Milestones

| Milestone | Contents | Gate after it |
| --- | --- | --- |
| M0 Foundations | Repository, schema, CI, core library | The cryptography spec is reviewed |
| M1 Identity | Registration, identity keys, tokens | |
| M2 Channels and chat | Invites, rekeying, signed messages | Two clients chat while a hostile test server tries to read, forge and replay |
| M3 Integrations and UI | ChatTwo, import wizard, key-verification UI | |
| M4 Hardening and beta | Hardening, beta testing | No open high-severity findings, then the 1.0 release |

Version 0.2 covers M1 and M2 and the key-verification UI of M3. Of M3's
ChatTwo integration, only sticky mode's (sending through ChatTwo's input, and
naming the channel in it) is built. CI (from M0), the rest of the ChatTwo
integration and the import wizard aren't built yet.

## Decisions

The owner's decisions, and why.

- **Sealed epoch keys now, MLS later (2026-10-03).** Build the signed
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
  only tolerated.
- **Friends-only local chat (2026-10-05).** No party or Free Company option,
  since those can include people a player doesn't trust.
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

## Open questions

- **Secret storage on Wine and Proton:** is the local key-file fallback
  enough?
- **Key-change policy:** warn and continue (current), or block until
  re-verified? (Decided for keys re-verified through the Lodestone; see
  Decisions.)
- **ChatTwo IPC names:** reuse `ExtraChat.*`, or use `LookingGlass.*` and ask
  ChatTwo to support them? Sticky mode already sends on
  `ExtraChat.OverrideChannelColour`, the only one ChatTwo listens to for its
  input's label; asking ChatTwo for a neutral override (one that names the
  command to send plain text with, and adds no "(Warning: ...)") would make
  the label exact.
- **Message history:** in 1.0, or later?
- **Limits:** confirm after beta load testing.
- **Public hosting:** who runs it, the cost, a privacy note, and an acceptable
  Lodestone volume.
- **Moving to MLS:** plan the switch as its own milestone, including how
  existing channels migrate.
