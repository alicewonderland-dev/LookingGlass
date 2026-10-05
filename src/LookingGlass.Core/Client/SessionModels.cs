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
    bool NewIdentity = false) {
    public static readonly SessionSnapshot Empty = new(
        ConnectionState.Stopped, null, null, null,
        ImmutableArray<ChannelView>.Empty, ImmutableArray<InviteView>.Empty,
        null, false, null, ImmutableArray<User>.Empty, false);

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
/// <param name="MembershipWarning">Something wrong with the channel's membership the user should know about (a fork, a hidden change).</param>
/// <param name="OldKeyMembership">
/// The verified log has this user as a member under identity keys they no longer have (they registered again with new
/// keys before registering moved channels along, or the server didn't move this one). Nothing can be done here with the
/// current keys: not reading, sending or leaving (a leave must be signed by the old keys). Offer "Remove from my list"
/// (<see cref="ClientSession.ForgetChannelAsync"/>) instead of Leave.
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
    bool OldKeyMembership = false) {
    public string DisplayName => this.Name ?? PlaceholderName(this.Id);

    /// <summary>What to show before a channel's name has been decrypted. Safe for IDs of any length.</summary>
    public static string PlaceholderName(string id) => $"(encrypted channel {(id.Length > 8 ? id[..8] : id)})";
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

public sealed record IncomingMessage(
    string ChannelId,
    string? ChannelName,
    User Sender,
    bool IsOwn,
    string? Text,
    bool Unsupported,
    DateTimeOffset Timestamp);

public enum NoticeLevel {
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record SessionNotice(NoticeLevel Level, string Text, string? ChannelId = null);

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
}

/// <summary>The connection closed before the request was answered.</summary>
public sealed class SessionDisconnectedException(string message) : Exception(message);

/// <summary>
/// The server answered "register" with a Lodestone code that wasn't made for it and this client's key (see
/// <see cref="Crypto.LodestoneCode"/>), as a server passing on another server's code would, to take the character's
/// account there once the user puts the code in their profile. The code isn't shown.
/// </summary>
public sealed class RelayedRegistrationCodeException() : InvalidOperationException(
    "This server sent a registration code that doesn't belong to it. It may be passing on another server's code. " +
    "Don't put it in your Lodestone profile.");

public sealed class ClientSessionOptions {
    public required Uri ServerUri { get; init; }
    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Opens the WebSocket. Defaults to <see cref="WebSocketConnector.ConnectAsync"/>, which never follows redirects; tests replace it.</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? Connect { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectMinDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

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
}
