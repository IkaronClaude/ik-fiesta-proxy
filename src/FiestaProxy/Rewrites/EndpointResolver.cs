using System.Net;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// Resolves an operator-facing endpoint for a service into a 4-byte IPv4 + port.
/// Per the spec, DNS is queried fresh on every rewrite so the proxy reacts
/// immediately to scale events that move a service to a new host.
/// </summary>
public static class EndpointResolver
{
    public static (byte[] Ipv4, ushort Port) ResolveForService(string serviceName, ushort fallbackPort, ProxyConfig config)
    {
        var (host, port) = config.ExternalEndpoint(serviceName, fallbackPort);
        var ipv4 = ResolveIpv4(host);
        return (ipv4, port);
    }

    public static byte[] ResolveIpv4(string host)
    {
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return literal.GetAddressBytes();

        var addresses = Dns.GetHostAddresses(host);
        foreach (var a in addresses)
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return a.GetAddressBytes();

        throw new InvalidOperationException($"No IPv4 address for host '{host}'");
    }

    /// <summary>
    /// Writes an IPv4 dotted-quad ASCII string into a 16-byte Name4 buffer,
    /// zero-padding the remainder. Matches how the official server fills
    /// PROTO_NC_USER_WORLDSELECT_ACK.ip on the wire.
    /// </summary>
    public static void WriteName4Ip(Span<byte> dest16, byte[] ipv4)
    {
        if (dest16.Length < 16) throw new ArgumentException("dest must be >= 16 bytes", nameof(dest16));
        dest16[..16].Clear();
        var s = $"{ipv4[0]}.{ipv4[1]}.{ipv4[2]}.{ipv4[3]}";
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        if (bytes.Length > 16) throw new InvalidOperationException($"IP '{s}' does not fit in Name4 (16 bytes)");
        bytes.CopyTo(dest16);
    }
}
