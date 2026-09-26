using System;
using System.Linq;
using Bridge2026;
using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// NC_SKILL_EMPOWALLOC_REQ 0x4811: the 2026 client sends {skill u16, plus 6 B, minus 6 B}; the 2016 zone reads
/// {skill u16, plus SKILL_EMPOWER u16, minus SKILL_EMPOWER u16} (4 nibbles: damage, sp, keeptime, cooltime) and, fed the
/// 2026 bytes, stored nothing (operator 2026-09-26: Magic Burst's empower gone after a relog).
/// </summary>
public class SkillEmpowerTests
{
    private static byte[] Req2026(ushort skill, byte[] plus, byte[] minus)
        => BitConverter.GetBytes(skill).Concat(plus).Concat(minus).ToArray();

    [Fact]
    public void Official_sample_lands_in_keeptime()
    {
        // live-20260919-201932: GreatSwing01 (200) plus 00 00 00 00 50 00; its login list then went 0x5005 -> 0x5505
        var t = T.SkillEmpowAlloc2026To2016(Req2026(200, [0, 0, 0, 0, 0x50, 0], new byte[6]))!;

        t.ShouldBe(new byte[] { 200, 0, 0x00, 0x05, 0, 0 });   // plus = 0x0500: keeptime 5
    }

    [Fact]
    public void Each_2016_field_comes_from_its_2026_slot_and_minus_too()
    {
        // slots 7..10 of the 48-bit block = damage, sp, keeptime, cooltime
        var plus = new byte[] { 0, 0, 0, 0x10, 0x32, 0x04 };   // slot 7 = 1, 8 = 2, 9 = 3, 10 = 4
        var minus = new byte[] { 0, 0, 0, 0x20, 0, 0 };         // slot 7 = 2
        var t = T.SkillEmpowAlloc2026To2016(Req2026(0x17FC, plus, minus))!;

        t.ShouldBe(new byte[] { 0xFC, 0x17, 0x21, 0x43, 0x02, 0x00 });
    }

    [Fact]
    public void Other_lengths_are_left_alone()
    {
        T.SkillEmpowAlloc2026To2016(new byte[6]).ShouldBeNull();
    }

    [Fact]
    public void The_session_rewrites_the_request_for_the_zone()
    {
        var s = new Bridge2026Session(new Bridge2026Plugin(),
            new PluginSessionInfo("Zone_0_4", 19028, "127.0.0.1", 9028, "10.0.0.2:50000", "10.0.0.1:19028"));
        var ctx = new PluginPacketContext(new FiestaPacket(Op.SkillEmpowAllocReq, Req2026(0x17FC, [0, 0, 0, 0, 0, 5], new byte[6])), fromClient: true);

        s.OnClientPacket(ctx);

        ctx.Forwarded!.Payload.ToArray().ShouldBe(new byte[] { 0xFC, 0x17, 0x00, 0x50, 0, 0 });   // cooltime 5
    }
}
