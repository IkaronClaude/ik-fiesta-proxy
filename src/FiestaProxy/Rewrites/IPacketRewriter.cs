using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// Transforms a packet on its way from upstream (a Fiesta server) to the client.
/// Implementations are stateless and registered against a single opcode.
/// Return the original packet unchanged when no rewrite applies.
/// </summary>
public interface IPacketRewriter
{
    ushort Opcode { get; }
    FiestaPacket Rewrite(FiestaPacket packet, ProxyConfig config);
}
