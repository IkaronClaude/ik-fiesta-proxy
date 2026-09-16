using Bridge2026;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// These assert MEASURED layouts, not intended ones. Each number here came off a capture, the 2016 PDB, or
/// the US client's disassembly, and several of them were wrong once and cost a real debugging session. The
/// point of the tests is that a future edit which quietly changes an offset fails here rather than as a
/// client crash with no obvious cause.
/// </summary>
public class TranslatorTests
{
    // ---------------------------------------------------------------- world list

    [Fact]
    public void WorldList_keeps_the_2016_entry_and_only_adds_a_header()
    {
        // {count, {worldno, name[16], status}}: one world, number 0, "INITIO", status 6 (Low)
        var ack16 = new byte[1 + 18];
        ack16[0] = 1;
        ack16[1] = 0;
        "INITIO"u8.CopyTo(ack16.AsSpan(2));
        ack16[18] = 6;

        var wl = T.WorldList2016To2026(ack16)!;

        wl.Length.ShouldBe(5 + 18);                      // 5-byte head, entry unchanged
        wl[0].ShouldBe((byte)1);
        wl.AsSpan(1, 4).ToArray().ShouldBe(T.WorldListHead);
        wl[5].ShouldBe((byte)0);                         // worldno stays FIRST in the entry
        wl.AsSpan(6, 6).ToArray().ShouldBe("INITIO"u8.ToArray());
        wl[22].ShouldBe((byte)6);                        // status stays LAST
    }

    [Fact]
    public void WorldList_divides_exactly_like_both_captures()
    {
        // US: 95 = 5 + 5*18. German: 293 = 5 + 16*18.
        T.WorldList2016To2026(Payload(1 + 18 * 5, n: 5))!.Length.ShouldBe(95);
        T.WorldList2016To2026(Payload(1 + 18 * 16, n: 16))!.Length.ShouldBe(293);
    }

    // ---------------------------------------------------------------- world select ack

    [Fact]
    public void WorldSelectAck_puts_the_port_at_offset_17()
    {
        // The one layout confirmed in the US client's own code: it reads word ptr [ebx+0x11].
        var p = new byte[83];
        p[0] = 6;
        p[17] = 0x96; p[18] = 0x23;                      // 0x2396 = 9110, the port the capture then dialled
        var ws = T.WorldSelectAck2016To2026(p, "10.0.0.5", world: 7)!;

        ws.Length.ShouldBe(84);
        ws[0].ShouldBe((byte)6);
        ws.AsSpan(1, 8).ToArray().ShouldBe("10.0.0.5"u8.ToArray());
        ((ushort)(ws[17] | (ws[18] << 8))).ShouldBe((ushort)9110);
        ws[83].ShouldBe((byte)7);                        // the trailing byte is the chosen world
    }

    // ---------------------------------------------------------------- avatars

    [Fact]
    public void Avatar_record_keeps_every_field_but_the_equipment_block()
    {
        var a = new byte[T.Avatar2016];
        BitConverter.GetBytes(0x1234u).CopyTo(a, 0);     // chrregnum
        "Aeyana"u8.CopyTo(a.AsSpan(4));                  // name
        a[24] = 100;                                     // level
        a[26] = 3;                                       // slot
        "Rou"u8.CopyTo(a.AsSpan(27));                    // loginmap
        new byte[] { 0x41, 0x4a }.CopyTo(a, 48);         // first worn slot
        new byte[] { 0x00, 0x00, 0xf0 }.CopyTo(a, 88);   // the 3-byte upgrade field
        "Eld"u8.CopyTo(a.AsSpan(95));                    // sKQMapName
        a[119] = 1;                                      // CharIDChangeData.bNeedChangeID

        var r = T.AvatarRecord2016To2026(a);

        r.Length.ShouldBe(T.Avatar2026);
        BitConverter.ToUInt32(r, 0).ShouldBe(0x1234u);
        r.AsSpan(4, 6).ToArray().ShouldBe("Aeyana"u8.ToArray());
        r[24].ShouldBe((byte)100);
        r[26].ShouldBe((byte)3);
        r.AsSpan(27, 3).ToArray().ShouldBe("Rou"u8.ToArray());
        r.AsSpan(48, 2).ToArray().ShouldBe(new byte[] { 0x41, 0x4a });
        r.AsSpan(118, 4).ToArray().ShouldBe(new byte[] { 0xff, 0x00, 0x00, 0xf0 });  // upgrade gains a byte
        r.AsSpan(126, 3).ToArray().ShouldBe("Eld"u8.ToArray());                      // moved 95 -> 126
        r[150].ShouldBe((byte)1);                                                    // rename flag, 119 -> 150
    }

    [Fact]
    public void Unworn_equipment_slots_are_ffff_not_zero()
    {
        // 0x0000 is item id 0; an empty slot is 0xFFFF.
        var r = T.AvatarRecord2016To2026(new byte[T.Avatar2016]);
        r.AsSpan(88, 30).ToArray().ShouldAllBe(b => b == 0xFF);
    }

    [Fact]
    public void Create_success_carries_the_same_widened_record()
    {
        // The US capture's ack is 162 B = 1 + 161.
        var p = new byte[1 + T.Avatar2016];
        p[0] = 2;
        var cs = T.CreateSucc2016To2026(p)!;
        cs.Length.ShouldBe(162);
        cs[0].ShouldBe((byte)2);
    }

    [Fact]
    public void Avatar_list_head_is_22_bytes_and_records_are_161()
    {
        // The US capture's frames are 22 (no avatars) and 183 = 22 + 161.
        T.Avatars2016To2026(new byte[3], "10.0.0.5")!.Length.ShouldBe(22);
        T.Avatars2016To2026(WithAvatars(1), "10.0.0.5")!.Length.ShouldBe(183);
        T.Avatars2016To2026(WithAvatars(5), "10.0.0.5")!.Length.ShouldBe(22 + 5 * 161);
    }

    [Fact]
    public void Create_request_loses_the_2026_appearance_byte()
    {
        T.CreateReq2026To2016(new byte[26])!.Length.ShouldBe(25);
        T.CreateReq2026To2016(new byte[25]).ShouldBeNull();   // already 2016-shaped: leave it alone
    }

    // ---------------------------------------------------------------- build-dependent widths

    [Theory]
    [InlineData(T.ClientBaseDe, 124)]
    [InlineData(T.ClientBaseUs, 362)]
    public void Client_base_takes_the_length_of_the_build(int total, int expected)
        => T.ClientBase2016To2026(new byte[105], total)!.Length.ShouldBe(expected);

    [Fact]
    public void Client_base_keeps_the_map_name_on_offset_67()
    {
        var p = new byte[105];
        "Rou"u8.CopyTo(p.AsSpan(66));
        var cb = T.ClientBase2016To2026(p, T.ClientBaseUs)!;
        cb.AsSpan(67, 3).ToArray().ShouldBe("Rou"u8.ToArray());
    }

    [Theory]
    [InlineData(0, 187)]   // German
    [InlineData(1, 188)]   // US: every briefinfo record is one byte longer
    public void Regen_mob_row_width_follows_the_build(int extra, int expected)
        => T.RegenMobRow2016To2026(new byte[149], extra).Length.ShouldBe(expected);

    [Fact]
    public void Mob_list_divides_like_the_us_capture()
    {
        // The US frame is 7145 = 1 + 38 * 188; every German frame is 1 + n * 187.
        var p = new byte[1 + 149 * 38];
        p[0] = 38;
        T.MobCmd2016To2026(p, extra: 1)!.Length.ShouldBe(7145);
        T.MobCmd2016To2026(p, extra: 0)!.Length.ShouldBe(1 + 38 * 187);
    }

    [Theory]
    [InlineData(0, 176)]
    [InlineData(1, 177)]
    public void Regen_mover_width_follows_the_build(int extra, int expected)
        => T.RegenMover2016To2026(new byte[139], extra)!.Length.ShouldBe(expected);

    [Theory]
    [InlineData(0, 304)]
    [InlineData(1, 305)]
    public void Login_character_width_follows_the_build(int extra, int expected)
        => T.LoginCharacter2016To2026(new byte[235], extra)!.Length.ShouldBe(expected);

    // ---------------------------------------------------------------- the zone-enter crash

    [Fact]
    public void Reward_inventory_is_eight_bytes_whatever_the_second_byte_holds()
    {
        // This was the zone-enter crash: our 2 bytes against the 8 the client reads, so it took a u32 count
        // from past the end of the buffer. The second byte varies per session and is not part of the 2026
        // frame; only the count byte is.
        var expected = new byte[] { 0, 0, 0, 0, 0, 0, 0x3f, 0x41 };
        T.RewardInven2016To2026(new byte[] { 0, 0x00 })!.ShouldBe(expected);
        T.RewardInven2016To2026(new byte[] { 0, 0x1e })!.ShouldBe(expected);
        T.RewardInven2016To2026(new byte[] { 0, 0x18 })!.ShouldBe(expected);
        T.RewardInven2016To2026(new byte[] { 3, 0x00 }).ShouldBeNull();  // a non-empty list is refused
    }

    [Fact]
    public void Charged_buff_is_six_bytes_with_the_count_at_offset_four()
    {
        // 94 = 6 + 4*22 and 28 = 6 + 1*22 in the capture; padding the 2016 pair on the right was both two
        // bytes short and put the count in the wrong field.
        T.ChargedBuff2016To2026(new byte[2])!.Length.ShouldBe(6);
        T.ChargedBuff2016To2026(new byte[] { 1, 0 }).ShouldBeNull();
    }

    // ---------------------------------------------------------------- misc

    [Fact]
    public void Login_loses_the_leading_padding_and_one_byte()
        => T.Login2026To2016(new byte[349])!.Length.ShouldBe(316);

    [Fact]
    public void Wm_login_widens_the_account_name_field()
    {
        var p = new byte[82];
        "test2026"u8.CopyTo(p);
        var wm = T.WmLogin2026To2016(p)!;
        wm.Length.ShouldBe(320);
        wm.AsSpan(0, 8).ToArray().ShouldBe("test2026"u8.ToArray());
    }

    [Fact]
    public void Client_item_count_widens_to_u32_and_records_start_at_six()
    {
        // The US head is `28 00 00 00 09 01`, and its empty frame is 6 bytes.
        var p = new byte[] { 0x28, 0x09, 0x01 };
        var ci = T.ClientItem2016To2026(p)!;
        ci.Length.ShouldBe(6);
        BitConverter.ToUInt32(ci, 0).ShouldBe(0x28u);
        ci[4].ShouldBe((byte)0x09);
        ci[5].ShouldBe((byte)0x01);
    }

    [Fact]
    public void Swing_widens_damage_to_u32()
    {
        var p = new byte[16];
        BitConverter.GetBytes((ushort)1234).CopyTo(p, 6);
        var sw = T.Swing2016To2026(p)!;
        sw.Length.ShouldBe(25);
        BitConverter.ToUInt32(sw, 6).ShouldBe(1234u);
    }

    [Fact]
    public void Target_info_widens_the_hp_change_order()
    {
        var p = new byte[30];
        BitConverter.GetBytes((ushort)777).CopyTo(p, 28);
        var ti = T.TargetInfo2016To2026(p)!;
        ti.Length.ShouldBe(41);
        BitConverter.ToUInt32(ti, 28).ShouldBe(777u);
    }

    [Fact]
    public void A_translation_refuses_a_shape_it_was_not_measured_against()
    {
        // Firing on an unexpected length produces a packet the client reads past the end of, and it then
        // crashes somewhere else entirely. Refusing is the safe answer.
        T.Login2026To2016(new byte[100]).ShouldBeNull();
        T.ClientBase2016To2026(new byte[104], T.ClientBaseUs).ShouldBeNull();
        T.RegenMover2016To2026(new byte[138], 0).ShouldBeNull();
        T.LoginCharacter2016To2026(new byte[234], 0).ShouldBeNull();
        T.WmLogin2026To2016(new byte[81]).ShouldBeNull();
        T.MobCmd2016To2026(new byte[] { 3, 1, 2 }, 0).ShouldBeNull();
    }

    // ---------------------------------------------------------------- opcode numbering

    [Theory]
    [InlineData(Op.C26Version, 2, 0x0c2e)]        // version key moves
    [InlineData(Op.C26WillSelect, 2, 0x0c36)]     // so does will-select
    [InlineData(Op.C26Login, 2, 0x0c01)]          // login does not
    [InlineData(Op.C26WorldList, 2, 0x0c06)]      // nor the world list
    [InlineData(Op.C26WorldSelect, 2, 0x0c0a)]    // nor world select
    [InlineData(Op.C26Version, 0, 0x0c2c)]        // German build: no shift at all
    public void Only_the_top_of_the_user_department_is_renumbered(ushort op, int shift, int expected)
        => Op.U(op, shift).ShouldBe((ushort)expected);

    // ---------------------------------------------------------------- helpers

    private static byte[] Payload(int length, int n)
    {
        var p = new byte[length];
        p[0] = (byte)n;
        return p;
    }

    private static byte[] WithAvatars(int n)
    {
        var p = new byte[3 + T.Avatar2016 * n];
        p[2] = (byte)n;
        return p;
    }
}
