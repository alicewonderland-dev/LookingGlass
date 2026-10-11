using System.Net;
using System.Net.NetworkInformation;

namespace LookingGlass.Server.Admin;

/// <summary>
/// This machine's own addresses: loopback, and every unicast address of every network interface, Tailscale's (100.x, and
/// fd7a:115c:a1e0::/48) included. <c>tailscale serve</c> gives a request from the machine itself the identity of the machine's
/// own Tailscale login (it looks the request's source up, and the source is the machine's own tailnet address), so the
/// operator web page refuses any request whose <c>X-Forwarded-For</c> is one of these: otherwise any program on the machine
/// could reach the page as the owner, around the socket's permissions. The addresses are read at start, and again whenever an
/// address isn't among those last read, since Tailscale's can appear after the server starts (the page has one operator: a
/// read per request is nothing).
/// </summary>
public sealed class LocalAddresses {
    private readonly Func<IEnumerable<IPAddress>> _source;
    private volatile HashSet<IPAddress> _known = [];

    /// <param name="source">Gives the machine's addresses (by default, its network interfaces'); for tests.</param>
    public LocalAddresses(Func<IEnumerable<IPAddress>>? source = null) {
        this._source = source ?? InterfaceAddresses;
        this.Read();
    }

    /// <summary>Whether <paramref name="address"/> is one of this machine's own (loopback and the unspecified addresses always are).</summary>
    public bool IsLocal(IPAddress address) {
        address = Normal(address);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) {
            return true;
        }

        // Not among those last read: read them again (an address may have come since), then decide.
        return this._known.Contains(address) || this.Read().Contains(address);
    }

    private HashSet<IPAddress> Read() {
        try {
            this._known = this._source().Select(Normal).ToHashSet();
        } catch (NetworkInformationException) {
            // Kept as they were.
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
