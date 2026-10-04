using System;
using System.IO;
using System.Linq;
using FiestaProxy.Config;
using Shouldly;
using Xunit;

namespace Bridge2026.Tests;

/// <summary>
/// FiestaProxy.conf: the routes come from the server's own ServerInfo.txt (its client-facing SERVER_INFO rows), the rest
/// from directives - so a registered service (no environment) runs from the files of the server tree it fronts.
/// </summary>
public class ConfFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confdir-" + Guid.NewGuid().ToString("N"));

    // the stock layout: one client-facing (kind 20) row per service plus the internal ones, which must be ignored
    private const string ServerInfo = """
        #DEFINE SERVER_INFO
        #ENDDEFINE
        SERVER_INFO  "PG_Login",     4, 0, 0,20,  "127.0.0.1",  	9010,  100,   200  ; 	PUBLIC_IP
        SERVER_INFO  "PG_Login",     4, 0, 0, 5,  "127.0.0.1",  	9011,  100,    50  ; 	LOCALHOST
        SERVER_INFO  "PG_W00_WM",    5, 0, 0,20,  "127.0.0.1",		9013,  100,  1500  ; 	PUBLIC_IP
        SERVER_INFO  "PG_W00_WM",    5, 0, 0, 6,  "127.0.0.1",  	9014,  100,   100  ; 	LOCALHOST
        SERVER_INFO  "PG_W00_Z00",   6, 0, 0,20,  "127.0.0.1",  	9016,  100,  1500  ; 	PUBLIC_IP
        SERVER_INFO  "PG_W00_Z03",   6, 0, 3,20,  "127.0.0.1",  	9025,  100,  1500  ; 	PUBLIC_IP
        ; SERVER_INFO  "PG_W00_Z04",   6, 0, 4,20,  "127.0.0.1",  	9028,  100,  1500  ; commented out
        SERVER_INFO  "PG_AccDB",     0, 0, 0, 0,  "127.0.0.1",  	9031,  100,   100  ; 	LOCALHOST
        #END
        """;

    public ConfFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ServerSource", "9Data", "ServerInfo"));
        Directory.CreateDirectory(Path.Combine(_dir, "Bridge"));
        File.WriteAllText(Path.Combine(_dir, "ServerSource", "9Data", "ServerInfo", "ServerInfo.txt"), ServerInfo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Conf(string body)
    {
        var p = Path.Combine(_dir, "Bridge", "FiestaProxy.conf");
        File.WriteAllText(p, body);
        return p;
    }

    private static string Get(System.Collections.Generic.List<(string Name, string Value)> v, string name) =>
        v.Single(x => x.Name == name).Value;

    [Fact]
    public void Routes_come_from_the_client_facing_rows_of_the_included_ServerInfo()
    {
        var v = ConfFile.Parse(Conf("""
            #include "..\ServerSource\9Data\ServerInfo\ServerInfo.txt"   ; the server
            ADVERTISE_IP 192.168.1.10
            """));
        Get(v, "PROXY_ROUTES").ShouldBe(
            "19010:Login:127.0.0.1:9010:rewrite;19013:WorldManager_0:127.0.0.1:9013:rewrite;" +
            "19016:Zone_0_0:127.0.0.1:9016:rewrite;19025:Zone_0_3:127.0.0.1:9025:rewrite");
        Get(v, "PUBLIC_IP").ShouldBe("192.168.1.10");
    }

    [Fact]
    public void Offset_listen_override_upstream_and_mode_apply()
    {
        var v = ConfFile.Parse(Conf("""
            #include ..\ServerSource\9Data\ServerInfo\ServerInfo.txt
            PORT_OFFSET 20000
            LISTEN Zone_0_3 9999
            UPSTREAM_HOST 10.0.0.5
            MODE rewrite
            """));
        Get(v, "PROXY_ROUTES").ShouldBe(
            "29010:Login:10.0.0.5:9010:rewrite;29013:WorldManager_0:10.0.0.5:9013:rewrite;" +
            "29016:Zone_0_0:10.0.0.5:9016:rewrite;9999:Zone_0_3:10.0.0.5:9025:rewrite");
    }

    [Fact]
    public void Set_expands_variables_and_Path_resolves_against_the_conf_folder()
    {
        var v = ConfFile.Parse(Conf("""
            #include "..\ServerSource\9Data\ServerInfo\ServerInfo.txt"
            ADVERTISE_IP 10.1.2.3
            SET  FIESTAPROXY_PLUGIN_BRIDGE2026_LOGIN_PORT ${LISTEN_Login}
            SET  FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE  ${ADVERTISE_IP}
            SET  FIESTAPROXY_PLUGIN_BRIDGE2026_PORT_OFFSET ${PORT_OFFSET}
            PATH XOR_TABLE_PATH xor-table.hex
            """));
        Get(v, "FIESTAPROXY_PLUGIN_BRIDGE2026_LOGIN_PORT").ShouldBe("19010");
        Get(v, "FIESTAPROXY_PLUGIN_BRIDGE2026_ADVERTISE").ShouldBe("10.1.2.3");
        Get(v, "FIESTAPROXY_PLUGIN_BRIDGE2026_PORT_OFFSET").ShouldBe("10000");
        Get(v, "XOR_TABLE_PATH").ShouldBe(Path.Combine(_dir, "Bridge", "xor-table.hex"));
    }

    [Fact]
    public void Mistakes_fail_loudly()
    {
        Should.Throw<InvalidOperationException>(() => ConfFile.Parse(Conf("#include missing.txt")))
              .Message.ShouldContain("not found");
        Should.Throw<InvalidOperationException>(() => ConfFile.Parse(Conf("ADVERTIZE_IP 1.2.3.4")))
              .Message.ShouldContain("unknown directive");
        Should.Throw<InvalidOperationException>(() => ConfFile.Parse(Conf("SET X ${NOPE}")))
              .Message.ShouldContain("not defined");
        Should.Throw<InvalidOperationException>(() => ConfFile.Parse(Conf(
            "#include ..\\ServerSource\\9Data\\ServerInfo\\ServerInfo.txt\nLISTEN Zone_0_9 1234")))
              .Message.ShouldContain("does not have");
    }
}
