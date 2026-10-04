using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// NC_MAP_LINKOTHER_CMD (0x180A), 30 B. The zone tells the client to change maps onto ANOTHER zone and names that
/// zone's address, which in a containerised stack is the internal one - so it is rewritten like CHAR_LOGIN_ACK.
/// Not in the PDB struct extract; read off Z:/Full.pcapng (2016):
///   05 00 5a 35 00 00 84 1e 00 00 | "62.171.171.24" zero-padded to 16 @10 | port u16 @26 (3b 23 = 9019) | 14 cd
/// The 2026 client gets the same 30 bytes (the zone hook does not reshape it).
/// </summary>
public sealed class LinkOtherRewriter : IPacketRewriter
{
    public ushort Opcode => 0x180A;
    public int PayloadSize => 30;

    private const int IpOffset = 10;
    private const int IpLen = 16;
    private const int PortOffset = 26;

    public FiestaPacket Rewrite(FiestaPacket packet, ProxyConfig config)
    {
        var payload = packet.Payload.ToArray();
        var originalIp = CharLoginAckRewriter.ReadName4Ip(payload.AsSpan(IpOffset, IpLen));
        var originalPort = (ushort)(payload[PortOffset] | (payload[PortOffset + 1] << 8));
        var route = CharLoginAckRewriter.FindZoneRoute(originalIp, originalPort, config);
        if (route is null)
        {
            Log.Warn($"MAP_LINKOTHER: no Zone route matches {originalIp}:{originalPort} - leaving original endpoint");
            return packet;
        }
        var (ipv4, port) = EndpointResolver.ResolveForService(route.ServiceName, (ushort)route.ListenPort, config);
        EndpointResolver.WriteName4Ip(payload.AsSpan(IpOffset, IpLen), ipv4);
        payload[PortOffset] = (byte)(port & 0xFF);
        payload[PortOffset + 1] = (byte)(port >> 8);
        Log.Debug($"MAP_LINKOTHER rewrite: {originalIp}:{originalPort} -> {ipv4[0]}.{ipv4[1]}.{ipv4[2]}.{ipv4[3]}:{port} ({route.ServiceName})");
        return new FiestaPacket(packet.Opcode, payload);
    }
}
