using System.Net;
using System.Net.NetworkInformation;

namespace LookingGlass.Server.Admin;

/// <summary>
/// This machine's own addresses: loopback, every unicast address of every network interface (Tailscale's 100.x and
/// fd7a:115c:a1e0::/48 ones included), and those the operator adds (<c>Admin:OwnAddresses</c>: in a container, the host's
/// tailnet addresses, which are on no interface the container sees). <c>tailscale serve</c> gives a request from the machine
/// itself the identity of the machine's own Tailscale login (it looks the request's source up, and the source is the machine's
/// own tailnet address), so the operator web page refuses any request whose <c>X-Forwarded-For</c> is one of these: otherwise
/// any program on the machine could reach the page as the owner, around the socket's permissions. The interfaces are read at
/// start, and again when an address isn't among those last read (the page asks only for requests from a listed login), since
/// Tailscale's can appear after the server starts. If they can't be read, every address counts as the machine's own: the page
/// refuses rather than guesses.
/// </summary>
public sealed class LocalAddresses {
    private readonly Func<IEnumerable<IPAddress>> _source;
    private readonly IReadOnlyList<IPNetwork> _extra;
    private volatile HashSet<IPAddress>? _known;
    private int _reads;

    /// <param name="extra">More addresses or prefixes counted as this machine's own (<c>Admin:OwnAddresses</c>, already checked).</param>
    /// <param name="source">Gives the machine's addresses (by default, its network interfaces'); for tests.</param>
    public LocalAddresses(IEnumerable<IPNetwork>? extra = null, Func<IEnumerable<IPAddress>>? source = null) {
        this._extra = extra?.ToList() ?? [];
        this._source = source ?? InterfaceAddresses;
        this.Read();
    }

    /// <summary>How many times the interfaces were read (for tests).</summary>
    internal int Reads => Volatile.Read(ref this._reads);

    /// <summary>The last read of the interfaces failed (so every address counts as this machine's own until one works).</summary>
    public bool ReadFailed => this._known == null;

    /// <summary>Whether <paramref name="address"/> is one of this machine's own (loopback and the unspecified addresses always are).</summary>
    public bool IsLocal(IPAddress address) {
        address = Normal(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || this._extra.Any(network => network.Contains(address))) {
            return true;
        }

        if (this._known is { } known && known.Contains(address)) {
            return true;
        }

        // Not among those last read: read them again (an address may have come since), then decide; unreadable counts as local.
        return this.Read() is not { } now || now.Contains(address);
    }

    /// <summary>
    /// An address or prefix as <c>Admin:OwnAddresses</c> takes it ("100.101.102.103", "fd7a:115c:a1e0::/48"; an address alone is
    /// itself only), or null if it isn't one.
    /// </summary>
    public static IPNetwork? ParseNetwork(string text) {
        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? trimmed : trimmed[..slash], out var address) || trimmed.Contains('%')) {
            return null;
        }

        address = Normal(address);
        var bits = address.GetAddressBytes().Length * 8;
        var length = bits;
        if (slash >= 0 && (!int.TryParse(trimmed[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length)
                           || length < 0 || length > bits)) {
            return null;
        }

        var bytes = address.GetAddressBytes();
        for (var bit = length; bit < bits; bit++) {
            bytes[bit / 8] &= (byte) ~(0x80 >> (bit % 8));
        }

        return new IPNetwork(new IPAddress(bytes), length);
    }

    private HashSet<IPAddress>? Read() {
        Interlocked.Increment(ref this._reads);
        try {
            this._known = this._source().Select(Normal).ToHashSet();
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            // Can't tell: nothing is known, so every address counts as this machine's own until a read works.
            this._known = null;
        }

        return this._known;
    }

    private static IEnumerable<IPAddress> InterfaceAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Select(unicast => unicast.Address);

    /// <summary>An IPv4 address written as IPv6 as itself, and an IPv6 address without its scope (a link-local one's interface).</summary>
    private static IPAddress Normal(IPAddress address) {
        if (address.IsIPv4MappedToIPv6) {
            return address.MapToIPv4();
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address;
    }
}
