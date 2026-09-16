using System.Text.Json;
using FiestaProxy.Plugins;

namespace Bridge2026;

/// <summary>
/// Lets an unmodified 2026 Fiesta client (US 10.6.4, or the German build) play on a 2016 server.
///
/// The two builds share a protocol but not its layouts: structs grew, the USER department was renumbered
/// between the 2026 builds themselves, and the client opens with handshakes the 2016 server has never heard
/// of. This plugin sits on a <c>bridge</c> route and translates both directions.
///
/// Settings, via FIESTAPROXY_PLUGIN_BRIDGE2026_&lt;KEY&gt;:
///   LOGIN_PORT      listen port that is the login stage          (default 9010)
///   ADVERTISE       host the client should dial for WM and zone  (default: the route's upstream host)
///   PORT_OFFSET     added to every port handed to the client, for when the proxy fronts a local stack
///   CHECKSUMS       file of the 49 ressystem checksums the 2016 zone expects, one hex line each
///   WORLD_STATUS    force every world row's status byte, for testing the client's display
///   OPCODES         JSON of the opcodes the 2016 build defines; without it nothing is filtered
///   ITEM_CLASSES    "<id> <class>" per line, from the 2026 client's ItemInfo. Without it inventory
///                   records cannot be translated and equipment stays invisible past the first slot.
///
/// Relaying an opcode the 2016 build does not define makes the server close the connection, so OPCODES is
/// worth supplying: it is the difference between a clean drop and an unexplained disconnect.
/// </summary>
public sealed class Bridge2026Plugin : IProxyPlugin
{
    private PluginHostContext? _host;
    private HashSet<ushort>? _known2016;
    private string _advertise = "";
    private int _portOffset;
    private Dictionary<int, int>? _itemClass;

    public string Name => "bridge2026";

    public int LoginPort { get; private set; } = 9010;
    public string AdvertiseHost => _advertise;
    public IReadOnlyList<byte[]> Checksums { get; private set; } = Array.Empty<byte[]>();

    /// <summary>-1 to relay the 2016 status untouched. 0-5 make the client refuse the world outright.</summary>
    public int WorldStatusOverride { get; private set; } = -1;

    public void Initialise(PluginHostContext host)
    {
        _host = host;
        var s = host.Settings;

        if (s.TryGetValue("LOGIN_PORT", out var lp) && int.TryParse(lp, out var lpv)) LoginPort = lpv;
        if (s.TryGetValue("PORT_OFFSET", out var po) && int.TryParse(po, out var pov)) _portOffset = pov;
        if (s.TryGetValue("ADVERTISE", out var adv)) _advertise = adv;
        if (s.TryGetValue("WORLD_STATUS", out var ws) && int.TryParse(ws, out var wsv)) WorldStatusOverride = wsv;

        if (s.TryGetValue("CHECKSUMS", out var cs) && File.Exists(cs))
        {
            // Each checksum goes on the wire as the 32 ASCII characters of an MD5 hex digest, NOT as the
            // 16 bytes they encode: MAP_LOGIN_REQ is 22 + 49 * 32 = 1590 bytes. Decoding them here would
            // halve the packet and the zone would refuse it.
            var sums = new List<byte[]>();
            foreach (var line in File.ReadAllLines(cs))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (t.Length != 32)
                {
                    host.Warn($"{Name}: skipping a {t.Length}-character checksum in {cs}; each must be 32 hex characters");
                    continue;
                }
                sums.Add(System.Text.Encoding.ASCII.GetBytes(t));
            }
            Checksums = sums;
            host.Info($"{Name}: {sums.Count} zone checksums from {cs}"
                      + (sums.Count == 49 ? "" : " -- the 2016 zone expects exactly 49"));
        }

        if (s.TryGetValue("ITEM_CLASSES", out var ic) && File.Exists(ic))
        {
            var map = new Dictionary<int, int>();
            foreach (var line in File.ReadAllLines(ic))
            {
                var t = line.AsSpan().Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                var sp = t.IndexOf(' ');
                if (sp > 0 && int.TryParse(t[..sp], out var id) && int.TryParse(t[(sp + 1)..], out var cls))
                    map[id] = cls;
            }
            _itemClass = map;
            host.Info($"{Name}: {map.Count} item classes from {ic}");
        }
        else
        {
            host.Warn($"{Name}: no ITEM_CLASSES file, so inventory records are relayed untranslated and "
                      + "equipment past the first slot will not appear");
        }

        if (s.TryGetValue("OPCODES", out var op) && File.Exists(op))
        {
            try
            {
                // The same all-enums.json the protocol tooling uses: { dept: { id, opcodes: { name: cmd } } }
                using var doc = JsonDocument.Parse(File.ReadAllText(op));
                var set = new HashSet<ushort>();
                foreach (var dept in doc.RootElement.EnumerateObject())
                {
                    var id = dept.Value.GetProperty("id").GetInt32();
                    foreach (var o in dept.Value.GetProperty("opcodes").EnumerateObject())
                        set.Add((ushort)((id << 10) | o.Value.GetInt32()));
                }
                _known2016 = set;
                host.Info($"{Name}: {set.Count} opcodes known to the 2016 build; anything else is dropped, not relayed");
            }
            catch (Exception ex)
            {
                host.Warn($"{Name}: could not read OPCODES from {op}: {ex.Message}; nothing will be filtered");
            }
        }

        host.Info($"{Name}: login port {LoginPort}, advertise '{(_advertise.Length == 0 ? "<route upstream>" : _advertise)}'"
                  + (_portOffset != 0 ? $", port offset {_portOffset}" : ""));
    }

    public IPluginSession? BeginSession(PluginSessionInfo info)
    {
        // Default the advertised host to whatever the route points at, so the common single-box case needs
        // no setting at all.
        if (_advertise.Length == 0) _advertise = info.UpstreamHost;
        return new Bridge2026Session(this, info);
    }

    /// <summary>Where the client should dial for a service the server advertised on <paramref name="port"/>.</summary>
    public (string Host, ushort Port) EndpointFor(ushort port)
        => (_advertise, (ushort)(port + _portOffset));

    /// <summary>The item's attribute class, or -1 when it is unknown or no table was supplied.</summary>
    public int ClassOf(int itemId)
        => _itemClass is not null && _itemClass.TryGetValue(itemId, out var c) ? c : -1;

    public bool HasItemClasses => _itemClass is not null;

    /// <summary>False only when an opcode list was supplied and this opcode is not in it.</summary>
    public bool IsKnownTo2016(ushort opcode) => _known2016 is null || _known2016.Contains(opcode);

    internal void Log(string message) => _host?.Info(message);
    internal void Warn(string message) => _host?.Warn(message);
}
