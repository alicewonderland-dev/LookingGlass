using System.Collections.Immutable;
using Google.Protobuf;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

/// <summary>
/// The account's devices (each computer signed in has a login of its own; see "Other computers signing in" in
/// docs/design.md). The player is told when another computer gets a login: at once, when the server says so (DeviceAdded),
/// or else at the next login, from a device in the list this computer hasn't seen (<see cref="ClientSecrets.KnownDevices"/>);
/// never about this computer, and nothing from the first list a computer has, as it can't tell what is new in it. Settings
/// shows the list, and "Sign out everywhere else" revokes every other login. All with a server that agreed to "devices.v1".
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
    // The server refused the identity key because another computer used "Sign out everywhere else" (Error.signed_out); until
    // a login works again.
    private bool _signedOutElsewhere;
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
    /// "Sign out everywhere else": the server revokes every other login of the account, and refuses its identity key until the
    /// character is registered again through the Lodestone, so a copy of the key can't sign straight back in. This computer
    /// stays signed in. The player is told how many were signed out.
    /// </summary>
    /// <returns>How many other devices were signed out.</returns>
    /// <exception cref="InvalidOperationException">Not logged in, or the server doesn't offer it (<see cref="DeviceWords.NotAvailable"/>).</exception>
    public async Task<int> SignOutOtherDevicesAsync(CancellationToken ct = default) {
        var connection = this.RequireDevices();
        var response = await this.RequestAsync(connection, new ClientFrame { SignOutOtherDevices = new SignOutOtherDevices() }, ct);
        var devices = response.Devices ?? throw Unexpected(response);
        this.ApplyDevices(connection, devices);
        var count = (int) Math.Min(devices.SignedOut, int.MaxValue);
        this.RaiseNotice(NoticeLevel.Info, DeviceWords.SignedOutOthers(count));
        return count;
    }

    /// <summary>Fetches the account's devices in the background, if the server keeps them; failing quietly (it is asked again at the next login).</summary>
    public void RefreshDevicesSoon() {
        if (!this.DevicesAvailable) {
            return;
        }

        this.RunBackground("Listing your computers", async ct => {
            try {
                await this.RefreshDevicesAsync(ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Disconnected meanwhile (another computer's login replaces this one's connection), say.
                this.Log(NoticeLevel.Info, $"Couldn't list this account's devices: {ex.Message}");
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

    /// <summary>Takes a list of the account's devices: shown in Settings, and any other computer's not seen before told about.</summary>
    private void ApplyDevices(Connection connection, Devices devices) {
        lock (this._lock) {
            // A list asked for on a connection that has dropped since may be another account's (a login replaced meanwhile).
            if (connection != this._connection) {
                return;
            }

            var known = this._secrets.KnownDevices;
            var shown = new List<DeviceView>();
            var listed = new List<string>();
            foreach (var device in devices.List.Take(MaxDevicesListed)) {
                if (IdOf(device.Id) is not { } id) {
                    continue;
                }

                listed.Add(id);
                var added = TimeOf(device.AddedUnix);
                if (added != null && TimeOf(device.LastUsedUnix) is { } used) {
                    shown.Add(new DeviceView(added.Value, used, device.ThisDevice));
                }

                // Never this computer; and nothing from the first list this computer has, which it can't tell what is new in.
                if (known != null && !device.ThisDevice && !known.Contains(id)) {
                    this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, DeviceWords.SignedInElsewhere(added)));
                }
            }

            this._devices = [.. shown];
            // Those listed now, then those seen before, so a device told of (by an event, say) is never told again.
            this._secrets.KnownDevices = listed.Concat(known ?? []).Distinct().Take(MaxKnownDevices).ToList();
            this._secretsVersion++;
        }

        this.SaveSecrets();
        this.Publish();
    }

    /// <summary>
    /// The server says the account just got a new device, not this computer's: told at once (unless it was seen already), and
    /// the list fetched again for Settings.
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
            if (known?.Contains(id) == true) {
                return;
            }

            this._pendingNotices.Add(SessionNotice.Of(NoticeLevel.Warning, DeviceWords.SignedInElsewhere(TimeOf(added.AddedUnix))));
            // Before this computer's first list (which is taken as it is) there is nothing to add it to.
            if (known != null) {
                this._secrets.KnownDevices = [id, .. known.Take(MaxKnownDevices - 1)];
                this._secretsVersion++;
            }
        }

        this.SaveSecrets();
        this.Publish();
        this.RefreshDevicesSoon();
    }

    /// <summary>A key login was refused: note whether it was because another computer signed out everywhere else.</summary>
    private void NoteKeyLoginRefused(ServerErrorException ex) {
        if (ex.Code != ErrorCode.NotAuthenticated) {
            // Limited, say: what the last refusal said still holds.
            return;
        }

        lock (this._lock) {
            this._signedOutElsewhere = ex.SignedOut;
        }
    }

    /// <summary>The status while the server refuses the saved login and the identity key. Call inside the lock.</summary>
    private Wording RejectedStatus() => this._signedOutElsewhere ? DeviceWords.SignedOutElsewhere : PlainMessages.LoginNotRecognized;

    /// <summary>A device's ID as remembered (hex), or null if it isn't one.</summary>
    private static string? IdOf(ByteString id) =>
        id.Length is > 0 and <= MaxDeviceIdBytes ? Convert.ToHexStringLower(id.Span) : null;

    /// <summary>A time the server gave in Unix seconds, or null if no clock has it.</summary>
    private static DateTimeOffset? TimeOf(long unixSeconds) =>
        unixSeconds > 0 && unixSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) : null;
}
