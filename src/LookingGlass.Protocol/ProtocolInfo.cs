namespace LookingGlass.Protocol;

/// <summary>Constants shared by client and server.</summary>
public static class ProtocolInfo {
    /// <summary>
    /// The protocol version client and server speak. Version 2 is v0.2's (signed membership log):
    /// version 1 clients and servers can't talk to it, and are told to update at Hello.
    /// </summary>
    public const uint CurrentVersion = 2;

    /// <summary>The fake home world used by debug accounts.</summary>
    public const string DebugWorldName = "Debug";

    /// <summary>Path of the WebSocket endpoint on the server.</summary>
    public const string WebSocketPath = "/ws";

    /// <summary>The highest channel name revision a server accepts (it stores them as signed 64-bit integers).</summary>
    public const ulong MaxNameRevision = long.MaxValue;

    /// <summary>The most membership log entries one FetchMembershipLog answer carries (about 300 bytes each).</summary>
    public const int MaxLogEntriesPerPage = 500;

    /// <summary>The most log entries a ChannelInfo carries; a client fetches the rest.</summary>
    public const int MaxLogEntriesInChannelInfo = 32;

    public static class Capabilities {
        public const string Chat = "chat.v1";
    }

    public static Limits DefaultLimits() => new() {
        // A rekey for 500 members is about 100 KB: each sealed key is about 200 bytes
        // (64-byte signature, 32-byte commitment, 32-byte ephemeral key, 48-byte box, IDs and framing).
        MaxFrameBytes = 128 * 1024,
        MaxMessageBytes = 4 * 1024,
        MaxMembersPerChannel = 500,
        MaxChannelsPerUser = 50,
        MessagesPerSecond = 1,
        MessageBurst = 5,
        MaxPendingInvitesPerChannel = 50,
        MaxIdentitiesPerRequest = 500,
    };

    /// <summary>Normalises a channel ID to 32 lowercase hex digits, or returns null if invalid.</summary>
    public static string? NormaliseChannelId(string? id) {
        return Guid.TryParse(id, out var guid) ? guid.ToString("N") : null;
    }

    public static bool IsDebugWorld(string? worldName) {
        return string.Equals(worldName, DebugWorldName, StringComparison.OrdinalIgnoreCase);
    }
}
