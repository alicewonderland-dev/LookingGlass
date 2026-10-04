using System.Net;
using System.Net.WebSockets;

namespace LookingGlass.Core.Client;

/// <summary>
/// Opens the WebSocket to a server: the default for <see cref="ClientSessionOptions.Connect"/>.
///
/// Redirects are never followed. Everything a client keeps (identity keys, the device token, channel keys) is filed
/// under the address it was given, and the token is sent on the connection; following a redirect would hand that
/// connection to whatever server the redirect names, while the client goes on filing what it learns there under the
/// first address. A server that moved says so to its users, who change the address in Settings.
/// </summary>
public static class WebSocketConnector {
    // Shared, as ClientWebSocket recommends; a WebSocket's connection leaves the pool once upgraded.
    private static readonly HttpMessageInvoker NoRedirects = new(new SocketsHttpHandler {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <exception cref="ServerRedirectException">The server answered with a redirect.</exception>
    public static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct) {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // For the status code of a refused upgrade.
        socket.Options.CollectHttpResponseDetails = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DefaultTimeout);
        try {
            await socket.ConnectAsync(uri, NoRedirects, timeout.Token);
            return socket;
        } catch (WebSocketException ex) when (socket.HttpStatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest) {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new ServerRedirectException(uri, status, ex);
        } catch {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The server answered the connection with a redirect, which the client doesn't follow.</summary>
public sealed class ServerRedirectException(Uri uri, HttpStatusCode status, Exception inner) : IOException(
    $"The server at {uri} answered with a redirect (HTTP {(int) status}) instead of accepting the connection. LookingGlass doesn't follow " +
    "redirects, so your login and keys only ever go to the address you set. If the server moved, check its new address with whoever runs it.",
    inner) {
    public HttpStatusCode Status { get; } = status;
}
