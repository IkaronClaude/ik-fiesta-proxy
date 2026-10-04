using FiestaLibReloaded.Networking;
using FiestaProxy.Plugins;
using System.Buffers.Binary;

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

    // (the 0x442E dialog-close state moved to the zone with bridge26 batch 3)

    private byte _world;
    private readonly Dictionary<byte, uint> _avatars = new();   // slot -> chrregnum
    private readonly HashSet<byte> _usedSlots = new();


    public Bridge2026Session(Bridge2026Plugin plugin, PluginSessionInfo info)
    {
        _plugin = plugin;
        _info = info;
        _isLoginStage = info.ListenPort == plugin.LoginPort;
        // A login connection measures the numbering for itself off the version key; every other connection
        // inherits what the last login measured, because it never sees that frame.
        if (!_isLoginStage)
        {
            _shift = plugin.LastShift;
            if (_shift == 0)
                plugin.Warn($"[{info.ServiceName}] no login has measured the client build yet, so this link "
                            + "assumes the German one. Every width that differs between the builds will be "
                            + "wrong here, which for a US client means a crash on zone enter.");
        }
    }

    /// <summary>A zone link: the zone hook (bridge26) translates its requests itself - map login, the 2026 logout
    /// opcodes, and opcodes the 2016 zone has no handler for (dropped there, not here).</summary>
    private bool IsZoneLink => _info.ServiceName.StartsWith("Zone_", StringComparison.Ordinal);

    /// <summary>The world-manager link: the WM hook (wm_bridge26) translates it - per session, from the 2026 WM login
    /// opcode. The bridge only rewrites the zone address in CHAR_LOGIN_ACK, because it is the client's way in.</summary>
    private bool IsWmLink => _info.ServiceName.StartsWith("WorldManager", StringComparison.Ordinal);

    /// <summary>True for the US 10.6.4 build, whose structs are wider than the German build's.</summary>
    private bool IsUsBuild => _shift != 0;

    /// <summary>The extra byte every US briefinfo record carries.</summary>
    private int UsExtra => IsUsBuild ? 1 : 0;
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
            _plugin.LastShift = _shift;        // the WM and zone connections read this
            _plugin.Log($"[{_info.ServiceName}] version opcode 0x{p.Opcode:X4}, {(_shift == 0 ? "German" : "US")} numbering ({_shift:+0;-0;0})");
        }

        if (IsWmLink) return;                             // MOVED TO THE WM HOOK (wm_bridge26): every WM-link request

        // QUEST DIALOGUE. Everything here is read out of the two client binaries, not inferred from
        // captures - three capture-only readings of this exchange were wrong in a row.
        //   2016: Z:/ClientSource/Fiesta.bin + Fiesta.pdb        2026: Z:/ClientOfficialUS/Fiesta.exe
        //   (NOT the Fiesta.bin beside it: that is a stale August build with no 441F sender and no 442E
        //   handler, and reading it cost an hour.)
        //
        // On_NC_QUEST_SCRIPT_CMD_REQ is the same 13-way switch on STRUCT_QSC.Command in both builds
        // (2016 0x4BA970, 2026 0x5B4490): 1 END -> CloseWin(NpcDialogWin), 2 SAY -> OpenNpcQuestDialog +
        // ShowWin, 6 ACCEPT, 10 DONE. Neither build acks ACCEPT or DONE; only a SAY is acked, from the
        // dialog "quest_ack" command, and that path is unchanged too.
        //
        // WHAT CHANGED IS WHO CLOSES THE WINDOW. NpcDialogWin::DirectMessage, message 0x24:
        //     2016 0x5F5730:  if (keepOpen) keepOpen = 0;  else CloseWin(this);
        //     2026 0x72B000:  if (keepOpen) keepOpen = 0;  if (g_C319D5 == 1) CloseWin(this);
        // g_C319D5 is cleared by OpenNpcQuestDialog / OpenUserQuestDialog on every page and set by exactly
        // one quest packet: 0x442E, whose handler (0x5B4BE0) ignores its payload, sets the flag and calls
        // NpcDialogWin close (0x72B1B0) on the spot. So a 2016 client shuts its own dialog on a click and
        // lets the next SAY reopen it, while a 2026 client waits to be told. The 2016 server never tells
        // it - hence Next / Complete Quest "doing nothing", Esc working, and the quest having progressed.
        //
        // THE FIX reproduces the 2016 client by construction: every click is followed by a close. When
        // the client acks a page, forward the ack and hand the client a 442E. If the script has another
        // page, the SAY reopens the window exactly as it does for a 2016 client; if it has not, the
        // window is simply closed. No script knowledge, no last-page detection, no timer.
        // ...unless this zone has shown it announces the end of a script itself (see OnServerPacket): then
        // the window must stay open between pages, and its own QSC_END closes it after the last one.
        // MOVED TO THE ZONE (bridge26 batch 2): QUEST_REWARD_SELECT index -> slot, SKILL_EMPOWALLOC 14 -> 6 B, the tracker
        // requests' 2-byte guard (0x441F / 0x4421) and the 2026 map-status request 0x182E (answered 0x182F in the zone).

        // MOVED TO THE ZONE (bridge26 batch 3): the per-ack 0x442E and the swallowed ENDOFTRADE echo of it.

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
            // The first 32 bytes are a one-time password: zeros on an ordinary login, and after "select
            // server" the OTP the 2016 world manager minted, with no username or password behind it. That
            // is NC_USER_LOGIN_WITH_OTP_REQ, and the 2016 login server redeems it itself - see Opcodes.
            if (login is not null && T.LoginOtp(payload) is { } otp)
            {
                ctx.ToServer(Op.LoginWithOtp16, otp);
                _plugin.Log($"[{_info.ServiceName}] login carries an OTP -> NC_USER_LOGIN_WITH_OTP_REQ");
                return;
            }
            ctx.ToServer(Op.Login16, login ?? payload);
            if (login is null)
                _plugin.Log($"[{_info.ServiceName}] login is {payload.Length} B, not the 349-byte 2026 layout: passed through");
            var xtrap = new byte[1 + T.XtrapKey2016.Length + 1];
            xtrap[0] = (byte)T.XtrapKey2016.Length;
            T.XtrapKey2016.CopyTo(xtrap, 1);
            ctx.ToServer(Op.XtrapReq16, xtrap);
            return;
        }

        // Logging out / back to character select. Same one-byte payload, different number - see Opcodes.
        // Only outside the login stage: there the client has not got far enough to log out of anything.
        if (p.Opcode == Op.C26NormalLogout && !_isLoginStage && !IsZoneLink && payload.Length == 1)
        {
            ctx.Replace(new FiestaPacket(Op.NormalLogout16, payload));
            _plugin.Log($"[{_info.ServiceName}] 0x0C15 -> NC_USER_NORMALLOGOUT_CMD (type {payload[0]})");
            return;
        }

        // 0x0C23 {inner opcode u16, inner payload}: the 2026 client's INSTANT logout to character select, used when
        // it leaves the world for the beauty shop (senders Fiesta.exe 0x57F3DE / 0x58072A: no LOGOUTREADY countdown,
        // the zone link gets the logout command wrapped, the world-manager link the plain 0x0C15). Seen live
        // 2026-09-23 as 0x0C23 {15 0C 01}. The 2016 build reads 0x0C23 as NC_USER_REGISENUMBER_REQ, so the zone
        // never logged the character out, the world manager kept PlayingCharNo, and re-entering from character
        // select failed with CHAR_LOGINFAIL 0x0145 ("map is under maintenance") until a full relog.
        // Unwrapped into the 2016 NC_USER_NORMALLOGOUT_CMD, exactly what the countdown path sends.
        if (p.Opcode == Op.C26WrappedCmd && !_isLoginStage && !IsZoneLink && payload.Length >= 2)
        {
            var inner = BitConverter.ToUInt16(payload, 0);
            if (inner == Op.C26NormalLogout && payload.Length == 3)
            {
                ctx.Replace(new FiestaPacket(Op.NormalLogout16, new[] { payload[2] }));
                _plugin.Log($"[{_info.ServiceName}] 0x0C23 {{0x0C15}} -> NC_USER_NORMALLOGOUT_CMD (type {payload[2]}, instant logout)");
            }
            else
            {
                ctx.Drop();
                _plugin.Warn($"[{_info.ServiceName}] 0x0C23 wraps 0x{inner:X4} ({payload.Length - 2} B): no translation, dropped");
            }
            return;
        }

        if (p.Opcode == Op.C26AvatarListReq && !_isLoginStage && payload.Length == 1
            && _info.ServiceName.StartsWith("WorldManager", StringComparison.Ordinal))
        {
            ctx.Replace(new FiestaPacket(Op.AvatarListReq16, payload));
            _plugin.Log($"[{_info.ServiceName}] 0x0C1A -> NC_USER_AVATAR_LIST_REQ");
            return;
        }

        // "Select server": the 2016 handover under its 2016 number. The world manager answers with the OTP.
        if (p.Opcode == Op.C26Back && !_isLoginStage)
        {
            ctx.Replace(new FiestaPacket(Op.WillWorldSelectReq16, payload));
            _plugin.Log($"[{_info.ServiceName}] 0x0C24 -> NC_USER_WILL_WORLD_SELECT_REQ");
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
            // The OPCODE always has to change: 2026 sends the world-manager login as 0x0c0e and the 2016
            // server accepts it only as 0x0c0f. The PAYLOAD only changes for the German build, which sends
            // 82 bytes; the US build already sends the 320-byte 2016 shape (measured: 82 in
            // Official1.pcapng, 320 in both US captures). So a refused translation means "already the right
            // shape", not "leave the packet alone" - relaying it under the 2026 opcode makes the world
            // manager hang up, which reaches the player as "Disconnected from World server".
            ctx.Drop();
            ctx.ToServer(Op.WmLogin16, T.WmLogin2026To2016(payload) ?? payload);
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

        // MOVED TO THE ZONE (bridge26 batch 7): MAP_LOGIN_REQ 1718 -> 1590 (the client's checksums in the zone's slots), the
        // 2026 logout opcodes 0x0C15 / wrapped 0x0C23 on the zone link, and dropping opcodes the zone has no handler for.

        // Anything the 2016 build has no opcode for would make the server hang up.
        if (!IsZoneLink && !_plugin.IsKnownTo2016(p.Opcode) && !Op.HandledByZonePlugins.Contains(p.Opcode))
        {
            _plugin.Log($"[{_info.ServiceName}] dropped 0x{p.Opcode:X4} ({payload.Length} B): no such opcode in the 2016 build");
            ctx.Drop();
        }
    }

    public void OnServerPacket(PluginPacketContext ctx)
    {
        var p = ctx.Packet;
        var payload = p.Payload.ToArray();
        // MOVED TO THE WM HOOK (wm_bridge26): avatar list, create success, guild member list, academy info, will-select ack.
        if (IsWmLink && p.Opcode != Op.CharLoginAck) return;

        // A 0x4401 whose STRUCT_QSC.Command is QSC_END (1): this zone tells its clients when a script ends.
        // Remembered, so the per-ack close stops for this zone - and NOT relayed: it becomes a 0x442E.
        //
        // Official never sends QSC_END (OfficialUS2.pcapng: every script, after its last SAY / ACCEPT / DONE,
        // ends with 0x442E and the client answers ENDOFTRADE). The two close the window differently in the
        // 2026 client. Case 1 of On_NC_QUEST_SCRIPT_CMD_REQ (0x5B4645) is a bare CloseWin(NpcDialogWin). The
        // 442E handler goes through NpcDialogWin close (0x72B1B0), which also re-shows the HUD group the quest
        // dialog hid (tail: find window [0xCE49E4] -> 0x5867A0(1)). A relayed END left the skill bar, the
        // bottom icons and chat hidden until relog (operator, 2026-09-23).
        // The ENDOFTRADE that close sends is swallowed like the per-ack one: a 2016 client closed on END via
        // CloseWin and sent nothing, so the 2016 zone never saw it there.
        // QSC_DONE (the reward was given): a script may stop there with no END after it, and the 2026 dialog then
        // never closes - Continue did nothing on "Mischievous Monsters" (operator 2026-09-24). The client only
        // closes after 0x442E, so DONE is relayed and followed by one.
        // MOVED TO THE ZONE (bridge26 batch 3): QSC_DONE -> relayed + 0x442E, QSC_END -> 0x442E.

        // The world manager's answer to "select server": {nError, sOTP[32]}, the same 34 bytes the 2026 client
        // expects, under the 2026 number. (2026 uses 0x0C34 for its create-character ack, so it cannot pass.)
        if (p.Opcode == Op.WillWorldSelectAck16 && !_isLoginStage && payload.Length == 34)
        {
            ctx.Drop();
            ctx.ToClient(Op.C26BackAck, payload);
            return;
        }

        // MOVED TO THE ZONE (ik-fiesta-patch-recipes zone/plugins/bridge26, hooksridge26.ini send=2026): these arrive
        // already in their 2026 shape and are relayed untouched. Batch 1 (2026-10-04): 0x2448 SWING_DAMAGE, 0x2449
        // SOMEONESWING_DAMAGE, 0x243C DOTDAMAGE, 0x2452 SKILLBASH_HIT_DAMAGE, 0x2402 TARGETINFO; batch 2: the four
        // SKILLBASH *_START frames 0x244E / 0x2450 / 0x244F / 0x2451. Their T.* translators stay
        // as the reference ZoneHookParityTests checks the zone against.
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
                // The 2026 login scene has no case for cmd 9: relayed as it was, the client ignored it and only saw
                // the close ("wrong pw => disconnected with no error message", operator 2026-09-24). Its fail case
                // is cmd 7, same {err u16}: GetErrMsg(err) in a modal box, then it closes the link itself.
                ctx.Drop();
                ctx.ToClient(U(Op.C26LoginFail), payload);
                return;
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

            // MOVED TO THE ZONE (bridge26 batch 5): the briefinfo records REGENMOB / MOB / REGENMOVER / LOGINCHARACTER /
            // CHARACTER (US width; the zone learns the states >= 792 itself and fills them into LOGINCHARACTER).

            // MOVED TO THE ZONE (bridge26 batch 6): the item records - ITEM_CELLCHANGE / EQUIPCHANGE (+ the folded-slot clears),
            // CHAR_CLIENT_ITEM, the counted record lists (sell / guild storage / booth search / academy reward / reward inventory /
            // storage) and the single items (sell insert / trade upboard / collect card), from the 2026 ItemInfo the zone reads.

            case Op.GuildMemberList:
                if (T.GuildMemberList2016To2026(payload, out var lastChunk) is { } gml)
                {
                    ctx.Replace(gml);
                    if (lastChunk) ctx.ToClient(Op.GuildMemberList, T.GuildMemberListEnd);
                    _plugin.Log($"[{_info.ServiceName}] 0x741B guild member list: {gml[1] | (gml[2] << 8)} member(s), flag {gml[0]}"
                                + (lastChunk ? " + closing empty chunk" : ""));
                }
                else
                    _plugin.Log($"[{_info.ServiceName}] 0x741B guild member list not translated ({payload.Length} B)");
                return;
            case Op.GuildAcademyInfo when T.GuildAcademyInfo2016To2026(payload) is { } gai:
                ctx.Replace(gai);
                return;


            // MOVED TO THE ZONE (bridge26 batch 3): the quest DOING list (+ 0x110F tracker list) and the REPEAT list.
            // MOVED TO THE ZONE (bridge26 batch 4): CHAR_CLIENT_BASE (the US 362 B), the CHARGEDBUFF list + BUFFSTART /
            // BUFFTERMINATE, and the six SHOPOPEN tables.

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
