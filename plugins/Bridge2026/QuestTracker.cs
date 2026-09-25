using System.Text.Json;

namespace Bridge2026;

/// <summary>
/// The 2026 client's QUEST TRACKER (patch 10.5.0, 06/10/2026: "Active quests can now be marked"), kept by the bridge
/// because the 2016 zone has no such thing. Read off the official wire (live-20260919-201932, 22 requests):
///   C-&gt;S 0x441F {u16 quest}                 track this quest - sent on every quest accept and by the "start
///                                             tracking" button (2016 numbers it NC_QUEST_JOBDUNGEON_FIND_RNG, a zone
///                                             RING packet, so the zone must never see it)
///   S-&gt;C 0x4420 {u16 result, u16 quest}     0x30B0 tracked; 0x30B5 answered to repeats of a tracked quest; 0x30B4
///                                             refusals (quests finished before the request landed)
///   S-&gt;C 0x110F 5 x {u16 quest}             the tracked set at every zone login, 0xFFFF = empty slot (the limit)
/// Official drops a quest from the set once it is no longer in progress; so does this store, by the login quest list.
/// State is one small JSON file: character number -&gt; quest ids.
/// </summary>
internal sealed class QuestTracker
{
    public const int Slots = 5;
    public const ushort Tracked = 0x30B0, Refused = 0x30B4, AlreadyTracked = 0x30B5;

    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<uint, List<ushort>> _byChar = new();

    public QuestTracker(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<ushort>>>(File.ReadAllText(path));
            if (raw is not null)
                _byChar = raw.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value);
        }
        catch (Exception) { /* a broken file starts empty; it is rewritten on the next change */ }
    }

    public int CharacterCount { get { lock (_lock) return _byChar.Count; } }

    /// <summary>The tracked quests of <paramref name="chr"/> that are still in progress (<paramref name="active"/>),
    /// or all of them when the active set is not known yet.</summary>
    public List<ushort> Get(uint chr, IReadOnlySet<ushort>? active)
    {
        lock (_lock)
        {
            var list = _byChar.TryGetValue(chr, out var l) ? l : new List<ushort>();
            return active is null ? new List<ushort>(list) : list.Where(active.Contains).ToList();
        }
    }

    /// <summary>Track <paramref name="quest"/>; quests no longer in progress (not in <paramref name="active"/>) make
    /// room first. Returns the 0x4420 result.</summary>
    public ushort Add(uint chr, ushort quest, IReadOnlySet<ushort>? active)
    {
        lock (_lock)
        {
            if (!_byChar.TryGetValue(chr, out var list)) _byChar[chr] = list = new List<ushort>();
            if (list.Contains(quest)) return AlreadyTracked;
            if (active is not null) list.RemoveAll(q => !active.Contains(q));
            if (list.Count >= Slots) return Refused;
            list.Add(quest);
            Save();
            return Tracked;
        }
    }

    /// <summary>The 0x110F payload: up to five quest ids, the rest 0xFFFF.</summary>
    public static byte[] ListPayload(IReadOnlyList<ushort> quests)
    {
        var p = new byte[Slots * 2];
        for (var i = 0; i < Slots; i++)
        {
            var q = i < quests.Count ? quests[i] : (ushort)0xFFFF;
            p[2 * i] = (byte)q;
            p[2 * i + 1] = (byte)(q >> 8);
        }
        return p;
    }

    private void Save()
    {
        if (_path is null) return;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_byChar.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
        File.Move(tmp, _path, overwrite: true);
    }
}
