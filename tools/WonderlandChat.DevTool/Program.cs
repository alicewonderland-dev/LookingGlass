using WonderlandChat.Core.Client;
using WonderlandChat.Core.Debug;
using WonderlandChat.Protocol;

// wcdev: developer tool for WonderlandChat servers with debug accounts enabled.
//
//   wcdev bot   --server ws://host:5180/ws [--name "Echo Bot"] [--secrets echo-bot.json]
//       Runs an echo bot until Ctrl+C.
//
//   wcdev smoke --server ws://host:5180/ws [--bot "Echo Bot"]
//       Registers a throwaway debug user, creates a channel, invites the bot,
//       sends a message and waits for the echo. Exit code 0 means the server works.

var command = args.FirstOrDefault();
var server = Option("--server") ?? "ws://127.0.0.1:5180/ws";

switch (command) {
    case "bot":
        return await RunBot();
    case "smoke":
        return await RunSmoke();
    default:
        Console.WriteLine("""
            Usage:
              wcdev bot   --server ws://host:5180/ws [--name "Echo Bot"] [--secrets echo-bot.json]
              wcdev smoke --server ws://host:5180/ws [--bot "Echo Bot"]
            The server needs WonderlandChat:Dev:AllowDebugAccounts=true.
            """);
        return 2;
}

async Task<int> RunBot() {
    var name = Option("--name") ?? "Echo Bot";
    var secrets = Option("--secrets") ?? $"{name.Replace(' ', '-').ToLowerInvariant()}-secrets.json";
    await using var bot = new EchoBot(Options(), new FileSecretStore(secrets, Log), name, Log);
    bot.Start();
    Log($"Echo bot \"{name}\" connecting to {server}. Invite it as \"{name}\" on world \"{ProtocolInfo.DebugWorldName}\". Ctrl+C to stop.");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => {
        e.Cancel = true;
        stop.TrySetResult();
    };
    await stop.Task;
    return 0;
}

async Task<int> RunSmoke() {
    var botName = Option("--bot") ?? "Echo Bot";
    var testerName = $"Smoke Tester {Random.Shared.Next(1000, 9999)}";
    await using var session = new ClientSession(Options(), new InMemorySecretStore());
    var replies = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    session.MessageReceived += message => {
        if (!message.IsOwn && message.Text != null) {
            replies.TrySetResult($"<{message.Sender.Name}> {message.Text}");
        }
    };
    session.Notice += notice => Log($"  [{notice.Level}] {notice.Text}");

    try {
        session.Start();
        Log($"1. Connecting to {server}");
        await WaitFor(() => session.Snapshot.State is ConnectionState.Unregistered or ConnectionState.Ready, "connect");
        if (!session.Snapshot.DebugAccountsEnabled) {
            Log("   The server has debug accounts disabled; set WonderlandChat:Dev:AllowDebugAccounts=true.");
            return 1;
        }

        Log($"2. Registering debug user \"{testerName}\"");
        await session.StartRegistrationAsync(new Character { Name = testerName, WorldName = ProtocolInfo.DebugWorldName });
        await session.CompleteRegistrationAsync();
        await WaitFor(() => session.Snapshot.State == ConnectionState.Ready, "log in");

        Log("3. Creating a channel");
        var channelId = await session.CreateChannelAsync("Smoke test");

        Log($"4. Inviting \"{botName}\"@{ProtocolInfo.DebugWorldName}");
        await session.InviteAsync(channelId, botName, ProtocolInfo.DebugWorldName);
        await WaitFor(() => session.Snapshot.FindChannel(channelId)?.Members.Any(m => m.User.Name == botName && m.Rank >= Rank.Member) == true, "the bot to join");
        await WaitFor(() => session.Snapshot.FindChannel(channelId) is { RekeyPending: false, HasKey: true }, "the rekey");
        Log($"   Bot joined; channel is at epoch {session.Snapshot.FindChannel(channelId)!.Epoch}");

        Log("5. Sending a message and waiting for the echo");
        await session.SendTextAsync(channelId, "smoke test");
        var reply = await replies.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Log($"   Got: {reply}");

        await session.DisbandAsync(channelId);
        Log("OK: registration, channels, invites, rekeying and messaging all work.");
        return 0;
    } catch (Exception ex) {
        Log($"FAILED: {ex.Message}");
        return 1;
    }

    async Task WaitFor(Func<bool> condition, string what) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) {
            try {
                await Task.Delay(50, timeout.Token);
            } catch (OperationCanceledException) {
                throw new TimeoutException($"Timed out waiting for {what} ({session.Snapshot.State}: {session.Snapshot.StatusText}).");
            }
        }
    }
}

ClientSessionOptions Options() => new() {
    ServerUri = new Uri(server),
    ClientVersion = "wcdev",
    Log = (level, text) => {
        if (level >= NoticeLevel.Warning) {
            Log($"  ({level}) {text}");
        }
    },
};

string? Option(string name) {
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void Log(string line) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
