using Bridge2026;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The 2026 translation is moving out of this proxy into the zone itself (ik-fiesta-patch-recipes
/// zone/plugins/bridge26, operator 2026-10-04), a few packets per batch. The zone plugin with verify=1 logs every
/// translation it makes - "OPCODE 2016hex 2026hex" per line in bridge26-verify.log - and this replays each pair
/// through the proxy's own translator, which stays here as the reference: the two must agree byte for byte.
///
///     BRIDGE26_VERIFY_LOG=&lt;zone dir&gt;/bridge26-verify.log dotnet test --filter ZoneHookParity
///
/// Without the variable the log check is skipped; the fixed vectors below always run.
/// </summary>
public class ZoneHookParityTests
{
    /// <summary>The proxy translator for each opcode the zone plugin owns (null = this payload is not that shape).</summary>
    private static readonly Dictionary<ushort, Func<byte[], byte[]?>> Reference = new()
    {
        [Op.SwingDamage] = T.Swing2016To2026,
        [Op.SomeoneSwing] = p => T.Tail7_2016To2026(p, 13),
        [Op.DotDamage] = p => T.Tail7_2016To2026(p, 13),
        [Op.SkillHitDamage] = T.SkillHit2016To2026,
        [Op.TargetInfo] = T.TargetInfo2016To2026,
        [Op.HitObjStart] = p => T.HitStart2016To2026(p, 6),
        [Op.HitFldStart] = p => T.HitStart2016To2026(p, 12),
        [Op.SomeoneHitObjStart] = p => T.HitStart2016To2026(p, 8),
        [Op.SomeoneHitFldStart] = p => T.HitStart2016To2026(p, 14),
        // batch 3: the zone strips the TRACKED bits first (QuestTracker.TakeTracked), then converts the list
        [Op.QuestDoing] = p => { var c = (byte[])p.Clone(); QuestTracker.TakeTracked(c, new()); return T.QuestDoing2016To2026(c, CounterRows); },
        [Op.QuestRepeat] = p => T.QuestRepeat2016To2026(p, CounterRows),
        // batch 4
        [Op.ChargedBuff] = T.ChargedBuff2016To2026,
        [Op.ChargedBuffStart] = T.ChargedBuffStart2016To2026,
        [Op.ChargedBuffTerminate] = T.ChargedBuffTerminate2016To2026,
        [0x3c03] = T.ShopTable2016To2026, [0x3c04] = T.ShopTable2016To2026, [0x3c06] = T.ShopTable2016To2026,
        [0x3c09] = T.ShopTable2016To2026, [0x3c0a] = T.ShopTable2016To2026, [0x3c0b] = T.ShopTable2016To2026,
        [Op.ClientBase] = p => T.ClientBase2016To2026(p, T.ClientBaseUs),
        // batch 5 (the US width; the zone logs the record BEFORE it fills the states >= 792 in)
        [Op.RegenMob] = p => p.Length == 149 ? T.RegenMobRow2016To2026(p, 1) : null,
        [Op.MobCmd] = p => T.MobCmd2016To2026(p, 1),
        [Op.RegenMover] = p => T.RegenMover2016To2026(p, 1),
        [Op.LoginCharacter] = p => T.LoginCharacter2016To2026(p, 1),
        [Op.CharacterList] = p => T.CharacterList2016To2026(p, 1),
        // batch 6: items, with the item classes the zone reads from the 2026 ItemInfo (here: BRIDGE26_ITEM_CLASSES, "CLASS id class"
        // lines); null = the zone leaves the packet as it is
        [Op.ItemCellChange] = p => Same(p, ItemAttr.TrailingItem2016To2026(p, 4, ClassOf)),
        [Op.ItemEquipChange] = p => Same(p, ItemAttr.TrailingItem2016To2026(p, 3, ClassOf)),
        [Op.ClientItem] = p => ItemAttr.ClientItem2016To2026(p, ClassOf, out _) ?? T.ClientItem2016To2026(p),
        [Op.SellItemList] = p => ItemAttr.RecordList2016To2026(p, 0, 3, ClassOf, out _),
        [Op.GuildStorageOpen] = p => ItemAttr.RecordList2016To2026(p, 18, 3, ClassOf, out _),
        [Op.BoothSearchItemList] = p => ItemAttr.RecordList2016To2026(p, 2, 15, ClassOf, out _),
        [Op.AcademyRewardStorageOpen] = p => ItemAttr.RecordList2016To2026(p, 10, 3, ClassOf, out _),
        [Op.RewardInvenAck] = p => ItemAttr.RecordList2016To2026(p, 0, ClassOf, out _),
        [Op.MenuOpenStorage] = p => ItemAttr.RecordList2016To2026(p, 11, ClassOf, out _),
        [Op.SellItemInsert] = p => Unchanged(p, ItemAttr.LeadingItem2016To2026(p, 2, ClassOf)),
        [Op.TradeOppositUpboard] = p => Unchanged(p, ItemAttr.LeadingItem2016To2026(p, 1, ClassOf)),
        [Op.CollectCardOpen] = p => Unchanged(p, ItemAttr.LeadingItem2016To2026(p, 3, ClassOf)),
    };

    private static readonly Dictionary<int, int> Classes = LoadClasses();
    private static int ClassOf(int id) => Classes.TryGetValue(id, out var c) ? c : -1;
    private static Dictionary<int, int> LoadClasses()
    {
        var map = new Dictionary<int, int>();
        var path = Environment.GetEnvironmentVariable("BRIDGE26_ITEM_CLASSES");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path))
        {
            var f = line.Split(' ');
            if (f.Length == 3 && f[0] == "CLASS" && int.TryParse(f[1], out var id) && int.TryParse(f[2], out var c)) map[id] = c;
        }
        return map;
    }
    /// <summary>The zone sends a single-item packet as it was when the 2026 form has the same length (TrailingItem) or bytes (LeadingItem).</summary>
    private static byte[]? Same(byte[] p, byte[]? t) => t is null || t.Length == p.Length ? null : t;
    private static byte[]? Unchanged(byte[] p, byte[]? t) => t is null || t.AsSpan().SequenceEqual(p) ? null : t;

    /// <summary>quest-counter-rows.txt (tools/bridge_data.py) - the same file the zone loads: BRIDGE26_COUNTER_ROWS</summary>
    private static readonly Dictionary<int, int[]> CounterRowsMap = LoadCounterRows();
    private static int[]? CounterRows(int quest) => CounterRowsMap.TryGetValue(quest, out var r) ? r : null;
    private static Dictionary<int, int[]> LoadCounterRows()
    {
        var map = new Dictionary<int, int[]>();
        var path = Environment.GetEnvironmentVariable("BRIDGE26_COUNTER_ROWS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return map;
        foreach (var line in File.ReadAllLines(path))
        {
            var f = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length is 6 or 8 && f.All(x => int.TryParse(x, out _))) map[int.Parse(f[0])] = f.Skip(1).Select(int.Parse).ToArray();
        }
        return map;
    }

    [Fact]
    public void Zone_plugin_translations_match_the_proxy()
    {
        var path = Environment.GetEnvironmentVariable("BRIDGE26_VERIFY_LOG");
        if (string.IsNullOrEmpty(path)) return;                       // nothing captured: skip
        File.Exists(path).ShouldBeTrue($"BRIDGE26_VERIFY_LOG={path} does not exist");

        var perOp = new Dictionary<ushort, int>();
        var bad = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var f = line.Trim().Split(' ');
            if (f.Length != 3) continue;
            var op = Convert.ToUInt16(f[0], 16);
            var p16 = Convert.FromHexString(f[1]);
            var zone = f[2] == "-" ? null : Convert.FromHexString(f[2]);
            Reference.ShouldContainKey(op, $"the zone owns 0x{op:X4} but this test has no reference translator for it");
            var proxy = Reference[op](p16);
            perOp[op] = perOp.GetValueOrDefault(op) + 1;
            if (zone is null && proxy is null) continue;              // both refused the shape
            if (zone is null || proxy is null || !zone.AsSpan().SequenceEqual(proxy))
                bad.Add($"0x{op:X4} {f[1]}: zone {f[2]} proxy {(proxy is null ? "-" : Convert.ToHexString(proxy))}");
        }
        perOp.ShouldNotBeEmpty("the log has no pairs");
        bad.ShouldBeEmpty(string.Join("\n", bad.Take(10)));
        Console.WriteLine("zone == proxy: " + string.Join(", ", perOp.Select(kv => $"0x{kv.Key:X4} x{kv.Value}")));
    }
}
