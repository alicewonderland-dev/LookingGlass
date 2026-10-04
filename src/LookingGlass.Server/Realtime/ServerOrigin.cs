namespace LookingGlass.Server.Realtime;

/// <summary>
/// Where a client reached the server: whether over TLS, the host name and the port. Key login signatures name the
/// server's address, and the server only accepts one whose origin is one of its configured public addresses, so a
/// signature a malicious server relays from its own users is refused (see <see cref="Core.Crypto.KeyLoginProof"/>).
/// <see cref="FromRequest"/> (the Host header) proves nothing against a relay, which sets it; it is only used, in
/// Development, when no public addresses are configured.
///
/// Only the origin counts, not the path: whoever serves an origin terminates its connections and so can read anything
/// sent to any path on it, device tokens included. Comparing paths would add nothing, and would break behind proxies
/// that rewrite them.
/// </summary>
/// <param name="Secure">wss (or https), rather than ws (or http).</param>
/// <param name="Host">Lowercase, IDN in ASCII form, without a trailing dot; IPv6 addresses in brackets.</param>
/// <param name="Port">Always given, the scheme's default if the address leaves it out.</param>
public sealed record ServerOrigin(bool Secure, string Host, int Port) {
    /// <summary>Longer addresses are refused without parsing.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The origin of a ws, wss, http or https URL, or null for anything else.</summary>
    public static ServerOrigin? FromUrl(string? url) {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
            return null;
        }

        bool secure;
        switch (uri.Scheme) {
            case "wss" or "https":
                secure = true;
                break;
            case "ws" or "http":
                secure = false;
                break;
            default:
                return null;
        }

        string host;
        try {
            // Host keeps an IPv6 address's brackets (and Uri normalises its form); IdnHost gives a name's ASCII form.
            host = (uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost).TrimEnd('.').ToLowerInvariant();
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) {
            // A host name IDN can't convert matches nothing.
            return null;
        }

        if (host.Length == 0) {
            return null;
        }

        // Uri knows the default ports of all four schemes, but not every runtime has always known ws and wss.
        var port = uri.IsDefaultPort || uri.Port < 0 ? (secure ? 443 : 80) : uri.Port;
        return new ServerOrigin(secure, host, port);
    }

    /// <summary>
    /// The origin a request was made to: its scheme (after X-Forwarded-Proto from a trusted proxy) and Host header.
    /// The Host header is whatever the connecting side sent, so this only says where an honest client connected.
    /// </summary>
    public static ServerOrigin? FromRequest(string scheme, string? host) {
        // Kestrel refuses Host headers that aren't a host and optional port; this doesn't rely on it.
        if (string.IsNullOrEmpty(host) || host.Length > MaxUrlLength || host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0) {
            return null;
        }

        return FromUrl($"{scheme}://{host}/");
    }

    public override string ToString() => $"{(this.Secure ? "wss" : "ws")}://{this.Host}:{this.Port}";
}
