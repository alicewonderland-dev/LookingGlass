using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// The plugin's operator panel, with a server that agreed to "operator.v1" (see "The plugin's operator panel" in
/// docs/operator-tools-plan.md).
/// <list type="bullet">
/// <item>The answer to this character's own login says whether it is one of the server's operators (a character the server's
/// settings list, whose keys are the ones pinned for it there): <see cref="SessionSnapshot.Operator"/>. A listed character
/// whose keys aren't the pinned ones (it was set up again), or none are yet, is told once a session, in chat
/// (<see cref="OperatorWords.NotOperatorNotice"/>). A server that didn't agree is never believed about it.</item>
/// <item>An operator reads the flags and bans (<see cref="RefreshOperatorListsAsync"/>), when asked: never in the background.</item>
/// <item>Each ban and lift (<see cref="OperatorBanAsync"/>, <see cref="OperatorLiftAsync"/>) is signed with the identity key
/// (<see cref="OperatorActionProof"/>), over a fresh nonce from lists asked for just before, this login and the server's address.</item>
/// </list>
/// The server decides everything again: this only shows what it says.
/// </summary>
public sealed partial class ClientSession {
    // ---- state guarded by _lock
    // The server agreed to "operator.v1" on the current connection.
    private bool _operatorAgreed;
    // How this character stands, as the answer to the current connection's login said.
    private OperatorStanding _operatorStanding;
    // With KeysChanged: the pinned keys' fingerprint.
    private string? _operatorPinnedFingerprint;
    // The lists as last fetched, while an operator.
    private OperatorLists? _operatorLists;
    // What the player was last told about not being an operator now (the standing and both fingerprints), so it is told once a
    // session, not at every reconnect.
    private string? _operatorToldAbout;
    // ----

    /// <summary>Logged in as one of the server's operators, on a server that agreed to "operator.v1".</summary>
    public bool IsOperator => this.Read(() => this.OperatorStandingNow() == OperatorStanding.Operator);

    /// <summary>
    /// Fetches the operator's lists (flags, bans in force, lifted or ended lately; addresses shortened by the server) into the
    /// snapshot (<see cref="SessionSnapshot.OperatorLists"/>).
    /// </summary>
    /// <param name="aboutUserId">A character to be told how it stands too (<see cref="OperatorLists.About"/>), for the dialog; 0 for none.</param>
    /// <exception cref="InvalidOperationException">Not logged in as an operator (<see cref="OperatorWords.NotAvailable"/>).</exception>
    public async Task<OperatorLists> RefreshOperatorListsAsync(long aboutUserId = 0, CancellationToken ct = default) {
        var connection = this.RequireOperator();
        var response = await this.RequestAsync(connection, new ClientFrame { GetOperatorView = new GetOperatorView { AboutUserId = aboutUserId } }, ct);
        return this.ApplyOperatorView(connection, response.OperatorView ?? throw Unexpected(response));
    }

    /// <summary>
    /// Bans a character from the server (never an address: the server refuses those from the plugin), replacing a ban it has.
    /// Signed with the identity key. The server posts it to the operator's alerts channel, naming this character.
    /// </summary>
    /// <param name="days">1 to <see cref="OperatorPanel.MaxDays"/>; 0 until lifted.</param>
    /// <param name="reason">One line, at most <see cref="OperatorPanel.MaxReasonLength"/> characters (the player is shown it); empty for none.</param>
    /// <returns>The lists afterwards.</returns>
    public Task<OperatorLists> OperatorBanAsync(long userId, int days, string reason, CancellationToken ct = default) {
        reason = (reason ?? "").Trim();
        if (days is < 0 or > OperatorPanel.MaxDays) {
            throw new ArgumentOutOfRangeException(nameof(days), days, OperatorWords.DaysProblem);
        }

        if (!OperatorPanel.ReasonFits(reason)) {
            throw new ArgumentException(OperatorWords.ReasonProblem, nameof(reason));
        }

        return this.OperatorActAsync(OperatorActionProof.Actions.Ban, userId, days, reason, ct);
    }

    /// <summary>Lifts the ban in force on a character (signed with the identity key, as a ban is).</summary>
    /// <returns>The lists afterwards.</returns>
    public Task<OperatorLists> OperatorLiftAsync(long userId, CancellationToken ct = default) =>
        this.OperatorActAsync(OperatorActionProof.Actions.Lift, userId, 0, "", ct);

    /// <summary>
    /// Who "Name@World" is on this server, as the server says (LookupUser), without trusting their keys for anything: for the
    /// "Ban from server" dialog, which bans by user ID. Null if nobody is registered by that name.
    /// </summary>
    public async Task<User?> FindPlayerAsync(string name, string worldName, CancellationToken ct = default) {
        Response response;
        try {
            response = await this.RequestAsync(new ClientFrame { LookupUser = new LookupUser { Name = name.Trim(), WorldName = worldName.Trim() } }, ct);
        } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotFound) {
            return null;
        }

        return response.Identities?.Identities_.FirstOrDefault()?.User is { UserId: not 0 } user ? user : null;
    }

    private async Task<OperatorLists> OperatorActAsync(string action, long target, int days, string reason, CancellationToken ct) {
        var connection = this.RequireOperator();
        // Never alongside a login, or "Sign out everywhere else": what this signs is the login the connection uses now.
        await this._loginGate.WaitAsync(ct);
        try {
            // The nonce of the lists last fetched on this connection (the dialog's, or the last action's answer), if it is fresh
            // and unused: each is good once, for a few minutes. Otherwise fresh lists, for a fresh one.
            var nonce = this.TakeOperatorNonce(connection);
            if (nonce == null) {
                return await this.SignAndSendAsync(connection, action, target, days, reason, await this.FreshOperatorNonceAsync(connection, target, ct), ct);
            }

            try {
                return await this.SignAndSendAsync(connection, action, target, days, reason, nonce, ct);
            } catch (ServerErrorException ex) when (ex.OperatorRefusal == OperatorRefusalReason.NonceStale) {
                // Too old after all (the server keeps only a connection's last few): once more, with a fresh one.
                return await this.SignAndSendAsync(connection, action, target, days, reason, await this.FreshOperatorNonceAsync(connection, target, ct), ct);
            }
        } finally {
            this._loginGate.Release();
        }
    }

    /// <summary>How long a nonce from the server's lists is used by this client: within the server's few minutes.</summary>
    private static readonly TimeSpan OperatorNonceUse = TimeSpan.FromMinutes(4);

    // ---- guarded by _lock: the nonce of the lists last fetched, on which connection, and when.
    private byte[]? _operatorNonce;
    private Connection? _operatorNonceConnection;
    private DateTimeOffset _operatorNonceAt;
    // ----

    /// <summary>The last nonce the server gave on <paramref name="connection"/>, if fresh enough, used up now; null if there is none.</summary>
    private byte[]? TakeOperatorNonce(Connection connection) {
        lock (this._lock) {
            var nonce = this._operatorNonce;
            var usable = nonce != null && this._operatorNonceConnection == connection && this._options.TimeProvider.GetUtcNow() - this._operatorNonceAt < OperatorNonceUse;
            this._operatorNonce = null;
            return usable ? nonce : null;
        }
    }

    /// <summary>Fresh lists (about the character acted on), for a fresh nonce; the lists are taken into the snapshot too.</summary>
    private async Task<byte[]> FreshOperatorNonceAsync(Connection connection, long target, CancellationToken ct) {
        var listed = await this.RequestAsync(connection, new ClientFrame { GetOperatorView = new GetOperatorView { AboutUserId = target } }, ct);
        this.ApplyOperatorView(connection, listed.OperatorView ?? throw Unexpected(listed));
        return this.TakeOperatorNonce(connection) ?? throw PlainMessages.Failure(OperatorWords.NotAvailable);
    }

    private async Task<OperatorLists> SignAndSendAsync(Connection connection, string action, long target, int days, string reason, byte[] nonce, CancellationToken ct) {
        var (identity, me, token) = this.Read(() => this.OperatorStandingNow() == OperatorStanding.Operator && connection == this._connection
            ? (this._identity, this._me, this._secrets.DeviceToken)
            : (null, null, null));
        if (identity == null || me == null || token == null || nonce.Length != OperatorActionProof.NonceSize) {
            throw PlainMessages.Failure(OperatorWords.NotAvailable);
        }

        var serverUrl = this._options.ServerUri.AbsoluteUri;
        var signature = ByteString.CopyFrom(OperatorActionProof.Sign(identity, action, me.UserId, target, days, reason, token, serverUrl, nonce));
        var frame = action == OperatorActionProof.Actions.Ban
            ? new ClientFrame {
                OperatorBan = new OperatorBan {
                    UserId = target, Days = (uint) days, Reason = reason, ServerUrl = serverUrl, Nonce = ByteString.CopyFrom(nonce), Signature = signature,
                },
            }
            : new ClientFrame {
                OperatorUnban = new OperatorUnban { UserId = target, ServerUrl = serverUrl, Nonce = ByteString.CopyFrom(nonce), Signature = signature },
            };
        var response = await this.RequestAsync(connection, frame, ct);
        return this.ApplyOperatorView(connection, response.OperatorView ?? throw Unexpected(response));
    }

    private Connection RequireOperator() {
        var connection = this.RequireConnection();
        if (!this.Read(() => this.OperatorStandingNow() == OperatorStanding.Operator && connection == this._connection)) {
            throw PlainMessages.Failure(OperatorWords.NotAvailable);
        }

        return connection;
    }

    /// <summary>How this character stands as an operator now: only while logged in, on a server that agreed. Call inside the lock.</summary>
    private OperatorStanding OperatorStandingNow() =>
        this._operatorAgreed && this._state == ConnectionState.Ready ? this._operatorStanding : OperatorStanding.None;

    /// <summary>Takes the lists the server sent on <paramref name="connection"/>, if it is still the current one.</summary>
    private OperatorLists ApplyOperatorView(Connection connection, OperatorView view) {
        var lists = OperatorLists.From(view, this._options.TimeProvider.GetUtcNow());
        lock (this._lock) {
            // Lists asked for on a connection that has dropped since may be another login's.
            if (connection != this._connection) {
                return lists;
            }

            this._operatorLists = lists;
            this._operatorNonce = view.Nonce.Length == OperatorActionProof.NonceSize ? view.Nonce.ToByteArray() : null;
            this._operatorNonceConnection = connection;
            this._operatorNonceAt = this._options.TimeProvider.GetUtcNow();
        }

        this.Publish();
        return lists;
    }

    /// <summary>
    /// A login was answered: how this character stands as an operator, if the server agreed to say. A listed character whose
    /// keys aren't the pinned ones, or none are yet, is told once a session (again only if what it would be told changes).
    /// <see cref="Publish"/> raises the notice.
    /// </summary>
    private void NoteOperatorStatus(AuthenticateOk ok) {
        lock (this._lock) {
            var standing = !this._operatorAgreed ? OperatorStanding.None : ok.OperatorStatus switch {
                OperatorLoginStatus.Operator => OperatorStanding.Operator,
                OperatorLoginStatus.KeysChanged => OperatorStanding.KeysChanged,
                OperatorLoginStatus.NotPinned => OperatorStanding.NotPinned,
                _ => OperatorStanding.None,
            };
            this._operatorStanding = standing;
            this._operatorPinnedFingerprint = standing == OperatorStanding.KeysChanged && ok.OperatorPinnedKeyHash.Length == 32
                ? IdentityKeys.FingerprintOfHash(ok.OperatorPinnedKeyHash.Span)
                : null;
            // Fetched afresh after every login (Settings asks again if it is open): what the last connection showed may be stale.
            this._operatorLists = null;

            if (standing is OperatorStanding.KeysChanged or OperatorStanding.NotPinned) {
                var mine = this._myKeys?.Fingerprint;
                var about = $"{standing} {mine} {this._operatorPinnedFingerprint}";
                if (this._operatorToldAbout != about) {
                    this._operatorToldAbout = about;
                    this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, OperatorWords.NotOperatorNotice(standing, mine, this._operatorPinnedFingerprint)));
                }
            }
        }
    }
}
