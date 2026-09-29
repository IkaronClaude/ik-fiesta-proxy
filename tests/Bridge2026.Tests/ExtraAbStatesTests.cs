using System;
using System.Linq;
using Shouldly;
using Xunit;
using Bridge2026;

namespace Bridge2026.Tests;

/// <summary>
/// States past the 2016 bitset (792+) reach other players' 2026 clients as bits in the brief-info record: the bridge
/// tracks them per handle from the index-carrying frames and sets them in the translated LOGINCHARACTER record.
/// </summary>
public class ExtraAbStatesTests
{
    private static byte[] Set(ushort handle, uint index) =>
        new[] { (byte)handle, (byte)(handle >> 8) }.Concat(BitConverter.GetBytes(index)).ToArray();

    private static byte[] Record(ushort handle)
    {
        var r = new byte[T.LoginCharacter2026Length(1)];
        r[0] = (byte)handle; r[1] = (byte)(handle >> 8);
        return r;
    }

    [Fact]
    public void A_state_past_792_set_by_index_lands_in_the_record_bits()
    {
        var x = new ExtraAbStates();
        x.Observe(Op.AbStateSet, Set(0x1234, 898));                 // Shield Increase T7 (idx 898)
        var r = Record(0x1234);
        x.Fill(r, 0, T.LoginCharacterExtraBitsAt).ShouldBe(1);
        var bit = 898 - 792;                                         // 106 -> byte 13, bit 2
        r[T.LoginCharacterExtraBitsAt + bit / 8].ShouldBe((byte)(1 << (bit % 8)));
        r.Where((b, i) => i != T.LoginCharacterExtraBitsAt + bit / 8 && i > 1).ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void Reset_and_list_are_followed_delete_keeps_and_2016_indexes_ignored()
    {
        var x = new ExtraAbStates();
        x.Observe(Op.AbStateSet, Set(7, 900));
        x.Observe(Op.AbStateSet, Set(7, 500));                       // a 2016 state: the zone's own bitset has it
        x.Observe(Op.AbStateReset, Set(7, 900));
        x.Count(7).ShouldBe(0);
        // 0x1C19: {handle, n, n x {index, time, strength}}
        var list = new byte[] { 7, 0, 2 }.Concat(BitConverter.GetBytes(950u)).Concat(new byte[8])
                                         .Concat(BitConverter.GetBytes(10u)).Concat(new byte[8]).ToArray();
        x.Observe(Op.AbStateSet, Set(7, 1000));                      // tracked, then the list says otherwise
        x.Observe(Op.BriefAbStateList, list);
        x.Count(7).ShouldBe(1);                                      // the list REPLACES: only 950 (10 is a 2016 index)
        x.Observe(Op.BriefInfoDelete, new byte[] { 7, 0 });
        x.Count(7).ShouldBe(1);                                      // out of view keeps it for the next appearance
    }

    [Fact]
    public void Indexes_past_the_2026_bitset_are_not_written()
    {
        var x = new ExtraAbStates();
        x.Observe(Op.AbStateSet, Set(3, 1093));                      // Gold Dragon's Grace - past bit 1079
        x.Count(3).ShouldBe(0);
    }
}
