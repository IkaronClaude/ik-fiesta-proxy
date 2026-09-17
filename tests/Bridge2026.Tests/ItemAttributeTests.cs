using Bridge2026;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The inventory record widths, checked against captured bytes on both wires.
///
/// These are the numbers that decide whether equipment appears at all. The 2026 client sizes each record
/// from the item's attribute class, not from the record's own size byte, so getting a width wrong walks the
/// whole box off alignment and only the first item shows.
/// </summary>
public class ItemAttributeTests
{
    // Classes seen on our own server's equipped box, from the item table.
    private const int Helmet = 6, Weapon = 5, Boots = 8, Amulet = 4;

    private static int ClassOf(int itemId) => itemId switch
    {
        64610 or 64611 or 453 => Helmet,  // IblisHelmet, IblisArmor, IblisHelmet_SD
        64599 => Weapon,               // IblisAxe
        64612 or 64613 => Boots,       // IblisPants, IblisBoots (class 8)
        31000 => 26,                   // House_MushRoom, a constant-width class
        _ => -1,
    };

    // ------------------------------------------------------------------ widths

    [Theory]
    // class, count byte, expected 2026 width. Fixed + (count >> 1) * 3.
    [InlineData(Helmet, 0, 14)]    // US capture: class 6 record 19 B => 14 attribute bytes
    [InlineData(Helmet, 8, 26)]    // German capture: record 31 B => 26 = 14 + 4*3
    [InlineData(Helmet, 4, 20)]    // German capture: record 25 B => 20 = 14 + 2*3
    [InlineData(Weapon, 0, 66)]    // US capture: class 5 record 71 B => 66
    [InlineData(Weapon, 8, 78)]    // German capture: record 83 B => 78 = 66 + 4*3
    [InlineData(Amulet, 8, 51)]    // German capture: record 56 B => 51 = 39 + 4*3
    [InlineData(Amulet, 4, 45)]    // German capture: record 50 B => 45 = 39 + 2*3
    [InlineData(Amulet, 10, 54)]   // German capture: record 59 B => 54 = 39 + 5*3
    public void Enchantable_width_is_fixed_plus_three_per_entry(int cls, byte countByte, int expected)
    {
        var fixedLen = expected - (countByte >> 1) * 3;
        var attr = new byte[expected];
        attr[fixedLen - 1] = countByte;              // the count byte is the last byte of the fixed part
        ItemAttr.Width2026(cls, attr).ShouldBe(expected);
    }

    [Fact]
    public void Constant_width_classes_come_from_the_jump_table()
    {
        ItemAttr.Width2026(0, new byte[8]).ShouldBe(1);
        ItemAttr.Width2026(9, new byte[64]).ShouldBe(36);
        ItemAttr.Width2026(23, new byte[32]).ShouldBe(19);   // US capture: class 23 record 24 B => 19
        ItemAttr.Width2026(30, new byte[32]).ShouldBe(9);    // US capture: class 30 record 14 B => 9
        ItemAttr.Width2026(31, new byte[32]).ShouldBe(5);    // US capture: class 31 record 10 B => 5
        ItemAttr.Width2026(36, new byte[64]).ShouldBe(26);
    }

    [Fact]
    public void Unknown_classes_report_minus_one_rather_than_a_guess()
        => ItemAttr.Width2026(99, new byte[8]).ShouldBe(-1);

    // ------------------------------------------------------------------ record translation

    [Fact]
    public void Equipment_record_gains_one_byte_before_the_count()
    {
        // Our server's helmet: datasize 17, location box 8 slot 1, item 64610, then 13 attribute bytes
        // whose last is the count. The 2026 build has 14, with the count still last.
        var rec = Rec(17, 0x2001, 64610, Attr(13, countAt: 12, count: 1));
        var outp = ItemAttr.Record2016To2026(rec, 0, rec.Length, ClassOf)!;

        outp.Length.ShouldBe(19);                    // 5 + 14, exactly the US capture's class 6 record
        outp[0].ShouldBe((byte)18);                  // datasize stays length - 1
        (outp[1] | (outp[2] << 8)).ShouldBe(0x2001); // location untouched
        (outp[3] | (outp[4] << 8)).ShouldBe(64610);  // item id untouched
        outp[ItemAttr.RecordHead + 12].ShouldBe((byte)0);   // the byte the 2026 build added
        outp[ItemAttr.RecordHead + 13].ShouldBe((byte)1);   // count byte, now last in the fixed part
    }

    [Fact]
    public void Weapon_record_becomes_the_width_the_us_capture_shows()
    {
        // Our axe carries 65 attribute bytes; the US capture's class 5 record is 71 B, i.e. 66.
        var rec = Rec(69, 0x200c, 64599, Attr(65, countAt: 64, count: 0));
        ItemAttr.Record2016To2026(rec, 0, rec.Length, ClassOf)!.Length.ShouldBe(71);
    }

    [Fact]
    public void Enchanted_equipment_keeps_its_entries()
    {
        // Four enchant entries: 2016 is 13 + 12 = 25 attribute bytes, 2026 is 14 + 12 = 26.
        var attr = Attr(25, countAt: 12, count: 8);
        for (var i = 13; i < 25; i++) attr[i] = (byte)(0xA0 + i);   // the entries
        var rec = Rec(29, 0x2001, 64610, attr);
        var outp = ItemAttr.Record2016To2026(rec, 0, rec.Length, ClassOf)!;

        outp.Length.ShouldBe(5 + 26);
        outp[ItemAttr.RecordHead + 13].ShouldBe((byte)8);            // count byte
        for (var i = 0; i < 12; i++)
            outp[ItemAttr.RecordHead + 14 + i].ShouldBe((byte)(0xA0 + 13 + i));   // entries follow it intact
    }

    [Fact]
    public void An_empty_slot_is_five_bytes_on_both_wires()
    {
        var rec = new byte[] { 4, 0x01, 0x20, 0xff, 0xff };
        ItemAttr.Record2016To2026(rec, 0, rec.Length, ClassOf)!.Length.ShouldBe(5);
    }

    [Fact]
    public void A_constant_width_record_that_already_matches_is_left_alone()
    {
        // class 26 is a constant 4 on the 2026 side, and our server sends 4.
        var rec = Rec(8, 0x3000, 31000, new byte[4]);
        ItemAttr.Record2016To2026(rec, 0, rec.Length, ClassOf)!.ShouldBe(rec);
    }

    [Fact]
    public void An_unmeasured_shape_is_refused_rather_than_guessed_at()
    {
        // An item whose class we have no table entry for.
        ItemAttr.Record2016To2026(Rec(10, 0x2001, 12345, new byte[6]), 0, 11, ClassOf).ShouldBeNull();
        // An equipment record whose attribute length does not match the rule.
        ItemAttr.Record2016To2026(Rec(11, 0x2001, 64610, Attr(7, 6, 0)), 0, 12, ClassOf).ShouldBeNull();
    }

    // ------------------------------------------------------------------ whole box

    [Fact]
    public void A_whole_box_translates_every_record_and_widens_the_count()
    {
        // The equipped box as our server sends it: helmet, weapon, boots.
        var body = new List<byte> { 3, 8, 0xF9 };
        body.AddRange(Rec(17, 0x2001, 64610, Attr(13, 12, 1)));
        body.AddRange(Rec(69, 0x200c, 64599, Attr(65, 64, 0)));
        body.AddRange(Rec(17, 0x2015, 64613, Attr(13, 12, 1)));

        var outp = ItemAttr.ClientItem2016To2026(body.ToArray(), ClassOf, out var refusal)!;

        refusal.ShouldBeNull();
        BitConverter.ToUInt32(outp, 0).ShouldBe(3u);         // count widened to u32
        outp[4].ShouldBe((byte)8);                           // box
        outp[5].ShouldBe((byte)0xF9);                        // flag
        outp.Length.ShouldBe(6 + 19 + 71 + 19);              // each record at its 2026 width

        // and it walks cleanly with the client's own rule: length is datasize + 1
        var o = 6;
        var seen = 0;
        while (o < outp.Length) { o += outp[o] + 1; seen++; }
        o.ShouldBe(outp.Length);                             // nothing left over
        seen.ShouldBe(3);
    }

    [Fact]
    public void A_box_it_cannot_translate_is_refused_with_a_reason()
    {
        var body = new List<byte> { 1, 8, 0xF9 };
        body.AddRange(Rec(10, 0x2001, 12345, new byte[6]));   // unknown class
        ItemAttr.ClientItem2016To2026(body.ToArray(), ClassOf, out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull();
        refusal.ShouldContain("12345");
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Rec(byte datasize, int location, int itemId, byte[] attr)
    {
        var r = new byte[5 + attr.Length];
        r[0] = datasize;
        r[1] = (byte)location; r[2] = (byte)(location >> 8);
        r[3] = (byte)itemId; r[4] = (byte)(itemId >> 8);
        attr.CopyTo(r, 5);
        return r;
    }

    private static byte[] Attr(int length, int countAt, byte count)
    {
        var a = new byte[length];
        a[countAt] = count;
        return a;
    }

    // ------------------------------------------------------------------ one item at the end of a packet

    // NC_ITEM_CELLCHANGE_CMD exactly as our zone sent it at 14:09:20 on 2026-09-17, one second before the
    // 2026 client crashed hovering this helmet (IblisHelmet_SD, 453, class 6, four enchant options).
    private static readonly byte[] CellChange453 = Convert.FromHexString(
        "0D240D24" + "C501" + "000000000000000000000000" + "09" + "09CC00" + "07BE00" + "0EB400" + "0A6000");

    [Fact]
    public void The_crashing_cell_change_gets_the_2026_byte_before_the_enchant_count()
    {
        var outp = ItemAttr.TrailingItem2016To2026(CellChange453, 4, ClassOf);

        outp.ShouldNotBeNull();
        Convert.ToHexString(outp!).ShouldBe(
            "0D240D24" + "C501" + "000000000000000000000000" + "00" + "09" + "09CC00" + "07BE00" + "0EB400" + "0A6000");
        (outp.Length - 6).ShouldBe(ItemAttr.Width2026(Helmet, outp.AsSpan(6)));   // what the client will read
    }

    [Fact]
    public void Equip_change_has_a_one_byte_location_and_the_same_rule()
    {
        var equip = Convert.FromHexString("0D2415" + "C501" + "000000000000000000000000" + "00");  // no options

        var outp = ItemAttr.TrailingItem2016To2026(equip, 3, ClassOf);

        Convert.ToHexString(outp!).ShouldBe("0D2415" + "C501" + "000000000000000000000000" + "00" + "00");
        outp!.Length.ShouldBe(19);                          // the official 2026 0x3002 for a helmet is 19 B
    }

    [Fact]
    public void An_emptied_slot_passes_through()
    {
        var empty = Convert.FromHexString("15200324" + "FFFF");     // official: 15 20 03 24 ff ff
        Convert.ToHexString(ItemAttr.TrailingItem2016To2026(empty, 4, ClassOf)!).ShouldBe("15200324FFFF");
    }

    [Fact]
    public void An_item_of_unknown_class_is_refused_not_guessed()
    {
        ItemAttr.TrailingItem2016To2026(Convert.FromHexString("0D240D24" + "3930" + "0000"), 4, ClassOf).ShouldBeNull();
    }
}
