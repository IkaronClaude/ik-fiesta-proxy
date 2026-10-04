namespace Bridge2026;

/// <summary>
/// Abnormal states past the 2016 bitset (AbStataIndex 792..1079: tier 7-8 scrolls and potions, Gold Dragon's Grace, the
/// newer boss states) in the brief-info bitset OTHER players' clients get when a character comes into view.
///
/// The 2016 zone keeps a 99-byte (792-bit) state bitset per object and its writers stop at 792, so the 2016 brief-info
/// records carry no bit for these; the 2026 records have 36 more bitset bytes (bits 792-1079), which the translators used to
/// fill with zeros - while official 2026 sets them. The zone DOES announce these states by INDEX (every setter calls
/// so_AbnormalState_BitSet and then BroadcastSet unconditionally; the lists 0x1C18 / 0x1C19 carry u32 indexes), so the
/// bridge keeps, per handle, the indexes >= 792 it has seen set and not reset, and sets their bits in the translated
/// LOGINCHARACTER records (byte = bit / 8, bit order LSB first - so_AbnormalState_BitSet's layout).
/// </summary>
internal sealed class ExtraAbStates
{
    public const int First = 792;           // the first index past the 2016 bitset
    public const int Bytes = 36;            // the 2026 record's extra bitset bytes; the US build's 37th byte is
                                            // padding (Translators: 'the US build's extra byte goes in the abstate padding')
    public const int Last = First + Bytes * 8 - 1;

    private readonly Dictionary<ushort, HashSet<int>> _states = new();

    public int Count(ushort handle) => _states.TryGetValue(handle, out var s) ? s.Count : 0;

    private void Set(ushort handle, int index, bool on)
    {
        if (index < First || index > Last) return;
        if (on)
        {
            if (!_states.TryGetValue(handle, out var s)) _states[handle] = s = new HashSet<int>();
            s.Add(index);
        }
        else if (_states.TryGetValue(handle, out var s))
        {
            s.Remove(index);
            if (s.Count == 0) _states.Remove(handle);
        }
    }

    /// <summary>Learn from a server->client frame (the payload after the opcode). Unknown opcodes are ignored.</summary>
    public void Observe(ushort opcode, byte[] p)
    {
        switch (opcode)
        {
            case Op.AbStateSet when p.Length >= 6:                  // {handle u16, index u32}
                Set(U16(p, 0), (int)U32(p, 2), true);
                break;
            case Op.AbStateReset when p.Length >= 6:
                Set(U16(p, 0), (int)U32(p, 2), false);
                break;
            case Op.BriefAbStateChange when p.Length >= 14:         // {handle u16, index u32, rest time u32, strength u32}
                Set(U16(p, 0), (int)U32(p, 2), true);
                break;
            case Op.BriefAbStateList when p.Length >= 3:            // {handle u16, n u8, n x {index u32, time u32, strength u32}}
            {                                                       // sent when objects meet: the handle's whole state -
                var handle = U16(p, 0);                             // it REPLACES what was tracked (a reset missed while
                _states.Remove(handle);                             // out of view no longer lingers)
                int n = p[2];
                for (var i = 0; i < n && 3 + 12 * i + 4 <= p.Length; i++) Set(handle, (int)U32(p, 3 + 12 * i), true);
                break;
            }
            // NC_BRIEFINFO_BRIEFINFODELETE (out of view) deliberately keeps the handle's states: when it comes back into
            // view the LOGINCHARACTER record is sent again and should carry them.
        }
    }

    /// <summary>Set the tracked bits of the record's handle (u16 at <paramref name="handleAt"/>) into the 36 extra bytes
    /// at <paramref name="bitsAt"/>. Returns how many bits were set.</summary>
    public int Fill(byte[] record, int handleAt, int bitsAt)
    {
        if (record.Length < bitsAt + Bytes || !_states.TryGetValue(U16(record, handleAt), out var s)) return 0;
        foreach (var index in s)
        {
            var bit = index - First;
            record[bitsAt + bit / 8] |= (byte)(1 << (bit % 8));
        }
        return s.Count;
    }

    private static ushort U16(byte[] p, int at) => (ushort)(p[at] | (p[at + 1] << 8));
    private static uint U32(byte[] p, int at) => (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));
}
