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

    // Set when the bridge has just told the client to close its NPC dialog (0x442E). The 2026 close
    // path answers with NC_ACT_ENDOFTRADE_CMD, which a 2016 client closing the same window never sends,
    // so that one frame is swallowed. Timestamped so a close that produced no ENDOFTRADE (the window
    // was already hidden) cannot eat a later, genuine one from a shop.
    // BRIDGE2026_CLOSE_DIALOG=0 turns the 442E off, for a client carrying the
    // client-2026-npc-dialog-self-close recipe (ik-fiesta-patch-recipes), which closes its own dialog the
    // way a 2016 client does. On by default: an unmodified 2026 client needs it.
    private static readonly bool CloseDialogForClient =
        Environment.GetEnvironmentVariable("BRIDGE2026_CLOSE_DIALOG") != "0";

    // Nullable, NOT a long.MinValue sentinel: TickCount64 - long.MinValue overflows negative, which
    // passed the "<= window" test and swallowed EVERY ENDOFTRADE of the session (2026-09-17, caught in
    // the first live run - shops could never tell the server they had closed).
    private long? _closeSentAt;
    private const int CloseEchoWindowMs = 1500;

    private byte _world;
    private readonly Dictionary<byte, uint> _avatars = new();   // slot -> chrregnum
    private readonly HashSet<byte> _usedSlots = new();
    private readonly HashSet<int> _foldedEmpty = new();         // 2026 equip slots this client already has empty

    private readonly List<ushort> _trackedQuests = new();       // quest tracker: this login's set, from the DOING list(s)

    /// <summary>
    /// NC_ITEM_EQUIPCHANGE_CMD {u16 key, u8 2016 equip slot, item record} names the SERVER's slot, but the 2026
    /// client draws an equipped item at the item's own 2026 Equip (Fiesta.exe 0x5AB150), and the server folds
    /// 2026 slots 30-44 into 2016 ones (Wings of Darkness: 2026 slot 34, server slot 9). An unequip of slot 9 then
    /// cleared the client's empty slot 9 and the wings stayed drawn at 34 (Q29, the client's unequip path
    /// 0x761F20 clears only the slot it is told). So when a 2016 slot changes, every 2026 slot folded into it is
    /// cleared too - except the one the newly equipped item itself is drawn at. Each is cleared at most once
    /// until something is drawn there again.
    /// </summary>
    private void ClearFoldedSlots(PluginPacketContext ctx, byte[] payload)
    {
        if (payload.Length < 5) return;
        var folded = _plugin.FoldedInto(payload[2]);
        if (folded.Count == 0) return;
        var item = BitConverter.ToUInt16(payload, 3);
        var drawnAt = item == 0xFFFF ? -1 : _plugin.FoldedEquipOf(item);
        if (drawnAt >= 0) _foldedEmpty.Remove(drawnAt);
        foreach (var slot in folded)
        {
            if (slot == drawnAt || !_foldedEmpty.Add(slot)) continue;
            ctx.ToClient(Op.ItemEquipChange, new byte[] { payload[0], payload[1], (byte)slot, 0xFF, 0xFF });
            _plugin.Log($"[{_info.ServiceName}] 0x3002 slot {payload[2]}: also cleared the folded 2026 slot {slot}");
        }
    }

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
        // NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD {quest u16, index u32}: the 2026 client counts its own QuestReward rows
        // (items first), the 2016 zone reads a QUEST_DATA.Reward slot (EXP and money first) - relayed as it was, a
        // mage choosing Magic Boots got the cleric's Litany Boots (operator 2026-09-24).
        if (p.Opcode == Op.QuestRewardSelect && payload.Length == 6)
        {
            var quest = BitConverter.ToUInt16(payload, 0);
            var index = (int)BitConverter.ToUInt32(payload, 2);
            var slot = _plugin.RewardSlot(quest, index);
            if (slot >= 0 && slot != index)
            {
                var moved = (byte[])payload.Clone();
                BitConverter.TryWriteBytes(moved.AsSpan(2), (uint)slot);
                ctx.Replace(moved);
            }
            _plugin.Log($"[{_info.ServiceName}] quest {quest} reward choice: client index {index} -> slot {slot}"
                        + (slot < 0 ? " (NOT in the map, relayed as is)" : ""));
            return;
        }

        if (p.Opcode == Op.QuestScriptCmdAck && CloseDialogForClient
            && !_plugin.ZoneAnnouncesQuestEnd.ContainsKey(_info.ServiceName))
        {
            ctx.ToClient(Op.QuestCloseDialog, Op.QuestCloseDialogPayload);
            _closeSentAt = Environment.TickCount64;
            return;                                    // the ack itself is relayed untouched
        }

        // The 2026 close path (0x72B1B0) sends ENDOFTRADE when the window was showing. The 2016 click-close
        // goes through plain CloseWin -> NpcDialogWin::OnClose (0x5F4750), which sends nothing; only
        // CloseDialog (Esc, linkto) does. So the echo of our own 442E is not something the 2016 server
        // ever saw mid-script, and it is dropped.
        if (p.Opcode == Op.ActEndOfTrade && _closeSentAt is long sentAt
            && Environment.TickCount64 - sentAt <= CloseEchoWindowMs)
        {
            ctx.Drop();
            _closeSentAt = null;
            return;
        }

        // 0x441F {u16 quest} track / 0x4421 {u16 quest} untrack = the 2026 QUEST TRACKER (see QuestTracker). The zone
        // plugin quest_track answers both (0x4420 / 0x4422) and keeps the set on the character's quest records, so they
        // go through unchanged. 2016 numbers them as zone-to-zone packets (FIND_RNG is 115 bytes there): anything but
        // the 2-byte client form is dropped. (Not a click or an ack: an earlier revision turned 0x441F into
        // NC_QUEST_SCRIPT_CMD_ACK and skipped the page after an ACCEPT.)
        if ((p.Opcode == Op.QuestTrackReq || p.Opcode == Op.QuestUntrackReq) && payload.Length != 2)
        {
            ctx.Drop();
            _plugin.Log($"[{_info.ServiceName}] 0x{p.Opcode:X4} {payload.Length} B dropped: not the 2-byte tracker request");
            return;
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
        if (p.Opcode == Op.C26NormalLogout && !_isLoginStage && payload.Length == 1)
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
        if (p.Opcode == Op.C26WrappedCmd && !_isLoginStage && payload.Length >= 2)
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

        // 2026's map-status request after every map login: no 2016 opcode, official answers 00 (see Op.C26MapInfoReq)
        if (p.Opcode == Op.C26MapInfoReq)
        {
            ctx.Drop();
            ctx.ToClient(Op.C26MapInfoAck, new byte[] { 0 });
            return;
        }

        // Anything the 2016 build has no opcode for would make the server hang up.
        if (!_plugin.IsKnownTo2016(p.Opcode) && !Op.HandledByZonePlugins.Contains(p.Opcode))
        {
            _plugin.Log($"[{_info.ServiceName}] dropped 0x{p.Opcode:X4} ({payload.Length} B): no such opcode in the 2016 build");
            ctx.Drop();
        }
    }

    public void OnServerPacket(PluginPacketContext ctx)
    {
        var p = ctx.Packet;
        var payload = p.Payload.ToArray();

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
        if (p.Opcode == Op.QuestScriptCmdReq && payload.Length >= 6
            && BitConverter.ToUInt32(payload, 2) == Op.QscDone && CloseDialogForClient)
        {
            ctx.ToClient(Op.QuestCloseDialog, Op.QuestCloseDialogPayload);
            _closeSentAt = Environment.TickCount64;
            _plugin.Log($"[{_info.ServiceName}] quest {BitConverter.ToUInt16(payload, 0)} script DONE -> 0x442E");
            return;
        }

        if (p.Opcode == Op.QuestScriptCmdReq && payload.Length >= 6
            && BitConverter.ToUInt32(payload, 2) == Op.QscEnd)
        {
            if (_plugin.ZoneAnnouncesQuestEnd.TryAdd(_info.ServiceName, true))
                _plugin.Log($"[{_info.ServiceName}] zone announces quest script END: per-ack 0x442E off for this zone");
            if (CloseDialogForClient)
            {
                ctx.Drop();
                ctx.ToClient(Op.QuestCloseDialog, Op.QuestCloseDialogPayload);
                _closeSentAt = Environment.TickCount64;
                _plugin.Log($"[{_info.ServiceName}] quest {BitConverter.ToUInt16(payload, 0)} script END -> 0x442E (restores the HUD)");
                return;
            }
        }

        // The world manager's answer to "select server": {nError, sOTP[32]}, the same 34 bytes the 2026 client
        // expects, under the 2026 number. (2026 uses 0x0C34 for its create-character ack, so it cannot pass.)
        if (p.Opcode == Op.WillWorldSelectAck16 && !_isLoginStage && payload.Length == 34)
        {
            ctx.Drop();
            ctx.ToClient(Op.C26BackAck, payload);
            return;
        }

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

            // Always the US width: the bridge targets the US build (the German client is for capturing only).
            case Op.ClientBase when T.ClientBase2016To2026(payload, T.ClientBaseUs) is { } cb:
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

            // One item at the end of the packet. Relayed untouched, an enchantable item (armour, weapon, ...) is
            // one byte short and the 2026 client reads its option list out of place: hovering such an item
            // after moving it crashed the client in the tooltip builder (2026-09-17, item 453).
            case Op.ItemCellChange or Op.ItemEquipChange when _plugin.HasItemClasses:
            {
                var at = p.Opcode == Op.ItemCellChange ? 4 : 3;
                if (ItemAttr.TrailingItem2016To2026(payload, at, _plugin.ClassOf) is { } moved)
                {
                    if (moved.Length != payload.Length) ctx.Replace(moved);
                }
                else if (payload.Length > at + 2)
                    _plugin.Log($"[{_info.ServiceName}] 0x{p.Opcode:X4}: item {BitConverter.ToUInt16(payload, at)} "
                                 + $"(class {_plugin.ClassOf(BitConverter.ToUInt16(payload, at))}) not translated, "
                                 + $"{payload.Length - at - 2} attribute bytes");
                if (p.Opcode == Op.ItemEquipChange) ClearFoldedSlots(ctx, payload);
                return;
            }

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

            case Op.ChargedBuffStart when T.ChargedBuffStart2016To2026(payload) is { } cbs:
                ctx.Replace(cbs);
                return;

            case Op.ChargedBuffTerminate when T.ChargedBuffTerminate2016To2026(payload) is { } cbt:
                ctx.Replace(cbt);
                return;

            // Counted record lists: the count widens to u32 and the records take their 2026 widths.
            case Op.SellItemList or Op.GuildStorageOpen or Op.BoothSearchItemList or Op.AcademyRewardStorageOpen
                when IsUsBuild && _plugin.HasItemClasses:
            {
                var (countAt, itemAt) = p.Opcode switch
                {
                    Op.GuildStorageOpen => (18, 3),
                    Op.AcademyRewardStorageOpen => (10, 3),
                    Op.BoothSearchItemList => (2, 15),
                    _ => (0, 3),
                };
                if (ItemAttr.RecordList2016To2026(payload, countAt, itemAt, _plugin.ClassOf, out var why) is { } list)
                    ctx.Replace(list);
                else
                    _plugin.Log($"[{_info.ServiceName}] 0x{p.Opcode:X4} not translated: {why}");
                return;
            }
            case Op.SellItemInsert or Op.TradeOppositUpboard or Op.CollectCardOpen when _plugin.HasItemClasses:
            {
                var at = p.Opcode switch { Op.SellItemInsert => 2, Op.TradeOppositUpboard => 1, _ => 3 };
                if (ItemAttr.LeadingItem2016To2026(payload, at, _plugin.ClassOf) is { } one)
                {
                    if (!one.AsSpan().SequenceEqual(payload)) ctx.Replace(one);
                }
                else
                    _plugin.Log($"[{_info.ServiceName}] 0x{p.Opcode:X4}: item not translated ({payload.Length} B)");
                return;
            }
            case Op.RewardInvenAck or Op.MenuOpenStorage when IsUsBuild && _plugin.HasItemClasses:
            {
                var countAt = p.Opcode == Op.MenuOpenStorage ? 11 : 0;
                if (ItemAttr.RecordList2016To2026(payload, countAt, _plugin.ClassOf, out var why) is { } list)
                    ctx.Replace(list);
                else
                    _plugin.Log($"[{_info.ServiceName}] 0x{p.Opcode:X4} not translated: {why}");
                return;
            }
            case Op.RewardInvenAck when IsUsBuild && T.RewardInven2016To2026(payload) is { } ri:
                ctx.Replace(ri);                       // no item table loaded: the empty case still works
                return;

            case Op.SwingDamage when T.Swing2016To2026(payload) is { } sw:
                ctx.Replace(sw);
                return;

            // ---- combat. Every one of these is a size change the 2026 client reads state out of; a frame
            // that goes through at its 2016 width leaves the client's cast bookkeeping stuck.
            case Op.HitObjStart or Op.HitFldStart or Op.SomeoneHitObjStart or Op.SomeoneHitFldStart:
            {
                var size = p.Opcode switch
                {
                    Op.HitObjStart => 6,
                    Op.HitFldStart => 12,
                    Op.SomeoneHitObjStart => 8,
                    _ => 14,
                };
                if (T.HitStart2016To2026(payload, size) is { } h) ctx.Replace(h);
                else _plugin.Warn($"[{_info.ServiceName}] cast-start 0x{p.Opcode:X4} is {payload.Length} B, "
                                  + $"expected {size}; relayed unchanged, which bricks the next cast.");
                return;
            }

            case Op.SkillHitDamage when T.SkillHit2016To2026(payload) is { } sh:
                ctx.Replace(sh);
                return;

            case Op.DotDamage or Op.SomeoneSwing:
                if (T.Tail7_2016To2026(payload, 13) is { } t7) ctx.Replace(t7);
                return;

            case Op.QuestDoing when payload.Length >= 6:
            {
                var tracked = QuestTracker.TakeTracked(payload, _trackedQuests);
                if (T.QuestDoing2016To2026(payload, _plugin.CounterRows) is not { } qd) return;
                ctx.Replace(qd);
                ctx.ToClient(Op.QuestTrackList, QuestTracker.ListPayload(tracked));
                _plugin.Log($"[{_info.ServiceName}] quest tracker: chr {BinaryPrimitives.ReadUInt32LittleEndian(payload)}, "
                            + $"tracked [{string.Join(",", tracked)}]");
                return;
            }

            case Op.QuestRepeat when T.QuestRepeat2016To2026(payload, _plugin.CounterRows) is { } qr:
                ctx.Replace(qr);
                return;

            case Op.CharacterList when T.CharacterList2016To2026(payload, UsExtra) is { } cl:
                ctx.Replace(cl);
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

            case var op when Array.IndexOf(Op.ShopTables, op) >= 0 && T.ShopTable2016To2026(payload) is { } st:
                ctx.Replace(st);
                return;

            case Op.MapLoginFail:
                _plugin.Warn($"[{_info.ServiceName}] MAP_LOGINFAIL_ACK {Convert.ToHexString(payload)}");
                return;
        }
    }

    public void Dispose() { }
}
