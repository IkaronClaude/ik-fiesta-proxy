using System.Linq;
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
    private static Bridge2026Session ZoneSession(Bridge2026Plugin? shared = null)
    {
        var plugin = shared ?? new Bridge2026Plugin();
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

    private static PluginPacketContext FromServer(Bridge2026Session s, ushort opcode, params byte[] payload)
    {
        var ctx = new PluginPacketContext(new FiestaPacket(opcode, payload), fromClient: false);
        s.OnServerPacket(ctx);
        return ctx;
    }

    private static byte[] ScriptCmd(ushort quest, uint command)
    {
        var b = new byte[103];
        BitConverter.TryWriteBytes(b.AsSpan(0), quest);
        BitConverter.TryWriteBytes(b.AsSpan(2), command);
        return b;
    }

    [Fact]
    public void A_zone_that_announces_script_END_stops_getting_a_close_per_ack()
    {
        var plugin = new Bridge2026Plugin();
        var s = ZoneSession(plugin);

        // END itself is replaced by 0x442E (d0637e2): the 2026 client restores its HUD only on 442E
        var end = FromServer(s, Op.QuestScriptCmdReq, ScriptCmd(20135, Op.QscEnd));
        end.Forwarded.ShouldBeNull();
        end.ExtraToClient.Count.ShouldBe(1);
        end.ExtraToClient[0].Opcode.ShouldBe(Op.QuestCloseDialog);

        var ack = FromClient(s, Op.QuestScriptCmdAck, 0xA7, 0x4E, 0x02, 0x01, 0x00, 0x00, 0x00);
        ack.Forwarded.ShouldNotBeNull();
        ack.ExtraToClient.ShouldBeEmpty();                                 // no 442E: no flicker between pages

        // learned per zone, and shared by every later session to that zone
        FromClient(ZoneSession(plugin), Op.QuestScriptCmdAck, 0xA7, 0x4E, 0x02, 0x01, 0x00, 0x00, 0x00)
            .ExtraToClient.ShouldBeEmpty();
    }

    [Fact]
    public void A_dialogue_page_is_not_mistaken_for_an_END()
    {
        var plugin = new Bridge2026Plugin();
        var s = ZoneSession(plugin);

        FromServer(s, Op.QuestScriptCmdReq, ScriptCmd(20135, 2));           // QSC_SAY

        FromClient(s, Op.QuestScriptCmdAck, 0xA7, 0x4E, 0x02, 0x01, 0x00, 0x00, 0x00)
            .ExtraToClient.Count.ShouldBe(1);                              // still closing per ack
    }

    [Fact]
    public void The_2026_logout_is_renumbered_to_the_2016_one_not_relayed_as_a_server_opcode()
    {
        var s = ZoneSession();

        var ctx = FromClient(s, Op.C26NormalLogout, 0x01);             // 01 = back to character select

        ctx.Forwarded.ShouldNotBeNull();
        ctx.Forwarded!.Opcode.ShouldBe(Op.NormalLogout16);
        ctx.Forwarded.Payload.ToArray().ShouldBe(new byte[] { 0x01 });
    }

    [Fact]
    public void The_2026_avatar_list_request_is_renumbered_on_the_world_manager_link_only()
    {
        var plugin = new Bridge2026Plugin();
        var wm = new Bridge2026Session(plugin,
            new PluginSessionInfo("WorldManager_0", 19013, "127.0.0.1", 9013, "10.0.0.2:50001", "10.0.0.1:19013"));

        var onWm = FromClient(wm, Op.C26AvatarListReq, 0x3F);
        onWm.Forwarded!.Opcode.ShouldBe(Op.AvatarListReq16);

        // on a zone link 0x0C1A is not ours to reinterpret
        var onZone = FromClient(ZoneSession(plugin), Op.C26AvatarListReq, 0x3F);
        (onZone.Forwarded?.Opcode ?? 0).ShouldNotBe(Op.AvatarListReq16);
    }

    // ---- "select server": the 2016 servers' own OTP handover, renumbered - the bridge holds nothing ----

    private static Bridge2026Session LoginSession(Bridge2026Plugin plugin)
        => new(plugin, new PluginSessionInfo("Login", plugin.LoginPort, "127.0.0.1", 9010, "10.0.0.2:50000", "10.0.0.1:19010"));

    private static Bridge2026Session WmSession(Bridge2026Plugin plugin)
        => new(plugin, new PluginSessionInfo("WorldManager_0", 19013, "127.0.0.1", 9013, "10.0.0.2:50001", "10.0.0.1:19013"));

    [Fact]
    public void Select_server_is_relayed_to_the_world_manager_as_WILL_WORLD_SELECT()
    {
        var ctx = FromClient(WmSession(new Bridge2026Plugin()), Op.C26Back);

        ctx.Forwarded!.Opcode.ShouldBe(Op.WillWorldSelectReq16);
        ctx.ExtraToClient.ShouldBeEmpty();                                  // the SERVER answers, not the bridge
    }

    [Fact]
    public void The_world_managers_OTP_reaches_the_client_under_the_2026_number_untouched()
    {
        var ack = new byte[34];
        ack[0] = 0x58; ack[1] = 0x1E;
        System.Text.Encoding.ASCII.GetBytes("5bb179a4586d21175a93dc67941e49bc").CopyTo(ack, 2);

        var ctx = FromServer(WmSession(new Bridge2026Plugin()), Op.WillWorldSelectAck16, ack);

        ctx.Forwarded.ShouldBeNull();                                       // 0x0C34 means something else to 2026
        var sent = ctx.ExtraToClient.Single();
        sent.Opcode.ShouldBe(Op.C26BackAck);
        sent.Payload.ToArray().ShouldBe(ack);
    }

    [Fact]
    public void A_login_carrying_an_OTP_becomes_LOGIN_WITH_OTP_and_nothing_else()
    {
        var login = new byte[349];
        System.Text.Encoding.ASCII.GetBytes("5bb179a4586d21175a93dc67941e49bc").CopyTo(login, 0);

        var ctx = FromClient(LoginSession(new Bridge2026Plugin()), Op.C26Login, login);

        ctx.Forwarded.ShouldBeNull();
        var sent = ctx.ExtraToServer.Single();                              // no credentials login, no xtrap
        sent.Opcode.ShouldBe(Op.LoginWithOtp16);
        System.Text.Encoding.ASCII.GetString(sent.Payload.ToArray()).ShouldBe("5bb179a4586d21175a93dc67941e49bc");
    }

    [Fact]
    public void An_ordinary_login_is_still_an_ordinary_login()
    {
        var login = new byte[349];
        System.Text.Encoding.ASCII.GetBytes("test2026").CopyTo(login, 32);

        var ctx = FromClient(LoginSession(new Bridge2026Plugin()), Op.C26Login, login);

        ctx.ExtraToServer.Select(x => x.Opcode).ShouldBe(new[] { Op.Login16, Op.XtrapReq16 });
    }
}
