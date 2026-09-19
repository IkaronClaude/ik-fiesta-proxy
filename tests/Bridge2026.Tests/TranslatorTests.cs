using System.Text;
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
        T.ChargedBuff2016To2026(new byte[] { 1, 0 }).ShouldBeNull();   // claims a record it does not carry
    }

    [Fact]
    public void Charged_buff_entries_widen_from_14_to_22_bytes()
    {
        // Our 2016 zone's real login frame, 2026-09-19: three permanent Iron Cases (handle 827 = 0x033B),
        // keys 0/1/2, end date 2255-12-31 packed as FF EC BB 76. This used to be refused and passed through
        // untranslated, and the client showed an empty list while the zone held all three.
        var p16 = Hex(("03 00" +
                      " 00 00 00 00 3B 03 1A 69 42 4C FF EC BB 76" +
                      " 01 00 00 00 3B 03 1A 69 42 64 FF EC BB 76" +
                      " 02 00 00 00 3B 03 1A 69 4A 26 FF EC BB 76").Replace(" ", ""));

        var p26 = T.ChargedBuff2016To2026(p16)!;

        p26.Length.ShouldBe(6 + 3 * 22);                         // the official shape: 94 = 6 + 4*22
        p26[..4].ShouldBe(new byte[4]);                          // head u32 0, as the official buff list
        (p26[4] | (p26[5] << 8)).ShouldBe(3);                    // count at offset 4
        for (int i = 0; i < 3; i++)
        {
            p26.AsSpan(6 + i * 22, 14).ToArray().ShouldBe(p16.AsSpan(2 + i * 14, 14).ToArray());
            p26.AsSpan(6 + i * 22 + 14, 8).ToArray().ShouldBe(new byte[8]);   // official tails are zero
        }
    }

    [Fact]
    public void Charged_buff_of_the_wrong_length_is_left_alone()
    {
        T.ChargedBuff2016To2026(new byte[] { 2, 0, 1, 2, 3 }).ShouldBeNull();
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
    public void Wm_login_refuses_the_us_form_so_the_caller_forwards_it_unchanged()
    {
        // The German build sends 82 bytes and needs widening; the US build already sends the 320-byte 2016
        // shape. Returning null there is the signal to forward the payload as-is under the 2016 OPCODE,
        // which still has to change from 0x0c0e to 0x0c0f or the world manager hangs up.
        T.WmLogin2026To2016(new byte[320]).ShouldBeNull();
        T.WmLogin2026To2016(new byte[82])!.Length.ShouldBe(320);
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

    // ---------------------------------------------------------------- combat

    [Theory]
    [InlineData(6, 10)]     // HIT_OBJ_START
    [InlineData(12, 16)]    // HIT_FLD_START
    [InlineData(8, 12)]     // SOMEONE_HIT_OBJ_START
    [InlineData(14, 18)]    // SOMEONE_HIT_FLD_START
    public void A_cast_start_gains_a_trailing_u32(int size2016, int size2026)
    {
        var outp = T.HitStart2016To2026(new byte[size2016], size2016)!;
        outp.Length.ShouldBe(size2026);
        BitConverter.ToUInt32(outp, size2016).ShouldBe(1u);
    }

    [Fact]
    public void A_cast_start_of_the_wrong_length_is_refused()
        => T.HitStart2016To2026(new byte[7], 6).ShouldBeNull();

    [Fact]
    public void Skill_hit_damage_gains_a_skill_id_and_widens_every_record()
    {
        // {index 9, caster 10, 2 records} + 2 x 14 B
        var p = Concat(Bits((ushort)9), Bits((ushort)10), new byte[] { 2 }, Fill(28, 0x40));
        var outp = T.SkillHit2016To2026(p)!;

        outp.Length.ShouldBe(9 + 42);
        BitConverter.ToUInt16(outp, 0).ShouldBe((ushort)9);      // index, which pairs back to the cast start
        BitConverter.ToUInt16(outp, 2).ShouldBe((ushort)10);     // caster
        outp[4].ShouldBe((byte)2);                               // count
        outp[5].ShouldBe((byte)0); outp[6].ShouldBe((byte)0);    // skill id, left zero
        outp[7].ShouldBe((byte)0xFF); outp[8].ShouldBe((byte)0xFF);
        for (var r = 0; r < 2; r++)
        {
            for (var i = 0; i < 14; i++) outp[9 + 21 * r + i].ShouldBe((byte)(0x40 + 14 * r + i));
            for (var i = 14; i < 21; i++) outp[9 + 21 * r + i].ShouldBe((byte)0);
        }
    }

    [Fact]
    public void Skill_hit_damage_with_a_count_that_does_not_divide_is_refused()
        => T.SkillHit2016To2026(new byte[] { 0, 0, 0, 0, 3, 1, 2 }).ShouldBeNull();

    [Fact]
    public void Dot_and_someone_swing_gain_seven_zero_bytes()
    {
        var outp = T.Tail7_2016To2026(Fill(13, 0x11), 13)!;
        outp.Length.ShouldBe(20);
        outp[12].ShouldBe((byte)(0x11 + 12));
        for (var i = 13; i < 20; i++) outp[i].ShouldBe((byte)0);
    }

    // ---------------------------------------------------------------- quests, shops, character list

    [Fact]
    public void A_quest_entry_widens_its_five_mob_counts_to_u16()
    {
        // A doing entry off the fighter fixture: its third mob count is 3, the rest zero.
        var q16 = Hex("180008f1377a6a000000009a397a6a0000000001000000000003000000000000");
        var outp = T.QuestDoing2016To2026(Concat(Hex("cd0b00000101"), q16))!;

        outp.Length.ShouldBe(6 + 37);
        outp.AsSpan(6, 24).ToArray().ShouldBe(q16[..24]);                          // head unchanged
        outp.AsSpan(30, 10).ToArray().ShouldBe(new byte[] { 0, 0, 3, 0, 0, 0, 0, 0, 0, 0 });
        outp.AsSpan(40, 3).ToArray().ShouldBe(q16[29..32]);                        // flags and time follow
    }

    [Fact]
    public void Quest_doing_takes_its_count_from_byte_five_and_repeat_from_a_u16()
    {
        var q16 = new byte[32];
        T.QuestDoing2016To2026(Concat(Hex("cd0b00000102"), q16, q16))!.Length.ShouldBe(6 + 74);
        T.QuestRepeat2016To2026(Concat(Hex("cd0b00000300"), q16, q16, q16))!.Length.ShouldBe(6 + 111);
    }

    [Fact]
    public void A_quest_list_whose_count_does_not_match_its_length_is_refused()
    {
        T.QuestDoing2016To2026(Concat(Hex("cd0b00000102"), new byte[32])).ShouldBeNull();
        T.QuestRepeat2016To2026(Concat(Hex("cd0b00000200"), new byte[32])).ShouldBeNull();
    }

    [Fact]
    public void A_shop_record_widens_its_slot_to_u32()
    {
        var p = Concat(Bits((ushort)2), Bits((ushort)0x1234),
                       new byte[] { 0, 0x10, 0x27, 7, 0x11, 0x27 });
        var outp = T.ShopTable2016To2026(p)!;

        outp.ShouldBe(Concat(Bits((ushort)2), Bits((ushort)0x1234),
                             Bits(0u), Bits((ushort)0x2710),
                             Bits(7u), Bits((ushort)0x2711)));
    }

    [Fact]
    public void A_shop_table_whose_count_does_not_match_its_length_is_refused()
        => T.ShopTable2016To2026(Concat(Bits((ushort)3), Bits((ushort)0), new byte[3])).ShouldBeNull();

    [Theory]
    [InlineData(0, 304)]    // German build
    [InlineData(1, 305)]    // US build, one more abstate byte
    public void A_character_list_translates_every_row(int usExtra, int rowSize)
    {
        var outp = T.CharacterList2016To2026(Concat(new byte[] { 2 }, new byte[235], new byte[235]), usExtra)!;
        outp[0].ShouldBe((byte)2);
        outp.Length.ShouldBe(1 + 2 * rowSize);
    }

    [Fact]
    public void A_character_list_whose_count_does_not_match_its_length_is_refused()
        => T.CharacterList2016To2026(Concat(new byte[] { 2 }, new byte[235]), 0).ShouldBeNull();

    // ---------------------------------------------------------------- character base

    [Fact]
    public void Client_base_lands_every_field_where_the_us_capture_has_it()
    {
        // PROTO_NC_CHAR_BASE_CMD as the 2016 server sends it, filled with the values OfficialUS2.pcapng's
        // login frame carries, so the assertions below are that capture read back.
        var p = new byte[105];
        BitConverter.GetBytes(0x00e18092u).CopyTo(p, 0);      // chrregnum
        Encoding.ASCII.GetBytes("_Anna").CopyTo(p, 4);        // charid
        p[24] = 0;                                            // slotno
        p[25] = 2;                                            // Level
        BitConverter.GetBytes(17UL).CopyTo(p, 26);            // Experience
        BitConverter.GetBytes((ushort)11).CopyTo(p, 38);      // CurHPStone
        BitConverter.GetBytes((ushort)14).CopyTo(p, 40);      // CurSPStone
        BitConverter.GetBytes(53u).CopyTo(p, 42);             // CurHP
        BitConverter.GetBytes(89u).CopyTo(p, 46);             // CurSP
        BitConverter.GetBytes(0u).CopyTo(p, 50);              // CurLP
        BitConverter.GetBytes(2u).CopyTo(p, 54);              // fame
        BitConverter.GetBytes(104UL).CopyTo(p, 58);           // Cen - the money on screen
        Encoding.ASCII.GetBytes("Rou").CopyTo(p, 66);         // logininfo.mapname
        BitConverter.GetBytes(4913u).CopyTo(p, 78);           // x
        BitConverter.GetBytes(5976u).CopyTo(p, 82);           // y

        var outp = T.ClientBase2016To2026(p, T.ClientBaseUs)!;

        outp.Length.ShouldBe(362);
        BitConverter.ToUInt32(outp, 0).ShouldBe(0x00e18092u);
        Encoding.ASCII.GetString(outp, 4, 5).ShouldBe("_Anna");
        outp[25].ShouldBe((byte)2);                            // Level, still at 25
        BitConverter.ToUInt64(outp, 26).ShouldBe(17UL);
        BitConverter.ToUInt32(outp, 42).ShouldBe(53u);
        BitConverter.ToUInt32(outp, 46).ShouldBe(89u);
        BitConverter.ToUInt32(outp, 55).ShouldBe(2u);          // fame, one later
        BitConverter.ToUInt64(outp, 59).ShouldBe(104UL);       // Cen: the whole 64 bits, one later
        Encoding.ASCII.GetString(outp, 67, 3).ShouldBe("Rou");
        BitConverter.ToUInt32(outp, 79).ShouldBe(4913u);
        BitConverter.ToUInt32(outp, 83).ShouldBe(5976u);
    }

    [Fact]
    public void Client_base_keeps_money_that_does_not_fit_in_32_bits()
    {
        // The field is a u64 and a capped character holds more than 4 billion cen; the old translation
        // copied four of its eight bytes.
        var p = new byte[105];
        BitConverter.GetBytes(9_000_000_000UL).CopyTo(p, 58);
        BitConverter.ToUInt64(T.ClientBase2016To2026(p, T.ClientBaseUs)!, 59).ShouldBe(9_000_000_000UL);
    }

    [Fact]
    public void Client_base_refuses_a_payload_that_is_not_the_2016_struct()
        => T.ClientBase2016To2026(new byte[104], T.ClientBaseUs).ShouldBeNull();

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
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static byte[] Bits(ushort v) => BitConverter.GetBytes(v);

    private static byte[] Bits(uint v) => BitConverter.GetBytes(v);

    /// <summary>A run of distinguishable bytes, so a translation that moves them shows where they went.</summary>
    private static byte[] Fill(int length, int from)
    {
        var p = new byte[length];
        for (var i = 0; i < length; i++) p[i] = (byte)(from + i);
        return p;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { p.CopyTo(outp, at); at += p.Length; }
        return outp;
    }
}
