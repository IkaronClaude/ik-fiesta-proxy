using Bridge2026;
using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The zone-session rules around the quest dialog: a page ack earns the client a 0x442E, and ONLY the
/// ENDOFTRADE that echoes that close is swallowed. The first live run swallowed every ENDOFTRADE of the
/// session (a long.MinValue sentinel overflowing), so a shop could never tell the server it had closed.
/// </summary>
public class QuestDialogCloseTests
{
    private static Bridge2026Session ZoneSession()
    {
        var plugin = new Bridge2026Plugin();
        return new Bridge2026Session(plugin,
            new PluginSessionInfo("Zone_0_4", 19028, "127.0.0.1", 9028, "10.0.0.2:50000", "10.0.0.1:19028"));
    }

    private static PluginPacketContext FromClient(Bridge2026Session s, ushort opcode, params byte[] payload)
    {
        var ctx = new PluginPacketContext(new FiestaPacket(opcode, payload), fromClient: true);
        s.OnClientPacket(ctx);
        return ctx;
    }

    [Fact]
    public void EndOfTrade_with_no_close_outstanding_is_relayed()
    {
        var s = ZoneSession();

        var ctx = FromClient(s, Op.ActEndOfTrade);

        ctx.Forwarded.ShouldNotBeNull();
    }

    [Fact]
    public void A_page_ack_is_relayed_and_the_client_is_told_to_close_its_dialog()
    {
        var s = ZoneSession();

        var ctx = FromClient(s, Op.QuestScriptCmdAck, 0x9F, 0x4E, 0x02, 0x01, 0x00, 0x00, 0x00);

        ctx.Forwarded.ShouldNotBeNull();
        ctx.ExtraToClient.Count.ShouldBe(1);
        ctx.ExtraToClient[0].Opcode.ShouldBe(Op.QuestCloseDialog);
    }

    [Fact]
    public void Only_the_EndOfTrade_echoing_our_close_is_swallowed()
    {
        var s = ZoneSession();
        FromClient(s, Op.QuestScriptCmdAck, 0x9F, 0x4E, 0x02, 0x01, 0x00, 0x00, 0x00);

        FromClient(s, Op.ActEndOfTrade).Forwarded.ShouldBeNull();        // the echo
        FromClient(s, Op.ActEndOfTrade).Forwarded.ShouldNotBeNull();     // a genuine one straight after
    }

    [Fact]
    public void The_441F_side_effect_of_ACCEPT_is_dropped_and_answers_nothing()
    {
        var s = ZoneSession();

        var ctx = FromClient(s, Op.QuestJobDungeonFindRng, 0x9F, 0x4E);

        ctx.Forwarded.ShouldBeNull();
        ctx.ExtraToServer.ShouldBeEmpty();
        ctx.ExtraToClient.ShouldBeEmpty();
    }
}
