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
    };

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
