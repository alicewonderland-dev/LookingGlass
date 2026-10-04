using System.Collections.Immutable;
using System.Net.WebSockets;
using WonderlandChat.Protocol;

namespace WonderlandChat.Core.Client;

public enum ConnectionState {
    Stopped,
    Connecting,
    /// <summary>Connected, but this character has no account on the server yet.</summary>
    Unregistered,
    /// <summary>A registration challenge is waiting to be completed.</summary>
    Registering,
    Ready,
    Reconnecting,
}

/// <summary>
/// An immutable view of the session. The UI only ever reads these; a new one
/// is published after every change.
/// </summary>
public sealed record SessionSnapshot(
    ConnectionState State,
    string? StatusText,
    User? Me,
    string? MyFingerprint,
    ImmutableArray<ChannelView> Channels,
    ImmutableArray<InviteView> Invites,
    Limits? Limits,
    bool DebugAccountsEnabled,
    RegistrationChallenge? PendingChallenge) {
    public static readonly SessionSnapshot Empty = new(
        ConnectionState.Stopped, null, null, null,
        ImmutableArray<ChannelView>.Empty, ImmutableArray<InviteView>.Empty,
        null, false, null);

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
public sealed record ChannelView(
    string Id,
    string? Name,
    ulong Epoch,
    ulong ServerEpoch,
    bool HasKey,
    bool RekeyPending,
    Rank MyRank,
    ImmutableArray<MemberView> Members) {
    public string DisplayName => this.Name ?? PlaceholderName(this.Id);

    /// <summary>What to show before a channel's name has been decrypted. Safe for IDs of any length.</summary>
    public static string PlaceholderName(string id) => $"(encrypted channel {(id.Length > 8 ? id[..8] : id)})";
}

public sealed record MemberView(User User, Rank Rank, string? Fingerprint, bool KeyChanged);

public sealed record InviteView(string ChannelId, User Inviter, string? ChannelName, bool Verified, DateTimeOffset Created);

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

/// <summary>The server answered a request with an error.</summary>
public sealed class ServerErrorException(ErrorCode code, string message) : Exception($"{message} ({code})") {
    public ErrorCode Code { get; } = code;
    public string ServerMessage { get; } = message;
}

/// <summary>The connection closed before the request was answered.</summary>
public sealed class SessionDisconnectedException(string message) : Exception(message);

public sealed class ClientSessionOptions {
    public required Uri ServerUri { get; init; }
    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>Opens the WebSocket. Defaults to <see cref="ClientWebSocket"/>; tests replace it.</summary>
    public Func<Uri, CancellationToken, Task<WebSocket>>? Connect { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectMinDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

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
}
