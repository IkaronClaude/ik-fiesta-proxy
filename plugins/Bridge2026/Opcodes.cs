namespace Bridge2026;

/// <summary>
/// Opcodes on both wires, and the one rule about how they differ.
///
/// An opcode is (department &lt;&lt; 10) | command. The 2016 numbers come from the PDB extract; the 2026
/// numbers were measured on Official1.pcapng (German client) and OfficialUS.pcapng (US 10.6.4).
///
/// Two USER commands were inserted between the German and US 2026 builds, so the top of that department
/// shifts by +2 and the bottom does not:
///     version key  DE 0x0c2c -> US 0x0c2e     login       0x0c01 on both
///     will-select  DE 0x0c34 -> US 0x0c36     world list  0x0c06 on both
///     challenge ans DE 0x0c3f -> US 0x0c41    world select 0x0c0a on both
/// The boundary is somewhere in (0x06, 0x2c]; <see cref="ShiftFrom"/> is the lowest command it is proven
/// for. The shift is measured per connection off the first client frame, which is always the version key,
/// rather than configured, so one proxy serves either build.
/// </summary>
internal static class Op
{
    public const ushort Seed = 0x0807;                 // S->C, carries the C->S cipher position

    /// <summary>Lowest USER command known to move between the 2026 builds.</summary>
    public const int ShiftFrom = 0x2c;

    // ---- 2026 client, German numbering (the reference; the US build adds the shift above 0x2c) ----
    public const ushort C26Version = 0x0c2c;
    public const ushort C26VersionAck = 0x0c2d;
    public const ushort C26Login = 0x0c01;
    public const ushort C26Ack1 = 0x0c40;
    public const ushort C26Ack2 = 0x0c47;
    public const ushort C26WorldList = 0x0c06;
    public const ushort C26Challenge = 0x0c3e;
    public const ushort C26WillSelect = 0x0c34;
    public const ushort C26ChallengeAnswer = 0x0c3f;
    public const ushort C26Otp = 0x0c35;
    public const ushort C26WorldSelect = 0x0c0a;
    public const ushort C26WorldSelectAck = 0x0c0b;
    public const ushort C26WmLogin = 0x0c0e;
    public const ushort C26WmAvatars = 0x0c0f;

    // ---- 2016 server ----
    public const ushort Version16 = 0x0c65;
    public const ushort VersionAck16 = 0x0c67;
    public const ushort Login16 = 0x0c5a;
    public const ushort XtrapReq16 = 0x0c04;
    public const ushort XtrapAck16 = 0x0c05;
    public const ushort LoginAck16 = 0x0c0a;
    public const ushort LoginFail16 = 0x0c09;          // NC_USER_LOGINFAIL_ACK {err u16}
    public const ushort WorldSelect16 = 0x0c0b;
    public const ushort WorldSelectAck16 = 0x0c0c;
    public const ushort WmLogin16 = 0x0c0f;
    public const ushort WmAvatars16 = 0x0c14;

    // ---- shared between the builds (never renumbered) ----
    public const ushort CharLoginReq = 0x1001;         // client -> WM {slot u8}
    public const ushort CharLoginAck = 0x1003;
    public const ushort MapLoginReq = 0x1801;
    public const ushort MapLoginFail = 0x1804;
    public const ushort LinkOther = 0x180a;
    public const ushort ClientBase = 0x1038;
    public const ushort QuestDoing = 0x103a;
    public const ushort QuestRepeat = 0x10d7;
    public const ushort ClientItem = 0x1047;
    /// <summary>{exchange u16, location u16, item}: an item moved, stacked, picked up or dropped.</summary>
    public const ushort ItemCellChange = 0x3001;
    /// <summary>{exchange u16, location u8, item}: an item equipped or taken off.</summary>
    public const ushort ItemEquipChange = 0x3002;
    /// <summary>{cen u64, maxpage u8, curpage u8, opentype u8, count, records}: a storage page. Header confirmed on the
    /// official wire 2026-09-18: 8 frames, count u32 at 11, pages 0..3 of a maximum of 16.</summary>
    public const ushort MenuOpenStorage = 0x3c08;
    // The rest of the item-record family (tickets.md P0). Headers read out of the 2026 handlers:
    //   0x305B  buyback list        count u32 at 0 (0x740C10: mov ebx,[edi]; add edi,4)
    //   0x7492  guild storage       count u32 at 18 (0x73F5B0: mov esi,[edx+0x12]; lea edi,[edx+0x16])
    //   0x986E  GUILD storage       count u32 at 10 (0x840B10: mov eax,[edx+0xa]; lea edi,[edx+0xe]). The enum name says
    //                               academy because that is what it PAYS FOR - the academy has no storage of its own, it
    //                               hands out milestone rewards from the guild's cen and items (operator). CONFIRMED on the
    //                               official wire 2026-09-18: a 980 B frame carries 81,984,769 cen and 125 items in
    //                               consecutive slots, every id a real item, 0 bytes left over. The department is NOT
    //                               renumbered - its switch (0x595BE6) takes 69, 6A, 6E as 2016 does
    //   0x6814  booth search        count u32 at 2, records from 6, item id 15 into each (0x65A8F0)
    //   0x305C  buyback insert      {handle u16, item at 2} (0x740D10)
    //   0x4C10  trade, other side   {slot u8, item at 1} (case 0x5938D1)
    //   0xC407  card collection     {err u16, slot u8, item at 3} (0x5BB6A0), 106 B on the official wire
    //   0x3052  mount upgrade       not translated: the client reads err/rare/upgrade/slot only (0x5913CC)
    public const ushort SellItemList = 0x305b;
    public const ushort SellItemInsert = 0x305c;
    public const ushort TradeOppositUpboard = 0x4c10;
    public const ushort BoothSearchItemList = 0x6814;
    public const ushort GuildStorageOpen = 0x7492;
    public const ushort AcademyRewardStorageOpen = 0x986e;
    public const ushort CollectCardOpen = 0xc407;
    public const ushort ChargedBuff = 0x104a;
    public const ushort ChargedBuffStart = 0x9003;        // NC_CHARGED_BUFFSTART_CMD
    public const ushort ChargedBuffTerminate = 0x9004;    // NC_CHARGED_BUFFTERMINATE_CMD
    public const ushort RewardInvenAck = 0x302d;
    public const ushort RegenMob = 0x1c08;
    public const ushort MobCmd = 0x1c09;
    public const ushort LoginCharacter = 0x1c06;
    public const ushort CharacterList = 0x1c07;
    public const ushort RegenMover = 0x1c1a;
    /// <summary>NC_QUEST_JOBDUNGEON_FIND_RNG: 2 bytes from the 2026 client, 115 in the 2016 build.</summary>
    public const ushort QuestScriptCmdReq = 0x4401;   // S->C: the server asks the client to run a script command
    public const ushort QuestScriptCmdAck = 0x4402;   // C->S: {u16 nQuestID, u8 nQSC, u32 nResult}
    public const int QuestScriptCmdAckSize = 7;
    public const uint QscEnd = 1;                       // STRUCT_QSC.Command: the script reached END

    // S->C. "Close the NPC dialog": the 2026 handler (Fiesta.exe 0x5B4BE0) sets g_C319D5 and calls
    // NpcDialogWin close, and never reads the payload. The official server sends FF FF, so that is
    // what goes out, but the bytes carry no meaning. No 2016 equivalent; that client closes itself.
    public const ushort QuestCloseDialog = 0x442e;
    public static readonly byte[] QuestCloseDialogPayload = { 0xff, 0xff };

    public const ushort ActEndOfTrade = 0x200b;       // C->S, empty: (8 << 10) | 0x0B
    public const ushort QuestJobDungeonFindRng = 0x441f;
    public const int QuestJobDungeonFindRng2016Size = 115;

    public const ushort SwingDamage = 0x2448;
    public const ushort SkillHitDamage = 0x2452;
    public const ushort TargetInfo = 0x2402;
    public const ushort HitObjStart = 0x244e;
    public const ushort HitFldStart = 0x2450;
    public const ushort SomeoneHitObjStart = 0x244f;
    public const ushort SomeoneHitFldStart = 0x2451;
    public const ushort DotDamage = 0x243c;
    public const ushort SomeoneSwing = 0x2449;
    public const ushort AvatarCreateReq = 0x1401;
    public const ushort AvatarCreateFail = 0x1404;
    public const ushort AvatarCreateSucc = 0x1406;

    /// <summary>NC_MENU_SHOPOPEN* and their TABLE forms, which share one record layout.</summary>
    public static readonly ushort[] ShopTables = { 0x3c03, 0x3c04, 0x3c06, 0x3c09, 0x3c0a, 0x3c0b };

    // ---- client frames that exist as 2016 opcodes but mean something else in 2026 ----
    /// <summary>
    /// "Select server" on character select - the 2016 servers' OWN one-time-password handover, renumbered.
    ///     2026 0x0C24 (empty)              = 2016 0x0C33 NC_USER_WILL_WORLD_SELECT_REQ   client -> WM
    ///     2026 0x0C25 {nError, sOTP[32]}   = 2016 0x0C34 NC_USER_WILL_WORLD_SELECT_ACK   WM -> client
    ///     2026 login, OTP in bytes 0..31   = 2016 0x0C37 NC_USER_LOGIN_WITH_OTP_REQ      client -> Login
    /// The world manager mints the OTP with the login server (0x0C35 / 0x0C36 between them) and the login
    /// server redeems it: tools/otp_handover_test.py in Fiesta2026on2016 runs the whole exchange against
    /// the 2016 stack with no bridge in the way and gets a world list back for the OTP alone. Even the
    /// success code matches - 0x1E58 heads the 2016 ack and the ack in OfficialUS2.pcapng alike.
    ///
    /// Relayed unrenumbered these hang the connection up: 0x0C24 is NC_USER_REGISENUMBER_ACK in 2016, a
    /// server->client message, and 2026 reuses 0x0C34 for its create-character ack.
    ///
    /// For one day (2026-09-17) the bridge answered 0x0C24 itself, first with a captured token replayed to
    /// everybody and then with tickets of its own that it redeemed against login bodies it remembered.
    /// The operator asked why the bridge was inventing what the server surely had; it had.
    /// </summary>
    public const ushort C26Back = 0x0c24;
    public const ushort C26BackAck = 0x0c25;
    public const ushort WillWorldSelectReq16 = 0x0c33;
    public const ushort WillWorldSelectAck16 = 0x0c34;
    public const ushort LoginWithOtp16 = 0x0c37;
    /// <summary>Opening the character-create screen; the reply carries the slot the new character takes.</summary>
    public const ushort C26CreateOpen = 0x0c32;
    public const ushort C26CreateOpenAck = 0x0c34;
    /// <summary>Sent mid-login and after create/erase. NC_USER_NORMALLOGOUT_CMD / CREATE_OTP_REQ in 2016.</summary>
    public const ushort C26MidLogin = 0x0c18;
    public const ushort C26PostCreate = 0x0c35;
    /// <summary>
    /// The 2026 client's NC_USER_NORMALLOGOUT_CMD: one byte of LogoutType, sent on the world-manager AND the
    /// zone link five seconds after NC_CHAR_LOGOUTREADY_CMD (0x1071). 01 = back to character select.
    /// In the 2016 build that opcode is NC_USER_LOGINWORLDFAIL_ACK, a SERVER->client message, so the zone
    /// asserts "Invalid protocol[3/21]" and both servers hang up: returning to character select
    /// disconnected the player (seen in the zone log from 2026-09-16 on). 2016 calls it 0x0C18, which the
    /// 2026 client in turn uses for something else (C26MidLogin). OfficialUS2.pcapng shows the same
    /// 1071 -> 0C15 {01} against the real server, answered with the avatar list.
    /// </summary>
    public const ushort C26NormalLogout = 0x0c15;
    /// <summary>2026 "instant" logout: {inner opcode u16, inner payload} = a wrapped 0x0C15. NC_USER_REGISENUMBER_REQ in 2016.</summary>
    public const ushort C26WrappedCmd = 0x0c23;
    public const ushort NormalLogout16 = 0x0c18;
    /// <summary>
    /// "Send me the character list again" - the second half of going back to character select, sent on the
    /// world-manager link straight after the logout above. 2026 numbers it 0x0C1A (one byte, 3F in
    /// OfficialUS2.pcapng, answered there with the avatar list 0x0C0F); 2016 calls 0x0C1A
    /// NC_USER_CONNECTCUT2WORLDMANAGER_CMD and has the request as 0x0C1F NC_USER_AVATAR_LIST_REQ, an empty
    /// struct handled by CParserClient::fc_NC_USER_AVATAR_LIST_REQ. Its answer is the 2016 avatar list
    /// (0x0C14), which OnServerPacket already turns into the 2026 one.
    /// </summary>
    public const ushort C26AvatarListReq = 0x0c1a;
    public const ushort AvatarListReq16 = 0x0c1f;

    /// <summary>Map a reference (German) USER opcode into this session's numbering.</summary>
    public static ushort U(ushort op, int shift)
        => shift != 0 && op >= 0x0c00 && op < 0x1000 && (op & 0x3ff) >= ShiftFrom ? (ushort)(op + shift) : op;
}
