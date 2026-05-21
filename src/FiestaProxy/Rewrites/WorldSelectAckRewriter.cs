using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// PROTO_NC_USER_WORLDSELECT_ACK (0x0C0C). Single-world ack. Payload (from PDB):
///   offset 0  : byte  worldstatus
///   offset 1  : 16    Name4 ip       (dotted-quad ASCII, zero-padded)
///   offset 17 : ushort port          (LE)
///   offset 19 : 64    ushort[32] validate_new
///
/// Rewrites ip/port to the operator-facing endpoint of the WorldManager that
/// the server picked. We assume one world per ProxyConfig route; the upstream
/// server identifies itself via the listen port the proxy received the
/// connection on, which is mapped back to a ServiceName.
/// </summary>
public sealed class WorldSelectAckRewriter : IPacketRewriter
{
    public ushort Opcode => 0x0C0C;

    private const int IpOffset = 1;
    private const int IpLen = 16;
    private const int PortOffset = 17;

    public FiestaPacket Rewrite(FiestaPacket packet, ProxyConfig config)
    {
        if (packet.Payload.Length < PortOffset + 2)
            return packet;

        var payload = packet.Payload.ToArray();
        var originalPort = (ushort)(payload[PortOffset] | (payload[PortOffset + 1] << 8));

        // The world server's identity isn't in this packet — the only signal we have
        // is that the Login server emitted it, so we redirect to the WorldManager
        // route on this proxy that owns the same worldno. With single-world
        // deployments that's unambiguous; multi-world support needs a per-port
        // listen lookup or a worldno→service map. Until then: pick the first
        // WorldManager route.
        var wmRoute = config.Routes.FirstOrDefault(r => r.ServiceName.StartsWith("WorldManager_", StringComparison.Ordinal));
        if (wmRoute is null) return packet;

        var (ipv4, port) = EndpointResolver.ResolveForService(wmRoute.ServiceName, (ushort)wmRoute.ListenPort, config);

        EndpointResolver.WriteName4Ip(payload.AsSpan(IpOffset, IpLen), ipv4);
        payload[PortOffset] = (byte)(port & 0xFF);
        payload[PortOffset + 1] = (byte)(port >> 8);

        Log.Debug($"WORLDSELECT_ACK rewrite: port {originalPort} -> {ipv4[0]}.{ipv4[1]}.{ipv4[2]}.{ipv4[3]}:{port}");
        return new FiestaPacket(packet.Opcode, payload);
    }
}
