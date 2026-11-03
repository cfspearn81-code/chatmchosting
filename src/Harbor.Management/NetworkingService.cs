using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Harbor.Management;

public sealed record ConnectionAddress(string Interface, string Address, bool IsVpnLike);
public static class NetworkingService
{
    public static IReadOnlyList<ConnectionAddress> GetLanAddresses()
    {
        var results = new List<ConnectionAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var vpn = adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel || adapter.Name.StartsWith("tun", StringComparison.Ordinal) || adapter.Name.StartsWith("wg", StringComparison.Ordinal) || adapter.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase);
            foreach (var address in adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            {
                if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast) continue;
                if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) results.Add(new(adapter.Name, address.ToString(), vpn));
            }
        }
        return results;
    }
    public const string Guidance = "Connect on this laptop using localhost and the server port. Other LAN devices use a reachable adapter address and that port. A LAN address does not prove internet reachability. UFW, router forwarding, dynamic addresses, IPv4 CGNAT and IPv6 firewall exposure may affect access. Harbor never changes firewall or router settings. Logging out, suspend, lid-close power actions and shutdown may stop hosting; a sleeping or powered-off laptop cannot serve players.";
}
