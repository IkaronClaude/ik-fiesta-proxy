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
    private Dictionary<(int Quest, int Index), int>? _rewardSlot;
    private Dictionary<int, int[]> _counterRows = new();    // quest -> the 2026 client row of each zone counter slot
    private Dictionary<int, int[]> _foldedInto = new();     // 2016 equip slot -> the 2026 slots the server folds into it
    private Dictionary<int, int> _equip26 = new();          // item id -> its 2026 Equip, only where that is a folded slot

    public string Name => "bridge2026";

    public int LoginPort { get; private set; } = 9010;
    public string AdvertiseHost => _advertise;
    public IReadOnlyList<byte[]> Checksums { get; private set; } = Array.Empty<byte[]>();

    /// <summary>-1 to relay the 2016 status untouched. 0-5 make the client refuse the world outright.</summary>
    public int WorldStatusOverride { get; private set; } = -1;

    // The three files tools/bridge_data.py writes (zone checksums, item classes, quest reward choices). They are
    // re-read whenever that script rewrites them - a data deploy regenerates them (Fiesta2026on2016's volume sync
    // does it on every sync), so the bridge can never keep old checksums after the zones' tables changed (the
    // "Client has been illegally manipulated" drift, 2026-09-23). A file caught mid-write keeps the old values.
    private IReadOnlyDictionary<string, string> _settings = new Dictionary<string, string>();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadTimer;

    private void LoadGenerated()
    {
        var host = _host!;
        var s = _settings;
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
                if (sums.Count == 49 || Checksums.Count == 0) Checksums = sums;   // never swap in a half-written file
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

            if (s.TryGetValue("QUEST_REWARD_INDEX", out var qr) && File.Exists(qr))
            {
                var map = new Dictionary<(int, int), int>();
                foreach (var line in File.ReadAllLines(qr))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    var f = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 3 && int.TryParse(f[0], out var q) && int.TryParse(f[1], out var i)
                        && int.TryParse(f[2], out var slot))
                        map[(q, i)] = slot;
                }
                _rewardSlot = map;
                host.Info($"{Name}: {map.Count} quest reward choices from {qr}");
            }
            else
            {
                host.Warn($"{Name}: no QUEST_REWARD_INDEX file - a chosen quest reward reaches the zone as the client's index "
                          + "and the player gets a different item");
            }

            if (s.TryGetValue("QUEST_COUNTER_ROWS", out var qc) && File.Exists(qc))
            {
                var map = new Dictionary<int, int[]>();
                foreach (var line in File.ReadAllLines(qc))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    var f = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length == 6 && int.TryParse(f[0], out var q)
                        && f.Skip(1).All(x => int.TryParse(x, out _)))
                        map[q] = f.Skip(1).Select(int.Parse).ToArray();
                }
                _counterRows = map;
                host.Info($"{Name}: {map.Count} quests with moved counter rows from {qc}");
            }
            else
            {
                host.Warn($"{Name}: no QUEST_COUNTER_ROWS file - a quest with more than 5 end rows shows its kill counts "
                          + "one row too high after a relog");
            }

            if (s.TryGetValue("EQUIP_FOLD", out var ef) && File.Exists(ef))
            {
                var fold = new Dictionary<int, List<int>>();
                var equip = new Dictionary<int, int>();
                foreach (var line in File.ReadAllLines(ef))
                {
                    var f = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length != 3 || !int.TryParse(f[1], out var a) || !int.TryParse(f[2], out var b)) continue;
                    if (f[0] == "fold") (fold.TryGetValue(b, out var l) ? l : fold[b] = new List<int>()).Add(a);
                    else if (f[0] == "item") equip[a] = b;
                }
                _foldedInto = fold.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
                _equip26 = equip;
                host.Info($"{Name}: {fold.Values.Sum(l => l.Count)} folded equip slots, {equip.Count} items drawn at them, from {ef}");
            }
            else
            {
                host.Warn($"{Name}: no EQUIP_FOLD file - unequipping an item the 2026 client draws at a 2026-only slot "
                          + "(wings, cosmetic backs) leaves it drawn");
            }
    }

    private void WatchGenerated()
    {
        var dirs = new[] { "CHECKSUMS", "ITEM_CLASSES", "QUEST_REWARD_INDEX", "EQUIP_FOLD", "QUEST_COUNTER_ROWS" }
            .Select(k => _settings.TryGetValue(k, out var f) ? Path.GetDirectoryName(Path.GetFullPath(f)) : null)
            .Where(d => d != null && Directory.Exists(d)).Distinct().ToList();
        if (dirs.Count != 1) return;                   // bridge_data writes all three into one folder
        _reloadTimer = new System.Threading.Timer(_ =>
        {
            try { LoadGenerated(); }
            catch (Exception ex) { _host!.Warn($"{Name}: reloading the generated files failed ({ex.Message}); keeping the old values"); }
        });
        _watcher = new FileSystemWatcher(dirs[0]) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler kick = (_, _) => _reloadTimer.Change(1000, System.Threading.Timeout.Infinite);   // debounce the burst
        _watcher.Changed += kick;
        _watcher.Created += kick;
        _watcher.Renamed += (o, e) => kick(o, e);
        _watcher.EnableRaisingEvents = true;
        _host!.Info($"{Name}: watching {dirs[0]} - the generated files reload when bridge_data.py rewrites them");
    }

    public void Initialise(PluginHostContext host)
    {
        _host = host;
        var s = host.Settings;

        if (s.TryGetValue("LOGIN_PORT", out var lp) && int.TryParse(lp, out var lpv)) LoginPort = lpv;
        if (s.TryGetValue("PORT_OFFSET", out var po) && int.TryParse(po, out var pov)) _portOffset = pov;
        if (s.TryGetValue("ADVERTISE", out var adv)) _advertise = adv;
        if (s.TryGetValue("WORLD_STATUS", out var ws) && int.TryParse(ws, out var wsv)) WorldStatusOverride = wsv;
        if (s.TryGetValue("QUEST_TRACKER", out var qt) && !string.IsNullOrWhiteSpace(qt)) Tracker = new QuestTracker(qt);

        _settings = s;
        LoadGenerated();
        WatchGenerated();

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

    /// <summary>
    /// The opcode shift last measured on a login connection: +2 for the US build, 0 for the German one.
    ///
    /// Only the login connection carries the version key the shift is read from. The world manager and zone
    /// are separate connections that never see it, so they inherit this. Getting that wrong is not cosmetic:
    /// those two links carry everything whose width depends on the build, so a zone session that thinks it is
    /// talking to a German client sends 124-byte character base, 187-byte mob records and an untranslated
    /// reward inventory, which is the zone-enter crash.
    ///
    /// One value per process, not per account. Two clients of different builds at once would get this wrong,
    /// which is a real limit but not one worth a session-correlation scheme here.
    /// </summary>
    public int LastShift { get; set; }

    /// <summary>
    /// Upstream services seen sending NC_QUEST_SCRIPT_CMD_REQ with command QSC_END. A stock 2016 zone never
    /// does (the case is dead code); one carrying the quest-script-end-notify recipe does, and its END closes
    /// the 2026 dialog on the last page by the client's own handler. For such a zone the per-ack 0x442E is
    /// not just unnecessary, it is the close-and-reopen flicker between pages. Learned, not configured, so a
    /// stack with some zones patched and some not is right on both.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> ZoneAnnouncesQuestEnd { get; } = new();

    /// <summary>
    /// The 2016 QUEST_DATA.Reward slot for the 2026 client's reward index (its QuestReward row order), or -1.
    /// </summary>
    public int RewardSlot(int quest, int index)
        => _rewardSlot is not null && _rewardSlot.TryGetValue((quest, index), out var s) ? s : -1;

    /// <summary>The 2026 client QuestEndNpc row of each zone counter slot, or null when they are the same.</summary>
    public int[]? CounterRows(int quest) => _counterRows.TryGetValue(quest, out var r) ? r : null;

    /// <summary>The 2026 quest tracker's store (QUEST_TRACKER = a JSON file; unset = kept in memory only).</summary>
    internal QuestTracker Tracker { get; private set; } = new QuestTracker(null);

    /// <summary>The item's attribute class, or -1 when it is unknown or no table was supplied.</summary>
    public int ClassOf(int itemId)
        => _itemClass is not null && _itemClass.TryGetValue(itemId, out var c) ? c : -1;

    public bool HasItemClasses => _itemClass is not null;

    /// <summary>The 2026 equip slots the server folds into this 2016 slot (none for most slots).</summary>
    public IReadOnlyList<int> FoldedInto(int slot2016)
        => _foldedInto.TryGetValue(slot2016, out var a) ? a : Array.Empty<int>();

    /// <summary>The 2026 slot the client draws this item at, when that is a folded slot; else -1.</summary>
    public int FoldedEquipOf(int itemId) => _equip26.TryGetValue(itemId, out var e) ? e : -1;

    /// <summary>False only when an opcode list was supplied and this opcode is not in it.</summary>
    public bool IsKnownTo2016(ushort opcode) => _known2016 is null || _known2016.Contains(opcode);

    public void Log(string message) => _host?.Info(message);
    public void Warn(string message) => _host?.Warn(message);
}
