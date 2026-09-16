using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;

namespace Bridge2026;

/// <summary>
/// One 2026 client talking to a 2016 server.
///
/// The login stage is the awkward part. The 2026 client opens with a version key, a challenge answer and an
/// OTP exchange that the 2016 Login has no notion of, so those are answered here from captured bytes rather
/// than relayed. Everything the 2016 server does understand is translated in both directions.
///
/// Two rules keep this from breaking the server:
///   * Never relay an opcode the 2016 build does not define. It answers an unknown opcode by closing the
///     connection, which reaches the player as a disconnect with no explanation.
///   * Some opcodes exist in both builds with different meanings and directions. 0x0c24 is the 2026
///     "Previous" button and a SERVER->client ack in 2016; relaying it hangs up the world manager. Those are
///     listed explicitly, because an opcode filter cannot catch them.
///
/// Frames in the login stage are matched by payload SIZE where the opcode moved between the German and US
/// builds, because the sizes are stable across that renumbering and the numbering is not.
/// </summary>
internal sealed class Bridge2026Session : IPluginSession
{
    private readonly Bridge2026Plugin _plugin;
    private readonly PluginSessionInfo _info;
    private readonly bool _isLoginStage;

    /// <summary>+2 for the US build, 0 for the German one. Measured off the first client frame.</summary>
    private int _shift;
    private bool _shiftKnown;

    private byte _world;
    private readonly Dictionary<byte, uint> _avatars = new();   // slot -> chrregnum
    private readonly HashSet<byte> _usedSlots = new();

    public Bridge2026Session(Bridge2026Plugin plugin, PluginSessionInfo info)
    {
        _plugin = plugin;
        _info = info;
        _isLoginStage = info.ListenPort == plugin.LoginPort;
    }

    private int UsExtra => _shift != 0 ? 1 : 0;
    private ushort U(ushort op) => Op.U(op, _shift);

    public void OnClientPacket(PluginPacketContext ctx)
    {
        var p = ctx.Packet;
        var payload = p.Payload.ToArray();

        // The first client frame of a login IS the version key, so the build's numbering is measured
        // from it rather than configured. A blanket shift over the whole department breaks the login.
        if (_isLoginStage && !_shiftKnown && p.Opcode >= 0x0c00 && p.Opcode < 0x1000 && payload.Length == 32)
        {
            _shift = p.Opcode - Op.C26Version;
            _shiftKnown = true;
            _plugin.Log($"[{_info.ServiceName}] version opcode 0x{p.Opcode:X4}, {(_shift == 0 ? "German" : "US")} numbering ({_shift:+0;-0;0})");
        }

        if (p.Opcode == U(Op.C26Version))
        {
            ctx.Drop();
            ctx.ToServer(Op.Version16, T.VersionKey2016);
            return;
        }

        if (p.Opcode == U(Op.C26Login))
        {
            var login = T.Login2026To2016(payload);
            ctx.Drop();
            ctx.ToServer(Op.Login16, login ?? payload);
            if (login is null)
                _plugin.Log($"[{_info.ServiceName}] login is {payload.Length} B, not the 349-byte 2026 layout: passed through");
            var xtrap = new byte[1 + T.XtrapKey2016.Length + 1];
            xtrap[0] = (byte)T.XtrapKey2016.Length;
            T.XtrapKey2016.CopyTo(xtrap, 1);
            ctx.ToServer(Op.XtrapReq16, xtrap);
            return;
        }

        // Answered here, never relayed: the 2016 server has no equivalent and hangs up on the opcode.
        if (p.Opcode == Op.C26Back)
        {
            ctx.Drop();
            ctx.ToClient(Op.C26BackAck, T.BackAck2026);
            return;
        }
        if (p.Opcode == Op.C26CreateOpen)
        {
            byte free = 0;
            while (_usedSlots.Contains(free) && free < 64) free++;
            var ack = new byte[6];
            T.CreateOpenHead.CopyTo(ack, 0);
            ack[2] = free;
            ctx.Drop();
            ctx.ToClient(Op.C26CreateOpenAck, ack);
            return;
        }
        if (p.Opcode is Op.C26MidLogin or Op.C26PostCreate)
        {
            // 2026-only in this direction; NC_USER_NORMALLOGOUT_CMD and a LOGIN-server opcode in 2016.
            ctx.Drop();
            return;
        }

        if (_isLoginStage)
        {
            // Sizes survive the renumbering where opcodes do not.
            switch (payload.Length)
            {
                case 8:                                   // will-select: nothing to relay
                    ctx.Drop();
                    return;
                case 65:                                  // challenge answer, not verified here
                    ctx.Drop();
                    ctx.ToClient(U(Op.C26Otp), T.Otp2026);
                    return;
                case 1 when p.Opcode == U(Op.C26WorldSelect):
                    _world = payload[0];
                    ctx.Drop();
                    ctx.ToServer(Op.WorldSelect16, new[] { _world });
                    return;
            }
        }

        if (p.Opcode == U(Op.C26WmLogin) && !_isLoginStage)
        {
            var wm = T.WmLogin2026To2016(payload);
            if (wm is not null) { ctx.Drop(); ctx.ToServer(Op.WmLogin16, wm); }
            return;
        }

        if (p.Opcode == Op.AvatarCreateReq && T.CreateReq2026To2016(payload) is { } cr)
        {
            ctx.Replace(cr);
            return;
        }

        if (p.Opcode == Op.CharLoginReq && payload.Length == 1)
        {
            _avatars.TryGetValue(payload[0], out var reg);
            _plugin.Log($"[{_info.ServiceName}] character slot {payload[0]} selected (chrregnum {reg})");
            return;                                       // forwarded unchanged
        }

        if (p.Opcode == Op.MapLoginReq)
        {
            var sums = _plugin.Checksums;
            if (sums.Count == 0)
            {
                _plugin.Warn($"[{_info.ServiceName}] MAP_LOGIN_REQ passed through untranslated: no checksum file configured, the zone will refuse it");
                return;
            }
            if (T.MapLogin2026To2016(payload, sums) is { } ml) ctx.Replace(ml);
            return;
        }

        // Anything the 2016 build has no opcode for would make the server hang up.
        if (!_plugin.IsKnownTo2016(p.Opcode))
        {
            _plugin.Log($"[{_info.ServiceName}] dropped 0x{p.Opcode:X4} ({payload.Length} B): no such opcode in the 2016 build");
            ctx.Drop();
        }
    }

    public void OnServerPacket(PluginPacketContext ctx)
    {
        var p = ctx.Packet;
        var payload = p.Payload.ToArray();

        switch (p.Opcode)
        {
            case Op.VersionAck16:
                ctx.Drop();
                ctx.ToClient(U(Op.C26VersionAck), new byte[] { 0xf6 });
                return;

            case Op.XtrapAck16:
                ctx.Drop();                               // the bridge asked; the client never knows
                return;

            case Op.LoginFail16:
            {
                var err = payload.Length >= 2 ? payload[0] | (payload[1] << 8) : -1;
                _plugin.Warn($"[{_info.ServiceName}] the 2016 Login REFUSED the account: err={err} (0x{err:X4}). " +
                             "This is the server rejecting the credentials, not the bridge.");
                return;                                   // relayed so the client stops waiting
            }

            case Op.LoginAck16 when _isLoginStage:
            {
                ctx.Drop();
                ctx.ToClient(U(Op.C26Ack1), new byte[] { 1 });
                ctx.ToClient(U(Op.C26Ack2), new byte[] { 1 });
                if (T.WorldList2016To2026(payload, _plugin.WorldStatusOverride) is { } wl)
                    ctx.ToClient(U(Op.C26WorldList), wl);
                ctx.ToClient(U(Op.C26Challenge), T.Challenge2026);
                return;
            }

            case Op.WorldSelectAck16:
            {
                if (payload.Length < 19) return;
                var port = (ushort)(payload[17] | (payload[18] << 8));
                var advertised = _plugin.EndpointFor(port);
                var patched = (byte[])payload.Clone();
                patched[17] = (byte)(advertised.Port & 0xFF);
                patched[18] = (byte)(advertised.Port >> 8);
                if (T.WorldSelectAck2016To2026(patched, advertised.Host, _world) is { } ws)
                {
                    ctx.Drop();
                    ctx.ToClient(U(Op.C26WorldSelectAck), ws);
                }
                return;
            }

            case Op.WmAvatars16:
            {
                for (var i = 0; i < (payload.Length >= 3 ? payload[2] : 0); i++)
                {
                    var at = 3 + T.Avatar2016 * i;
                    if (at + T.Avatar2016 > payload.Length) break;
                    var slot = payload[at + 26];
                    _usedSlots.Add(slot);
                    _avatars[slot] = BitConverter.ToUInt32(payload, at);
                }
                if (T.Avatars2016To2026(payload, _plugin.AdvertiseHost) is { } av)
                {
                    ctx.Drop();
                    ctx.ToClient(U(Op.C26WmAvatars), av);
                }
                return;
            }

            case Op.AvatarCreateSucc when T.CreateSucc2016To2026(payload) is { } cs:
                ctx.Replace(cs);
                return;

            case Op.ClientBase when T.ClientBase2016To2026(payload, _shift != 0 ? T.ClientBaseUs : T.ClientBaseDe) is { } cb:
                ctx.Replace(cb);
                return;

            case Op.RegenMob:
                ctx.Replace(T.RegenMobRow2016To2026(payload, UsExtra));
                return;

            case Op.MobCmd when T.MobCmd2016To2026(payload, UsExtra) is { } mc:
                ctx.Replace(mc);
                return;

            case Op.RegenMover when T.RegenMover2016To2026(payload, UsExtra) is { } rm:
                ctx.Replace(rm);
                return;

            case Op.LoginCharacter when T.LoginCharacter2016To2026(payload, UsExtra) is { } lc:
                ctx.Replace(lc);
                return;

            case Op.ClientItem:
            {
                // Translate the RECORDS, not just the header. The 2026 client sizes each record from the
                // item's attribute class rather than the record's own size byte, so a 2016 box walks at the
                // wrong stride and only its first item appears.
                if (_plugin.HasItemClasses)
                {
                    var full = ItemAttr.ClientItem2016To2026(payload, _plugin.ClassOf, out var refusal);
                    if (full is not null) { ctx.Replace(full); return; }
                    _plugin.Warn($"[{_info.ServiceName}] inventory box {(payload.Length > 1 ? payload[1] : -1)} "
                                 + $"not translated: {refusal}. Its records will be misread past that point.");
                }
                // Header-only fallback: better than nothing for an empty box, and visibly wrong for a full
                // one, which is preferable to silently mangling it.
                if (T.ClientItem2016To2026(payload) is { } ci) ctx.Replace(ci);
                return;
            }

            case Op.ChargedBuff when T.ChargedBuff2016To2026(payload) is { } cbf:
                ctx.Replace(cbf);
                return;

            case Op.RewardInvenAck when _shift != 0 && T.RewardInven2016To2026(payload) is { } ri:
                ctx.Replace(ri);
                return;

            case Op.SwingDamage when T.Swing2016To2026(payload) is { } sw:
                ctx.Replace(sw);
                return;

            case Op.TargetInfo when T.TargetInfo2016To2026(payload) is { } ti:
                ctx.Replace(ti);
                return;

            case Op.CharLoginAck when payload.Length >= 18:
            {
                var zonePort = (ushort)(payload[16] | (payload[17] << 8));
                var ep = _plugin.EndpointFor(zonePort);
                var patched = (byte[])payload.Clone();
                T.Name4(ep.Host).CopyTo(patched, 0);
                patched[16] = (byte)(ep.Port & 0xFF);
                patched[17] = (byte)(ep.Port >> 8);
                ctx.Replace(patched);
                return;
            }

            case Op.LinkOther when payload.Length >= 28:
            {
                var zonePort = (ushort)(payload[26] | (payload[27] << 8));
                var ep = _plugin.EndpointFor(zonePort);
                var patched = (byte[])payload.Clone();
                T.Name4(ep.Host).CopyTo(patched, 10);
                patched[26] = (byte)(ep.Port & 0xFF);
                patched[27] = (byte)(ep.Port >> 8);
                ctx.Replace(patched);
                return;
            }

            case Op.MapLoginFail:
                _plugin.Warn($"[{_info.ServiceName}] MAP_LOGINFAIL_ACK {Convert.ToHexString(payload)}");
                return;
        }
    }

    public void Dispose() { }
}
