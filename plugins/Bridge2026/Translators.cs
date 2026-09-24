using System.Buffers.Binary;
using System.Text;

namespace Bridge2026;

/// <summary>
/// Pure payload translations between the 2016 wire and the 2026 one. No state, no I/O: every method takes
/// a payload and returns a new one, or null when the input is not the shape it was measured against.
///
/// Returning null rather than guessing is deliberate. A translation that fires on an unexpected length
/// produces a packet the client reads past the end of, and the client crashes somewhere else entirely with
/// no sign of where it came from. Refusing leaves the original bytes to go through and the mismatch shows up
/// as a size difference in the log.
///
/// Every layout here is measured. The sources are Official1.pcapng (German 2026 client),
/// OfficialUS.pcapng (US 10.6.4), the 2016 PDB struct extract, and in two places the US client's own
/// disassembly. Each method says which.
/// </summary>
internal static class T
{
    public const int Avatar2016 = 130, Avatar2026 = 161;
    private const int EquipAt = 48;              // where the equipment block starts in both
    private const int OldSlots = 20, NewSlots = 35;  // u16 item ids; 0xFFFF means nothing worn

    public const int ClientBaseDe = 124, ClientBaseUs = 362;

    /// <summary>The four bytes after the world count. They differ per server and nothing observed depends
    /// on them, so the captured German value is replayed.</summary>
    public static readonly byte[] WorldListHead = { 0x00, 0x05, 0x00, 0x60 };

    /// <summary>Bytes the 2016 Login accepts in place of the 2026 version key and XTRAP blob.</summary>
    public static readonly byte[] VersionKey2016 =
        Concat(Encoding.ASCII.GetBytes("10022024000000"), new byte[50]);
    public static readonly byte[] XtrapKey2016 = Encoding.ASCII.GetBytes("33B543B0CA6E7C41E5D1D0651307");

    /// <summary>Captured 2026 handshakes the 2016 server has no notion of, replayed verbatim.</summary>
    public static readonly byte[] Challenge2026 = FromHex(
        "3335363834343131373839333139363530000c600000004038290c65dba3771000000000000500000000000000000000000000000000000000740028000000d0");
    public static readonly byte[] Otp2026 = FromHex("3973360006000000");
    /// <summary>The one-time password in the first 32 bytes of a 2026 login, or null for an ordinary login
    /// (zeros there). Returned as the 32 bytes NC_USER_LOGIN_WITH_OTP_REQ carries.</summary>
    public static byte[]? LoginOtp(byte[] login2026)
    {
        if (login2026.Length < 32 || login2026[0] == 0) return null;
        for (var i = 0; i < 32; i++)
            if (!Uri.IsHexDigit((char)login2026[i])) return null;
        return Slice(login2026, 0, 32);
    }
    public static readonly byte[] CreateOpenHead = FromHex("398e");
    /// <summary>Last two bytes of the US build's empty reward-inventory frame.</summary>
    public static readonly byte[] RewardInvenTail = FromHex("3f41");

    // ------------------------------------------------------------------ login

    /// <summary>349 B -> 316 B: drop the leading 32 zero bytes and the extra byte before spawnapps.</summary>
    public static byte[]? Login2026To2016(byte[] p)
        => p.Length != 349 ? null : Concat(Slice(p, 32, 328 - 32), Slice(p, 329));

    /// <summary>
    /// LOGIN_ACK {count u8, {worldno u8, name[16], status u8} x n} -> the same entries behind a 5-byte head.
    ///
    /// The ENTRY LAYOUT IS UNCHANGED from 2016; only the header differs. Both captures divide exactly on
    /// that reading and on no other: US 95 = 5 + 5*18, German 293 = 5 + 16*18, and the entries then land on
    /// their names. US statuses are 06 06 0a 06 06 against a screen reading of Low, Low, High, Low, Low, so
    /// 0x06 is Low and 0x0a is High and the 2016 byte passes straight through.
    ///
    /// An earlier version rebuilt the entry as {status, worldno, name} with a trailing last-world byte, on a
    /// misreading that took the German header's fifth byte (0x60) for the first world's status. Under the
    /// real layout that trailing byte lands exactly where the client reads the FIRST world's status, and it
    /// was 0, which is Closed: every world drew offline regardless of what was written to the wrong field.
    /// </summary>
    public static byte[]? WorldList2016To2026(byte[] p, int statusOverride = -1)
    {
        if (p.Length < 1) return null;
        int n = p[0];
        if (p.Length < 1 + 18 * n) return null;
        var entries = Slice(p, 1, 18 * n);
        if (statusOverride >= 0)
            for (var i = 0; i < n; i++) entries[18 * i + 17] = (byte)statusOverride;
        return Concat(new[] { (byte)n }, WorldListHead, entries);
    }

    /// <summary>
    /// 83 B {status, ip[16], port u16, validate[64]} -> 84 B, the same fields plus the chosen world number.
    ///
    /// The port is at offset 17, NOT 18. The German ack reads `09 | "162.19.147.96"+3 zeros | 96 23 | 64 B |
    /// 0a`, 0x2396 is 9110, and the capture shows the client opening its next connection to that port. This
    /// is also the one layout confirmed in the US client's own code: its handler reads the port as
    /// word ptr [ebx+0x11]. Reading it at 18 gives 37923, which is a port nothing listens on.
    /// </summary>
    public static byte[]? WorldSelectAck2016To2026(byte[] p, string advertiseIp, byte world)
        => p.Length < 83 ? null : Concat(new[] { p[0] }, Name4(advertiseIp), Slice(p, 17, 83 - 17), new[] { world });

    /// <summary>
    /// 82 B {user[18], validate[64]} -> 320 B {user Name256Byte, validate[64]}, the German build's form.
    /// The US build already sends 320 bytes, so this returns null there and the caller forwards the payload
    /// unchanged under the 2016 opcode.
    /// </summary>
    public static byte[]? WmLogin2026To2016(byte[] p)
    {
        if (p.Length != 82) return null;
        var user = CString(Slice(p, 0, 18));
        var outp = new byte[320];
        user.CopyTo(outp, 0);
        Slice(p, 18, 82 - 18).CopyTo(outp.AsSpan(256));
        return outp;
    }

    // ------------------------------------------------------------------ avatars

    /// <summary>
    /// One 130-byte 2016 avatar record as the 161-byte 2026 one. Only the equipment block changes size;
    /// every other field keeps its 2016 offset, which is what makes the character list render at all.
    ///   0 chrregnum u32 | 4 name[20] | 24 level u16 | 26 slot u8 | 27 loginmap[12] | 39 delinfo[5]
    ///   44 shape[4] | 48 equip (2016: 20 slots + 3 upgrade = 43; 2026: 35 slots + 4 upgrade = 74)
    ///   then nKQHandle, sKQMapName, nKQCoord, dKQDate, CharIDChangeData, TutorialInfo, all unchanged.
    /// Cross-checked three ways on the five avatars of Official1.pcapng: the second map name lands on 126,
    /// nKQCoord at 138 decodes as real coordinates, and CharIDChangeData.bNeedChangeID at 150 is 1 for the
    /// one character that owed a rename. An empty equipment slot is 0xFFFF, so the fifteen added slots are
    /// filled with 0xFF rather than zeroed: 0x0000 would be item id 0.
    /// </summary>
    public static byte[] AvatarRecord2016To2026(byte[] a)
    {
        const int upgrade = EquipAt + OldSlots * 2;       // 88
        const int added = (NewSlots - OldSlots) * 2;      // 30
        var fill = new byte[added + 1];
        Array.Fill(fill, (byte)0xFF);                     // new slots, plus the upgrade field's extra byte
        return Concat(Slice(a, 0, upgrade), fill, Slice(a, upgrade, 3), Slice(a, upgrade + 3));
    }

    /// <summary>
    /// LOGINWORLD_ACK {wm u16, count u8, avatar[130] x n}
    ///   -> {wm u16, count u8, slots u8, ip[16], port u16, avatar[161] x n}.
    /// The US capture confirms the record width independently: its frames are 22 (no avatars) and 183,
    /// and 183 = 22 + 161.
    /// </summary>
    public static byte[]? Avatars2016To2026(byte[] p, string advertiseIp)
    {
        if (p.Length < 3) return null;
        int n = p[2];
        var outp = new List<byte>(22 + Avatar2026 * n);
        outp.AddRange(Slice(p, 0, 3).ToArray());
        outp.Add(0x0c);                                   // character slots this account may use
        outp.AddRange(Name4(advertiseIp));
        outp.AddRange(new byte[] { 0, 0 });               // a second endpoint's port; unused against 2016
        for (var i = 0; i < n; i++)
        {
            var at = 3 + Avatar2016 * i;
            if (at + Avatar2016 > p.Length) break;
            outp.AddRange(AvatarRecord2016To2026(Slice(p, at, Avatar2016)));
        }
        return outp.ToArray();
    }

    /// <summary>NC_AVATAR_CREATESUCC_ACK {count u8, avatar[130]} -> {count u8, avatar[161]}, 162 B on the
    /// US wire. Untranslated the client reads a character 31 bytes short of what it expects.</summary>
    public static byte[]? CreateSucc2016To2026(byte[] p)
        => p.Length != 1 + Avatar2016 ? null : Concat(Slice(p, 0, 1), AvatarRecord2016To2026(Slice(p, 1)));

    /// <summary>
    /// NC_AVATAR_CREATE_REQ 26 B -> 25 B: the 2026 appearance block is 5 bytes where 2016 has 4. Measured on
    /// `00 "Anna"+pad(20) 41 01 16 06 06` and `01 "test321"+pad(20) d5 04 00 00 06`. The first four are the
    /// 2016 shape (packed race/class/gender, hair, colour, face); the fifth is new and 0x06 in both samples,
    /// so it is dropped rather than guessed at.
    /// </summary>
    public static byte[]? CreateReq2026To2016(byte[] p)
        => p.Length != 26 ? null : Slice(p, 0, 25);

    // ------------------------------------------------------------------ zone

    /// <summary>1718 B (22 head + 53 checksums) -> 1590 B (22 head + the 49 the 2016 server wants).</summary>
    public static byte[]? MapLogin2026To2016(byte[] p, IReadOnlyList<byte[]> checksums)
    {
        if (p.Length < 22 + 32) return null;
        var outp = new List<byte>(22 + 32 * checksums.Count);
        outp.AddRange(Slice(p, 0, 22).ToArray());
        foreach (var c in checksums) outp.AddRange(c);
        return outp.ToArray();
    }

    /// <summary>
    /// 105 B -> 362 B: PROTO_NC_CHAR_BASE_CMD with ONE byte inserted at offset 54, then zero-padded.
    ///
    /// Every field of the 2016 struct (from the PDB extract) lands one byte later in the US 2026 frame, and
    /// the whole of OfficialUS2.pcapng's login frame reads correctly on that and on nothing else:
    ///     slotno 24, Level 25 = 2          Experience u64 26 = 17        CurHP u32 42 = 53, CurSP 46 = 89
    ///     CurLP u32 50 = 0                 the inserted byte at 54       fame u32 55 = 2
    ///     Cen u64 59 = 104                 logininfo 67 = "Rou"          x 79 = 4913, y 83 = 5976
    /// The capture's own NC_CHAR_CENCHANGE_CMD then walks that character 104 -> 156 -> 208 -> ... -> 1114,
    /// which is what the player saw on screen.
    ///
    /// This was three separate transcription errors before: two filler bytes at 54 and one more at 58 put
    /// fame and cen at the wrong offsets, only the LOW HALF of the 64-bit cen was copied, and the byte at
    /// 2016 offset 86 was dropped so everything from statdistribute on lost the shift. The visible symptom
    /// was money reading as a large wrong number (44059 on a character holding 1).
    /// </summary>
    private const int ClientBaseInsertAt = 54;

    public static byte[]? ClientBase2016To2026(byte[] p, int total)
    {
        if (p.Length != 105 || total < 106) return null;
        var outp = new byte[total];
        Array.Copy(p, 0, outp, 0, ClientBaseInsertAt);
        Array.Copy(p, ClientBaseInsertAt, outp, ClientBaseInsertAt + 1, p.Length - ClientBaseInsertAt);
        return outp;
    }

    /// <summary>
    /// 149 B -> 187 (German) or 188 (US): zero bytes after the 99-byte abstate array at 114, then a trailing
    /// one. The US build's briefinfo records are each ONE byte longer, and the arithmetic settles it:
    /// 0x1c09 is {count u8, REGENMOB x n}, every German frame equals 1 + n*187 and the US frame is
    /// 1 + 38*188. The extra byte sits in the padding, not at the end: both builds close the record with the
    /// same `02 00 00`, at offset 184 in the German record and 185 in the US one.
    /// </summary>
    public static byte[] RegenMobRow2016To2026(byte[] row, int extra)
        => row.Length != 149 ? row.ToArray()
                             : Concat(Slice(row, 0, 114), new byte[37 + extra], Slice(row, 114), new byte[1]);

    public static byte[]? MobCmd2016To2026(byte[] p, int extra)
    {
        if (p.Length < 1) return null;
        int n = p[0];
        if (n == 0 || (p.Length - 1) % n != 0 || (p.Length - 1) / n != 149) return null;
        var outp = new List<byte>(1 + n * (187 + extra)) { (byte)n };
        for (var i = 0; i < n; i++) outp.AddRange(RegenMobRow2016To2026(Slice(p, 1 + 149 * i, 149), extra));
        return outp.ToArray();
    }

    /// <summary>139 B -> 176 (German) or 177 (US): the abstate array at 19 grows, as in REGENMOB.</summary>
    public static byte[]? RegenMover2016To2026(byte[] p, int extra)
        => p.Length != 139 ? null : Concat(Slice(p, 0, 118), new byte[37 + extra], Slice(p, 118));

    /// <summary>
    /// 235 B -> 304 (German) or 305 (US): head and shape to 82, 31 more bytes of equipment, then
    /// polymorph/emoticon/title, one byte, the abstate bits, the tail (guild @258, level @265, animation
    /// @266, mover @298, KQ team @301) and two bytes. The US build's extra byte goes in the abstate padding.
    /// </summary>
    public static byte[]? LoginCharacter2016To2026(byte[] p, int extra)
        => p.Length != 235 ? null
           : Concat(Slice(p, 0, 82), new byte[31], Slice(p, 82, 91 - 82), new byte[1], Slice(p, 91, 190 - 91), new byte[36 + extra],
                    Slice(p, 190, 234 - 190), new byte[2]);

    /// <summary>
    /// CLIENT_ITEM {count u8, box u8, flag u8, records} -> {count u32, box u8, flag u8, records}.
    /// The US capture's head is `28 00 00 00 09 01`, and its empty frame is 6 bytes with a count of 0, so
    /// the records start at 6. The records themselves are NOT translated: see the note in
    /// <see cref="Bridge2026Session"/> on what is still open there.
    /// </summary>
    public static byte[]? ClientItem2016To2026(byte[] p)
        => p.Length < 3 ? null : Concat(Slice(p, 0, 1), new byte[3], Slice(p, 1));

    /// <summary>
    /// CHARGEDBUFF, the login-time list of charged effects (inventory/storage expansions, the void inventory,
    /// every time-limited item effect):
    ///     2016  {count u16}                       then count x 14 B {key u32, handle u16, use u32, end u32}
    ///     2026  {u32 = 0, count u16 at OFFSET 4}  then count x 22 B: the same 14 bytes, then 8 zero bytes
    ///
    /// Measured in every official 0x104A in every capture, German and US alike: all of them are exactly
    /// 6 + 22*n. The void character's list reads key 5/6/9 handle 920 and key 35 handle 7750 (the Void
    /// Inventory item), each followed by 8 zero bytes, and both dates are packed exactly as ours are - a
    /// permanent end date is `FF EC BB 76` on both sides - so the 14 bytes carry over unchanged.
    ///
    /// THIS USED TO REFUSE ANY NON-EMPTY LIST, because only the empty form had been seen. A refused
    /// translation falls through and the 2016 bytes reach the client as they are, where the 2026 layout
    /// reads them as nothing - so every charged effect vanished at each login while the zone still held all
    /// of them. Seen 2026-09-19: three Iron Cases saved and applied server-side, zero in the client's list,
    /// and a fourth refused because the zone knew about the other three.
    ///
    /// Official also sends a second 0x104A per login whose u32 head is 1, with 0-1 entries; what that list
    /// is has not been worked out. The 2016 server sends only this one, which is the head-0 list.
    /// </summary>
    public const int ChargedBuffRecord2016 = 14, ChargedBuffRecord2026 = 22, ChargedBuffHead2026 = 6;

    public static byte[]? ChargedBuff2016To2026(byte[] p)
    {
        if (p.Length < 2) return null;
        int n = p[0] | (p[1] << 8);
        if (p.Length != 2 + n * ChargedBuffRecord2016) return null;      // not the shape we measured: leave it
        var outp = new byte[ChargedBuffHead2026 + n * ChargedBuffRecord2026];
        outp[4] = p[0];
        outp[5] = p[1];
        for (int i = 0; i < n; i++)
            Array.Copy(p, 2 + i * ChargedBuffRecord2016, outp, ChargedBuffHead2026 + i * ChargedBuffRecord2026,
                       ChargedBuffRecord2016);                           // the trailing 8 bytes stay zero
        return outp;
    }

    /// <summary>
    /// NC_CHARGED_BUFFSTART_CMD (0x9003, a charged item just used): 2016 sends one bare 14-byte
    /// PROTO_CHARGEDBUFF_INFO; the 2026 handler (Fiesta.exe 0x5B6EA0) reads a u32 at +14 - the first of the
    /// 8 bytes the 2026 record carries (see <see cref="ChargedBuff2016To2026"/>) - and on NON-ZERO files the
    /// buff in its second list (0x8172F0) instead of the normal one (0x816570). Relayed raw, that u32 was read
    /// past the end of our payload, so used buffs landed in the wrong list or nowhere: "none of these buffs
    /// show up in the charged effect list" (operator 2026-09-24). Pad with the same 8 zero bytes.
    /// </summary>
    public static byte[]? ChargedBuffStart2016To2026(byte[] p)
        => p.Length != ChargedBuffRecord2016 ? null : Concat(p, new byte[ChargedBuffRecord2026 - ChargedBuffRecord2016]);

    /// <summary>
    /// NC_CHARGED_BUFFTERMINATE_CMD (0x9004): 2016 {key u32}; the 2026 handler (0x5B6F60) also reads a byte at
    /// +4 that picks the same list as above (1 = the second list, 0x817370; else the normal one, 0x8165F0).
    /// Our buffs are all in the normal list: append 0.
    /// </summary>
    public static byte[]? ChargedBuffTerminate2016To2026(byte[] p)
        => p.Length != 4 ? null : Concat(p, new byte[1]);

    /// <summary>
    /// NC_ITEM_REWARDINVENOPEN_ACK: our 2016 server sends 2 bytes, the US client reads 8. THIS WAS THE
    /// ZONE-ENTER CRASH. The client requests it at zone enter and walks it as a count-prefixed list, reading
    /// a u32 count from offset 0; with a 2-byte payload two of those bytes come from past the end. Four
    /// dumps showed the count as 0x40ED1E00 once and 0x00001E00 three times, the low half being our payload
    /// and the high half whatever followed. The US empty frame is `00 00 00 00 00 00 3f 41`.
    /// Only the count byte of the 2016 pair is meaningful; the second byte varies per session.
    /// </summary>
    public static byte[]? RewardInven2016To2026(byte[] p)
        => p.Length != 2 || p[0] != 0 ? null : Concat(new byte[6], RewardInvenTail);

    /// <summary>16 B -> 25 B: damage widens to u32 and seven zero bytes follow.</summary>
    public static byte[]? Swing2016To2026(byte[] p)
    {
        if (p.Length != 16) return null;
        var outp = new byte[25];
        Slice(p, 0, 6).CopyTo(outp);                                              // attacker, defender, flag
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(6), BinaryPrimitives.ReadUInt16LittleEndian(Slice(p, 6)));
        Slice(p, 8, 16 - 8).CopyTo(outp.AsSpan(10));                                 // resthp, order, index, sequence
        return outp;
    }

    /// <summary>30 B -> 41 B: the head is unchanged, hpchangeorder widens to u32, nine zero bytes follow.</summary>
    public static byte[]? TargetInfo2016To2026(byte[] p)
    {
        if (p.Length != 30) return null;
        var outp = new byte[41];
        Slice(p, 0, 28).CopyTo(outp);
        BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(28), BinaryPrimitives.ReadUInt16LittleEndian(Slice(p, 28)));
        return outp;
    }

    // ------------------------------------------------------------------ combat

    /// <summary>
    /// The four SKILLBASH *_START frames, each the 2016 struct plus a trailing u32:
    /// HIT_OBJ_START 6 -> 10, HIT_FLD_START 12 -> 16, SOMEONE_HIT_OBJ_START 8 -> 12,
    /// SOMEONE_HIT_FLD_START 14 -> 18 (measured on both 2026 captures).
    ///
    /// The field's meaning is unmeasured - the instance captures carry 1, 13, 20 and 3 - but a single hit
    /// carries 1, which is what we send. It is not cosmetic: the 2026 client reads the cast bookkeeping out
    /// of a 10-byte frame, so a 6-byte one leaves the cast it opened never closed and every later cast is
    /// refused with "Cannot use the skill yet" even though the cooldown display has run out.
    /// </summary>
    public static byte[]? HitStart2016To2026(byte[] p, int size2016)
        => p.Length != size2016 ? null : Concat(p, BitConverter.GetBytes(1));

    /// <summary>DOTDAMAGE and SOMEONESWING_DAMAGE, both 13 -> 20: the 2016 layout then 7 zero bytes.</summary>
    public static byte[]? Tail7_2016To2026(byte[] p, int size2016)
        => p.Length != size2016 ? null : Concat(p, new byte[7]);

    /// <summary>
    /// NC_BAT_SKILLBASH_HIT_DAMAGE: {index u16, caster u16, n u8} + n x 14 B
    /// -> the same head, then a skill id u16 and 0xFFFF, then n x 21 B (each record plus 7 zero bytes).
    ///
    /// The two inserted head bytes are left zero: the client already knows which skill it cast from the
    /// index, which pairs this frame back to the *_START that opened it.
    /// </summary>
    public static byte[]? SkillHit2016To2026(byte[] p)
    {
        if (p.Length < 5) return null;
        int n = p[4];
        if (p.Length != 5 + 14 * n) return null;

        var outp = new byte[9 + 21 * n];
        Array.Copy(p, 0, outp, 0, 5);
        outp[7] = 0xFF; outp[8] = 0xFF;
        for (var i = 0; i < n; i++) Array.Copy(p, 5 + 14 * i, outp, 9 + 21 * i, 14);
        return outp;
    }

    // ------------------------------------------------------------------ quests, shops, character list

    /// <summary>
    /// PLAYER_QUEST_INFO 32 B -> 37 B: the 32 bytes are the SAME in 2026 and 5 zero bytes follow.
    /// Measured on the official EU wire (Official1.pcapng, 36 quests): the kill counters are still the five
    /// End_NPCMobCount BYTES at 24 - quest 552 reads 153 of its 180 kills at +25 (slot 1, the kill objective;
    /// slot 0 is the talk NPC), 2530 reads 36 of 40 - and bytes 29-36 are zero in every entry. The earlier
    /// reading "the counters are u16 on the 2026 wire" put slot 1 at +26 = the client's slot 2: every partly
    /// done quest showed 0/N after a relog (operator 2026-09-24, Buzzel 0/30).
    /// </summary>
    private static void QuestEntry2016To2026(byte[] src, int at, byte[] dst, int to)
        => Array.Copy(src, at, dst, to, 32);

    /// <summary>CLIENT_QUEST_DOING {chrregnum u32, flag u8, count u8} + entries.</summary>
    public static byte[]? QuestDoing2016To2026(byte[] p)
        => p.Length < 6 ? null : QuestList(p, p[5]);

    /// <summary>CLIENT_QUEST_REPEAT {chrregnum u32, count u16} + entries.</summary>
    public static byte[]? QuestRepeat2016To2026(byte[] p)
        => p.Length < 6 ? null : QuestList(p, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4)));

    /// <summary>Both quest lists carry a 6-byte head and then the same entries.</summary>
    private static byte[]? QuestList(byte[] p, int n)
    {
        if (p.Length != 6 + 32 * n) return null;
        var outp = new byte[6 + 37 * n];
        Array.Copy(p, 0, outp, 0, 6);
        for (var i = 0; i < n; i++) QuestEntry2016To2026(p, 6 + 32 * i, outp, 6 + 37 * i);
        return outp;
    }

    /// <summary>
    /// NC_MENU_SHOPOPEN* and their TABLE forms: {itemnum u16, npc u16} then one record per item.
    /// A 2016 record is {slot u8, itemid u16}; a 2026 one widens the slot to u32.
    /// </summary>
    public static byte[]? ShopTable2016To2026(byte[] p)
    {
        if (p.Length < 4) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (p.Length != 4 + 3 * n) return null;

        var outp = new byte[4 + 6 * n];
        Array.Copy(p, 0, outp, 0, 4);
        for (var i = 0; i < n; i++)
        {
            outp[4 + 6 * i] = p[4 + 3 * i];                       // slot, zero-extended to u32
            outp[4 + 6 * i + 4] = p[5 + 3 * i];
            outp[4 + 6 * i + 5] = p[6 + 3 * i];                   // item id
        }
        return outp;
    }

    /// <summary>NC_BRIEFINFO_CHARACTER_CMD: {count u8} then that many LOGINCHARACTER records.</summary>
    public static byte[]? CharacterList2016To2026(byte[] p, int usExtra)
    {
        if (p.Length < 1) return null;
        int n = p[0];
        if (p.Length != 1 + 235 * n) return null;

        var rows = new byte[n][];
        for (var i = 0; i < n; i++)
        {
            rows[i] = LoginCharacter2016To2026(Slice(p, 1 + 235 * i, 235), usExtra)!;
            if (rows[i] is null) return null;
        }
        return Concat(new[] { new byte[] { (byte)n } }.Concat(rows).ToArray());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A dotted-quad as the 16-byte zero-padded ASCII field the protocol uses for addresses.</summary>
    public static byte[] Name4(string ip)
    {
        var outp = new byte[16];
        Encoding.ASCII.GetBytes(ip.AsSpan(0, Math.Min(ip.Length, 15)), outp);
        return outp;
    }

    /// <summary>Bytes up to the first NUL.</summary>
    public static byte[] CString(byte[] s)
    {
        var i = Array.IndexOf(s, (byte)0);
        return i < 0 ? s : Slice(s, 0, i);
    }

    public static byte[] FromHex(string hex)
    {
        var outp = new byte[hex.Length / 2];
        for (var i = 0; i < outp.Length; i++) outp[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return outp;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var outp = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { p.CopyTo(outp, at); at += p.Length; }
        return outp;
    }

    /// <summary>A copy of a byte range; the payloads here are small and this keeps the code readable.</summary>
    private static byte[] Slice(byte[] src, int start) => Slice(src, start, src.Length - start);

    private static byte[] Slice(byte[] src, int start, int length)
    {
        var outp = new byte[length];
        Array.Copy(src, start, outp, 0, length);
        return outp;
    }
}
