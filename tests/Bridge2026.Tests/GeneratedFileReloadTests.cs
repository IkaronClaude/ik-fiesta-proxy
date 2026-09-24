using System.Text;
using Bridge2026;
using FiestaProxy.Config;
using FiestaProxy.Plugins;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// The files tools/bridge_data.py generates are re-read when it rewrites them: the bridge must never keep the zone
/// checksums of tables the zones no longer run (every login then fails "Client has been illegally manipulated").
/// </summary>
public class GeneratedFileReloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bridge-reload-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Sums(char digit, int count = 49)
        => string.Join('\n', Enumerable.Repeat(new string(digit, 32), count)) + '\n';

    private Bridge2026Plugin Start()
    {
        var config = new ProxyConfig
        {
            Routes = [], S2sRoutes = [], S2sAllowedCidrs = [], PublicIp = "127.0.0.1", XorTable = null,
            UpstreamConnectTimeout = TimeSpan.FromSeconds(1), PacketLogEnabled = false,
        };
        var settings = new Dictionary<string, string>
        {
            ["CHECKSUMS"] = Path.Combine(_dir, "zone-checksums.txt"),
            ["ITEM_CLASSES"] = Path.Combine(_dir, "item-classes.txt"),
        };
        var plugin = new Bridge2026Plugin();
        plugin.Initialise(new PluginHostContext(config, _dir, settings));
        return plugin;
    }

    private static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(100);
    }

    [Fact]
    public void Rewritten_checksums_are_picked_up_without_a_restart()
    {
        File.WriteAllText(Path.Combine(_dir, "zone-checksums.txt"), Sums('a'));
        File.WriteAllText(Path.Combine(_dir, "item-classes.txt"), "1 2\n");
        var plugin = Start();
        Encoding.ASCII.GetString(plugin.Checksums[0]).ShouldBe(new string('a', 32));

        File.WriteAllText(Path.Combine(_dir, "zone-checksums.txt"), Sums('b'));

        WaitFor(() => Encoding.ASCII.GetString(plugin.Checksums[0]) == new string('b', 32));
        Encoding.ASCII.GetString(plugin.Checksums[0]).ShouldBe(new string('b', 32));
        plugin.Checksums.Count.ShouldBe(49);
    }

    [Fact]
    public void A_half_written_checksum_file_does_not_replace_a_complete_list()
    {
        File.WriteAllText(Path.Combine(_dir, "zone-checksums.txt"), Sums('a'));
        File.WriteAllText(Path.Combine(_dir, "item-classes.txt"), "1 2\n");
        var plugin = Start();

        File.WriteAllText(Path.Combine(_dir, "zone-checksums.txt"), Sums('c', 10));
        Thread.Sleep(2500);                                   // past the 1 s debounce

        plugin.Checksums.Count.ShouldBe(49);
        Encoding.ASCII.GetString(plugin.Checksums[0]).ShouldBe(new string('a', 32));
    }
}
