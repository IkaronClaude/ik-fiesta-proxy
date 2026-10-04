using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// Transforms a packet on its way from upstream (a Fiesta server) to the client.
/// Implementations are stateless and registered against an opcode AND an exact payload size: opcodes are reused
/// between the 2016 and 2026 client protocols, so the size is part of the key (a frame of another size is never
/// touched).
/// Return the original packet unchanged when no rewrite applies.
/// </summary>
public interface IPacketRewriter
{
    ushort Opcode { get; }
    int PayloadSize { get; }
    FiestaPacket Rewrite(FiestaPacket packet, ProxyConfig config);
}
