using System.Collections.Immutable;
using Google.Protobuf;
using LookingGlass.Core.Crypto;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// The account's devices (each computer signed in has a login of its own; see "Other computers signing in" in
/// docs/design.md), with a server that agreed to "devices.v1".
/// <list type="bullet">
/// <item>The player is told when another computer gets a login: at once when the server says so (DeviceAdded), or else at
/// the next login, from a device in the list this computer hasn't seen (<see cref="ClientSecrets.KnownDevices"/>); never
/// about this computer, nothing from the first list a computer has (it can't tell what is new in it), several in one notice,
/// and a stream of them now and then (<see cref="ClientSessionOptions.DeviceNoticeInterval"/>).</item>
/// <item>The player is told, gently, when this computer's own login was used elsewhere since it last logged in: the sign a
/// copy of it leaves (<see cref="ClientSecrets.LastLoginUnix"/>).</item>
/// <item>"Sign out everywhere else", signed with the identity key, revokes every other login and replaces this computer's
/// (saved before it is sent: <see cref="ClientSecrets.PendingDeviceToken"/>). A computer signed out says which kind of
/// computer did it (<see cref="SignedOutBy"/>).</item>
/// </list>
/// </summary>
public sealed partial class ClientSession {
    /// <summary>The most device IDs remembered (see <see cref="ClientSecrets.KnownDevices"/>); a server keeps 20 an account.</summary>
    public const int MaxKnownDevices = 64;

    // The most devices of one list looked at, whatever a server sends.
    private const int MaxDevicesListed = 50;

    // The most bytes a device's ID may have (the server's have 8).
    private const int MaxDeviceIdBytes = 32;

    // ---- state guarded by _lock
    // The server agreed to "devices.v1" on the current connection.
    private bool _devicesAgreed;
    // The account's devices as last listed (kept while disconnected).
    private ImmutableArray<DeviceView> _devices = ImmutableArray<DeviceView>.Empty;
    // The server refused the identity key because the account signed out everywhere else (Error.signed_out), and who did, as
    // this computer can tell; until a login works again.
    private SignedOutBy _signedOutBy;

    // New computers not told yet (held back after a notice), the newest one's time, when the last notice was, and whether
    // telling the held ones is due.
    private int _heldNewDevices;
    private DateTimeOffset? _heldNewest;
    private DateTimeOffset _devicesToldAt = DateTimeOffset.MinValue;
    private bool _devicesTellScheduled;
    // Devices told of before this computer's first list (when there is nothing to remember them in yet).
    private readonly HashSet<string> _devicesToldBeforeList = new();
    // Lists fetched in the background: one under way, another wanted after it, one waiting for its turn, and when the last began.
    private bool _devicesFetching;
    private bool _devicesFetchAgain;
    private bool _devicesFetchScheduled;
    private DateTimeOffset _devicesFetchedAt = DateTimeOffset.MinValue;
    // ----

    /// <summary>Logged in, on a server that agreed to list the account's devices.</summary>
    public bool DevicesAvailable => this.Read(() => this._devicesAgreed && this._state == ConnectionState.Ready);

    /// <summary>
    /// Fetches the account's devices into the snapshot (<see cref="SessionSnapshot.Devices"/>), telling the player about any
    /// other computer's this one hasn't seen (see the class summary).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not logged in, or the server doesn't keep the list (<see cref="DeviceWords.NotAvailable"/>).</exception>
    public async Task RefreshDevicesAsync(CancellationToken ct = default) {
        var connection = this.RequireDevices();
        var response = await this.RequestAsync(connection, new ClientFrame { ListDevices = new ListDevices() }, ct);
        this.ApplyDevices(connection, response.Devices ?? throw Unexpected(response));
    }

    /// <summary>
    /// "Sign out everywhere else": the server revokes every other login of the account, replaces this computer's with a new one
    /// (so a copy of the old one is no use), and refuses the identity key until the character is registered again through the
    /// Lodestone, so a copy of the key can't sign straight back in. Signed with the identity key, as a login alone mustn't be
    /// enough. The new login is saved before it is sent: if the answer is lost, the connection is dropped, and the next login
    /// tries the new one first (see <see cref="ClientSecrets.PendingDeviceToken"/>). The player is told how many were signed out.
    /// </summary>
    /// <returns>How many other devices were signed out.</returns>
    /// <exception cref="InvalidOperationException">Not logged in, or the server doesn't offer it (<see cref="DeviceWords.NotAvailable"/>).</exception>
    public async Task<int> SignOutOtherDevicesAsync(CancellationToken ct = default) {
        var connection = this.RequireDevices();
        // Never alongside a login: what this signs is the login the connection uses now, and it replaces it.
        await this._loginGate.WaitAsync(ct);
        try {
            // A fresh list, for a fresh nonce to sign.
            var listed = await this.RequestAsync(connection, new ClientFrame { ListDevices = new ListDevices() }, ct);
            var fresh = listed.Devices ?? throw Unexpected(listed);
            this.ApplyDevices(connection, fresh);
            var nonce = fresh.SignOutNonce.ToByteArray();
            var (identity, me, token, pending) = this.Read(() => this._state == ConnectionState.Ready && connection == this._connection
                ? (this._identity, this._me, this._secrets.DeviceToken, this._secrets.PendingDeviceToken)
                : (null, null, null, null));
            if (identity == null || me == null || token == null || nonce.Length != SignOutProof.NonceSize) {
                throw PlainMessages.Failure(DeviceWords.NotAvailable);
            }

            if (pending == null) {
                // Saved before it is sent. One left from a try the server refused is used again: it never took it.
                pending = DeviceTokens.New();
                lock (this._lock) {
                    this._secrets.PendingDeviceToken = pending;
                    this._secretsVersion++;
                }

                this.SaveSecrets();
            }

            var serverUrl = this._options.ServerUri.AbsoluteUri;
            Response response;
            try {
                response = await this.RequestAsync(connection, new ClientFrame {
                    SignOutOtherDevices = new SignOutOtherDevices {
                        ServerUrl = serverUrl,
                        NewDeviceToken = pending,
                        Nonce = ByteString.CopyFrom(nonce),
                        Signature = ByteString.CopyFrom(SignOutProof.Sign(identity, me.UserId, token, pending, serverUrl, nonce)),
                    },
                }, ct);
            } catch (Exception ex) when (ex is not ServerErrorException) {
                // No answer: whether the server took the new login is only known by trying it, which the next login does first.
                connection.Abort("No answer to signing out everywhere else");
                throw;
            }

            var devices = response.Devices ?? throw Unexpected(response);
            lock (this._lock) {
                this._secrets.DeviceToken = pending;
                this._secrets.PendingDeviceToken = null;
                this._secrets.SignedOutOthers = true;
                this._secretsVersion++;
            }

            this.SaveSecrets();
            this.ApplyDevices(connection, devices);
            var count = (int) Math.Min(devices.SignedOut, int.MaxValue);
            this.RaiseNotice(NoticeLevel.Info, DeviceWords.SignedOutOthers(count));
            return count;
        } finally {
            this._loginGate.Release();
        }
    }

    /// <summary>
    /// Fetches the account's devices in the background, if the server keeps them; no sooner than
    /// <see cref="ClientSessionOptions.DeviceListInterval"/> after the last one (one under way, or waiting, takes the place of
    /// more), and failing quietly: it is asked again at the next login.
    /// </summary>
    public void RefreshDevicesSoon() {
        if (!this.DevicesAvailable) {
            return;
        }

        TimeSpan wait;
        lock (this._lock) {
            if (this._devicesFetching) {
                this._devicesFetchAgain = true;
                return;
            }

            if (this._devicesFetchScheduled) {
                return;
            }

            var now = this._options.TimeProvider.GetUtcNow();
            wait = this._devicesFetchedAt == DateTimeOffset.MinValue ? TimeSpan.Zero : this._devicesFetchedAt + this._options.DeviceListInterval - now;
            if (wait > TimeSpan.Zero) {
                this._devicesFetchScheduled = true;
            } else {
                this._devicesFetching = true;
                this._devicesFetchedAt = now;
            }
        }

        this.RunBackground("Listing your computers", async ct => {
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, this._options.TimeProvider, ct);
                lock (this._lock) {
                    this._devicesFetchScheduled = false;
                    if (this._devicesFetching) {
                        this._devicesFetchAgain = true;
                        return;
                    }

                    this._devicesFetching = true;
                    this._devicesFetchedAt = this._options.TimeProvider.GetUtcNow();
                }
            }

            var again = false;
            try {
                await this.RefreshDevicesAsync(ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Disconnected meanwhile (another computer's login replaces this one's connection), say.
                this.Log(NoticeLevel.Info, $"Couldn't list this account's devices: {ex.Message}");
            } finally {
                lock (this._lock) {
                    this._devicesFetching = false;
                    again = this._devicesFetchAgain;
                    this._devicesFetchAgain = false;
                }
            }

            if (again) {
                this.RefreshDevicesSoon();
            }
        });
    }

    private Connection RequireDevices() {
        var connection = this.RequireConnection();
        if (!this.Read(() => this._devicesAgreed && this._state == ConnectionState.Ready && connection == this._connection)) {
            throw PlainMessages.Failure(DeviceWords.NotAvailable);
        }

        return connection;
    }

    /// <summary>
    /// Logs in with the saved login, after the one a "Sign out everywhere else" replaced it with if there is one (its answer
    /// was lost): that one is kept if it works, and dropped if the old one works instead (the server never took it). Call
    /// inside <see cref="_loginGate"/>.
    /// </summary>
    private async Task<AuthenticateOk> AuthenticateWithSavedLoginAsync(Connection connection, CancellationToken ct) {
        var (token, pending) = this.Read(() => (this._secrets.DeviceToken, this._secrets.PendingDeviceToken));
        if (pending != null) {
            try {
                var renewed = await this.RequestAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = pending } }, ct);
                var accepted = renewed.AuthenticateOk ?? throw Unexpected(renewed);
                lock (this._lock) {
                    this._secrets.DeviceToken = pending;
                    this._secrets.PendingDeviceToken = null;
                    // It took it: the sign-out was done.
                    this._secrets.SignedOutOthers = true;
                    this._secretsVersion++;
                }

                this.SaveSecrets();
                this.Log(NoticeLevel.Info, "Logged in with the new login from signing out everywhere else, whose answer was lost");
                return accepted;
            } catch (ServerErrorException ex) when (ex.Code == ErrorCode.NotAuthenticated) {
                // Never taken: the old login is still this computer's.
            }
        }

        var response = await this.RequestAsync(connection, new ClientFrame { Authenticate = new Authenticate { DeviceToken = token } }, ct);
        var ok = response.AuthenticateOk ?? throw Unexpected(response);
        if (pending != null) {
            lock (this._lock) {
                if (this._secrets.PendingDeviceToken == pending) {
                    this._secrets.PendingDeviceToken = null;
                    this._secretsVersion++;
                }
            }

            this.SaveSecrets();
        }

        return ok;
    }

    /// <summary>
    /// A login worked: if the server says this computer's login was last used at another time than this computer last logged
    /// in, a copy of it was used elsewhere meanwhile, and the player is told (gently: a server restored from a backup, or an
    /// answer to a login lost on the way, does the same). Then this login's time is remembered.
    /// </summary>
    private void NoteLogin(AuthenticateOk ok) {
        lock (this._lock) {
            if (this._secrets.LastLoginUnix is { } last && ok.PreviousUsedUnix > 0 && ok.PreviousUsedUnix != last) {
                this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, DeviceWords.LoginUsedElsewhere(TimeOf(ok.PreviousUsedUnix))));
            }

            if (ok.UsedUnix > 0) {
                this._secrets.LastLoginUnix = ok.UsedUnix;
                this._secretsVersion++;
            }
        }

        this.SaveSecrets();
    }

    /// <summary>
    /// This computer has a new login of its own (a registration, a key login), or none: what was kept about the old one goes.
    /// Call inside the lock.
    /// </summary>
    private void NewLogin() {
        this._secrets.PendingDeviceToken = null;
        this._secrets.LastLoginUnix = null;
        this._secrets.SignedOutOthers = false;
    }

    /// <summary>Takes a list of the account's devices: shown in Settings, and other computers' not seen before told about.</summary>
    private void ApplyDevices(Connection connection, Devices devices) {
        lock (this._lock) {
            // A list asked for on a connection that has dropped since may be another account's (a login replaced meanwhile).
            if (connection != this._connection) {
                return;
            }


            var known = this._secrets.KnownDevices;
            var shown = new List<DeviceView>();
            var listed = new List<string>();
            var fresh = 0;
            DateTimeOffset? newest = null;
            foreach (var device in devices.List.Take(MaxDevicesListed)) {
                if (IdOf(device.Id) is not { } id || listed.Contains(id)) {
                    continue;
                }

                listed.Add(id);
                if (device.ThisDevice) {
                    this._secrets.ThisDeviceId = id;
                }

                var added = TimeOf(device.AddedUnix);
                if (added != null && TimeOf(device.LastUsedUnix) is { } used) {
                    shown.Add(new DeviceView(added.Value, used, device.ThisDevice));
                }

                // Never this computer; and nothing from the first list this computer has, which it can't tell what is new in.
                if (known != null && !device.ThisDevice && !known.Contains(id)) {
                    fresh++;
                    if (added > newest || newest == null) {
                        newest = added;
                    }
                }
            }

            this._devices = [.. shown];
            // Those listed now, then those seen before, so a device told of (by an event, say) is never told again.
            this._secrets.KnownDevices = listed.Concat(known ?? []).Distinct().Take(MaxKnownDevices).ToList();
            this._secretsVersion++;
            this.TellNewDevices(fresh, newest);
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>
    /// The server says the account just got a new device, not this computer's: told at once (unless it was seen already, or a
    /// notice was a moment ago), and the list fetched again for Settings.
    /// </summary>
    private void OnDeviceAdded(DeviceAdded added) {
        if (IdOf(added.Id) is not { } id) {
            return;
        }

        lock (this._lock) {
            // A server that didn't agree has no business sending it.
            if (!this._devicesAgreed) {
                return;
            }

            var known = this._secrets.KnownDevices;
            if (known != null) {
                if (known.Contains(id)) {
                    return;
                }

                this._secrets.KnownDevices = [id, .. known.Take(MaxKnownDevices - 1)];
                this._secretsVersion++;
            } else if (!this._devicesToldBeforeList.Add(id) || this._devicesToldBeforeList.Count > MaxKnownDevices) {
                // Before this computer's first list (which is taken as it is), there is nothing to remember it in but this.
                return;
            }

            this.TellNewDevices(1, TimeOf(added.AddedUnix));
        }

        this.SaveSecrets();
        this.Publish();
        this.RefreshDevicesSoon();
    }

    /// <summary>
    /// Tells the player about new computers: now, together with any held back, unless the last such notice was less than
    /// <see cref="ClientSessionOptions.DeviceNoticeInterval"/> ago; then they are held, and told together once it has passed.
    /// Call inside the lock; <see cref="Publish"/> raises the notice.
    /// </summary>
    private void TellNewDevices(int count, DateTimeOffset? newest) {
        if (count <= 0) {
            return;
        }

        this._heldNewDevices += count;
        if (newest != null && (this._heldNewest == null || newest > this._heldNewest)) {
            this._heldNewest = newest;
        }

        var now = this._options.TimeProvider.GetUtcNow();
        var due = this._devicesToldAt == DateTimeOffset.MinValue ? now : this._devicesToldAt + this._options.DeviceNoticeInterval;
        if (now >= due) {
            this.TellHeldDevices(now);
            return;
        }

        if (this._devicesTellScheduled) {
            return;
        }

        this._devicesTellScheduled = true;
        this.RunBackground("Telling you about new computers", async ct => {
            await Task.Delay(due - now, this._options.TimeProvider, ct);
            lock (this._lock) {
                this._devicesTellScheduled = false;
                this.TellHeldDevices(this._options.TimeProvider.GetUtcNow());
            }

            this.Publish();
        });
    }

    /// <summary>Tells the player about the new computers held back, if any, in one notice. Call inside the lock.</summary>
    private void TellHeldDevices(DateTimeOffset now) {
        if (this._heldNewDevices == 0) {
            return;
        }

        this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, DeviceWords.SignedInElsewhere(this._heldNewDevices, this._heldNewest)));
        this._heldNewDevices = 0;
        this._heldNewest = null;
        this._devicesToldAt = now;
    }

    /// <summary>A key login was refused: note whether it was because the account signed out everywhere else, and who did.</summary>
    private void NoteKeyLoginRefused(ServerErrorException ex) {
        if (ex.Code != ErrorCode.NotAuthenticated) {
            // Limited, say: what the last refusal said still holds.
            return;
        }

        lock (this._lock) {
            this._signedOutBy = ex.SignedOut ? this.WhoSignedOut(ex.SignedOutBy) : SignedOutBy.None;
        }
    }

    /// <summary>
    /// Which kind of computer the device that signed out everywhere else is, as this one can tell: itself, if it did that and
    /// has lost its login since; one it has seen; or one it never saw, or a copy of its own login (it is this computer's ID,
    /// but this computer never did it). Call inside the lock.
    /// </summary>
    private SignedOutBy WhoSignedOut(byte[] deviceId) {
        var id = deviceId.Length is > 0 and <= MaxDeviceIdBytes ? Convert.ToHexStringLower(deviceId) : null;
        if (id != null && id == this._secrets.ThisDeviceId) {
            return this._secrets.SignedOutOthers ? SignedOutBy.ThisComputer : SignedOutBy.UnknownComputer;
        }

        return id != null && this._secrets.KnownDevices?.Contains(id) == true ? SignedOutBy.YourOtherComputer : SignedOutBy.UnknownComputer;
    }

    /// <summary>The status while the server refuses the saved login and the identity key. Call inside the lock.</summary>
    private Wording RejectedStatus() => this._signedOutBy != SignedOutBy.None ? DeviceWords.SignedOutStatus(this._signedOutBy) : PlainMessages.LoginNotRecognized;

    /// <summary>A device's ID as remembered (hex), or null if it isn't one.</summary>
    private static string? IdOf(ByteString id) =>
        id.Length is > 0 and <= MaxDeviceIdBytes ? Convert.ToHexStringLower(id.Span) : null;

    /// <summary>A time the server gave in Unix seconds, or null if no clock has it.</summary>
    private static DateTimeOffset? TimeOf(long unixSeconds) =>
        unixSeconds > 0 && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) : null;
}
