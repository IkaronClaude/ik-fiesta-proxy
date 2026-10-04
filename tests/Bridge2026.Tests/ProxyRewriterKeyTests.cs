using FiestaLibReloaded.Networking;
using FiestaProxy.Config;
using FiestaProxy.Rewrites;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The proxy's native address rewrites serve 2016 AND 2026 clients (no plugin since 2026-10-04). They are keyed on opcode
/// AND exact payload size, because opcodes are reused between the two client protocols: a frame of another size is
/// never touched.
/// </summary>
public class ProxyRewriterKeyTests
{
    private static readonly ProxyConfig Config = new()
    {
        Routes =
        [
            new ProxyRoute(19013, "WorldManager_0", "10.0.0.5", 9013),
            new ProxyRoute(19019, "Zone_0_1", "10.0.0.5", 9019),
        ],
        S2sRoutes = [], S2sAllowedCidrs = [], PublicIp = "192.168.1.50", XorTable = null,
        UpstreamConnectTimeout = TimeSpan.FromSeconds(1), PacketLogEnabled = false,
    };

    private static readonly PacketRewriterRegistry Registry = PacketRewriterRegistry.Default(Config);

    private static byte[] Endpoint(int size, int ipAt, int portAt, string ip, ushort port)
    {
        var p = new byte[size];
        for (var i = 0; i < size; i++) p[i] = (byte)(0xA0 + i % 16);          // recognisable filler
        var name = new byte[16];
        System.Text.Encoding.ASCII.GetBytes(ip).CopyTo(name, 0);
        name.CopyTo(p, ipAt);
        p[portAt] = (byte)(port & 0xFF);
        p[portAt + 1] = (byte)(port >> 8);
        return p;
    }

    private static (string Ip, ushort Port) Read(byte[] p, int ipAt, int portAt)
    {
        var name = p.AsSpan(ipAt, 16);
        var nul = name.IndexOf((byte)0);
        return (System.Text.Encoding.ASCII.GetString(name[..(nul < 0 ? 16 : nul)]), (ushort)(p[portAt] | (p[portAt + 1] << 8)));
    }

    [Fact]
    public void The_2016_world_select_ack_points_at_the_proxys_world_manager()
    {
        var p = Endpoint(83, 1, 17, "10.0.0.5", 9013);
        var r = Registry.Apply(new FiestaPacket(0x0C0C, p)).Payload.ToArray();
        Read(r, 1, 17).ShouldBe(("192.168.1.50", (ushort)19013));
        r[19..].ShouldBe(p[19..]);                                             // validate_new untouched
    }

    [Fact]
    public void The_2026_world_select_ack_is_rewritten_at_the_same_offsets_and_keeps_its_world_byte()
    {
        var p = Endpoint(84, 1, 17, "10.0.0.5", 9013);
        var r = Registry.Apply(new FiestaPacket(0x0C0B, p)).Payload.ToArray();
        Read(r, 1, 17).ShouldBe(("192.168.1.50", (ushort)19013));
        r[83].ShouldBe(p[83]);
    }

    [Theory]
    [InlineData(0x0C0B, 1)]      // 2016 WORLDSELECT_REQ {world}: same opcode, client->server shape
    [InlineData(0x0C0C, 84)]     // a 2016 opcode at the 2026 size
    [InlineData(0x0C0B, 83)]     // a 2026 opcode at the 2016 size
    [InlineData(0x1003, 20)]
    [InlineData(0x180A, 28)]
    public void A_known_opcode_at_another_size_is_left_alone(int opcode, int size)
    {
        var p = new byte[size];
        var pkt = new FiestaPacket((ushort)opcode, p);
        Registry.Apply(pkt).ShouldBeSameAs(pkt);
    }

    [Fact]
    public void Char_login_ack_points_at_the_proxys_zone_listener()
    {
        var r = Registry.Apply(new FiestaPacket(0x1003, Endpoint(18, 0, 16, "10.0.0.5", 9019))).Payload.ToArray();
        Read(r, 0, 16).ShouldBe(("192.168.1.50", (ushort)19019));
    }

    [Fact]
    public void A_map_link_to_another_zone_points_at_the_proxys_zone_listener()
    {
        // the 2016 capture's frame shape: 10 bytes, ip[16] @10, port @26, 2 bytes
        var p = Endpoint(30, 10, 26, "10.0.0.5", 9019);
        var r = Registry.Apply(new FiestaPacket(0x180A, p)).Payload.ToArray();
        Read(r, 10, 26).ShouldBe(("192.168.1.50", (ushort)19019));
        r[..10].ShouldBe(p[..10]);
        r[28..].ShouldBe(p[28..]);
    }
}
