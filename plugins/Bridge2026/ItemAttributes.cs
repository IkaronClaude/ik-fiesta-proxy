namespace Bridge2026;

/// <summary>
/// How wide an inventory record's attribute block is, per item attribute class, and how to turn a 2016 one
/// into the 2026 shape.
///
/// This is the piece that makes equipment visible. An inventory record is
///     datasize u8 | location u16 | itemid u16 | attributes...
/// and the 2026 client IGNORES datasize. Its parser (Fiesta.exe 0x0079d410) reads the item id at offset 3,
/// looks the item up, takes an attribute class from the item's entry, bounds-checks it against 0x27 and
/// jumps through a 40-arm table at 0x0079df24. Each arm reports the attribute width, and every arm converges
/// on the same tail, which sets the record length to that width plus 5.
///
/// So a 2016 box handed over untouched is walked at strides the server never wrote: the first record parses
/// and everything after it is misaligned. That is why only the helmet appeared in the equipment window while
/// the model still rendered and the damage was still right, since those come from the briefinfo record.
///
/// Where the numbers come from
/// ---------------------------
/// Thirty arms load a constant width. The other ten call a parser that returns one; those parsers were
/// decoded and every one has the same shape, a fixed part followed by a variable list:
///
///     width = Fixed + (attributes[Fixed - 1] >> 1) * 3
///
/// with the count byte always the last byte of the fixed part. Fixed is 39 for class 4, 66 for class 5, and
/// 14 for classes 6, 7, 8 and 38, which share one parser. Classes 13 and 15 have their own shapes.
///
/// Checked against every sample in three captures, with nothing left over:
///     class 6   US 14 = 14 + 0*3      German 26 = 14 + 4*3 and 20 = 14 + 2*3
///     class 5   US 66 = 66 + 0*3      German 78 = 66 + 4*3
///     class 4   German 51, 45, 54, 45 = 39 + 12, 39 + 6, 39 + 15, 39 + 6
///
/// The 2016 side
/// -------------
/// Our own server's records were captured through the bridge: a class 6 helmet carries 13 attribute bytes
/// ending in the count byte, against 14 in 2026, and a class 5 weapon carries 65 against 66. So for these
/// classes the 2026 build added exactly one byte to the fixed part and left the count byte last, the same
/// pattern as the briefinfo records. The translation inserts one zero immediately before the count byte,
/// which keeps the count and its entries where the client reads them.
/// </summary>
internal static class ItemAttr
{
    /// <summary>Attribute width per class where it is a constant, read out of the jump table arms.</summary>
    private static readonly int[] ConstantWidth =
    {
        //  0   1   2   3    4    5    6    7    8   9  10  11  12   13   14  15
            1,  2,  4,  2,  -1,  -1,  -1,  -1,  -1, 36,  8,  1,  1,  -1,   1, -1,
        // 16  17  18  19   20   21   22   23   24  25  26  27  28   29   30  31
            1, -1,  4,  1,   1,   2,   1,  19,   4,  1,  4,  4,  1,  12,   9,  5,
        // 32  33  34  35   36   37   38   39
           -1,  1,  1,  2,  26,   4,  -1, 16,
    };

    /// <summary>
    /// Classes whose width is Fixed + (count &gt;&gt; 1) * 3, with the count byte last in the fixed part, and
    /// whose 2016 fixed part is one byte shorter.
    /// </summary>
    private static readonly Dictionary<int, int> EnchantableFixed = new()
    {
        [4] = 39,    // amulet          parser 0x0079f290
        [5] = 66,    // weapon          parser 0x0079f380
        [6] = 14,    // armor           parser 0x0079f2d0
        [7] = 14,    // shield          same parser
        [8] = 14,    // boot            same parser
        [38] = 14,   // bracelet        same parser
    };

    /// <summary>Header bytes before the attribute block: datasize, location, item id.</summary>
    public const int RecordHead = 5;

    /// <summary>
    /// The 2026 attribute width for a record, or -1 when this class's rule is not known.
    /// <paramref name="attr2026"/> is the attribute block as the 2026 client would read it.
    /// </summary>
    public static int Width2026(int cls, ReadOnlySpan<byte> attr2026)
    {
        if (EnchantableFixed.TryGetValue(cls, out var fixedLen))
            return attr2026.Length < fixedLen ? -1 : fixedLen + (attr2026[fixedLen - 1] >> 1) * 3;
        if (cls == 13) return attr2026.Length < 1 ? -1 : 1 + attr2026[0] * 10;          // parser 0x0079f320
        if (cls == 15) return attr2026.Length < 1 ? -1 : 1 + (attr2026[0] & 0xf) * 8;   // parser 0x0079f350
        if (cls >= 0 && cls < ConstantWidth.Length) return ConstantWidth[cls];
        return -1;
    }

    /// <summary>
    /// One 2016 inventory record as the 2026 one, or null when this class's rule is not known well enough
    /// to be sure. Refusing leaves the original bytes in place, which shows up as a stride mismatch in the
    /// client rather than as a packet it reads past the end of.
    /// </summary>
    public static byte[]? Record2016To2026(byte[] record, int at, int length, Func<int, int> classOf)
    {
        if (length < RecordHead) return null;
        var itemId = record[at + 3] | (record[at + 4] << 8);
        var attrLen = length - RecordHead;

        if (itemId == 0xFFFF)                      // empty slot: 5 bytes on both wires
            return attrLen == 0 ? Slice(record, at, length) : null;

        var cls = classOf(itemId);
        if (cls < 0) return null;

        if (EnchantableFixed.TryGetValue(cls, out var fixed2026))
        {
            // 2016 fixed part is one byte shorter, count byte last in it.
            var fixed2016 = fixed2026 - 1;
            if (attrLen < fixed2016) return null;
            var count = record[at + RecordHead + fixed2016 - 1] >> 1;
            if (attrLen != fixed2016 + count * 3) return null;   // not the shape this rule describes

            var outp = new byte[length + 1];
            Array.Copy(record, at, outp, 0, RecordHead + fixed2016 - 1);   // head + fixed part up to the count
            outp[RecordHead + fixed2016 - 1] = 0;                          // the byte the 2026 build added
            Array.Copy(record, at + RecordHead + fixed2016 - 1,
                       outp, RecordHead + fixed2016, attrLen - (fixed2016 - 1));  // count byte and entries
            outp[0] = (byte)(outp.Length - 1);                             // datasize stays length - 1
            return outp;
        }

        // Every other class: the width is a constant on the 2026 side. If ours already matches it, the
        // record needs no change; if it does not, this class's 2016 layout is unmeasured and guessing at it
        // would desynchronise the whole box.
        var want = Width2026(cls, record.AsSpan(at + RecordHead, attrLen));
        return want == attrLen ? Slice(record, at, length) : null;
    }

    /// <summary>
    /// A whole 2016 CLIENT_ITEM body translated record by record, or null if any record cannot be.
    /// The 2016 body is {count u8, box u8, flag u8, records}; the 2026 one widens the count to u32.
    /// A record's length is its datasize byte plus one, on both wires.
    /// </summary>
    public static byte[]? ClientItem2016To2026(byte[] p, Func<int, int> classOf, out string? refusal)
    {
        refusal = null;
        if (p.Length < 3) { refusal = "shorter than a header"; return null; }
        var outp = new List<byte>(p.Length + 16) { p[0], 0, 0, 0, p[1], p[2] };
        var o = 3;
        while (o < p.Length)
        {
            var len = p[o] + 1;
            if (len < RecordHead || o + len > p.Length)
            {
                refusal = $"record at {o} says {p[o]} with {p.Length - o} bytes left";
                return null;
            }
            var rec = Record2016To2026(p, o, len, classOf);
            if (rec is null)
            {
                var id = p[o + 3] | (p[o + 4] << 8);
                refusal = $"item {id} (class {classOf(id)}) with {len - RecordHead} attribute bytes";
                return null;
            }
            outp.AddRange(rec);
            o += len;
        }
        return outp.ToArray();
    }

    /// <summary>
    /// A packet whose LAST field is one item - {itemid u16, attributes} with no size byte, running to the end -
    /// with that item in the 2026 shape. <paramref name="at"/> is where the item id starts; everything before
    /// it is copied unchanged. Null when the record translator refuses (unmeasured class, unexpected width),
    /// and the caller then relays the original.
    ///
    /// NC_ITEM_CELLCHANGE_CMD, NC_ITEM_EQUIPCHANGE_CMD and the other single-item packets carry exactly the
    /// inventory record minus its size byte and location, so this wraps the item as such a record and runs
    /// it through <see cref="Record2016To2026"/>: one rule for every packet.
    /// </summary>
    public static byte[]? TrailingItem2016To2026(byte[] p, int at, Func<int, int> classOf)
    {
        var body = p.Length - at;                               // itemid + attributes
        if (body < 2 || body + RecordHead - 2 > 255) return null;
        var rec = new byte[body + RecordHead - 2];              // size byte, a 2-byte location, then the body
        rec[0] = (byte)(rec.Length - 1);
        Array.Copy(p, at, rec, RecordHead - 2, body);
        var t = Record2016To2026(rec, 0, rec.Length, classOf);
        if (t is null) return null;
        var outp = new byte[at + t.Length - (RecordHead - 2)];
        Array.Copy(p, 0, outp, 0, at);
        Array.Copy(t, RecordHead - 2, outp, at, t.Length - (RecordHead - 2));
        return outp;
    }

    private static byte[] Slice(byte[] src, int at, int len)
    {
        var outp = new byte[len];
        Array.Copy(src, at, outp, 0, len);
        return outp;
    }
}
