using System.Collections.Immutable;
using System.Net.WebSockets;
using LookingGlass.Core.Crypto;
using LookingGlass.Core.Membership;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

public enum ConnectionState {
    Stopped,
    Connecting,
    /// <summary>Connected, but this character has no account on the server yet.</summary>
    Unregistered,
    /// <summary>A registration challenge is waiting to be completed.</summary>
    Registering,
    Ready,
    Reconnecting,
    /// <summary>
    /// Connected, but the server doesn't recognise this device's saved login (the wrong server, or one that was
    /// reset or restored), and signing in with the identity key didn't work either: the server doesn't know the
    /// account or this key, or there is no key. The login is kept and tried again now and then; registering again replaces it.
    /// </summary>
    LoginNotRecognized,

    /// <summary>
    /// The server's operator blocked this character, or the address this computer connects from (a ban): the server refuses
    /// it, and the session tries again only now and then (<see cref="ClientSessionOptions.BlockedRetryDelay"/>), or when asked to.
    /// </summary>
    Blocked,
}

/// <summary>
/// An immutable view of the session. The UI only ever reads these; a new one
/// is published after every change.
/// </summary>
/// <param name="ChannelsLoaded">
/// <see cref="Channels"/> is the server's complete list, fetched on this connection.
/// Until then it may be empty or left over from an earlier connection, so a channel
/// missing from it may still exist.
/// </param>
/// <param name="LoginRejected">
/// The server refused this device's saved login on this connection, and a key login. The login is kept and tried again now and then;
/// registering again replaces it. True in <see cref="ConnectionState.LoginNotRecognized"/>, and while registering again from it.
/// </param>
/// <param name="AddressNotListed">
/// Set while connected to a server that lists its own addresses (Welcome's public_urls) without the one this client uses:
/// what to tell the user, naming both. Such a server refuses registering, key login and "Reset my identity" through it.
/// </param>
/// <param name="NewIdentity">
/// This client has no identity keys registered on this server yet (none at all, as on a new computer or with a lost file, or
/// new ones after "Reset my identity"). Registering them takes the character's account over if it has one here: its
/// channels, ranks and invites move to the new keys.
/// </param>
/// <param name="StatusText">The connection's status, in advanced mode's words. See <see cref="StatusFor"/>.</param>
/// <param name="PlainStatusText">The same, in simple mode's words (see <see cref="Wording"/>).</param>
/// <param name="PlainAddressNotListed"><paramref name="AddressNotListed"/>, in simple mode's words.</param>
public sealed record SessionSnapshot(
    ConnectionState State,
    string? StatusText,
    User? Me,
    string? MyFingerprint,
    ImmutableArray<ChannelView> Channels,
    ImmutableArray<InviteView> Invites,
    Limits? Limits,
    bool DebugAccountsEnabled,
    RegistrationChallenge? PendingChallenge,
    ImmutableArray<User> BlockedUsers,
    bool ChannelsLoaded,
    bool LoginRejected = false,
    string? AddressNotListed = null,
    bool NewIdentity = false,
    string? PlainStatusText = null,
    string? PlainAddressNotListed = null) {
    public static readonly SessionSnapshot Empty = new(
        ConnectionState.Stopped, null, null, null,
        ImmutableArray<ChannelView>.Empty, ImmutableArray<InviteView>.Empty,
        null, false, null, ImmutableArray<User>.Empty, false);

    /// <summary>
    /// The account's devices (logins) as last listed, oldest first, one of them this computer's (see "Other computers signing in"
    /// in docs/design.md). Kept while disconnected; empty until first listed, and with a server that doesn't keep the list.
    /// </summary>
    public ImmutableArray<DeviceView> Devices { get; init; } = ImmutableArray<DeviceView>.Empty;

    /// <summary>Logged in, on a server that lists the account's devices and signs them out (capability "devices.v1").</summary>
    public bool DevicesAvailable { get; init; }

    /// <summary>
    /// With <see cref="LoginRejected"/>: the server refused the identity key because another computer used "Sign out everywhere
    /// else" (<see cref="DeviceWords.SignedOutElsewhere"/>), not because it doesn't know the login or the key.
    /// </summary>
    public bool SignedOutElsewhere { get; init; }

    /// <summary>The status in a mode's words. Never null where <see cref="StatusText"/> isn't: a missing plain text falls back to it.</summary>
    public string? StatusFor(bool advanced) => advanced ? this.StatusText : this.PlainStatusText ?? this.StatusText;

    /// <summary><see cref="AddressNotListed"/> in a mode's words; null in both or neither.</summary>
    public string? AddressNotListedFor(bool advanced) => advanced ? this.AddressNotListed : this.PlainAddressNotListed ?? this.AddressNotListed;

    public ChannelView? FindChannel(string channelId) {
        foreach (var channel in this.Channels) {
            if (channel.Id == channelId) {
                return channel;
            }
        }

        return null;
    }
}

/// <param name="Epoch">The newest epoch this client holds a key for (what it sends with), or the server's epoch if it holds none.</param>
/// <param name="ServerEpoch">The epoch the server last reported. Only a hint: the server can claim anything.</param>
/// <param name="HasKey">The client holds a key for the server's current epoch.</param>
/// <param name="MyRank">This user's rank in the verified membership log; Unspecified if not a member under their current keys.</param>
/// <param name="Members">Members and invitees according to the verified membership log, never the server's list.</param>
/// <param name="LogHead">The newest membership log entry this client has verified.</param>
/// <param name="MembershipWarning">
/// Something wrong with the channel's membership the user should know about (a fork, a hidden change), in advanced mode's
/// words; <paramref name="PlainMembershipWarning"/> in simple mode's. See <see cref="WarningFor"/>.
/// </param>
/// <param name="OldKeyMembership">
/// The verified log has this user as a member under identity keys they no longer have (they registered again with new
/// keys before registering moved channels along, or the server didn't move this one). Nothing can be done here with the
/// current keys: not reading, sending or leaving (a leave must be signed by the old keys). Offer "Remove from my list"
/// (<see cref="ClientSession.ForgetChannelAsync"/>) instead of Leave.
/// </param>
/// <param name="KeyMovedAway">
/// With <see cref="OldKeyMembership"/>: the log moved this user's place to keys this client doesn't hold (a key recovered
/// entry: their character was re-verified with other keys, normally their own on another computer). Say so, and that
/// "Reset my identity" takes it back if it wasn't them (<see cref="PlainMessages.KeyMovedAwayChannel"/>).
/// </param>
public sealed record ChannelView(
    string Id,
    string? Name,
    ulong Epoch,
    ulong ServerEpoch,
    bool HasKey,
    bool RekeyPending,
    Rank MyRank,
    ImmutableArray<MemberView> Members,
    LogPosition? LogHead = null,
    string? MembershipWarning = null,
    bool OldKeyMembership = false,
    bool KeyMovedAway = false,
    string? PlainMembershipWarning = null) {
    public string DisplayName => this.Name ?? PlaceholderName(this.Id);

    /// <summary>
    /// <see cref="MembershipWarning"/> in a mode's words (see <see cref="Wording"/>). Never null where it isn't: a missing
    /// plain text falls back to the technical one, so simple mode never loses a warning.
    /// </summary>
    public string? WarningFor(bool advanced) => advanced ? this.MembershipWarning : this.PlainMembershipWarning ?? this.MembershipWarning;

    /// <summary>The name, or in simple mode's words (see <see cref="Wording"/>) what to show before it is known.</summary>
    public string DisplayNameFor(bool advanced) => this.Name ?? (advanced ? PlaceholderName(this.Id) : PlainPlaceholderName(this.Id));

    private const string Placeholder = "(encrypted channel ";
    private const string PlainPlaceholder = "(channel ";

    /// <summary>What to show before a channel's name has been decrypted. Safe for IDs of any length.</summary>
    public static string PlaceholderName(string id) => $"{Placeholder}{(id.Length > 8 ? id[..8] : id)})";

    /// <summary><see cref="PlaceholderName"/> in simple mode's words.</summary>
    public static string PlainPlaceholderName(string id) => PlainNames(PlaceholderName(id));

    /// <summary>Text for simple mode with every <see cref="PlaceholderName"/> in it in simple mode's words.</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public static string? PlainNames(string? text) => text?.Replace(Placeholder, PlainPlaceholder, StringComparison.Ordinal);
}

/// <param name="Fingerprint">Of the keys the membership log binds them to.</param>
/// <param name="KeyChanged">Their keys changed since this client first saw them, and the user hasn't marked the new ones verified.</param>
/// <param name="FingerprintCompared">
/// The user marked these keys verified (compared fingerprints over /tell). Until then their keys
/// are trusted on first use: whoever invited them took them from the server.
/// </param>
/// <param name="KeyReplaced">
/// They registered again: the server has other keys for them now, which aren't a member until
/// someone removes them and invites them again.
/// </param>
/// <param name="NewFingerprint">
/// If <paramref name="KeyReplaced"/>, the fingerprint of the keys they registered again with. <see cref="KeyChanged"/>
/// is about those keys, so it is this fingerprint, not <paramref name="Fingerprint"/>, that is shown and marked verified.
/// </param>
/// <param name="Online">
/// Connected to the server right now, as the server last said on this connection. Yourself while you are
/// connected. Always false for invitees (their presence isn't shared until they join) and while disconnected.
/// </param>
/// <param name="KeyRecovered">
/// Their key changed because they re-verified their character with a new one (the channel's membership log says so), and
/// the user hasn't compared the new one yet. Expected, so not <see cref="KeyChanged"/>'s warning, but worth showing: that it
/// is really them is the server's word.
/// </param>
public sealed record MemberView(User User, Rank Rank, string? Fingerprint, bool KeyChanged, bool FingerprintCompared = false, bool KeyReplaced = false,
    string? NewFingerprint = null, bool Online = false, bool KeyRecovered = false);

/// <param name="Verified">
/// The invite is open in the channel's verified log, for this user's current keys, made by the inviter it names, and its
/// channel name (if it has one) is signed by the key the log says they invited with.
/// </param>
/// <param name="ChannelName">
/// Null until verified, and for an invite made for keys this user had before re-verifying their character (the log moved it to
/// their new keys, but the name in it was sealed to the old ones): that one shows once they have joined.
/// </param>
/// <param name="InviterKeyChanged">
/// The inviter's identity key changed (or their name moved to another account)
/// and the user hasn't marked the new one verified. Don't offer Accept until they do.
/// </param>
/// <param name="InviterFingerprint">The inviter's current fingerprint, to compare over /tell.</param>
public sealed record InviteView(
    string ChannelId,
    User Inviter,
    string? ChannelName,
    bool Verified,
    DateTimeOffset Created,
    bool InviterKeyChanged,
    string? InviterFingerprint);

/// <summary>One of the account's devices (each computer signed in has a login of its own), as the server lists them.</summary>
/// <param name="Added">When the server gave it its login (a registration, or a key login).</param>
/// <param name="LastUsed">When it last logged in.</param>
/// <param name="ThisDevice">It is this computer's login.</param>
public sealed record DeviceView(DateTimeOffset Added, DateTimeOffset LastUsed, bool ThisDevice);

/// <param name="Text">The message as plain text, each link as its "[name]".</param>
public sealed record IncomingMessage(
    string ChannelId,
    string? ChannelName,
    User Sender,
    bool IsOwn,
    string? Text,
    bool Unsupported,
    DateTimeOffset Timestamp) {
    /// <summary>
    /// The links in <see cref="Text"/> that passed the checks every received link must (<see cref="MessageContent.ValidLinks"/>);
    /// before one is shown as a link, it is checked against the game's data too (<see cref="ChatLinks.Check"/>).
    /// </summary>
    public IReadOnlyList<MessageLink> Links { get; init; } = [];

    /// <summary>The text and its links (see <see cref="LinkedText.Parts"/>).</summary>
    public LinkedText Linked => new(this.Text ?? "", this.Links);

    /// <summary>
    /// Sent while this client was disconnected, and caught up from the server when it came back (see
    /// <see cref="ClientSession.MessagesCaughtUp"/>): checked as a live message is, but older. <see cref="Timestamp"/> is when
    /// it was sent.
    /// </summary>
    public bool CaughtUp { get; init; }
}

/// <summary>
/// A local chat message (see <see cref="LocalChat"/>) that opened and was signed by the key this client holds for its sender,
/// or one of the player's own once the server took it. The session doesn't know where anyone is: the plugin shows one from
/// someone else only if <see cref="LocalChat.Judge"/> says the sender is near and a friend.
/// </summary>
public sealed record IncomingLocalMessage(User Sender, bool IsOwn, string? Text, bool Unsupported, DateTimeOffset Timestamp) {
    /// <summary>The links in <see cref="Text"/> that passed the checks every received link must, as for a channel's message.</summary>
    public IReadOnlyList<MessageLink> Links { get; init; } = [];

    /// <summary>The text and its links (see <see cref="LinkedText.Parts"/>).</summary>
    public LinkedText Linked => new(this.Text ?? "", this.Links);

    /// <summary>
    /// The sender's identity as the server sent it, when no key was held for them as it arrived: it was checked against
    /// these, which <see cref="ClientSession.ConfirmLocalSender"/> pins once it is shown. Null if their keys were held.
    /// </summary>
    internal UserIdentity? FirstSeen { get; init; }

    /// <summary>The message's ID (hex), for <see cref="ClientSession.ConfirmLocalSender"/> to record it against replays once shown. Null for the player's own.</summary>
    internal string? MessageId { get; init; }
}

/// <summary>
/// The messages of one channel sent while this client was disconnected, caught up from the server when it came back,
/// oldest first, each checked as a live message is and accepted once (see "Message catch-up" in docs/design.md).
/// </summary>
public sealed record CaughtUpMessages(string ChannelId, string? ChannelName, IReadOnlyList<IncomingMessage> Messages);

public enum NoticeLevel {
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Something the user should be told.</summary>
/// <param name="Text">In advanced mode's words. See <see cref="TextFor"/>.</param>
public sealed record SessionNotice(NoticeLevel Level, string Text, string? ChannelId = null) {
    /// <summary>The same, in simple mode's words (see <see cref="Wording"/>); null if they are the same.</summary>
    public string? Plain { get; init; }

    /// <summary>What it is about.</summary>
    public NoticeKind Kind { get; init; }

    /// <summary>
    /// The words for a mode. The mode only changes the words, never whether the notice is shown: with no plain words, simple
    /// mode shows the technical ones rather than nothing.
    /// </summary>
    public string TextFor(bool advanced) => advanced ? this.Text : this.Plain ?? this.Text;

    public static SessionNotice Of(NoticeLevel level, Wording wording, string? channelId = null) =>
        new(level, wording.Technical, channelId) { Plain = wording.Plain, Kind = wording.Kind };
}

public sealed record TraceEntry(DateTimeOffset Time, bool Outgoing, string Summary);

/// <summary>
/// The server answered a request with an error. Its message is shown to the user, so it holds no registration code but
/// <paramref name="keepCode"/>, the one this client checked (see <see cref="Crypto.LodestoneCode.Redact"/>): a server
/// could otherwise answer "LGC-... isn't in your Lodestone profile yet" with another server's code.
/// </summary>
public sealed class ServerErrorException(ErrorCode code, string message, string? keepCode = null)
    : Exception($"{Crypto.LodestoneCode.Redact(message, keepCode)} ({code})") {
    public ErrorCode Code { get; } = code;

    /// <summary>What the server said, without registration codes (see the class documentation).</summary>
    public string ServerMessage { get; } = Crypto.LodestoneCode.Redact(message, keepCode);

    /// <summary>With <see cref="ErrorCode.Blocked"/>: the operator's block, as the server describes it.</summary>
    public Block? Block { get; init; }

    /// <summary>A refused key login: another computer used "Sign out everywhere else" (see <see cref="Protocol.Error.SignedOut"/>).</summary>
    public bool SignedOut { get; init; }
}

/// <summary>The connection closed before the request was answered.</summary>
public sealed class SessionDisconnectedException(string message) : Exception(message);

/// <summary>
/// The server answered "register" with a Lodestone code that wasn't made for it and this client's key (see
/// <see cref="Crypto.LodestoneCode"/>), as a server passing on another server's code would, to take the character's
/// account there once the user puts the code in their profile. The code isn't shown.
/// </summary>
public sealed class RelayedRegistrationCodeException() : InvalidOperationException(PlainMessages.RelayedRegistrationCode.Technical);

public sealed class ClientSessionOptions {
    public required Uri ServerUri { get; init; }
    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Opens the WebSocket. Defaults to <see cref="WebSocketConnector.ConnectAsync"/>, which never follows redirects; tests replace it.</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? Connect { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectMinDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before connecting again once the server says its operator blocked this character or address: much
    /// longer than <see cref="ReconnectMaxDelay"/>, so a blocked plugin doesn't keep knocking (sooner if the block ends sooner).
    /// </summary>
    public TimeSpan BlockedRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The least time between tries while blocked, even when the block is said to end sooner: a clock ahead of the server's
    /// would otherwise see it as over, and try again and again.
    /// </summary>
    public TimeSpan BlockedRetryMinDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait, at first, before trying a saved login the server didn't recognise again on the same
    /// connection. The wait doubles after every try, up to <see cref="LoginRetryMaxDelay"/>.
    /// </summary>
    public TimeSpan LoginRetryMinDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan LoginRetryMaxDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Rekey automatically when the server designates this client.</summary>
    public bool AutoRekeyWhenDesignated { get; init; } = true;

    public int MaxReceiveBytes { get; init; } = 4 * 1024 * 1024;
    public int TraceCapacity { get; init; } = 200;

    /// <summary>
    /// Diagnostic log sink. Called from background threads. Never given user
    /// content (names, channel names, messages): that only goes to <see cref="ClientSession.Notice"/>.
    /// </summary>
    public Action<NoticeLevel, string>? Log { get; init; }

    /// <summary>The clock used to judge message ages. Tests replace it.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The membership layer (who is in a channel). The signed log in v0.2.</summary>
    public IMembershipProvider Membership { get; init; } = SignedLogMembershipProvider.Instance;

    /// <summary>The group-key layer (channel keys and what they encrypt). Sealed epoch keys in v0.2.</summary>
    public IGroupKeyProvider GroupKeys { get; init; } = SealedEpochKeyProvider.Instance;

    /// <summary>The protocol version offered in Hello. Only tests change it, to play an older plugin.</summary>
    internal uint ProtocolVersion { get; init; } = ProtocolInfo.CurrentVersion;

    /// <summary>How often a channel's whole log may be fetched again to look into a possible fork. Only tests change it.</summary>
    internal TimeSpan ForkCheckInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Offer message catch-up (the "history.v1" capability) in Hello. Only tests turn it off, to play a plugin from before
    /// it (0.2.5), which never asks for missed messages.
    /// </summary>
    internal bool OfferMessageCatchUp { get; init; } = true;

    /// <summary>
    /// What a channel's catch-up asks for when this client has no position in it yet (the first login with this version, or
    /// on a new computer): the messages the server stored in this long. Those already received live before are recognised
    /// and left out; a new computer's keys can't read older ones anyway.
    /// </summary>
    public TimeSpan CatchUpWithoutPosition { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The most live messages held back while catching up; past that they are taken as they come (and the channel's missed
    /// messages judged against what was had before). Only tests change it.
    /// </summary>
    internal int MaxHeldLiveMessages { get; init; } = 2000;

    /// <summary>How long a channel's failed catch-up waits before it is tried again (doubling each time). Only tests change it.</summary>
    internal TimeSpan CatchUpRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How soon a change to the message times and catch-up positions is saved (once for all the changes meanwhile), so a crash
    /// shows little again as missed. Only tests change it.
    /// </summary>
    internal TimeSpan ReplayStateSaveDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Offer local chat (the "local.v1" capability) in Hello. Only tests turn it off, to play a plugin from before it, which
    /// the server never sends local messages to.
    /// </summary>
    internal bool OfferLocalChat { get; init; } = true;

    /// <summary>
    /// Offer the account's devices (the "devices.v1" capability) in Hello. Only tests turn it off, to play a plugin from before
    /// it, which the server never tells of a new device.
    /// </summary>
    internal bool OfferDevices { get; init; } = true;
}
