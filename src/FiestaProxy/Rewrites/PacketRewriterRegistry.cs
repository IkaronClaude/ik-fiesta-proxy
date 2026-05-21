using FiestaLibReloaded.Networking;
using FiestaProxy.Config;

namespace FiestaProxy.Rewrites;

/// <summary>
/// Dispatches upstream-to-client packets through any rewriter registered for
/// their opcode. Rewriters are keyed by opcode for O(1) lookup on the hot path.
/// </summary>
public sealed class PacketRewriterRegistry
{
    private readonly Dictionary<ushort, IPacketRewriter> _byOpcode;
    private readonly ProxyConfig _config;

    public PacketRewriterRegistry(ProxyConfig config, IEnumerable<IPacketRewriter> rewriters)
    {
        _config = config;
        _byOpcode = rewriters.ToDictionary(r => r.Opcode);
    }

    public FiestaPacket Apply(FiestaPacket packet)
        => _byOpcode.TryGetValue(packet.Opcode, out var r) ? r.Rewrite(packet, _config) : packet;

    public static PacketRewriterRegistry Default(ProxyConfig config) => new(config, new IPacketRewriter[]
    {
        new WorldSelectAckRewriter(), // 0x0C0C  Login -> client : world's WM endpoint
        new CharLoginAckRewriter(),   // 0x1003  WM    -> client : zone endpoint
    });
}
