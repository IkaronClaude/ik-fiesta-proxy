using System.Linq;
using System.Text;
using Shouldly;
using Xunit;
using Bridge2026;

namespace Bridge2026.Tests;

/// <summary>
/// The zone checks the 2026 client's OWN table checksums (zone plugin client_checksums): the proxy forwards them, each in
/// its table's zone slot. Client list (Fiesta.exe 0xB6F504) = the zone's 49 minus MapLinkPoint/MapWayPoint (zone 24, 25),
/// then 6 tables the 2016 zone has no slot for.
/// </summary>
public class MapLoginChecksumTests
{
    private static byte[] Sum(int i) => Encoding.ASCII.GetBytes(i.ToString("x32"));   // 32 hex chars naming client slot i

    private static byte[] Login2026()
    {
        var head = Enumerable.Range(0, 22).Select(i => (byte)(0xA0 + i)).ToArray();
        return head.Concat(Enumerable.Range(0, 53).SelectMany(Sum)).ToArray();
    }

    [Fact]
    public void Client_checksums_land_in_their_zone_slots()
    {
        var m = T.MapLogin2026To2016Mapped(Login2026())!;
        m.Length.ShouldBe(1590);
        m.Take(22).ShouldBe(Login2026().Take(22));
        byte[] Slot(int z) => m.Skip(22 + 32 * z).Take(32).ToArray();
        Slot(0).ShouldBe(Sum(0));                       // Abstate
        Slot(23).ShouldBe(Sum(23));                     // ActionViewInfo
        Slot(24).ShouldBe(Enumerable.Repeat((byte)'0', 32).ToArray());   // MapLinkPoint: not in the 2026 list
        Slot(25).ShouldBe(Enumerable.Repeat((byte)'0', 32).ToArray());   // MapWayPoint
        Slot(26).ShouldBe(Sum(24));                     // AbStateView
        Slot(48).ShouldBe(Sum(46));                     // ChargedDeletableBuff; client 47..52 (quest tables) dropped
    }

    [Fact]
    public void A_frame_of_another_size_is_not_translated()
        => T.MapLogin2026To2016Mapped(new byte[1590]).ShouldBeNull();
}
