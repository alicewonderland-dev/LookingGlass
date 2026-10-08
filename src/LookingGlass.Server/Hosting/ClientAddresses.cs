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
    /// The <see cref="ConnectionLimitKey"/> for a <see cref="LimitKey"/> (an IPv6 /64 widened to its /56; anything else as it
    /// is), for limits that count per customer rather than per /64, such as registrations.
    /// </summary>
    public static string WidenToConnectionKey(string limitKey) {
        return limitKey.EndsWith("/64", StringComparison.Ordinal) && IPAddress.TryParse(limitKey[..^3], out var prefix)
            ? ConnectionLimitKey(prefix)
            : limitKey;
    }

    /// <summary>The narrowest IPv4 network a ban may cover, as a prefix length: a /16 (65,536 addresses).</summary>
    public const int WidestIpv4Ban = 16;

    /// <summary>The widest IPv6 prefix a ban may cover: a /32, as large as a whole ISP's.</summary>
    public const int WidestIpv6Ban = 32;

    /// <summary>
    /// What an operator's ban on an address covers, written one way: an IPv4 address ("203.0.113.5") or network
    /// ("203.0.113.0/24", a /16 or narrower), or an IPv6 prefix of /32 to /64 ("2001:db8:1:2::/64"; an IPv6 address alone
    /// stands for its /64, which one client usually has, as limits count it). Bits past the prefix are cleared, so a flag's
    /// address and a ban on it read the same.
    /// </summary>
    /// <returns>The prefix, or null (and <paramref name="problem"/> says why) if it isn't one that can be banned.</returns>
    public static string? BanPrefix(string text, out string? problem) {
        problem = null;
        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/');
        var addressPart = slash < 0 ? trimmed : trimmed[..slash];
        if (!IPAddress.TryParse(addressPart, out var address) || addressPart.Contains('%')) {
            problem = $"\"{trimmed}\" isn't an address or a prefix (such as 203.0.113.5, 203.0.113.0/24 or 2001:db8:1:2::/64).";
            return null;
        }

        var ipv4 = address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6;
        int length;
        if (slash < 0) {
            length = ipv4 ? 32 : 64;
        } else if (!int.TryParse(trimmed[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length)) {
            problem = $"\"{trimmed}\" has no prefix length after the /.";
            return null;
        } else if (address.IsIPv4MappedToIPv6) {
            // An IPv4 address written as IPv6 (::ffff:203.0.113.0/120) is that IPv4 network.
            length -= 96;
        }

        if (ipv4) {
            address = address.MapToIPv4();
            if (length is < WidestIpv4Ban or > 32) {
                problem = $"An IPv4 ban covers a /{WidestIpv4Ban} at the widest, and a /32 (one address) at the narrowest; {trimmed} doesn't.";
                return null;
            }
        } else if (length is < WidestIpv6Ban or > 64) {
            problem = $"An IPv6 ban covers a /{WidestIpv6Ban} at the widest, and a /64 at the narrowest (one client usually has a whole /64); {trimmed} doesn't.";
            return null;
        }

        var bytes = address.GetAddressBytes();
        for (var bit = length; bit < bytes.Length * 8; bit++) {
            bytes[bit / 8] &= (byte) ~(0x80 >> (bit % 8));
        }

        var network = new IPAddress(bytes);
        return ipv4 && length == 32 ? network.ToString() : $"{network}/{length}";
    }

    /// <summary>
    /// Whether a ban's prefix (as <see cref="BanPrefix"/> writes it) covers an address as <see cref="LimitKey"/> gives it (an
    /// IPv4 address, or an IPv6 /64, which a prefix of /64 or wider covers whole or not at all).
    /// </summary>
    public static bool Covers(string prefix, string limitKey) {
        var slash = limitKey.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? limitKey : limitKey[..slash], out var address)
            || !System.Net.IPNetwork.TryParse(prefix.Contains('/') ? prefix : prefix + "/32", out var network)) {
            return false;
        }

        return network.Contains(address);
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
