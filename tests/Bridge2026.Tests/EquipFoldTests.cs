using System.Linq;
using Bridge2026;
using FiestaLibReloaded.Networking;
using FiestaProxy.Config;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// Q29: the 2026 client draws an item at its own 2026 Equip, the server names its folded 2016 slot. Unequipping
/// Wings of Darkness (2026 slot 34, server slot 9) must also clear slot 34 on the client, or the wings stay drawn.
/// </summary>
public class EquipFoldTests : IDisposable
{
    private const ushort Wings = 12858;              // DevilKingWings01_P: 2026 Equip 34, server Equip 9
    private readonly string _dir = Directory.CreateTempSubdirectory("bridge-fold-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private Bridge2026Session Session()
    {
        File.WriteAllText(Path.Combine(_dir, "item-classes.txt"), $"{Wings} 10\n");
        File.WriteAllText(Path.Combine(_dir, "equip-fold.txt"),
            "# test\nfold 34 9\nfold 35 8\nfold 42 13\nfold 43 13\n" + $"item {Wings} 34\n");
        var config = new ProxyConfig
        {
            Routes = [], S2sRoutes = [], S2sAllowedCidrs = [], PublicIp = "127.0.0.1", XorTable = null,
            UpstreamConnectTimeout = TimeSpan.FromSeconds(1), PacketLogEnabled = false,
        };
        var plugin = new Bridge2026Plugin();
        plugin.Initialise(new PluginHostContext(config, _dir, new Dictionary<string, string>
        {
            ["ITEM_CLASSES"] = Path.Combine(_dir, "item-classes.txt"),
            ["EQUIP_FOLD"] = Path.Combine(_dir, "equip-fold.txt"),
        }));
        return new Bridge2026Session(plugin,
            new PluginSessionInfo("Zone_0_4", 19028, "127.0.0.1", 9028, "10.0.0.2:50000", "10.0.0.1:19028"));
    }

    private static PluginPacketContext FromServer(Bridge2026Session s, params byte[] payload)
    {
        var ctx = new PluginPacketContext(new FiestaPacket(Op.ItemEquipChange, payload), fromClient: false);
        s.OnServerPacket(ctx);
        return ctx;
    }

    private static byte[] Unequip(byte slot) => [0x02, 0x24, slot, 0xFF, 0xFF];

    private static byte[] Equip(byte slot, ushort item) => [0x02, 0x24, slot, (byte)item, (byte)(item >> 8), 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Unequipping_the_folded_slot_also_clears_the_2026_slot_the_item_is_drawn_at()
    {
        var s = Session();

        var ctx = FromServer(s, Unequip(9));

        ctx.Forwarded.ShouldNotBeNull();
        var extra = ctx.ExtraToClient.Single();
        extra.Opcode.ShouldBe(Op.ItemEquipChange);
        extra.Payload.ToArray().ShouldBe(new byte[] { 0x02, 0x24, 34, 0xFF, 0xFF });
    }

    [Fact]
    public void A_slot_already_cleared_is_not_cleared_again_until_something_is_drawn_there()
    {
        var s = Session();
        FromServer(s, Unequip(9));

        FromServer(s, Unequip(9)).ExtraToClient.ShouldBeEmpty();
        FromServer(s, Equip(9, Wings)).ExtraToClient.ShouldBeEmpty();        // drawn at 34: 34 must not be cleared
        FromServer(s, Unequip(9)).ExtraToClient.Single().Payload.ToArray()[2].ShouldBe((byte)34);
    }

    [Fact]
    public void Equipping_a_plain_item_into_the_slot_clears_a_folded_item_still_drawn()
    {
        var s = Session();

        var ctx = FromServer(s, Equip(9, 4000));                             // an item drawn at 9 itself

        ctx.ExtraToClient.Single().Payload.ToArray()[2].ShouldBe((byte)34);
    }

    [Fact]
    public void Both_2026_slots_folded_into_one_server_slot_are_cleared()
    {
        var s = Session();

        var ctx = FromServer(s, Unequip(13));

        ctx.ExtraToClient.Select(p => p.Payload.ToArray()[2]).ShouldBe(new byte[] { 42, 43 });
    }

    [Fact]
    public void A_slot_nothing_folds_into_gets_no_extra_packet()
    {
        var s = Session();

        FromServer(s, Unequip(5)).ExtraToClient.ShouldBeEmpty();
    }
}
