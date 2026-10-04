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
}

/// <summary>What the servers at a client's current and new addresses said about each other. Only <see cref="ServerMove.CheckAsync"/> makes these.</summary>
public sealed class ServerMoveCheck {
    internal ServerMoveCheck(string currentUrl, string newUrl, ServerMoveVerdict verdict, IReadOnlyList<string> listedUrls, string message) {
        this.CurrentUrl = currentUrl;
        this.NewUrl = newUrl;
        this.Verdict = verdict;
        this.ListedUrls = listedUrls;
        this.Message = message;
    }

    public string CurrentUrl { get; }
    public string NewUrl { get; }
    public ServerMoveVerdict Verdict { get; }

    /// <summary>The addresses the current server listed as its own (as it gave them).</summary>
    public IReadOnlyList<string> ListedUrls { get; }

    /// <summary>For the user: what the servers said, or why one couldn't be asked.</summary>
    public string Message { get; }
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
/// </list>
/// </summary>
public static class ServerMove {
    /// <summary>
    /// Asks the server at <paramref name="currentUrl"/> whether <paramref name="newUrl"/> is one of its addresses and, if
    /// it says so, the server at <paramref name="newUrl"/> whether <paramref name="currentUrl"/> is one of its own.
    /// </summary>
    /// <param name="currentUrl">The address in use, as configured (secrets are filed under it).</param>
    /// <param name="connect">Opens a WebSocket; by default <see cref="WebSocketConnector.ConnectAsync"/> (no redirects).</param>
    public static async Task<ServerMoveCheck> CheckAsync(string currentUrl, string newUrl, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null,
        CancellationToken ct = default) {
        connect ??= WebSocketConnector.ConnectAsync;
        var current = currentUrl.Trim();
        newUrl = newUrl.Trim();
        var newOrigin = Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri) && newUri.Scheme is "ws" or "wss" ? ServerOrigin.FromUrl(newUrl) : null;
        if (newOrigin == null || newUri == null) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.InvalidAddress, [],
                $"{newUrl} isn't a server address: it should look like ws://host:5180/ws or wss://host/ws.");
        }

        Welcome welcome;
        try {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var currentUri)) {
                throw new InvalidOperationException("it isn't a valid address");
            }

            welcome = await HelloAsync(currentUri, connect, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.Unreachable, [],
                $"Couldn't ask the server at {current} whether {newUrl} is its new address: {ex.Message}");
        }

        var listed = welcome.PublicUrls.ToList();
        if (listed.Count == 0) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NothingListed, listed,
                $"The server at {current} doesn't say which addresses are its own (its operator hasn't set LookingGlass:PublicUrls), " +
                $"so it can't confirm that {newUrl} is the same server.");
        }

        if (!Lists(listed, newOrigin)) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NotListed, listed,
                $"The server at {current} doesn't list {newUrl} as one of its addresses (it lists {string.Join(", ", listed)}), so it may be a different server.");
        }

        // The old server vouches; the new address must agree.
        Welcome confirming;
        try {
            confirming = await HelloAsync(newUri, connect, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.Unreachable, listed,
                $"The server at {current} lists {newUrl} as one of its addresses, but {newUrl} couldn't be reached to confirm it: {ex.Message}");
        }

        var currentOrigin = ServerOrigin.FromUrl(current);
        if (currentOrigin == null || !Lists(confirming.PublicUrls, currentOrigin)) {
            var theirs = confirming.PublicUrls.Count == 0 ? "no addresses" : string.Join(", ", confirming.PublicUrls);
            return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.NotConfirmed, listed,
                $"The server at {current} lists {newUrl} as one of its addresses, but the server at {newUrl} doesn't list {current} " +
                $"(it lists {theirs}), so they may not be the same server.");
        }

        return new ServerMoveCheck(current, newUrl, ServerMoveVerdict.SameServer, listed,
            $"The server at {current} lists {newUrl} as one of its own addresses, and the server there lists {current}.");
    }

    /// <summary>
    /// Copies the identity (keys, login, channel keys and state) from the current address's store to the new one's,
    /// re-bound to the new address. Only for a move both servers vouched for; the old store is left as it is, so
    /// switching back works.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The check didn't vouch for this move (or was for another one), the new address already has an identity, or the
    /// current one has none. Nothing was copied.
    /// </exception>
    public static void CopyIdentity(ServerMoveCheck check, ServerBoundSecretStore from, ServerBoundSecretStore to) {
        if (check.Verdict != ServerMoveVerdict.SameServer) {
            throw new InvalidOperationException($"Nothing was copied: {check.Message}");
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

    private static bool Lists(IEnumerable<string> urls, ServerOrigin origin) => urls.Any(url => ServerOrigin.FromUrl(url.Trim()) == origin);

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
