using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// Dispatches upstream-to-client packets through any rewriter registered for
/// their (opcode, exact payload size). Opcodes are reused between the 2016 and 2026 client protocols, so a rewriter
/// only ever sees the one shape it was written for.
/// </summary>
public sealed class PacketRewriterRegistry
{
    private readonly Dictionary<(ushort Opcode, int Size), IPacketRewriter> _byKey;
    private readonly ProxyConfig _config;

    public PacketRewriterRegistry(ProxyConfig config, IEnumerable<IPacketRewriter> rewriters)
    {
        _config = config;
        _byKey = rewriters.ToDictionary(r => (r.Opcode, r.PayloadSize));
    }

    public FiestaPacket Apply(FiestaPacket packet)
        => _byKey.TryGetValue((packet.Opcode, packet.Payload.Length), out var r) ? r.Rewrite(packet, _config) : packet;

    public static PacketRewriterRegistry Default(ProxyConfig config) => new(config, new IPacketRewriter[]
    {
        new WorldSelectAckRewriter(0x0C0C, 83), // Login -> 2016 client: world's WM endpoint
        new WorldSelectAckRewriter(0x0C0B, 84), // Login -> 2026 client (login_bridge26's form: + world byte), same offsets
        new CharLoginAckRewriter(),             // 0x1003 18 B  WM   -> client: zone endpoint (one form for both clients)
        new LinkOtherRewriter(),                // 0x180A 30 B  zone -> client: the next zone's endpoint on a map change
    });
}
