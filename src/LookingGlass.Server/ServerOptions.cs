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

    // ---------------------------------------------------------------- invites (see "Abuse limits" in docs/design.md)

    /// <summary>
    /// Invites one user may send at once, to anyone; past it, one more every <see cref="InviteIntervalSecondsPerInviter"/>.
    /// </summary>
    public int InviteBurstPerInviter { get; set; } = 60;

    /// <summary>Seconds between invites one user may send once <see cref="InviteBurstPerInviter"/> is spent.</summary>
    public int InviteIntervalSecondsPerInviter { get; set; } = 5;

    /// <summary>
    /// Invites one user may be sent at once, by everyone together; past it, one more every <see cref="InviteIntervalSecondsPerInvitee"/>.
    /// </summary>
    public int InviteBurstPerInvitee { get; set; } = 30;

    /// <summary>Seconds between invites one user may be sent once <see cref="InviteBurstPerInvitee"/> is spent.</summary>
    public int InviteIntervalSecondsPerInvitee { get; set; } = 10;

    /// <summary>
    /// Invites one user may send one other user at once (checked first, so they spend nothing of the invitee's allowance
    /// past it); past it, one more every <see cref="InviteIntervalSecondsPerPair"/>. Less than <see cref="InviteBurstPerInvitee"/>,
    /// so one inviter (perhaps one the invitee blocked, which the server doesn't know) can't use up all of it.
    /// </summary>
    public int InviteBurstPerPair { get; set; } = 20;

    /// <summary>
    /// Seconds between invites one user may send one other user once <see cref="InviteBurstPerPair"/> is spent. More than
    /// <see cref="InviteIntervalSecondsPerInvitee"/>, for the same reason.
    /// </summary>
    public int InviteIntervalSecondsPerPair { get; set; } = 60;

    /// <summary>Pending invites one user can have at once, across all channels (as many as the channels they can be in).</summary>
    public int MaxPendingInvitesPerUser { get; set; } = 50;

    /// <summary>
    /// Pending invites one user can have from any one inviter. Less than <see cref="MaxPendingInvitesPerUser"/>, so a single
    /// inviter (with many channels) can't take all of them, whether or not the invitee blocked them.
    /// </summary>
    public int MaxPendingInvitesFromOneInviter { get; set; } = 25;

    /// <summary>
    /// Players one user may look up by name at once (each invite by name starts with one; the plugin reuses a lookup for 10
    /// minutes); past it, one more every <see cref="LookupIntervalSeconds"/>. As many as <see cref="InviteBurstPerInviter"/>.
    /// </summary>
    public int LookupBurst { get; set; } = 60;

    /// <summary>Seconds between lookups one user may make once <see cref="LookupBurst"/> is spent.</summary>
    public int LookupIntervalSeconds { get; set; } = 1;

    /// <summary>The most a burst setting here can be.</summary>
    public const int MaxBurst = 10_000;

    /// <summary>The most an interval setting here can be, in seconds (a day).</summary>
    public const int MaxIntervalSeconds = 86_400;

    /// <summary>
    /// The most <see cref="MaxPendingInvitesPerUser"/> can be: every pending invite is in the invitee's channel list, which
    /// must stay well within what a client accepts in one response (each invite is under 1 KB).
    /// </summary>
    public const int MaxMaxPendingInvitesPerUser = 200;

    /// <summary>Why the invite or lookup limits are out of range (the server doesn't start then), or null if they aren't.</summary>
    public string? Problem() {
        foreach (var (name, value, what, fallback) in new[] {
                     (nameof(this.InviteBurstPerInviter), this.InviteBurstPerInviter, "invites", 60),
                     (nameof(this.InviteBurstPerInvitee), this.InviteBurstPerInvitee, "invites", 30),
                     (nameof(this.InviteBurstPerPair), this.InviteBurstPerPair, "invites", 20),
                     (nameof(this.LookupBurst), this.LookupBurst, "lookups", 60),
                 }) {
            if (value is < 1 or > MaxBurst) {
                return $"LookingGlass:Limits:{name} is {value}, so the server won't start: it must be 1 to {MaxBurst} ({what} at once; {fallback} by default).";
            }
        }

        foreach (var (name, value, what, fallback) in new[] {
                     (nameof(this.InviteIntervalSecondsPerInviter), this.InviteIntervalSecondsPerInviter, "invites", 5),
                     (nameof(this.InviteIntervalSecondsPerInvitee), this.InviteIntervalSecondsPerInvitee, "invites", 10),
                     (nameof(this.InviteIntervalSecondsPerPair), this.InviteIntervalSecondsPerPair, "invites", 60),
                     (nameof(this.LookupIntervalSeconds), this.LookupIntervalSeconds, "lookups", 1),
                 }) {
            if (value is < 1 or > MaxIntervalSeconds) {
                return $"LookingGlass:Limits:{name} is {value}, so the server won't start: it must be 1 to {MaxIntervalSeconds} " +
                       $"(seconds between {what} once the burst is spent; {fallback} by default).";
            }
        }

        if (this.InviteBurstPerPair >= this.InviteBurstPerInvitee) {
            return $"LookingGlass:Limits:InviteBurstPerPair ({this.InviteBurstPerPair}) must be less than InviteBurstPerInvitee " +
                   $"({this.InviteBurstPerInvitee}), so the server won't start: otherwise one inviter could use up everything others can send the invitee.";
        }

        if (this.InviteIntervalSecondsPerPair <= this.InviteIntervalSecondsPerInvitee) {
            return $"LookingGlass:Limits:InviteIntervalSecondsPerPair ({this.InviteIntervalSecondsPerPair}) must be more than InviteIntervalSecondsPerInvitee " +
                   $"({this.InviteIntervalSecondsPerInvitee}), so the server won't start: otherwise one inviter could use up everything others can send the invitee.";
        }

        if (this.MaxPendingInvitesPerUser is < 2 or > MaxMaxPendingInvitesPerUser) {
            return $"LookingGlass:Limits:MaxPendingInvitesPerUser is {this.MaxPendingInvitesPerUser}, so the server won't start: it must be 2 to " +
                   $"{MaxMaxPendingInvitesPerUser} (pending invites one user can have; 50 by default).";
        }

        if (this.MaxPendingInvitesFromOneInviter < 1 || this.MaxPendingInvitesFromOneInviter >= this.MaxPendingInvitesPerUser) {
            return $"LookingGlass:Limits:MaxPendingInvitesFromOneInviter is {this.MaxPendingInvitesFromOneInviter}, so the server won't start: it must be " +
                   $"1 to {this.MaxPendingInvitesPerUser - 1}, less than MaxPendingInvitesPerUser, so one inviter can't fill them all (25 by default).";
        }

        return null;
    }
}

public sealed class DatabaseOptions {
    /// <summary>
    /// Minutes between explicit PASSIVE checkpoints of the write-ahead log; 0 (the default) for none, leaving it to SQLite's
    /// own automatic checkpoints (PASSIVE, every 1000 pages) and to Litestream if it replicates the database. The server never
    /// makes a checkpoint that blocks (RESTART or TRUNCATE): Litestream must be able to read the log before it is folded in.
    /// </summary>
    public int CheckpointMinutes { get; set; }
}
