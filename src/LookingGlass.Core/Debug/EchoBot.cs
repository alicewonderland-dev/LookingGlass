using LookingGlass.Core.Client;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Debug;

/// <summary>
/// A headless test user. It registers as a debug account (world "Debug"),
/// accepts every invite, takes part in rekeys, and answers every message so
/// one person can test LookingGlass alone. Requires a server with
/// <c>Dev:AllowDebugAccounts</c> enabled.
///
/// Messages starting with "!" are commands: "!ping", "!rekey", "!leave".
/// </summary>
public sealed class EchoBot : IAsyncDisposable {
    public const string EchoPrefix = "echo: ";

    private readonly ClientSession _session;
    private readonly string _name;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _registration;

    public EchoBot(ClientSessionOptions options, ISecretStore store, string name, Action<string>? log = null) {
        this._name = name;
        this._log = log ?? (_ => { });
        this._session = new ClientSession(options, store);
        this._session.SnapshotChanged += this.OnSnapshot;
        this._session.InviteReceived += this.OnInvite;
        this._session.MessageReceived += this.OnMessage;
        this._session.Notice += notice => this._log($"[{notice.Level}] {notice.Text}");
    }

    public ClientSession Session => this._session;

    public void Start() => this._session.Start();

    /// <summary>Completes once the bot is registered and logged in.</summary>
    public async Task WaitUntilReadyAsync(TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        while (this._session.Snapshot.State != ConnectionState.Ready) {
            await Task.Delay(50, cts.Token);
        }
    }

    private void OnSnapshot(SessionSnapshot snapshot) {
        // A server that doesn't recognise the bot's login lost it (the bot runs on its own server), and a debug
        // account needs no Lodestone, so it registers again straight away rather than waiting for the login to work again.
        if (snapshot.State is not (ConnectionState.Unregistered or ConnectionState.LoginNotRecognized) || this._registration is { IsCompleted: false }) {
            return;
        }

        if (!snapshot.DebugAccountsEnabled) {
            this._log("This server doesn't allow debug accounts; the echo bot can't register.");
            return;
        }

        this._registration = Task.Run(async () => {
            try {
                var challenge = await this._session.StartRegistrationAsync(new Character {
                    Name = this._name,
                    WorldId = 0,
                    WorldName = ProtocolInfo.DebugWorldName,
                }, this._cts.Token);

                if (!challenge.VerificationSkipped) {
                    this._log("The server wants Lodestone verification for the bot; is Dev:AllowDebugAccounts on?");
                    return;
                }

                await this._session.CompleteRegistrationAsync(this._cts.Token);
                this._log($"Registered as {this._name}@{ProtocolInfo.DebugWorldName}");
            } catch (Exception ex) when (!this._cts.IsCancellationRequested) {
                this._log($"Registration failed: {ex.Message}");
            }
        });
    }

    private void OnInvite(InviteView invite) {
        this.Run($"accepting invite to {invite.ChannelName}", async ct => {
            await this._session.RespondToInviteAsync(invite.ChannelId, true, ct);
            this._log($"Joined \"{invite.ChannelName}\" (invited by {invite.Inviter.Name})");
        });
    }

    private void OnMessage(IncomingMessage message) {
        if (message.IsOwn || message.Text == null || message.Text.StartsWith(EchoPrefix, StringComparison.Ordinal)) {
            return;
        }

        this._log($"<{message.Sender.Name}> {message.Text}");
        var text = message.Text.Trim();

        this.Run("replying", async ct => {
            switch (text) {
                case "!ping":
                    await this._session.SendTextAsync(message.ChannelId, $"{EchoPrefix}pong ({(DateTimeOffset.UtcNow - message.Timestamp).TotalMilliseconds:0} ms since you sent it)", ct);
                    break;
                case "!rekey":
                    await this._session.RekeyAsync(message.ChannelId, ct, force: true);
                    var epoch = this._session.Snapshot.FindChannel(message.ChannelId)?.Epoch;
                    await this._session.SendTextAsync(message.ChannelId, $"{EchoPrefix}rekeyed to epoch {epoch}", ct);
                    break;
                case "!leave":
                    await this._session.SendTextAsync(message.ChannelId, $"{EchoPrefix}bye!", ct);
                    await this._session.LeaveAsync(message.ChannelId, ct);
                    break;
                default:
                    await this._session.SendTextAsync(message.ChannelId, EchoPrefix + text, ct);
                    break;
            }
        });
    }

    private void Run(string what, Func<CancellationToken, Task> work) {
        _ = Task.Run(async () => {
            try {
                await work(this._cts.Token);
            } catch (Exception ex) when (!this._cts.IsCancellationRequested) {
                this._log($"Failed {what}: {ex.Message}");
            }
        });
    }

    public async ValueTask DisposeAsync() {
        await this._cts.CancelAsync();
        await this._session.DisposeAsync();
        this._cts.Dispose();
    }
}
