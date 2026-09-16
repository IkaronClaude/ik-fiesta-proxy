using FiestaLibReloaded.Networking;

namespace FiestaProxy.Plugins;

/// <summary>
/// A plugin loaded from an assembly at startup. One instance per process.
///
/// The <see cref="IPacketRewriter"/> seam this sits beside handles the common case: a stateless,
/// single-opcode, server-to-client edit. A plugin exists for the cases that one cannot express —
/// per-session state, both directions, packets that have to be answered locally rather than relayed,
/// and one packet turning into several. The 2026-client bridge needs all four.
/// </summary>
public interface IProxyPlugin
{
    /// <summary>Short name, used in log lines and to identify the plugin in config.</summary>
    string Name { get; }

    /// <summary>
    /// Called once after loading, before any session exists. Throwing here disables the plugin and
    /// is logged; it does not take the proxy down.
    /// </summary>
    void Initialise(PluginHostContext host);

    /// <summary>
    /// Called for every accepted connection on a route the plugin might care about. Return null to
    /// take no part in this session, which costs nothing on the hot path afterwards.
    /// </summary>
    IPluginSession? BeginSession(PluginSessionInfo info);
}

/// <summary>What the host gives a plugin at startup: config it may need, and a place to log.</summary>
public sealed class PluginHostContext
{
    public PluginHostContext(Config.ProxyConfig config, string pluginDirectory, IReadOnlyDictionary<string, string> settings)
    {
        Config = config;
        PluginDirectory = pluginDirectory;
        Settings = settings;
    }

    public Config.ProxyConfig Config { get; }

    /// <summary>Directory the plugin was loaded from; the place to look for its own data files.</summary>
    public string PluginDirectory { get; }

    /// <summary>
    /// Key/value settings for this plugin, from FIESTAPROXY_PLUGIN_&lt;NAME&gt;_&lt;KEY&gt; environment
    /// variables. Keys are upper-case with the plugin prefix stripped.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; }

    public void Info(string message) => Log.Info(message);
    public void Warn(string message) => Log.Warn(message);
    public void Debug(string message) => Log.Debug(message);
}

/// <summary>Everything a plugin can know about a connection when it is offered one.</summary>
public sealed record PluginSessionInfo(
    string ServiceName,
    int ListenPort,
    string UpstreamHost,
    int UpstreamPort,
    string ClientEndpoint,
    string LocalEndpoint);

/// <summary>
/// Per-connection plugin state. Every method is called from that connection's pump, one packet at a
/// time, so an implementation needs no locking for its own fields.
/// </summary>
public interface IPluginSession : IDisposable
{
    /// <summary>A packet the client sent. Decide its fate through <paramref name="ctx"/>.</summary>
    void OnClientPacket(PluginPacketContext ctx);

    /// <summary>A packet the upstream server sent.</summary>
    void OnServerPacket(PluginPacketContext ctx);
}

/// <summary>
/// One packet, and what the plugin wants done with it.
///
/// Doing nothing forwards the packet unchanged, so a plugin only has to speak up about the frames it
/// cares about. <see cref="Drop"/> and <see cref="Replace"/> decide this packet; <see cref="ToClient"/>
/// and <see cref="ToServer"/> queue extra packets, which is how a bridge answers a handshake the
/// upstream server has no notion of.
/// </summary>
public sealed class PluginPacketContext
{
    private readonly List<FiestaPacket> _toClient = new();
    private readonly List<FiestaPacket> _toServer = new();

    public PluginPacketContext(FiestaPacket packet, bool fromClient)
    {
        Packet = packet;
        FromClient = fromClient;
        Forwarded = packet;
    }

    /// <summary>The packet as it arrived.</summary>
    public FiestaPacket Packet { get; }

    /// <summary>True when the client sent it, false when the upstream server did.</summary>
    public bool FromClient { get; }

    /// <summary>What will actually be relayed on, or null if the packet is being dropped.</summary>
    public FiestaPacket? Forwarded { get; private set; }

    public IReadOnlyList<FiestaPacket> ExtraToClient => _toClient;
    public IReadOnlyList<FiestaPacket> ExtraToServer => _toServer;

    /// <summary>Do not relay this packet. Anything queued with ToClient/ToServer is still sent.</summary>
    public void Drop() => Forwarded = null;

    /// <summary>Relay this instead of what arrived.</summary>
    public void Replace(FiestaPacket packet) => Forwarded = packet;

    /// <summary>Relay the same opcode with a new payload.</summary>
    public void Replace(ReadOnlyMemory<byte> payload) => Forwarded = new FiestaPacket(Packet.Opcode, payload);

    /// <summary>Send an extra packet to the client, after this one is dealt with.</summary>
    public void ToClient(FiestaPacket packet) => _toClient.Add(packet);

    public void ToClient(ushort opcode, ReadOnlyMemory<byte> payload) => _toClient.Add(new FiestaPacket(opcode, payload));

    /// <summary>Send an extra packet to the upstream server.</summary>
    public void ToServer(FiestaPacket packet) => _toServer.Add(packet);

    public void ToServer(ushort opcode, ReadOnlyMemory<byte> payload) => _toServer.Add(new FiestaPacket(opcode, payload));
}
