using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace LookingGlass.Server.Hosting;

/// <summary>Which proxies to believe about client addresses, and how addresses are grouped for per-IP limits.</summary>
public static class ClientAddresses {
    /// <summary>
    /// Trusts each entry of <c>LookingGlass:TrustedProxies</c>: a single address
    /// ("10.0.0.5") or a network in CIDR form ("172.17.0.0/16", for example a
    /// Docker bridge network a host proxy connects from).
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is neither, so startup stops instead of silently trusting nothing.</exception>
    public static void AddTrustedProxies(ForwardedHeadersOptions options, IEnumerable<string> entries) {
        foreach (var raw in entries) {
            var entry = raw.Trim();
            if (entry.Contains('/')) {
                if (!System.Net.IPNetwork.TryParse(entry, out var network)) {
                    throw new InvalidOperationException($"LookingGlass:TrustedProxies: \"{entry}\" isn't a valid network (expected CIDR form, like 172.17.0.0/16).");
                }

                options.KnownIPNetworks.Add(network);
            } else if (IPAddress.TryParse(entry, out var address)) {
                options.KnownProxies.Add(address);
            } else {
                throw new InvalidOperationException($"LookingGlass:TrustedProxies: \"{entry}\" isn't an IP address or a network.");
            }
        }
    }

    /// <summary>
    /// The key per-IP limits count against. An IPv6 client usually controls a
    /// whole /64, so all of it counts as one address.
    /// </summary>
    public static string LimitKey(IPAddress? address) {
        if (address == null) {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// The key connection limits count against: like <see cref="LimitKey"/>, but a whole IPv6 /56, the least an ISP commonly
    /// gives one customer (many give a /48), so holding connections open takes many customers' worth of addresses, not
    /// just many /64s of one.
    /// </summary>
    public static string ConnectionLimitKey(IPAddress? address) {
        if (address == null) {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 7, 9);
        return $"{new IPAddress(bytes)}/56";
    }
}
