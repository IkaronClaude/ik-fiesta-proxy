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
    private static Bridge2026Session ZoneSession()
        => new(new Bridge2026Plugin(),
               new PluginSessionInfo("Zone_0_4", 19028, "127.0.0.1", 9028, "10.0.0.2:50000", "10.0.0.1:19028"));

    private static PluginPacketContext FromClient(Bridge2026Session s, ushort opcode, params byte[] payload)
    {
        var ctx = new PluginPacketContext(new FiestaPacket(opcode, payload), fromClient: true);
        s.OnClientPacket(ctx);
        return ctx;
    }

    private static PluginPacketContext FromServer(Bridge2026Session s, ushort opcode, byte[] payload)
    {
        var ctx = new PluginPacketContext(new FiestaPacket(opcode, payload), fromClient: false);
        s.OnServerPacket(ctx);
        return ctx;
    }

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
    public void Track_and_untrack_requests_go_to_the_zone()
    {
        var s = ZoneSession();

        FromClient(s, Op.QuestTrackReq, 200, 0).Forwarded.ShouldNotBeNull();
        FromClient(s, Op.QuestUntrackReq, 200, 0).Forwarded.ShouldNotBeNull();
    }

    [Fact]
    public void Login_list_becomes_the_tracked_set_and_loses_the_bit()
    {
        var s = ZoneSession();
        var doing = Doing(true, (100, 6, true), (200, 6, false), (300, 8, true));

        var ctx = FromServer(s, Op.QuestDoing, doing);

        var list = ctx.ExtraToClient.Single();
        list.Opcode.ShouldBe(Op.QuestTrackList);
        Slots(list.Payload).ShouldBe(new ushort[] { 100, 300, 0xFFFF, 0xFFFF, 0xFFFF });
        var relayed = ctx.Forwarded!.Payload.ToArray();
        relayed.Length.ShouldBe(6 + 37 * 3);
        relayed.ShouldNotContain((byte)0x81);                          // End_Location kept, tracked bit gone
    }

    [Fact]
    public void A_second_list_packet_adds_to_the_first()
    {
        var s = ZoneSession();
        FromServer(s, Op.QuestDoing, Doing(true, (100, 6, true)));

        var ctx = FromServer(s, Op.QuestDoing, Doing(false, (400, 6, true)));

        Slots(ctx.ExtraToClient.Single().Payload).ShouldBe(new ushort[] { 100, 400, 0xFFFF, 0xFFFF, 0xFFFF });
    }

    [Fact]
    public void Take_tracked_keeps_five_and_clears_the_bit()
    {
        var p = Doing(true, (1, 6, true), (2, 6, true), (3, 6, true), (4, 6, true), (5, 6, true), (6, 6, true));

        QuestTracker.TakeTracked(p, new()).ShouldBe(new ushort[] { 1, 2, 3, 4, 5 });
        Enumerable.Range(0, 6).All(i => p[6 + 32 * i + 0x1D] == 0x01).ShouldBeTrue();
    }
}
