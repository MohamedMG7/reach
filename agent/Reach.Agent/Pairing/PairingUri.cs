using System.Buffers.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Reach.Agent.Pairing;

/// <summary>The link shown as a QR code: reach://pair?v=1&amp;h=&lt;ip&gt;[,&lt;ip&gt;…]&amp;p=&amp;k=&amp;c=&amp;n= (spec §3.3).</summary>
public static class PairingUri
{
    public static string Build(IEnumerable<IPAddress> hosts, int port, byte[] pcPublicKey, string code, string pcName) =>
        $"reach://pair?v=1&h={string.Join(',', hosts)}&p={port}&k={Base64Url.EncodeToString(pcPublicKey)}" +
        $"&c={code}&n={Uri.EscapeDataString(pcName)}";

    /// <summary>RFC 1918 IPv4 only: 10/8, 172.16/12, 192.168/16.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// Private IPv4 addresses of every interface that is up. Interfaces with a default gateway
    /// (the real Wi-Fi/Ethernet link) come first, so the phone tries them before virtual adapters.
    /// </summary>
    public static IReadOnlyList<IPAddress> LocalPrivateAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses.Select(u => u.Address))
            .Where(IsPrivate)
            .Distinct()
            .ToList();
}
