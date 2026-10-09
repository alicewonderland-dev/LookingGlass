# Should LookingGlass move to MLS?

An evaluation written for the owner on 2026-10-09, and the owner's decision
on it. It is based on [design.md](design.md), [server.md](server.md), the
protocol schema (`src/LookingGlass.Protocol/Protos/lookingglass.proto`), the
core library's crypto and membership code, the client session, the server's
request handler and database, and public sources on MLS libraries (cited
inline, and listed at the end). The decision comes first; the analysis behind
it follows, as it was written, except that its suggestion of a commissioned
review has been replaced by the review the owner chose.

---

## Decision (2026-10-09)

1. **LookingGlass is not moving to MLS.** It is no longer a milestone. The
   conditions for looking at it again are listed under
   [When to look at MLS again](#when-to-look-at-mls-again).
2. **No commissioned, paid review.** In the owner's words, "we're making an
   FFXIV plugin, not Signal". Instead, once the design is finalized, the
   design and its implementation are run past adversarial reviews by several
   different AI models (see
   [How the review is done](#how-the-review-is-done)).
3. **Hardening as recommended.** Before the public release: H1 (a notice when
   the identity signs in from another device, a list of devices, and **Sign
   out everywhere else**), H2 (members' log heads gossiped inside messages)
   and H3 (a maximum epoch age of about 7 days). H3 is built and merged (see
   [Keys have a maximum age](design.md#keys-have-a-maximum-age)); H1 and H2
   are still being built, and each gets its own section in
   [design.md](design.md) when it is. After the
   public release: H4 (an optional strict mode) and H5 (an optional passphrase
   for the local key file on Wine and Proton).
4. **No promise of post-compromise security in 1.0.**
5. **No MLS spike.**
6. **The design document's MLS section is corrected**: it no longer says the
   switch would leave server routing untouched (done the same day).

---

## 1. Executive summary

### The recommendation

**Don't move LookingGlass to MLS: not before the public release, and not as a
planned milestone after it.** Keep the current protocol. Before the public
release, have it reviewed adversarially (as decided: by several AI models,
against a written specification and the code), and make a few targeted
hardening changes (listed below) that capture the parts of MLS's benefit that
matter for LookingGlass. Replace "Moving to MLS" in the design's open
questions and planned features with a short list of conditions under which it
would be worth looking at again.

This is a judgement about LookingGlass as it is (one developer, a fast-moving
plugin inside a game process, channels capped at 500, an identity system
rooted in the Lodestone and vouched for by the server), not about MLS, which
is a good protocol.

### Why

1. **MLS would replace the smallest and simplest part of LookingGlass's
   cryptography, and none of the hard parts.** What MLS does is the group key
   layer: deriving each epoch's key, getting it to exactly the members, and
   encrypting and authenticating messages under it. In LookingGlass that is
   `ChannelCrypto` and `SealedBoxes`, a few hundred lines built from standard
   pieces (X25519, HKDF-SHA256, XChaCha20-Poly1305, Ed25519, with careful
   domain separation). Everything that makes LookingGlass's security subtle
   stays custom with MLS, because MLS has no notion of it:
   - ranks, invites that can be declined or cancelled, admins and moderators
     (the signed membership log);
   - identity recovery through the Lodestone, where the server vouches for new
     keys ("recovery restores everything");
   - the "nobody holds the key" placeholder-name rule;
   - message catch-up for a week, with its first-epoch authorization and its
     fail-closed dating rules;
   - registration codes, signed server addresses, key login, server moves;
   - local chat, which isn't a group at all.

   An MLS migration keeps all of these, and adds a new custom layer binding
   them to MLS. A reviewer still has to review all of it.

2. **It adds a second state machine that has to agree with the first.** Today
   the membership log is the one source of truth, and each epoch key names the
   log position it was made for. With MLS there would be the log (who may do
   what) and the MLS ratchet tree (who holds keys), and every commit would have
   to be checked against the log. Keeping two sources of truth in lockstep is a
   classic source of security bugs, and it brings failure modes LookingGlass
   doesn't have today: members stuck behind a commit they can't process,
   commits the server must keep and order for as long as anyone might be
   behind, forks that need a "resync" (an external commit), per-channel state
   on disk that must never be rolled back (by the `.bak` restore, a restored
   backup, or a server move's copy), and Welcome/GroupInfo messages around
   140 KB for a full channel, over today's 128 KiB frame limit.

3. **The benefits are real but narrow in LookingGlass's threat model.** MLS
   adds forward secrecy (FS: keys for messages already read are deleted) and
   post-compromise security (PCS: after a member's next update, someone who
   copied their keys once is locked out again). But:
   - On a player's computer, the identity key, the device token and all
     channel state sit in one secrets file. Whoever steals it can usually also
     sign in as the player with the key, which MLS doesn't stop.
   - On Wine and Proton (Steam Deck), the secrets file's protection key sits in
     the same folder, so a copied folder is a full compromise either way.
   - Message catch-up deliberately keeps a week of ciphertext that any current
     member can read with their current state. MLS keeps that property (an
     absent member must still be able to read what they missed), so it doesn't
     shrink the "stolen key reads a week of stored messages" limitation as much
     as one might expect; it mainly protects what the victim had *already read*
     before the theft.
   - Players who keep the opt-in chat log on their computer keep plaintext
     history under a key in the same folder; FS adds nothing for them.
   - The biggest documented trust gaps (the server vouching for re-verified
     keys, trust on first use when inviting by name, metadata, a server that
     drops, delays or forks) are untouched by MLS.

4. **The cost is large for one developer, and lands at the worst time.** It
   means a native Rust library inside the game process (plus a Rust toolchain
   in CI for Windows x64, Linux x64 and Linux ARM64), a rewrite of the channel,
   rekey and catch-up parts of `ClientSession` and of the server's epoch
   handling, a new storage layer for group state, rewriting the
   malicious-server, malicious-member and catch-up test suites, and then a
   review of the MLS integration anyway. There is no mature MLS library for
   .NET, so the best option (OpenMLS, section 2) means writing and maintaining
   a Rust shim, and following OpenMLS's security releases (five advisories
   since late 2025, the latest fixed on 2026-10-07) with plugin updates. The
   project was a week old at the time of writing, changed daily, and was
   heading for its first public release.

5. **The design document's premise was more optimistic than the code.** The
   design said MLS "solves the same problems as the membership log and epoch
   keys" and that, behind `IGroupKeyProvider` and `IMembershipProvider`, "the
   switch replaces them without touching chat, UI or server routing". In
   practice the interfaces are shaped around sealed keys
   (`SealToMembers(epochKey, ..., LogPosition, recipients, ...)`,
   `OpenEpochKey(SealedEpochKey, ...)`), MLS wouldn't replace the log (ranks
   and recovery stay), and the server's routing would change (it would have to
   sequence and keep commits, store GroupInfo, and accept larger frames). That
   section of the design has been corrected (2026-10-09).

6. **Scale isn't a reason here.** MLS's tree scales to very large groups. At
   LookingGlass's cap of 500 members, a full rekey today is about 100 KB, rate
   limited, and only happens on membership changes. MLS commits would be much
   smaller (a few KB), but adding someone to a full channel would need a
   Welcome or GroupInfo carrying the whole tree, of the same order of size.

### The three paths compared

| | A. Keep the protocol, review and harden (chosen) | B. MLS before the public release | C. MLS after 1.0, channel by channel |
| --- | --- | --- | --- |
| What it costs | A written protocol spec for reviewers (M), adversarial reviews by several models (the owner's time to run them and sort the findings), fixes, a handful of small hardening changes (S each) | XL: native library, new client and server layers, new tests, migration, then a review of the result | XL as B, plus running two channel formats side by side for a long time |
| Security gained | Several adversarial reviewers looking at all of the custom crypto, which is where the risk is; specific gaps closed (section 1, "What to do instead") | FS and PCS for channel messages; a standard key schedule | The same as B, later |
| New risks | Little: changes are small and additive | Native code in the game process; two sources of truth; stuck and forked groups; state rollback; release delayed; review needed anyway | All of B's, plus a downgrade surface between the two formats that must be closed carefully |
| Reversible | Yes | Hard: channels, keys and stored messages change format | Per channel, but the code for both stays |

### What to do instead

1. **Write a compact protocol specification** for the reviewers (size M). The
   design document is long, careful prose written for the project; a model
   reviewer works best from one self-contained document that fits alongside
   the code it is checking, with: every message and stored structure, every
   signed and hashed payload with its domain string (`Domains` in
   `SigningPayload.cs`), the state each side keeps, the client's acceptance
   rules as a numbered list, the security properties claimed, and the threat
   model with its accepted limitations (so reviewers don't spend their effort
   rediscovering them). Much of this can be lifted from the design document
   and the code comments.
2. **Run adversarial reviews by several different models** of that
   specification and the code that implements it (the core library's `Crypto`
   and `Membership` folders, the client session's channel, catch-up and local
   chat code, and the server's request handler and database), once the design
   is finalized. Scope and method are in
   [section 6](#6-work-breakdown-phases-and-tests). In the design's
   milestones this is now M4's gate, in place of M0's old one ("The
   cryptography spec is reviewed").
3. **Make these hardening changes**, each small and additive. As decided, H1
   to H3 come before the public release (H3 is built and merged; H1 and H2
   are being built now); H4 and H5 come after it.
   - **H1. Tell players when their identity signs in elsewhere.** Today a key
     login issues a new device token and "other devices keep theirs", silently.
     Someone with a copy of the secrets file can sign in with the key and read
     every channel until the player resets their identity, without the player
     being told. A notice ("Your character signed in from another device at
     14:05; if that wasn't you, reset your identity") and a list of the
     account's devices with **Sign out everywhere else** close most of the
     practical gap that PCS would close, for this threat model. Server and
     plugin change, no protocol version change.
   - **H2. Members gossip their log head inside messages.** Put the sender's
     verified membership log position (sequence number and hash) in an
     encrypted field of each message's content, as links were added. A
     receiver whose log differs at that position learns at once that the server
     is showing different members different logs, including different key
     recovered entries, which the design says today "only comparing whole logs
     reveals". No server change; older clients skip the field.
   - **H3. Give epochs a maximum age.** Today a channel whose membership doesn't
     change keeps one epoch key indefinitely. Rekeying when the newest key is
     older than, say, 7 days (the first member online makes the key, as for any
     rekey) bounds how long one leaked epoch key reads new messages. On its own
     this isn't PCS (keys are still sealed to the long-term X25519 key), but it
     is cheap and limits the damage of a leaked epoch key (a crash dump, a
     debug log, a memory read).
   - **H4. An optional strict mode** that refuses to invite, or seal keys to,
     anyone whose fingerprint hasn't been compared (the design's own "no strict
     mode yet").
   - **H5. Decide the Wine/Proton secrets question** (an open question in the
     design): for example an optional passphrase for the local key file on
     Wine and Proton, so that a copied plugin folder isn't a full compromise.
   - **Only if the owner wants PCS as a promised property** (decided: not for
     1.0): the way to get it without MLS is to rotate the key that epoch keys
     are sealed to (a medium-term X25519 key, signed by the identity key,
     announced in the log), which is in effect re-deriving part of MLS by hand.
     If that is wanted, it is the strongest argument for revisiting MLS instead
     (see the triggers below), rather than building it.
4. **Correct the design document's MLS section**, and record the decision and
   the triggers for revisiting it (done 2026-10-09).

### When to look at MLS again

Revisit if one of these becomes true:

- clients outside the game (web, mobile, a desktop app), or several devices
  per character, become goals: MLS's interoperability and its model of a user
  as several leaves then pay for themselves;
- channels need to grow well beyond 500 members;
- forward secrecy and post-compromise security become promised properties of
  the product (say, for a public claim of "Signal-grade" security);
- the adversarial reviews find structural problems in the epoch-key layer that
  are better fixed by replacing it than by patching it;
- a mature, maintained, audited MLS implementation with a stable C ABI or a
  managed .NET binding appears (see section 2), which would remove most of the
  integration cost.

The rest of this document shows how MLS would map onto LookingGlass, to
support this recommendation and to be ready if a trigger fires. It is
deliberately shorter in the parts (migration, detailed work plan) that only
matter if MLS goes ahead.

---

## 2. Library choice

Facts checked on 2026-10-09; sources in brackets refer to the list at the end.

**Short answer:** if LookingGlass ever adopts MLS, use **OpenMLS** through a
small Rust "shim" library of LookingGlass's own, with a narrow C interface,
called from C# by P/Invoke. No managed .NET implementation is fit for this
today, and none of the ready-made .NET bindings is a good basis. This doesn't
change the recommendation in section 1: the library question is solvable, and
it isn't the reason not to adopt MLS.

### The candidates

| Library | Language, licence | Maturity and maintenance | Audit | Ciphersuites of interest | How C# would call it | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| **OpenMLS** | Rust, MIT [1] | Very active: 0.9.1 released 2026-10-07, commits daily; used by Wire's core-crypto (on a fork), XMTP and Phoenix's Air [1][2][3] | Independent audit by SRLabs, published around May 2026: 8 findings, 1 high, fixed in 0.8.1 and 0.7.3 [4]. Several high and moderate advisories since, the latest (a stack overflow when parsing extensions, reachable by the server or any sender) fixed in 0.9.1 on 2026-10-07 [5] | 0x0001 (X25519, AES-128-GCM, SHA-256, Ed25519), the mandatory one; 0x0003 (X25519, ChaCha20-Poly1305) [1] | No official C API or C# binding (only WebAssembly) [1]. You write a C-ABI shim crate | **Best choice if MLS goes ahead** |
| **AWS mls-rs** | Rust, Apache-2.0 or MIT [6] | Active (0.56.0, August 2026); vendored in Firefox and AOSP [6][7] | The README says it has **not** had a full third-party audit [6] | 1, 2, 3 with RustCrypto (marked experimental); stable providers need OpenSSL or aws-lc (C code to cross-compile) [6] | `mls-rs-uniffi` exists but is minimal: OpenSSL only, one suite, no external commits, PSKs or custom extensions, tested only from Python and Kotlin; on crates.io only a 2024 version [8]. `mls-rs-ffi` is a code-generation helper, not a stable C API [8]. With `uniffi-bindgen-cs` (MPL-2.0, released 2026-10-09, targets UniFFI 0.31 while upstream is at 0.32, known async callback bug) [9] it is possible but fragile | Second choice; would also need your own shim |
| **Cisco MLSpp** | C++17, BSD-2-Clause [10] | Active; the basis of Discord's DAVE voice encryption [11] | Reviewed as part of DAVE by Trail of Bits in 2024 (one finding: commit leaf nodes not validated by MLSpp) [12] | All registered suites, on OpenSSL or BoringSSL [10] | No C API; you would write a C wrapper over C++ and ship OpenSSL too | Workable, but more native code to own |
| **Wire core-crypto** | Rust, GPL-3.0 [13] | Active | Inherits OpenMLS's | As OpenMLS | Bindings for Kotlin, Swift and TypeScript only, no .NET [13]; built around Wire's own model | Not a fit |
| **Managed .NET** (DotnetMls, Universal.Common.Security.Mls) | C#, MIT / CC0 [14] | Betas or single releases from 2026, a few thousand downloads, no test vectors, interop or audit stated | None | 0x0001 only | Directly | **Not acceptable** for security-critical use |
| **.NET wrappers over OpenMLS** (GBPStack, NostrNet.Marmot) | C# over Rust, Apache-2.0 / MIT [14] | Small projects with their own protocols on top; earlier GBPStack versions deprecated for "critical bugs" | None | As OpenMLS | Via their native packages | Not a basis: you'd depend on someone else's protocol and release cadence |
| BouncyCastle | Java has MLS since 2024; **C# edition has none** [15] | | | | | Not available |

Licences: MIT, Apache-2.0, BSD-2-Clause and MPL-2.0 can all be combined into
an AGPL-3.0 program (Apache-2.0 is compatible with version 3 of the GPL family);
GPL-3.0 code (core-crypto) can be combined with AGPL-3.0 code through the
licences' own compatibility clause. None is an obstacle.

### Why OpenMLS, and why your own shim

- It is the only candidate with a published independent audit, it is
  actively maintained, and it has the features this design would need:
  external commits, group context extensions, custom proposals, self-remove,
  "safe" authenticated data, exporters, and a public-group mode for a server
  that wants to check commits without being a member [1][16].
- Its crypto backend can be pure Rust (RustCrypto), so the one native library
  carries no OpenSSL and builds the same way for Windows x64, Linux x64 and
  Linux ARM64.
- A shim of LookingGlass's own keeps the boundary narrow: perhaps a dozen
  functions (create a group, process an incoming message, make a commit for
  these changes, encrypt, decrypt, export a key, save and load state), each
  taking and returning byte buffers. Narrow means auditable and easy to keep
  memory-safe from C#. Generic bindings (uniffi) expose the library's whole
  object model across the boundary, with callbacks for storage, which is more
  to get wrong and harder to version.
- The cost of this choice: OpenMLS changes its API between minor versions,
  and it is still producing security advisories at a steady rate (five since
  late 2025 [5]). The shim must be kept up to date, and a release of
  LookingGlass would follow every OpenMLS security release, by a plugin
  update to every player.

### Shipping a native library in the plugin

- **It is already done.** The plugin depends on NSec.Cryptography, which
  brings libsodium as a native library. The release zip already carries
  `runtimes/win-x64/native/libsodium.dll` (and, unnecessarily, libsodium for
  Android, iOS, macOS and Linux: about 19 MB of the zip is other platforms'
  copies, worth trimming whatever happens). Since testers on Linux and Steam
  Deck run the plugin, a native DLL from the plugin folder already works under
  Wine and Proton. Dalamud's plugin load context resolves native libraries
  from `runtimes/<rid>/native` and from the plugin folder [17].
- **What would be new:** a DLL built from Rust rather than a widely used C
  library. Points a spike would have to check, if a trigger ever fires:
  - build with the MSVC target and a statically linked C runtime
    (`+crt-static`), so it needs no Visual C++ redistributable [18];
  - Rust's standard library since 1.78 imports `ProcessPrng` from
    `bcryptprimitives.dll`, which Wine only gained in mid-2023; very old Wine
    builds would fail to load it [19];
  - unsigned Rust DLLs are known to trip Microsoft Defender's machine-learning
    detections now and then [20]; signing the DLL helps, and players would
    need to be told what to do if it happens;
  - it runs inside the game process: a Rust panic that crosses the C boundary
    would take the game down, so every shim function must catch panics and
    return an error.
- **Distribution:** the plugin is loaded as a dev plugin today and isn't in a
  repository yet. A custom repository has no build constraints [21]. The
  official repository builds from source in a container with no Rust
  toolchain and no network, so a Rust library could only get there as a
  prebuilt binary (committed, or in a NuGet package), subject to its informal
  code review [22]. Precedent exists for native libraries in official plugins
  (ChatTwo ships SQLite's) [23]; a project-built Rust DLL may draw more
  questions. (Separately from MLS: the official repository now asks plugins
  to disclose AI use and rejects fully AI-generated submissions [24], which
  matters for the public release whatever is decided here.)

### Does the server need MLS code?

Not necessarily, but it should have it.

- **The minimum**: to sequence commits, the server only needs each message's
  group, epoch and content type, which sit unencrypted at the start of every
  MLS message. A small parser in C# is enough to accept the first commit for
  each epoch and refuse the rest, store everything in order, and refuse
  application messages for an old epoch. RFC 9750 describes exactly this kind
  of delivery service [25].
- **What that leaves open**: a malicious member can send a commit that is
  well formed on the outside but invalid inside (a bad tree, bad signatures).
  A server that doesn't check would store and relay it, and every member
  would refuse it, leaving the channel stuck until someone resyncs. Discord's
  DAVE review found a closely related issue [12]. With OpenMLS's public-group
  mode, the server can check every commit's structure and signatures against
  the public tree before accepting it, and it can also check the commit
  against the membership log (an add only for an accepted invite, a remove
  only for a remove or leave entry). It still can't check path secrets
  encrypted to individual members (RFC 9420 §16.12 [26]), so the client-side
  resync is needed regardless.
- **Cost of having it**: the same shim, built for Linux ARM64 and x64 in the
  server's self-contained package; the server's CI already builds both.

**Verdict:** OpenMLS with a LookingGlass shim is acceptable if MLS ever goes
ahead. The library landscape is the least of the obstacles.

---

## 3. How LookingGlass's features would map onto MLS

### The shape of an MLS LookingGlass

For readers new to MLS: a group (here, a channel) has a binary tree whose
leaves are the members. Each leaf has a credential (here, the user's identity),
a signature key and an encryption (HPKE) key. A **commit** moves the group to
its next **epoch**: it applies proposals (add, remove, update) and refreshes
the keys along the committer's path in the tree. Every member processes every
commit, in order, and derives the same epoch secret, from which message keys
are ratcheted (each message key is used once and deleted). A new member gets a
**Welcome** (sent by whoever adds them), or joins on their own with an
**external commit** using the group's published **GroupInfo**. MLS relies on
the **delivery service** (here, the server) to deliver commits in one order,
and leaves to the application the **authentication service**: deciding which
credentials belong to which people, and who may make which changes.

The most plausible design for LookingGlass, if it were done:

- **The membership log stays**, as the authorization layer: ranks, invites,
  accepts, declines, cancels, removals, leaves, rank changes and key recovered
  entries are still signed log entries, checked by every client exactly as
  today. MLS has nothing that replaces them. (The IETF's MLS extensions work
  has room for application data in the group context, but putting ranks there
  would mean re-implementing the log's rules inside MLS validation, with less
  history and no invites.)
- **Every commit names the log position it was made for**, in a group context
  extension, and every client checks after each commit that the tree's members
  (by identity key) are exactly the log's members at that position. A commit
  that breaks this is refused, and the channel is marked "check members", as a
  fork is today.
- **The leaf signature key is the identity Ed25519 key**, so fingerprints,
  pinning and comparing over /tell stay as they are, and the server still
  can't put a key in the tree that no member admitted. (MLS signs a structure
  that starts with a one-byte length and a label beginning "MLS 1.0 ", and
  every LookingGlass payload starts with a 4-byte length whose first byte is
  zero, then a `lookingglass/...` domain, so one kind of signature can never be
  taken for the other. A reviewer should still confirm this.)
- **The server becomes the sequencer**: it accepts the first commit for each
  epoch, refuses later ones as stale (as `ApplyRekey` refuses a stale epoch
  today), stores commits in order with the messages, and relays them in that
  order. It can do this by reading the plaintext header of each MLS message
  (group, epoch, content type) without a full MLS library; checking commits
  fully would need the library on the server too.
- **Joins and recoveries use external commits**: the joiner (after their
  accept entry) or the re-verified member (after their key recovered entry)
  joins on their own from the latest GroupInfo the server keeps, and clients
  accept such a commit only when the log authorizes it. This removes the need
  for another member to be online to let someone in, which is a real
  improvement over today. One caution: a 2026 analysis of RFC 9420 (Cremers
  et al., "ETK", EuroCrypt 2026 [27]) shows that external commits and proposals
  can be used to undermine post-compromise security, and proposes a hardened
  variant using pre-shared keys. A design that leans on external commits
  gives up part of the very property that motivates MLS here, unless it adopts
  such a fix.

### Feature by feature

| Feature | Today | With MLS | Lost, gained, or just different |
| --- | --- | --- | --- |
| Creating a channel | Genesis entry, epoch 0 key sealed to the creator, encrypted name | Genesis entry and a new MLS group of one; the name encrypted under a key exported from the epoch | Different, not better |
| Invites | Invite entry; the name sealed to the invitee's X25519 key; no key until they accept | The same: MLS has no pending invites, so this stays app-level | No change |
| Accepting (joining) | Accept entry; the server asks an online member to rekey | Accept entry, then the joiner joins by external commit from the stored GroupInfo, or a member adds them from a KeyPackage | Gained: no member needs to be online. New: the server stores GroupInfo (about 140 KB for a full channel) after every commit; frame limit raised |
| Leaving | Leave entry; rekey pending; the server refuses messages until a member rekeys | Leave entry plus a Remove proposal; another member commits it (MLS doesn't let a member commit their own removal, except with the draft "self-remove" extension) | The same shape as today |
| Removing (kick) | Remove entry; the remover rekeys at once | Remove entry and the remover's commit, sent together and applied in one transaction | Slightly cleaner: the removal and the new epoch are one step |
| Ranks, admin, moderators | Signed log entries | Unchanged, app-level | No change |
| Signed membership log | The single source of truth | Kept, plus the tree; every commit checked against it | New risk: two sources of truth to keep in lockstep |
| Rekeys and epochs | On every membership change only; one 256-bit random key sealed to each member (about 100 KB at 500 members) | Every membership change is a commit (a few KB); periodic update commits needed for PCS | Gained: FS within an epoch, PCS after updates, smaller commits. New: commit retention, ordering, resync |
| Concurrent changes | The server accepts one rekey per epoch at the log head; losers refetch and retry | The same, for commits; losers process the winner and redo their change | No real change; the existing pattern carries over |
| Forks | Two validly signed log entries at one position are reported | The same for the log; for MLS, members in different branches simply can't decrypt each other, and a client that sees two commits for one epoch reports it | About equal |
| Catch-up (7 days, 5,000 messages) | Ciphertext stored with its epoch; old epoch keys fetched sealed to the member; first-epoch authorization; dating rules | Commits and messages stored in one ordered stream; a returning member processes every commit since they left, reading messages between them. First-epoch authorization stays. Dating rules stay (the commit's signed authenticated data carries its time and log position, replacing `created_unix_ms`) | Different, and harder: the server must keep every commit since the oldest point any member is at, or members further behind must resync. Live and caught-up messages must be processed strictly in order, because message keys are deleted as they are used: the current "gap" handling (live messages taken first, missed ones fetched later) would need MLS's out-of-order window raised or would lose messages |
| Forward secrecy vs stored messages | None: the server keeps sealed epoch keys (4 epochs, up to 64 while they have stored messages), sealed to the long-term key | Message keys deleted once used; a later copy of the state can't read what was already read | Gained, with the limits in section 1 |
| Members away longer than commits are kept | Fetch the newest key; older messages are gone anyway | Must resync: an external commit replacing their own leaf, which every member must accept as authorized | New flow, new rules, new tests |
| Local chat log (opt-in) | Plaintext lines under the log's own key | Unchanged | FS gives nothing to players who keep it |
| Channel windows history | In memory, from the session | Unchanged | None |
| Multiple devices | None: keys are per computer; a new computer re-verifies | Could be modelled as several leaves per user | Possible later, not needed now |
| Identity keys, Lodestone verification | Ed25519 and X25519 identity keys, registered through the Lodestone | Unchanged; the Ed25519 key also signs as the MLS leaf | None |
| Identity reset and "recovery restores everything" | Server-appended key recovered entry; the place moves; a member rekeys; placeholder name if nobody holds the key | The key recovered entry stays; the new keys rejoin by external commit removing the old leaf; the placeholder rule stays | The trust is exactly as today (the server's word). Gained: the recovered member can rejoin without another member online |
| Places under old keys, "Remove from my list" | Old keys stay in the log and are sealed to | Old leaves stay in the tree and are encrypted to | No change |
| Server moves keeping identity | Keys, login and channel state copied to the new address | The MLS state must *move*, not be copied: two copies of one leaf used in turn would reuse message generations, which other members reject (and which weakens AES-GCM's nonce safety to MLS's 32-bit reuse guard). The old copy must be marked moved, and resync before any use | New hazard |
| Secrets file and `.bak` backups | One DPAPI-protected file, rewritten on change, previous copy kept | Group state (up to about 7 MB for 50 full channels) needs its own files; restoring an old copy is a rollback that must be detected and resynced | New hazard and new storage code |
| Key pinning, fingerprints, advanced-mode comparison, key-change warnings | Over identity keys | Unchanged (leaf signature key = identity key) | None |
| Blocking | Client-side | Unchanged | None |
| Operator bans | On the Lodestone character; places stay | Unchanged; a banned member's leaf stays until removed | None |
| Local chat (friends only) | One key per message, sealed to each recipient's X25519 key, signed | Unchanged: an ad hoc, changing set of nearby friends isn't a group, and MLS would cost a commit per change of who is near | None. Local chat stays outside MLS either way |
| Online presence | The server's word | Unchanged | None |
| "Show in game chat", windows only | Routing of decrypted lines | Unchanged | None |
| Messages | XChaCha20-Poly1305 under the epoch key, random nonce, signed by the sender's identity key; the server checks the signature before relaying | MLS PrivateMessage: signed by the leaf and encrypted; the server can no longer check the signature, only size, epoch and rate | Lost: the server's early rejection of garbage. Gained: per-message keys |
| Channel names | Encrypted under the epoch key, signed, bound to the log position and a revision | The same, under a key exported from each epoch | No real change |
| Replay protection | Seen-sets, clock windows, newest time per sender | MLS stops replays within an epoch; the app rules still needed across epochs and for catch-up | Slight gain |
| Debug tooling, echo bot, `lgdev` | Managed code | All need the native library too | More to build and ship |

## 4. The open questions MLS touches

How the evaluation answered the design's open questions, and what the owner
decided.

- **Moving to MLS** ("plan the switch as its own milestone, including how
  existing channels migrate"): answered by this document. Recommendation: drop
  it as a milestone, record the triggers in section 1. Decided: not moving;
  dropped as a milestone.
- **Key-change policy** ("warn and continue, or block until re-verified?"):
  MLS doesn't change the answer, since keys still come from the server on first
  use and through Lodestone recovery. Recommendation: keep warn and continue
  as the default, and add the optional strict mode (H4) for channels or players
  who want it. Don't block by default: in a game community most key changes are
  new computers, and blocking would train players to click through. Decided:
  as recommended, with H4 after the public release.
- **Secret storage on Wine and Proton**: MLS makes this matter more, not less,
  because its benefits assume the state on disk is protected. Recommendation:
  an optional passphrase on Wine and Proton (H5), whatever happens with MLS.
  Decided: H5, after the public release.
- **Limits**: MLS would force the frame limit up (Welcome and GroupInfo for
  500 members are about 140 KB). Not an issue on the chosen path.

---

## 5. Migration (only if MLS ever goes ahead)

Kept short, since it isn't recommended.

- **For today's testers: a hard cutover.** The tester group is small and
  closed, plugin and server are already updated together, and the protocol
  version mechanism already refuses older plugins at `Hello`. A new protocol
  version (4) would mark MLS channels. Existing channels can't be converted
  without every member online, so the simplest safe path is the one used for
  0.1 to 0.2: channels are recreated. The admin's client could make this
  easier: create the MLS channel under the same name, and invite every current
  member with their pinned keys, ranks carried over by the admin signing the
  new log's rank entries.
- **What is lost in a cutover**: stored catch-up messages (they are under old
  epoch keys; let them expire rather than convert), and nothing else: chat logs
  on players' computers, channel numbers, nicknames, colours and windows are
  client settings and can be mapped to the new channel ID.
- **After a public release: channel by channel.** Each channel would carry a
  format (sealed keys or MLS), fixed at creation and changed only by an
  admin's signed "upgrade" log entry once every member's client offers an MLS
  capability. The rule that keeps this safe: **a channel's format only ever
  moves forward**, every client refuses to send under the old format once it
  has seen the upgrade entry, and the server can't change the format (it is in
  the signed log). Neither format ever falls back to plaintext.
- **Rollback**: before cutover, keep the old server and database untouched;
  rolling back is reinstalling the previous server and plugin, with the
  channels as they were. After players have used MLS channels, rollback means
  recreating channels again.

---

## 6. Work breakdown, phases and tests

### The chosen path

| Phase | Work | Size |
| --- | --- | --- |
| 1 | Correct the MLS section of the design, record the decision and triggers | S (done 2026-10-09) |
| 1, in parallel | H1 sign-in notices and device list | S to M (in progress) |
| 1, in parallel | H2 log head in messages | S (in progress) |
| 1, in parallel | H3 maximum epoch age | S (done: merged) |
| 2 | Protocol specification and threat model for the reviewers, drawn from the finalized design and the code | M |
| 3 | Adversarial reviews by several models, against the specification and the code | The owner's time to run them and sort the findings |
| 4 | Fix the findings, and review the fixes the same way | Unknown until the reviews; plan for M |
| After the public release | H4 strict mode, H5 Wine/Proton passphrase | S each |

**Scope for the review**, in order of where the risk is: the membership log
and its rules (including key recovered entries); identity recovery, reset and
retirement; message catch-up and its dating rules; registration, codes and
signed addresses (relay protection); local chat; the epoch-key layer, names
and messages; secrets storage on Windows and Wine. The malicious-server and
malicious-member test suites are part of it: reviewers should say which
attacks they cover and which they don't.

### How the review is done

The owner's choice (2026-10-09) in place of a commissioned review. Once the
design is finalized:

- **Several different models**, from more than one provider, each reviewing on
  its own, so that one model's blind spots aren't shared by all of them.
- **Adversarial, not descriptive.** Each reviewer is given the specification,
  the claimed properties and the accepted limitations, and asked to break
  them: to find a message, a sequence of server or member actions, or a stored
  state that leads to a key reaching someone it shouldn't, a forged or
  replayed message being accepted, a member list the log doesn't support, or a
  limitation worse than the one documented.
- **One area at a time**, in the order of the scope above, with the
  specification's part for that area and the code that implements it, rather
  than the whole repository at once.
- **Findings must be concrete**: the steps of the attack and the code or rule
  it relies on. Each is checked against the code before it counts, and a
  confirmed one gets a test (in the malicious-server or malicious-member
  suites where it fits) before it is fixed.
- **Repeated after the fixes**, and for later changes to the cryptography.

Model reviews are cheap and can be repeated, which suits a project that
changes daily; they are not the same as a human cryptographer's review, which
is one reason the 1.0 release promises no post-compromise security and keeps
its documented limitations in plain view.

### The MLS path, for comparison

If a trigger fires, this is the order, with a spike first. (No spike is
planned now; decided 2026-10-09.)

| Phase | Work | Size |
| --- | --- | --- |
| 0. Spike (prove it before committing) | A minimal Rust shim around the chosen library with a C ABI; load it in the plugin on Windows, Wine/Proton and Steam Deck; run create, add, commit, encrypt and decrypt for a 500-member group off the game thread; measure sizes (commit, Welcome, GroupInfo, state per group) and times; build the server on Linux ARM64; check antivirus scanners against the unsigned DLL | M |
| 1. Design | A written design for the binding (log position in group context, tree-equals-log rule, who commits, external-commit authorization, resync, commit retention, state storage and rollback detection) and its review | M |
| 2. Core | The shim, the C# wrapper, group state storage, new `IGroupKeyProvider` shape, client session rework (channels, rekeys, catch-up) | XL |
| 3. Server | Commit sequencing and storage, GroupInfo storage, frame limits, retention, optional commit checking with the library | L |
| 4. Tests | New malicious-server suite (reordered, withheld and forked commits, stale GroupInfo, replayed Welcomes), malicious-member suite (commits that break the log rule, bad path secrets for some members, unauthorized external joins), catch-up suite rewritten, crash and rollback tests for state | L |
| 5. Migration and release | Version 4, cutover tooling for admins, checklists | M |
| 6. Review | Adversarial multi-model review of the MLS integration, as above | The owner's time |

**Decision points**: after the spike (does the native library behave in the
game on every platform, and are the sizes acceptable?); after the design
review (is the binding sound?); before release (do the new test suites pass,
including the existing acceptance tests for the membership log?).

### Test strategy on either path

Keep the existing approach: unit tests for every rule, end-to-end tests with a
real server, and the malicious-server and malicious-member suites that inject
forged, replayed and reordered events. For H2, add tests where the server
shows two members different logs (including different key recovered entries)
and both are warned. For H1, tests that a key login from a new device is
announced to the account's other sessions, and that **Sign out everywhere
else** revokes them.

---

## 7. Decisions for the owner

The questions the evaluation put to the owner, its recommendation, and what
was decided on 2026-10-09.

1. **Move to MLS, or not?** Recommended: not now, and not as a planned
   milestone; record the triggers for revisiting it (section 1).
   **Decided: as recommended.**
2. **An independent review before the public release?** Recommended: yes, of
   the current protocol, against a written specification.
   **Decided: no commissioned or paid review; adversarial reviews by several
   models of the finalized design and implementation instead**
   ([How the review is done](#how-the-review-is-done)). The written
   specification stays, as the reviewers' input.
3. **Which hardening to do before the public release?** Recommended: H1
   (sign-in notices and device list), H2 (log head in messages) and H3
   (maximum epoch age) before release; H4 (strict mode) and H5 (Wine/Proton
   passphrase) after. **Decided: as recommended.**
4. **Is post-compromise security a property you want to promise?**
   Recommended: no, not for 1.0. If yes, that is the trigger to revisit MLS
   rather than to build it by hand. **Decided: no promise in 1.0.**
5. **Run the MLS spike anyway, as cheap insurance?** Recommended: only if a
   trigger is likely within the next year (clients outside the game, for
   example). Otherwise skip it; the library landscape will have changed by the
   time it matters. **Decided: no spike.**
6. **Correct the design document's MLS section now?** Recommended: yes, so it
   no longer says the switch would leave server routing untouched.
   **Decided: yes; done.**

---

## Sources

Library facts were checked on 2026-10-09. Where a source didn't settle
something, the text says so.

1. OpenMLS repository and README (ciphersuites, platforms, bindings): https://github.com/openmls/openmls ; versions: https://crates.io/api/v1/crates/openmls/versions ; changelog: https://github.com/openmls/openmls/blob/main/CHANGELOG.md
2. Wire core-crypto's dependency on a Wire fork of OpenMLS: https://raw.githubusercontent.com/wireapp/core-crypto/main/Cargo.toml
3. Phoenix Air (AGPL-3.0): https://github.com/phnx-im/air ; NCC Group's XMTP review (OpenMLS's own scope unclear): https://www.nccgroup.com/research/public-report-xmtp-mls-implementation-review/
4. OpenMLS independent security audit (SRLabs, Sovereign Tech Agency): https://blog.phnx.im/openmls-independent-security-audit/ ; funding: https://sovereign.tech/tech/openmls
5. OpenMLS security advisories: https://github.com/openmls/openmls/security/advisories (the latest: https://github.com/openmls/openmls/security/advisories/GHSA-gc79-23g3-8g52)
6. AWS mls-rs (licence, providers, conformance, "not yet received a full security audit"): https://github.com/awslabs/mls-rs ; versions: https://crates.io/api/v1/crates/mls-rs/versions ; API: https://docs.rs/mls-rs/latest/mls_rs/
7. mls-rs in Firefox: https://searchfox.org/firefox-main/source/third_party/rust/mls-rs/README.md ; in AOSP: https://android.googlesource.com/platform/external/rust/crates/mls-rs/+/51f31cc
8. mls-rs-uniffi: https://raw.githubusercontent.com/awslabs/mls-rs/main/mls-rs-uniffi/src/lib.rs and https://crates.io/api/v1/crates/mls-rs-uniffi/versions ; mls-rs-ffi: https://raw.githubusercontent.com/awslabs/mls-rs/main/mls-rs-ffi/Cargo.toml
9. uniffi-bindgen-cs: https://github.com/NordSecurity/uniffi-bindgen-cs and its releases https://api.github.com/repos/NordSecurity/uniffi-bindgen-cs/releases?per_page=6
10. Cisco MLSpp: https://github.com/cisco/mlspp
11. Discord libdave (depends on MLSpp): https://github.com/discord/libdave ; DAVE protocol: https://daveprotocol.com/
12. Trail of Bits' review of DAVE (2024): https://trailofbits.com/library/discord-dave-2024/
13. Wire core-crypto: https://github.com/wireapp/core-crypto
14. DotnetMls: https://www.nuget.org/packages/DotnetMls/ ; Universal.Common.Security.Mls: https://www.nuget.org/packages/Universal.Common.Security.Mls ; GBPStack: https://www.nuget.org/packages/GBPStack ; NostrNet.Marmot: https://www.nuget.org/packages/NostrNet.Marmot/
15. BouncyCastle Java MLS: https://www.bouncycastle.org/resources/support-for-mls-improved-osgi-support-and-several-security-updates/ ; bc-csharp sources (no MLS): https://github.com/bcgit/bc-csharp
16. OpenMLS API and book (external commits, custom proposals, storage): https://docs.rs/openmls/latest/openmls/group/struct.MlsGroup.html , https://book.openmls.tech/ , https://docs.rs/openmls_traits/latest/openmls_traits/
17. Dalamud's plugin load context (native library resolution): https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Internal/Loader/ManagedLoadContext.cs
18. Rust static C runtime on Windows: https://rust-lang.github.io/rfcs/1721-crt-static.html
19. Wine and `ProcessPrng`: https://list.winehq.org/hyperkitty/list/wine-gitlab@list.winehq.org/thread/7Y6GFGJS24X43WRXMP63LDVRAMNWJ36Q/
20. Defender false positives on Rust binaries (one example): https://users.rust-lang.org/t/trojan-win32-bearfoos-a-ml/137445
21. Dalamud plugin restrictions (custom repositories): https://dalamud.dev/plugin-publishing/restrictions
22. Official repository build pipeline: https://github.com/goatcorp/Plogon (BuildProcessor.cs, Dockerfile) and https://github.com/goatcorp/DalamudPluginsD17
23. ChatTwo's SQLite dependency: https://raw.githubusercontent.com/Infiziert90/ChatTwo/main/ChatTwo/ChatTwo.csproj
24. Dalamud AI usage policy: https://dalamud.dev/plugin-publishing/ai-policy
25. RFC 9750, MLS Architecture (delivery service ordering, partitions, multi-device, idle clients): https://www.rfc-editor.org/rfc/rfc9750
26. RFC 9420, MLS (ordering §14, compromised DS §16.9, malicious insiders §16.12, deletion schedule §9.2, external joins §12.4.3): https://www.rfc-editor.org/rfc/rfc9420
27. Cremers et al., "ETK: External-Operations TreeKEM and the Security of MLS in RFC 9420", EuroCrypt 2026: https://cispa.de/en/research/publications/212965-etk-external-operations-treekem-and-the-security-of-mls-in-rfc-9420
28. MLS extensions draft (not yet an RFC): https://datatracker.ietf.org/doc/draft-ietf-mls-extensions/
29. OpenMLS performance at scale (Welcome and GroupInfo grow linearly with the group): https://arxiv.org/html/2502.18303v2

Sizes in this document for a 500-member channel (a tree of about 140 KB, so
Welcome and GroupInfo around that) are the evaluation's estimate from the
structures in RFC 9420, consistent with [29]; a spike would measure them.
