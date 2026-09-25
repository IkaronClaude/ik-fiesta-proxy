using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bridge2026;
using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The 2026 quest tracker the bridge keeps: 0x441F {quest} answered by 0x4420 {result, quest}, the tracked set sent as
/// 0x110F after the login quest list, finished quests dropping out, five slots.
/// </summary>
public class QuestTrackerTests
{
    private static Bridge2026Session ZoneSession(Bridge2026Plugin plugin)
        => new(plugin, new PluginSessionInfo("Zone_0_4", 19028, "127.0.0.1", 9028, "10.0.0.2:50000", "10.0.0.1:19028"));

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

    /// <summary>A 2016 quest DOING list: {chrregnum u32, needClear u8, count u8} + 32-byte entries.</summary>
    private static byte[] Doing(uint chr, params ushort[] quests)
    {
        var p = new byte[6 + 32 * quests.Length];
        BitConverter.GetBytes(chr).CopyTo(p, 0);
        p[4] = 1;
        p[5] = (byte)quests.Length;
        for (var i = 0; i < quests.Length; i++) BitConverter.GetBytes(quests[i]).CopyTo(p, 6 + 32 * i);
        return p;
    }

    private static byte[] Q(ushort quest) => BitConverter.GetBytes(quest);

    private static ushort[] Slots(byte[] p) => Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt16(p, 2 * i)).ToArray();

    [Fact]
    public void Track_request_is_answered_and_never_reaches_the_zone()
    {
        var s = ZoneSession(new Bridge2026Plugin());
        FromServer(s, Op.QuestDoing, Doing(7, 100, 200));

        var ctx = FromClient(s, Op.QuestTrackReq, Q(200));

        ctx.Forwarded.ShouldBeNull();
        ctx.ExtraToClient.Single().Opcode.ShouldBe(Op.QuestTrackAck);
        ctx.ExtraToClient.Single().Payload.ToArray().ShouldBe(new byte[] { 0xB0, 0x30, 200, 0 });
        FromClient(s, Op.QuestTrackReq, Q(200)).ExtraToClient.Single().Payload.ToArray().ShouldBe(new byte[] { 0xB5, 0x30, 200, 0 });
    }

    [Fact]
    public void Login_list_sends_the_tracked_set_without_finished_quests()
    {
        var plugin = new Bridge2026Plugin();
        var s = ZoneSession(plugin);
        FromServer(s, Op.QuestDoing, Doing(7, 100, 200));
        FromClient(s, Op.QuestTrackReq, Q(100));
        FromClient(s, Op.QuestTrackReq, Q(200));

        // relog: quest 100 was handed in meanwhile
        var ctx = FromServer(ZoneSession(plugin), Op.QuestDoing, Doing(7, 200, 300));

        var list = ctx.ExtraToClient.Single();
        list.Opcode.ShouldBe(Op.QuestTrackList);
        Slots(list.Payload.ToArray()).ShouldBe(new ushort[] { 200, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF });
    }

    [Fact]
    public void A_quest_accepted_this_session_counts_as_in_progress()
    {
        var s = ZoneSession(new Bridge2026Plugin());
        FromServer(s, Op.QuestDoing, Doing(7));

        FromClient(s, Op.QuestTrackReq, Q(500));
        FromClient(s, Op.QuestTrackReq, Q(501));

        new Bridge2026Plugin().Tracker.CharacterCount.ShouldBe(0);
        FromServer(s, Op.QuestDoing, Doing(7)).ExtraToClient.Single().Payload.ToArray().ShouldBe(
            QuestTracker.ListPayload(new List<ushort> { 500, 501 }));
    }

    [Fact]
    public void Sixth_quest_is_refused_and_a_finished_one_makes_room()
    {
        var t = new QuestTracker(null);
        var active = new HashSet<ushort> { 1, 2, 3, 4, 5, 6 };
        foreach (ushort q in new ushort[] { 1, 2, 3, 4, 5 }) t.Add(9, q, active).ShouldBe(QuestTracker.Tracked);

        t.Add(9, 6, active).ShouldBe(QuestTracker.Refused);
        active.Remove(3);
        t.Add(9, 6, active).ShouldBe(QuestTracker.Tracked);
        t.Get(9, active).ShouldBe(new ushort[] { 1, 2, 4, 5, 6 });
    }

    [Fact]
    public void Store_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qt-{Guid.NewGuid():N}.json");
        try
        {
            new QuestTracker(path).Add(42, 1234, null);
            new QuestTracker(path).Get(42, null).ShouldBe(new ushort[] { 1234 });
        }
        finally { File.Delete(path); }
    }
}
