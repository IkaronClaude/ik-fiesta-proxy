using System.Buffers.Binary;

namespace Bridge2026;

/// <summary>
/// The 2026 client's QUEST TRACKER (patch 10.5.0, 06/10/2026: "Active quests can now be marked"). Read off the
/// official wire (live-20260919-201932) and our stack (2026-09-25):
///   C-&gt;S 0x441F {u16 quest}                 track - sent on every quest accept and by the "start tracking" button
///   S-&gt;C 0x4420 {u16 result, u16 quest}     0x30B0 tracked; 0x30B5 already tracked; 0x30B4 refused
///   C-&gt;S 0x4421 {u16 quest}                 stop tracking (the button)
///   S-&gt;C 0x4422 {u16 0x30B8, u16 quest}     removed - also sent unasked after a tracked quest's reward
///   S-&gt;C 0x110F 5 x {u16 quest}             the tracked set at every zone login, 0xFFFF = empty slot
/// The set belongs to the CHARACTER (operator 2026-09-25): the zone plugin quest_track (ik-fiesta-patch-recipes) answers
/// 0x441F / 0x4421 and keeps a TRACKED bit in each quest record (PLAYER_QUEST_INFO +0x1D, bit 7, a bitfield byte whose
/// other bits the zone preserves), which the zone's own character save writes to tQuest.sData. The bridge only
/// translates: it reads the bits off the login DOING list for 0x110F and strips them from what the 2026 client gets.
/// </summary>
internal static class QuestTracker
{
    public const int Slots = 5;
    public const int RecordSize = 32, RecordStatus = 2, RecordFlags = 0x1D;
    public const byte TrackedBit = 0x80;

    /// <summary>A 2016 quest DOING list {chrregnum u32, needClear u8, count u8, 32-byte records}: add the tracked
    /// quests to <paramref name="tracked"/> (emptied first when needClear is set), clear the bit in
    /// <paramref name="payload"/> and return the set so far.</summary>
    public static List<ushort> TakeTracked(byte[] payload, List<ushort> tracked)
    {
        if (payload.Length < 6) return new List<ushort>(tracked);
        if (payload[4] != 0) tracked.Clear();
        int n = payload[5];
        for (var i = 0; i < n && 6 + RecordSize * (i + 1) <= payload.Length; i++)
        {
            var at = 6 + RecordSize * i;
            var flags = payload[at + RecordFlags];
            if ((flags & TrackedBit) == 0) continue;
            payload[at + RecordFlags] = (byte)(flags & ~TrackedBit);
            var status = payload[at + RecordStatus];
            var quest = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
            if (status is >= 6 and <= 8 && !tracked.Contains(quest) && tracked.Count < Slots) tracked.Add(quest);
        }
        return new List<ushort>(tracked);
    }

    /// <summary>The 0x110F payload: up to five quest ids, the rest 0xFFFF.</summary>
    public static byte[] ListPayload(IReadOnlyList<ushort> quests)
    {
        var p = new byte[Slots * 2];
        for (var i = 0; i < Slots; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2 * i), i < quests.Count ? quests[i] : (ushort)0xFFFF);
        return p;
    }
}
