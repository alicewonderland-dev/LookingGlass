namespace WonderlandChat.Protocol;

/// <summary>Constants shared by client and server.</summary>
public static class ProtocolInfo {
    public const uint CurrentVersion = 1;

    /// <summary>The fake home world used by debug accounts.</summary>
    public const string DebugWorldName = "Debug";

    /// <summary>Path of the WebSocket endpoint on the server.</summary>
    public const string WebSocketPath = "/ws";

    /// <summary>The highest channel name revision a server accepts (it stores them as signed 64-bit integers).</summary>
    public const ulong MaxNameRevision = long.MaxValue;

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
    };

    /// <summary>Normalises a channel ID to 32 lowercase hex digits, or returns null if invalid.</summary>
    public static string? NormaliseChannelId(string? id) {
        return Guid.TryParse(id, out var guid) ? guid.ToString("N") : null;
    }

    public static bool IsDebugWorld(string? worldName) {
        return string.Equals(worldName, DebugWorldName, StringComparison.OrdinalIgnoreCase);
    }
}
