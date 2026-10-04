using System;
using System.Linq;
using Bridge2026;
using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The 2026 quest tracker through the bridge: the zone (quest_track plugin) owns the set and answers the requests;
/// the bridge relays them and turns the TRACKED bit of the login quest records into the 0x110F list.
/// </summary>
public class QuestTrackerTests
{
    /// <summary>A 2016 quest DOING list: {chrregnum u32, needClear u8, count u8} + 32-byte records.</summary>
    private static byte[] Doing(bool clear, params (ushort quest, byte status, bool tracked)[] quests)
    {
        var p = new byte[6 + 32 * quests.Length];
        BitConverter.GetBytes(7u).CopyTo(p, 0);
        p[4] = (byte)(clear ? 1 : 0);
        p[5] = (byte)quests.Length;
        for (var i = 0; i < quests.Length; i++)
        {
            BitConverter.GetBytes(quests[i].quest).CopyTo(p, 6 + 32 * i);
            p[6 + 32 * i + 2] = quests[i].status;
            p[6 + 32 * i + 0x1D] = (byte)(0x01 | (quests[i].tracked ? 0x80 : 0));   // End_Location set too
        }
        return p;
    }

    private static ushort[] Slots(ReadOnlyMemory<byte> p)
        => Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt16(p.Span.Slice(2 * i, 2))).ToArray();


    [Fact]
    public void Take_tracked_keeps_five_and_clears_the_bit()
    {
        var p = Doing(true, (1, 6, true), (2, 6, true), (3, 6, true), (4, 6, true), (5, 6, true), (6, 6, true));

        QuestTracker.TakeTracked(p, new()).ShouldBe(new ushort[] { 1, 2, 3, 4, 5 });
        Enumerable.Range(0, 6).All(i => p[6 + 32 * i + 0x1D] == 0x01).ShouldBeTrue();
    }
}
