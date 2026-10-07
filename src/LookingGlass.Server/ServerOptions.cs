namespace LookingGlass.Server;

/// <summary>Bound from the "LookingGlass" configuration section.</summary>
public sealed class ServerOptions {
    public const string Section = "LookingGlass";

    /// <summary>Folder for the database and echo bot keys. Relative paths are relative to the server's install folder.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Shown to every client when it connects.</summary>
    public string Announcement { get; set; } = "";

    /// <summary>
    /// Every address clients connect to, such as "wss://chat.example.com/ws". Registrations through the Lodestone, key
    /// logins and retirements are only accepted when signed for one of these (scheme, host and port; not the path), and
    /// Welcome lists them, so a client moving to one of them keeps its identity (the server at the address the client
    /// already uses lists the new one, and the server at the new one lists the old). Required outside Development: the
    /// server refuses to start without them. In Development, empty means going by each connection's Host header and
    /// scheme, which is weaker: whoever connects chooses the Host header, so a relaying server sends the address the user
    /// signed for.
    /// </summary>
    public string[] PublicUrls { get; set; } = [];

    public LodestoneOptions Lodestone { get; set; } = new();
    public DevOptions Dev { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public MessageOptions Messages { get; set; } = new();
}

/// <summary>
/// Message catch-up: the messages the server relays are kept (as the ciphertext it relays, which it can't read) so that a
/// member who was disconnected gets them when they come back. See "Message catch-up" in docs/design.md.
/// </summary>
public sealed class MessageOptions {
    /// <summary>Days a relayed message is kept, 0 to 365. 0 keeps none (and deletes what was kept at the next sweep).</summary>
    public int KeepDays { get; set; } = 7;

    /// <summary>Messages kept per channel at most, 0 to 100,000; past it the oldest go first. 0 keeps none.</summary>
    public int MaxPerChannel { get; set; } = 5000;

    public const int MaxKeepDays = 365;
    public const int MaxMaxPerChannel = 100_000;

    /// <summary>Whether messages are kept at all.</summary>
    public bool Enabled => this.KeepDays > 0 && this.MaxPerChannel > 0;

    /// <summary>Why the settings are out of range (the server doesn't start then), or null if they aren't.</summary>
    public string? Problem() {
        return this.KeepDays is < 0 or > MaxKeepDays
            ? $"LookingGlass:Messages:KeepDays is {this.KeepDays}, so the server won't start: it must be 0 to {MaxKeepDays} (days a relayed message is kept for members who were away; 7 by default, 0 keeps none)."
            : this.MaxPerChannel is < 0 or > MaxMaxPerChannel
                ? $"LookingGlass:Messages:MaxPerChannel is {this.MaxPerChannel}, so the server won't start: it must be 0 to {MaxMaxPerChannel} (messages kept per channel at most; 5000 by default, 0 keeps none)."
                : null;
    }
}

public sealed class LodestoneOptions {
    public string BaseUrl { get; set; } = "https://na.finalfantasyxiv.com";

    /// <summary>Minimum gap between Lodestone requests, server-wide.</summary>
    public double MinDelaySeconds { get; set; } = 2;

    /// <summary>
    /// How long a registration challenge (and its Lodestone code) can be completed, from <see cref="MinChallengeMinutes"/>
    /// to <see cref="MaxChallengeMinutes"/>: the server doesn't start with anything else. It bounds how long a malicious
    /// server can hold this server's codes open while looking for one it can pass on (see LodestoneCode).
    /// </summary>
    public int ChallengeMinutes { get; set; } = 15;

    public const int MinChallengeMinutes = 1;
    public const int MaxChallengeMinutes = 60;
}

public sealed class DevOptions {
    /// <summary>
    /// Allows characters on the fake world "Debug" to register without
    /// Lodestone. For local testing only; never enable on a public server.
    /// </summary>
    public bool AllowDebugAccounts { get; set; }

    /// <summary>Runs the echo bot inside the server. Requires AllowDebugAccounts.</summary>
    public bool HostEchoBot { get; set; }

    public string EchoBotName { get; set; } = "Echo Bot";

    /// <summary>WebSocket URL the hosted echo bot connects to. Empty: derived from the server's own address.</summary>
    public string EchoBotServerUrl { get; set; } = "";

    /// <summary>
    /// Lets <see cref="AllowDebugAccounts"/> and <see cref="HostEchoBot"/> be on outside Development (a test server run in
    /// Production, say). Without it the server refuses to start with either there.
    /// </summary>
    public bool AllowOutsideDevelopment { get; set; }
}

public sealed class LimitOptions {
    public int RegistrationsPerHourPerIp { get; set; } = 5;

    /// <summary>
    /// Registrations one IP address may have refused in an hour for naming an address this server doesn't list (each is
    /// logged as a warning); past that they are refused without one. Counted apart from <see cref="RegistrationsPerHourPerIp"/>.
    /// </summary>
    public int RefusedRegistrationsPerHourPerIp { get; set; } = 10;

    /// <summary>Key login challenges one IP address may ask for in an hour.</summary>
    public int KeyLoginsPerHourPerIp { get; set; } = 60;

    /// <summary>Failed key logins after which an IP address gets no more challenges, for an hour.</summary>
    public int KeyLoginFailuresPerHourPerIp { get; set; } = 10;

    /// <summary>Concurrent WebSocket connections allowed from one IP address.</summary>
    public int ConnectionsPerIp { get; set; } = 20;

    /// <summary>
    /// Connections from one IP address (IPv6: one /56) that haven't logged in yet, at once. Plugins with a saved login log in
    /// within milliseconds; this bounds connections that sit there without logging in.
    /// </summary>
    public int NotLoggedInConnectionsPerIp { get; set; } = 4;

    /// <summary>Seconds a connection may stay without logging in, unless it is registering (then until its challenge expires).</summary>
    public int NotLoggedInSeconds { get; set; } = 180;

    /// <summary>
    /// WebSocket connections the server takes in all. At the cap, the oldest that hasn't logged in is closed to make room
    /// for a new one; only when all have logged in is a new one refused (503). See "Limits worth knowing" in docs/server.md.
    /// </summary>
    public int MaxConnections { get; set; } = 10_000;
    public int MaxIdentitiesPerRequest { get; set; } = 500;
    public int SendQueueLength { get; set; } = 256;

    /// <summary>New WebSocket connections allowed from one IP address per minute (reconnect storms, connection churn).</summary>
    public int ConnectionsPerMinutePerIp { get; set; } = 60;

    /// <summary>
    /// Requests one connection may make per second, on average. Past <see cref="RequestBurstPerConnection"/> at once, the
    /// server reads the connection's next request only when it is due: a busy client is slowed, never refused.
    /// </summary>
    public double RequestsPerSecondPerConnection { get; set; } = 20;

    /// <summary>Requests one connection may make at once before <see cref="RequestsPerSecondPerConnection"/> applies (a client connecting with 50 channels asks about 100).</summary>
    public int RequestBurstPerConnection { get; set; } = 200;
}

public sealed class DatabaseOptions {
    /// <summary>
    /// Minutes between explicit PASSIVE checkpoints of the write-ahead log; 0 (the default) for none, leaving it to SQLite's
    /// own automatic checkpoints (PASSIVE, every 1000 pages) and to Litestream if it replicates the database. The server never
    /// makes a checkpoint that blocks (RESTART or TRUNCATE): Litestream must be able to read the log before it is folded in.
    /// </summary>
    public int CheckpointMinutes { get; set; }
}
