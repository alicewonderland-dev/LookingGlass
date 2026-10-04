namespace WonderlandChat.Server;

/// <summary>Bound from the "WonderlandChat" configuration section.</summary>
public sealed class ServerOptions {
    public const string Section = "WonderlandChat";

    /// <summary>Folder for the database and echo bot keys. Relative paths are relative to the server's install folder.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Shown to every client when it connects.</summary>
    public string Announcement { get; set; } = "";

    public LodestoneOptions Lodestone { get; set; } = new();
    public DevOptions Dev { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
}

public sealed class LodestoneOptions {
    public string BaseUrl { get; set; } = "https://na.finalfantasyxiv.com";

    /// <summary>Minimum gap between Lodestone requests, server-wide.</summary>
    public double MinDelaySeconds { get; set; } = 2;

    public int ChallengeMinutes { get; set; } = 15;
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
}

public sealed class LimitOptions {
    public int RegistrationsPerHourPerIp { get; set; } = 5;

    /// <summary>Concurrent WebSocket connections allowed from one IP address.</summary>
    public int ConnectionsPerIp { get; set; } = 20;
    public int MaxIdentitiesPerRequest { get; set; } = 500;
    public int SendQueueLength { get; set; } = 256;
}
