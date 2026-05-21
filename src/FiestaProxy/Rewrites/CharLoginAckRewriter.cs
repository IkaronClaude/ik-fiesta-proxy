using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// PROTO_NC_CHAR_LOGIN_ACK (0x1003). Sent by WorldManager to a logged-in client
/// telling it which Zone host:port to connect to. Sizeof 18 — well-defined:
///   offset  0 : 16     Name4 zoneip    (ASCII dotted-quad, zero-padded)
///   offset 16 : ushort zoneport       (LE)
///
/// Confirmed from a real capture: WM emits its zone's internal address here.
/// In a containerised stack the internal address is unreachable from outside,
/// so the proxy must rewrite it to the operator-facing endpoint of the matching
/// Zone_W_Z service.
///
/// The packet itself doesn't say which Zone — we identify the zone by reverse-
/// lookup: parse the (host, port) the WM emitted, find a ProxyRoute whose
/// (UpstreamHost-resolved, UpstreamPort) match, then advertise that route's
/// external endpoint.
/// </summary>
public sealed class CharLoginAckRewriter : IPacketRewriter
{
    public ushort Opcode => 0x1003;

    private const int IpOffset = 0;
    private const int IpLen = 16;
    private const int PortOffset = 16;
    private const int Sizeof = 18;

    public FiestaPacket Rewrite(FiestaPacket packet, ProxyConfig config)
    {
        if (packet.Payload.Length < Sizeof) return packet;

        var payload = packet.Payload.ToArray();
        var originalIp = ReadName4Ip(payload.AsSpan(IpOffset, IpLen));
        var originalPort = (ushort)(payload[PortOffset] | (payload[PortOffset + 1] << 8));

        var route = FindZoneRoute(originalIp, originalPort, config);
        if (route is null)
        {
            Log.Warn($"CHAR_LOGIN_ACK: no Zone route matches {originalIp}:{originalPort} — leaving original endpoint");
            return packet;
        }

        var (ipv4, port) = EndpointResolver.ResolveForService(route.ServiceName, (ushort)route.ListenPort, config);

        EndpointResolver.WriteName4Ip(payload.AsSpan(IpOffset, IpLen), ipv4);
        payload[PortOffset] = (byte)(port & 0xFF);
        payload[PortOffset + 1] = (byte)(port >> 8);

        Log.Debug($"CHAR_LOGIN_ACK rewrite: {originalIp}:{originalPort} -> {ipv4[0]}.{ipv4[1]}.{ipv4[2]}.{ipv4[3]}:{port} ({route.ServiceName})");
        return new FiestaPacket(packet.Opcode, payload);
    }

    private static ProxyRoute? FindZoneRoute(string emittedIp, ushort emittedPort, ProxyConfig config)
    {
        // Port match is sufficient for distinct zones — each zone has a unique
        // upstream port. We don't reverse-resolve the host because the WM
        // emits the address it was configured with (per-zone ServerInfo) which
        // may be a literal docker service name or its resolved IP; either way
        // the port disambiguates among Zone_* routes.
        return config.Routes.FirstOrDefault(r =>
            r.ServiceName.StartsWith("Zone_", StringComparison.Ordinal) &&
            r.UpstreamPort == emittedPort);
    }

    private static string ReadName4Ip(ReadOnlySpan<byte> ipField)
    {
        var nul = ipField.IndexOf((byte)0);
        var len = nul < 0 ? ipField.Length : nul;
        return System.Text.Encoding.ASCII.GetString(ipField[..len]);
    }
}
