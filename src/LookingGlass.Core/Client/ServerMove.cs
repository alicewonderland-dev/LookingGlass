using System.Net.WebSockets;
using LookingGlass.Protocol;

namespace LookingGlass.Core.Client;

public enum ServerMoveVerdict {
    /// <summary>The server at the current address lists the new address's origin, and the server there lists the current one's.</summary>
    SameServer,

    /// <summary>The current server lists addresses, but not the new one's origin.</summary>
    NotListed,

    /// <summary>The current server lists no addresses (no PublicUrls configured, or an older server), so it can't vouch for any.</summary>
    NothingListed,

    /// <summary>The current server lists the new address, but the server at the new address doesn't list the current one.</summary>
    NotConfirmed,

    /// <summary>One of the two couldn't be asked: unreachable, or it didn't answer Hello.</summary>
    Unreachable,

    /// <summary>The new address isn't a ws:// or wss:// address.</summary>
    InvalidAddress,

    /// <summary>
    /// The new address is plain ws://, so nothing proves which server answers there: an identity is never carried to it.
    /// Neither server is asked.
    /// </summary>
    NotSecure,
}

/// <summary>What the servers at a client's current and new addresses said about each other. Only <see cref="ServerMove.CheckAsync"/> makes these.</summary>
public sealed class ServerMoveCheck {
    internal ServerMoveCheck(string currentUrl, string newUrl, ServerMoveVerdict verdict, IReadOnlyList<string> listedUrls, string message, DateTimeOffset checkedAt) {
        this.CurrentUrl = currentUrl;
        this.NewUrl = newUrl;
        this.Verdict = verdict;
        this.ListedUrls = listedUrls;
        this.Message = message;
        this.CheckedAt = checkedAt;
    }

    public string CurrentUrl { get; }
    public string NewUrl { get; }
    public ServerMoveVerdict Verdict { get; }

    /// <summary>The addresses the current server listed as its own (as it gave them).</summary>
    public IReadOnlyList<string> ListedUrls { get; }

    /// <summary>For the user: what the servers said, or why one couldn't be asked.</summary>
    public string Message { get; }

    /// <summary>When the servers were asked (by the clock <see cref="ServerMove.CheckAsync"/> was given).</summary>
    public DateTimeOffset CheckedAt { get; }

    /// <summary>
    /// Whether this check is recent enough to act on: made at most <see cref="ServerMove.MaxCheckAge"/> ago, and not in
    /// the future (a clock set back since). What the servers said may have changed since then.
    /// </summary>
    public bool IsFresh(TimeProvider? time = null) {
        var age = (time ?? TimeProvider.System).GetUtcNow() - this.CheckedAt;
        return age >= TimeSpan.Zero && age <= ServerMove.MaxCheckAge;
    }
}

/// <summary>
/// Carrying an identity over to a server's new address. Identities are kept per address (see <see cref="ServerSecretFiles"/>),
/// so without this a server reached at a new address (another name, TLS added) would mean registering again through the
/// Lodestone with new keys and losing every channel.
///
/// The evidence that a new address is the same server is the operator's configured addresses (PublicUrls), which Welcome
/// lists, from both sides:
/// <list type="bullet">
/// <item>The server at the current address, the one the client already uses and sends its login to, must list the new
/// address's origin. This is what counts: a server at the new address claiming to be the old one gets nothing without
/// it, and isn't even asked. The check is as trustworthy as the connection the client logs in over anyway: someone who
/// could forge the old server's Welcome could read its login too.</item>
/// <item>The server at the new address must list the current address's origin. Without this, a malicious (or
/// compromised) old server could list an honest server's address, so its users carry their identity key there; it
/// could then relay that server's key login challenges to them (which only that server's PublicUrls stops) and link
/// the two identities. Asked only after the old server vouched, with nothing but Hello.</item>
/// <item>The new address must be wss://. Both servers vouch for names, not for whoever answers at them: over plain
/// ws:// (or a short name the local network resolves, through LLMNR say), someone who answers at the new name can
/// confirm the old address by repeating the real server's PublicUrls, then receive the copied login in the clear and
/// relay key login challenges to the real server. TLS is what proves the new name's server is the one the operator
/// named. A plain ws:// address is still fine for key login on a private network; it just can't be a move's target.</item>
/// </list>
/// A long-term server key in Welcome, signed over by both addresses, wouldn't replace TLS: a relaying attacker forwards
/// the real server's Welcome and any proof made with that key unchanged, and nothing binds the proof to the connection
/// it arrives on (no channel binding over ws://), so it would prove only that the real server exists somewhere.
///
/// A check is acted on only while fresh (<see cref="MaxCheckAge"/>), so a dialog left open never copies on old evidence.
/// </summary>
public static class ServerMove {
    /// <summary>How long a check may be acted on (see <see cref="ServerMoveCheck.IsFresh"/>); after that, ask again.</summary>
    public static readonly TimeSpan MaxCheckAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Asks the server at <paramref name="currentUrl"/> whether <paramref name="newUrl"/> is one of its addresses and, if
    /// it says so, the server at <paramref name="newUrl"/> whether <paramref name="currentUrl"/> is one of its own. A
    /// <paramref name="newUrl"/> that isn't wss:// is refused without asking either.
    /// </summary>
    /// <param name="currentUrl">The address in use, as configured (secrets are filed under it).</param>
    /// <param name="connect">Opens a WebSocket; by default <see cref="WebSocketConnector.ConnectAsync"/> (no redirects).</param>
    /// <param name="time">The clock the check is dated by (see <see cref="ServerMoveCheck.CheckedAt"/>).</param>
    public static async Task<ServerMoveCheck> CheckAsync(string currentUrl, string newUrl, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null,
        CancellationToken ct = default, TimeProvider? time = null) {
        connect ??= WebSocketConnector.ConnectAsync;
        time ??= TimeProvider.System;
        var current = currentUrl.Trim();
        newUrl = newUrl.Trim();
        // Dated from the start: what the servers say is only ever as recent as the first question.
        var started = time.GetUtcNow();
        var newOrigin = Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri) && newUri.Scheme is "ws" or "wss" ? ServerOrigin.FromUrl(newUrl) : null;
        if (newOrigin == null || newUri == null) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.InvalidAddress, [],
                $"{newUrl} isn't a server address: it should look like ws://host:5180/ws or wss://host/ws.", started);
        }

        if (newUri.Scheme != "wss") {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NotSecure, [],
                $"{newUrl} isn't a wss:// address, so nothing proves which server answers there, and LookingGlass only carries your identity " +
                "to a wss:// address. To keep your identity, use the server's wss:// address instead.", started);
        }

        Welcome welcome;
        try {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri)) {
                throw new InvalidOperationException("it isn't a valid address");
            }

            welcome = await HelloAsync(currentUri, connect, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.Unreachable, [],
                $"Couldn't ask the server at {current} whether {newUrl} is its new address: {ex.Message}", started);
        }

        var listed = welcome.PublicUrls.ToList();
        if (listed.Count == 0) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NothingListed, listed,
                $"The server at {current} doesn't say which addresses are its own (its operator hasn't set LookingGlass:PublicUrls), " +
                $"so it can't confirm that {newUrl} is the same server.", started);
        }

        if (!newOrigin.IsListedIn(listed)) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NotListed, listed,
                $"The server at {current} doesn't list {newUrl} as one of its addresses (it lists {string.Join(", ", listed)}), so it may be a different server.",
                started);
        }

        // The old server vouches; the new address must agree.
        Welcome confirming;
        try {
            confirming = await HelloAsync(newUri, connect, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.Unreachable, listed,
                $"The server at {current} lists {newUrl} as one of its addresses, but {newUrl} couldn't be reached to confirm it: {ex.Message}", started);
        }

        var currentOrigin = ServerOrigin.FromUrl(current);
        if (currentOrigin == null || !currentOrigin.IsListedIn(confirming.PublicUrls)) {
            var theirs = confirming.PublicUrls.Count == 0 ? "no addresses" : string.Join(", ", confirming.PublicUrls);
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NotConfirmed, listed,
                $"The server at {current} lists {newUrl} as one of its addresses, but the server at {newUrl} doesn't list {current} " +
                $"(it lists {theirs}), so they may not be the same server.", started);
        }

        return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.SameServer, listed,
            $"The server at {current} lists {newUrl} as one of its own addresses, and the server there lists {current}.", started);
    }

    /// <summary>
    /// Copies the identity (keys, login, channel keys and state) from the current address's store to the new one's,
    /// re-bound to the new address. Only for a move both servers vouched for, in a check that is still fresh (see
    /// <see cref="ServerMoveCheck.IsFresh"/>); the old store is left as it is, so switching back works.
    /// </summary>
    /// <param name="time">The clock the check's age is measured by (as given to <see cref="CheckAsync"/>).</param>
    /// <exception cref="InvalidOperationException">
    /// The check didn't vouch for this move (or was for another one, or is too old), the new address already has an
    /// identity, or the current one has none. Nothing was copied.
    /// </exception>
    public static void CopyIdentity(ServerMoveCheck check, ServerBoundSecretStore from, ServerBoundSecretStore to, TimeProvider? time = null) {
        if (check.Verdict != ServerMoveVerdict.SameServer) {
            throw new InvalidOperationException($"Nothing was copied: {check.Message}");
        }

        // CheckAsync never vouches for one, but this is what the copy's safety rests on: check rather than assume.
        if (!Uri.TryCreate(check.NewUrl, UriKind.Absolute, out var newUri) || newUri.Scheme != "wss") {
            throw new InvalidOperationException($"Nothing was copied: {check.NewUrl} isn't a wss:// address.");
        }

        if (!check.IsFresh(time)) {
            throw new InvalidOperationException(
                $"Nothing was copied: the servers were asked more than {MaxCheckAge.TotalSeconds:0} seconds ago, and may say otherwise now. Ask them again.");
        }

        if (from.ServerUrl != ServerSecretFiles.NormaliseUrl(check.CurrentUrl) || to.ServerUrl != ServerSecretFiles.NormaliseUrl(check.NewUrl)) {
            throw new InvalidOperationException("Nothing was copied: the servers vouched for another move.");
        }

        var existing = to.Load();
        if (existing.SigningPrivateKey != null || existing.DeviceToken != null) {
            throw new InvalidOperationException($"Nothing was copied: there already is an identity for {check.NewUrl}.");
        }

        var secrets = from.Load();
        if (secrets.SigningPrivateKey == null || secrets.AgreementPrivateKey == null) {
            throw new InvalidOperationException($"Nothing was copied: there is no identity for {check.CurrentUrl}.");
        }

        // Deliberately re-bound: the store stamps the new address.
        secrets.ServerUrl = null;
        secrets.ServerOrigin = null;
        to.Save(secrets);
    }

    private static async Task<Welcome> HelloAsync(Uri uri, Func<Uri, CancellationToken, Task<WebSocket>> connect, CancellationToken ct) {
        var socket = await connect(uri, ct);
        await using var connection = new Connection(socket, 1024 * 1024, WebSocketConnector.DefaultTimeout, _ => { }, _ => { }, (_, _) => { });
        connection.Start();
        var hello = new Hello { ClientVersion = "move-check" };
        hello.ProtocolVersions.Add(ProtocolInfo.CurrentVersion);
        var response = await connection.RequestAsync(new ClientFrame { Hello = hello }, ct);
        return response.Welcome
               ?? throw new InvalidOperationException(response.Error is { } error ? $"the server said: {error.Message}" : "the server didn't answer as a LookingGlass server");
    }
}
