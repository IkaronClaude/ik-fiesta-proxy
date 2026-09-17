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
    public const ushort ChargedBuff = 0x104a;
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

    // S->C, two bytes, a quest id. The 2026 server sends it at both ends of a dialogue script and the
    // client closes the dialogue window on it. Every one of the 43 in OfficialUS2.pcapng carries
    // 0xFFFF - the zone sentinel for "no quest" - so the packet reads as "the quest script context is
    // now none". The 2016 server never sends it (0 in Full.pcapng, AbandonQuest.pcapng, JCQ.pcapng),
    // which is why the 2026 client leaves the window open on our stack.
    public const ushort QuestCurrentScript = 0x442e;
    public static readonly byte[] QuestCurrentScriptNone = { 0xff, 0xff };
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
    /// <summary>"Previous" on character select. NC_USER_REGISENUMBER_ACK in 2016, a SERVER->client
    /// message, so relaying it makes the world manager hang up.</summary>
    public const ushort C26Back = 0x0c24;
    public const ushort C26BackAck = 0x0c25;
    /// <summary>Opening the character-create screen; the reply carries the slot the new character takes.</summary>
    public const ushort C26CreateOpen = 0x0c32;
    public const ushort C26CreateOpenAck = 0x0c34;
    /// <summary>Sent mid-login and after create/erase. NC_USER_NORMALLOGOUT_CMD / CREATE_OTP_REQ in 2016.</summary>
    public const ushort C26MidLogin = 0x0c18;
    public const ushort C26PostCreate = 0x0c35;

    /// <summary>Map a reference (German) USER opcode into this session's numbering.</summary>
    public static ushort U(ushort op, int shift)
        => shift != 0 && op >= 0x0c00 && op < 0x1000 && (op & 0x3ff) >= ShiftFrom ? (ushort)(op + shift) : op;
}
