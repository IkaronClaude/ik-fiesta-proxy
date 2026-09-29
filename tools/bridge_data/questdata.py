"""The 2016 bespoke QuestData.shn (client + server share it). Layout = QUEST_DATA from Fiesta.pdb,
compiler-computed offsets (FiestaLib-Reloaded/docs/extracted/Client/QUEST_DATA.txt):

  file:   u16 marker (6), u16 count, then count records
  record: 680-byte QUEST_DATA as laid out in memory (incl. the 3 dead char* at 668/672/676), then the
          three scripts back to back in the order Start, Doing, End, each NUL-terminated, lengths at
          660/662/664 as SizeOfScriptStart / SizeOfScriptEnd / SizeOfScriptDoing (note End before Doing
          in the size triple). nQuestDataSize at 0 = 680 + the three sizes.

read(path) -> list of quest dicts (fields by PDB name, nested start/end/actions/rewards, scripts as bytes)
write(path, quests) -> byte-identical to the source for an unmodified read() result.
"""
import struct

FIXED = 680
MARKER = 6

START_FIELDS = [  # (name, fmt, offset within Start @24)
    ('bIsWaitListView', 'B', 0), ('bIsWaitListProgress', 'B', 1), ('bLevel', 'B', 2), ('LevelMin', 'B', 3),
    ('LevelMax', 'B', 4), ('bNPC', 'B', 5), ('NPCID', 'H', 6), ('bItem', 'B', 8), ('ItemID', 'H', 10),
    ('ItemLot', 'H', 12), ('bLocation', 'B', 14), ('Location', 'H', 16), ('LocationX', 'I', 20),
    ('LocationY', 'I', 24), ('LocationRange', 'I', 28), ('bQuest', 'B', 32), ('QuestID', 'H', 34),
    ('bRace', 'B', 36), ('Race', 'B', 37), ('bClass', 'B', 38), ('Class', 'B', 39), ('bGender', 'B', 40),
    ('Gender', 'B', 41), ('bDate', 'B', 42), ('DateMode', 'B', 43), ('DateStart', 'q', 48), ('DateEnd', 'q', 56),
]
END_FIELDS = [  # within End @88; NPCMobList @4 (5 x 8), ItemList @44 (5 x 6) handled separately
    ('bIsWaitListProgress', 'B', 0), ('bLevel', 'B', 1), ('Level', 'B', 2), ('bLocation', 'B', 74),
    ('Location', 'H', 76), ('LocationX', 'I', 80), ('LocationY', 'I', 84), ('LocationRange', 'I', 88),
    ('bScenario', 'B', 92), ('ScenarioID', 'H', 94), ('bRace', 'B', 96), ('Race', 'B', 97), ('bClass', 'B', 98),
    ('Class', 'B', 99), ('bTimeLimit', 'B', 100), ('TimeLimit', 'H', 102),
]
NPCMOB_FIELDS = [('bNPCMob', 'B', 0), ('NPCMobID', 'H', 2), ('NPCMobAction', 'B', 4), ('NPCMobCount', 'B', 5), ('TargetGroup', 'B', 6)]
ITEM_FIELDS = [('bItem', 'B', 0), ('ItemID', 'H', 2), ('ItemLot', 'H', 4)]
ACTION_FIELDS = [('IfType', 'B', 0), ('IfTarget', 'I', 4), ('ThenType', 'B', 8), ('ThenTarget', 'I', 12),
                 ('ThenPersent', 'I', 16), ('ThenCountMin', 'I', 20), ('ThenCountMax', 'I', 24), ('TargetGroup', 'B', 28)]
HEAD_FIELDS = [('nQuestDataSize', 'I', 0), ('ID', 'H', 4), ('NameID', 'I', 8), ('BrifingID', 'I', 12), ('Region', 'B', 16),
               ('Type', 'B', 17), ('Repeatable', 'B', 18), ('nDailyQuestType', 'B', 19)]
START, END, NUMACT, ACTION, REWARD = 24, 88, 192, 196, 516


# struct's own ranges, by format character, for checking a value before packing it.
_RANGES = {'B': (0, 255), 'H': (0, 65535), 'I': (0, 4294967295),
           'b': (-128, 127), 'h': (-32768, 32767), 'i': (-2147483648, 2147483647),
           'q': (-(1 << 63), (1 << 63) - 1)}


def misfits(q):
    """The fields of `q` the 2016 record cannot hold, as [(path, value, low, high)].

    The 2026 tables are wider than the 2016 record in places - Region is a byte here and a word there -
    so a quest can carry a value that simply has nowhere to go. Saying which field and which value lets
    the caller park the quest with a reason instead of packing a truncated one.
    """
    bad = []

    def check(prefix, fields, d):
        for n, f, _o in fields:
            v = d.get(n)
            if not isinstance(v, int):
                continue
            lo, hi = _RANGES.get(f, (None, None))
            if lo is not None and not (lo <= v <= hi):
                bad.append((prefix + n, v, lo, hi))

    check('', [x for x in HEAD_FIELDS if x[0] != 'nQuestDataSize'], q)
    check('start.', START_FIELDS, q['start'])
    check('end.', END_FIELDS, q['end'])
    for i, o in enumerate(q['end']['NPCMobList']):
        check('end.NPCMobList[%d].' % i, NPCMOB_FIELDS, o)
    for i, o in enumerate(q['end']['ItemList']):
        check('end.ItemList[%d].' % i, ITEM_FIELDS, o)
    return bad


def _unpack(b, base, fields):
    return {n: struct.unpack_from('<' + f, b, base + o)[0] for n, f, o in fields}


def _pack(b, base, fields, d):
    for n, f, o in fields:
        struct.pack_into('<' + f, b, base + o, d[n])


def read(path):
    b = open(path, 'rb').read()
    marker, count = struct.unpack_from('<HH', b, 0)
    if marker != MARKER:
        raise ValueError('QuestData marker %d != %d' % (marker, MARKER))
    off = 4
    out = []
    for i in range(count):
        size = struct.unpack_from('<I', b, off)[0]
        rec = b[off:off + size]
        fx = rec[:FIXED]
        q = _unpack(fx, 0, HEAD_FIELDS)
        q['start'] = _unpack(fx, START, START_FIELDS)
        q['end'] = _unpack(fx, END, END_FIELDS)
        q['end']['NPCMobList'] = [_unpack(fx, END + 4 + k * 8, NPCMOB_FIELDS) for k in range(5)]
        q['end']['ItemList'] = [_unpack(fx, END + 44 + k * 6, ITEM_FIELDS) for k in range(5)]
        q['NumOfAction'] = fx[NUMACT]
        q['Action'] = [_unpack(fx, ACTION + k * 32, ACTION_FIELDS) for k in range(10)]
        q['Reward'] = [{'Use': fx[REWARD + k * 12], 'Type': fx[REWARD + k * 12 + 1],
                        'Value': fx[REWARD + k * 12 + 4:REWARD + k * 12 + 12]} for k in range(12)]
        sz_start, sz_end, sz_doing = struct.unpack_from('<HHH', fx, 660)
        p = FIXED
        q['ScriptStart'] = rec[p:p + sz_start]
        p += sz_start
        q['ScriptDoing'] = rec[p:p + sz_doing]
        p += sz_doing
        q['ScriptEnd'] = rec[p:p + sz_end]
        p += sz_end
        if p != size:
            raise ValueError('quest %d: scripts end at %d, record size %d' % (q['ID'], p, size))
        q['_fixed'] = fx  # the untouched block: pad bytes, dead pointers, unknown bytes are kept from here
        out.append(q)
        off += size
    if off != len(b):
        raise ValueError('%d trailing bytes' % (len(b) - off))
    return out


def build_fixed(q):
    """680 bytes from the dict. Bytes the fields do not cover come from q['_fixed'] (or zeros)."""
    fx = bytearray(q.get('_fixed') or bytes(FIXED))
    q = dict(q)
    q['nQuestDataSize'] = FIXED + len(q['ScriptStart']) + len(q['ScriptDoing']) + len(q['ScriptEnd'])
    _pack(fx, 0, HEAD_FIELDS, q)
    _pack(fx, START, START_FIELDS, q['start'])
    _pack(fx, END, END_FIELDS, q['end'])
    for k in range(5):
        _pack(fx, END + 4 + k * 8, NPCMOB_FIELDS, q['end']['NPCMobList'][k])
        _pack(fx, END + 44 + k * 6, ITEM_FIELDS, q['end']['ItemList'][k])
    fx[NUMACT] = q['NumOfAction']
    for k in range(10):
        _pack(fx, ACTION + k * 32, ACTION_FIELDS, q['Action'][k])
    for k in range(12):
        r = q['Reward'][k]
        fx[REWARD + k * 12] = r['Use']
        fx[REWARD + k * 12 + 1] = r['Type']
        fx[REWARD + k * 12 + 4:REWARD + k * 12 + 12] = bytes(r['Value']).ljust(8, b'\0')[:8]
    struct.pack_into('<HHH', fx, 660, len(q['ScriptStart']), len(q['ScriptEnd']), len(q['ScriptDoing']))
    return bytes(fx)


def write(path, quests):
    out = bytearray(struct.pack('<HH', MARKER, len(quests)))
    for q in quests:
        out += build_fixed(q) + q['ScriptStart'] + q['ScriptDoing'] + q['ScriptEnd']
    open(path, 'wb').write(out)


def reward_decode(r):
    """(Type, payload) -> for items {id, lot}; else the u32 amount."""
    v = r['Value']
    if r['Type'] == 2:
        return {'ItemID': struct.unpack_from('<H', v, 0)[0], 'ItemLot': struct.unpack_from('<H', v, 2)[0]}
    return struct.unpack_from('<I', v, 0)[0]
