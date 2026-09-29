"""The data files ik-fiesta-proxy's Bridge2026 plugin needs, generated from YOUR server and client (nothing shipped).

    python tools/bridge_data/bridge_data.py --server <your server>/9Data --client26 <your 2026 client folder> \
        --out deploy/bridge2026

Standalone: needs only Python 3 (numpy optional, faster); shn.py and questdata.py beside it read the tables.

  zone-checksums.txt  the 49 ressystem checksums the 2016 zone compares at map login, from the DEPLOYED server
                      tree (what the zone actually compares against). The 2026 client hashes its own, different
                      tables; the bridge swaps in these. Regenerate whenever the server tables are rebuilt: a stale
                      line makes the client report at zone enter that it "has been illegally manipulated".
  item-classes.txt    item id -> ItemInfo Class of the 2026 client. The client picks each inventory record's
                      attribute block by this value, so the bridge needs it to know how wide a record is.
  quest-reward-index.txt  quest, 2026 index, 2016 slot - for NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD (0x4411). The 2026
                      client sends the chosen reward as its row in ITS QuestReward table (items first); the 2016 zone
                      reads it as a slot of QUEST_DATA.Reward[12] (EXP and money first). Matched by reward type and
                      value; only quests with a choice (a Selectable 2 row) are listed.
  equip-fold.txt      the 2026 equip slots the server folds into 2016 slots (EQUIP_REMAP below, 30-44 -> 0-29)
                      and every item whose 2026 Equip is one of them. The 2026 client draws an item at ITS Equip
                      (Wings of Darkness: 34) while the server says slot 9, so an unequip of slot 9 left the wings
                      drawn (Q29); the bridge clears the folded slots too.

  quest-counter-rows.txt  quest, then for each of the zone's 5 kill-counter slots the 2026 client's QuestEndNpc row
                      it belongs to - only for quests where they differ. The 2016 record has no room for the hand-in row
                      of a quest with more than 5 end rows (a quest merge that keeps the kills), so its counters sit one slot
                      lower than the 2026 client's rows (talk first); the bridge moves them in the login quest lists.

Both used to be made by hand; this reproduces them byte for byte (checked 2026-09-19).
"""
import argparse
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import questdata as qd  # noqa: E402
import shn  # noqa: E402

# the 49 ressystem tables the 2016 zone checksums at map login, in the order it expects (Zone.exe CShnDataFileCheckSum)
FILES = [
    "AbState", "ActiveSkill", "CharacterTitleData", "ChargedEffect", "ClassName",
    "Gather", "GradeItemOption", "ItemDismantle", "ItemInfo", "MapInfo",
    "MiniHouse", "MiniHouseFurniture", "MiniHouseObjAni", "MobInfo", "PassiveSkill",
    "Riding", "SubAbState", "UpgradeInfo", "WeaponAttrib", "WeaponTitleData",
    "MiniHouseFurnitureObjEffect", "MiniHouseEndure", "DiceDividind", "ActionViewInfo", "MapLinkPoint",
    "MapWayPoint", "AbStateView", "ActiveSkillView", "CharacterTitleStateView", "EffectViewInfo",
    "ItemShopView", "ItemViewInfo", "MapViewInfo", "MobViewInfo", "NPCViewInfo",
    "PassiveSkillView", "ProduceView", "CollectCardView", "GTIView", "ItemViewEquipTypeInfo",
    "SingleData", "MarketSearchInfo", "ItemMoney", "PupMain", "ChatColor",
    "TermExtendMatch", "MinimonInfo", "MinimonAutoUseItem", "ChargedDeletableBuff",
]
# the 2026 equip slots (30-44) a 2016 server folds into its own 30 slots: 2026 slot -> 2016 slot. Your server may fold
# differently - edit this to match what your ItemInfo conversion does.
EQUIP_REMAP = {31: 27, 32: 26, 33: 24, 34: 9, 35: 8, 36: 20, 37: 22, 38: 25, 39: 18, 41: 17, 42: 13, 43: 13, 44: 29}


def shn_crypt(d):
    return shn.crypt(d)

CHECKSUM_HEAD = """# MD5 of each ressystem table the 2016 zone verifies at map login, in the order it expects.
# 32 hex characters each, sent as ASCII: MAP_LOGIN_REQ is 22 + 49 * 32 = 1590 bytes.
# The hash covers the SHN header plus the DECRYPTED body (shn.crypt).
#
# Generated from the DEPLOYED SERVER tree (9Data/Shine and 9Data/Shine/View), which is what
# the zone compares against, and cross-checked against the merged client tree. Regenerate
# whenever those tables are rebuilt: a stale entry makes the client report at zone enter
# that it "has been illegally manipulated".
"""

CLASSES_HEAD = """# item id -> attribute class, from the 2026 US client ItemInfo.shn Class column.
# The client picks each inventory record attribute block by this value, so the bridge needs it
# to know how wide a record is. %d items.
"""


FOLD_HEAD = """# 2026 equip slots the server folds into 2016 slots, and the items drawn at them (tools/bridge_data.py).
#   fold <2026 slot> <2016 slot>   (EQUIP_REMAP)
#   item <item id> <2026 slot>     (2026 client ItemInfo.Equip, only where it is a folded slot) - %d items
# The 2026 client draws an item at its own Equip; the server's NC_ITEM_EQUIPCHANGE_CMD names the 2016 slot, so
# the bridge also clears the folded 2026 slots when that 2016 slot changes (Q29, wings stayed drawn).
"""


def server_table(shine, name):
    for d in (shine, os.path.join(shine, 'View')):
        p = os.path.join(d, name + '.shn')
        if os.path.exists(p):
            return p
    raise SystemExit('%s.shn is in neither %s nor its View/ - is --server a 9Data tree?' % (name, shine))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--server', required=True, help='the deployed server 9Data (the one the zones run)')
    ap.add_argument('--client26', required=True, help='the 2026 client root (its ressystem/ItemInfo.shn)')
    ap.add_argument('--out', required=True, help='where to write the two files (fiesta-proxy deploy/bridge2026)')
    a = ap.parse_args()

    shine = os.path.join(a.server, 'Shine')
    lines = []
    for name in FILES:
        d = open(server_table(shine, name), 'rb').read()
        lines.append(hashlib.md5(d[:0x24] + shn_crypt(d[0x24:])).hexdigest())
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, 'zone-checksums.txt'), 'w', newline='\n') as f:
        f.write(CHECKSUM_HEAD + '\n'.join(lines) + '\n')

    rows = shn.read(os.path.join(a.client26, 'ressystem', 'ItemInfo.shn'))['rows']
    with open(os.path.join(a.out, 'item-classes.txt'), 'w', newline='\n') as f:
        f.write(CLASSES_HEAD % len(rows) + ''.join('%d %d\n' % (r['ID'], r['Class']) for r in sorted(rows, key=lambda r: r['ID'])))
    n = reward_index(shine, a.client26, os.path.join(a.out, 'quest-reward-index.txt'))
    nc = counter_rows(shine, a.client26, os.path.join(a.out, 'quest-counter-rows.txt'))
    folded = sorted((r['ID'], r['Equip']) for r in rows if r['Equip'] in EQUIP_REMAP)
    with open(os.path.join(a.out, 'equip-fold.txt'), 'w', newline='\n') as f:
        f.write(FOLD_HEAD % len(folded)
                + ''.join('fold %d %d\n' % (e, EQUIP_REMAP[e]) for e in sorted(EQUIP_REMAP))
                + ''.join('item %d %d\n' % (i, e) for i, e in folded))
    print('wrote %d checksums, %d item classes, %d quest reward choices, %d folded-slot items and %d moved quest '
          'counters to %s' % (len(lines), len(rows), n, len(folded), nc, a.out))


NL = chr(10)


def reward_index(shine, client26, out):
    import struct
    rows = shn.read(os.path.join(client26, 'ressystem', 'QuestReward.shn'))['rows']
    by_quest = {}
    for r in rows:                                   # file order = the client's index
        by_quest.setdefault(r['ID'], []).append(r)
    server = {q['ID']: q for q in qd.read(os.path.join(shine, 'QuestData.shn'))}
    lines, bad = [], []
    for qid, lst in sorted(by_quest.items()):
        if not any(r['Selectable'] == 2 for r in lst) or qid not in server:
            continue
        slots = [(k, x) for k, x in enumerate(server[qid]['Reward']) if x['Use']]
        used = set()
        for idx, r in enumerate(lst):
            low = r['Flag'] & 0xFFFFFFFF
            hit = next((k for k, x in slots if k not in used and x['Type'] == r['RewardType']
                        and (struct.unpack_from('<H', x['Value'], 0)[0] == low if r['RewardType'] == 2
                             else struct.unpack_from('<I', x['Value'], 0)[0] == low)), None)
            if hit is None:
                bad.append((qid, idx))
                continue
            used.add(hit)
            lines.append('%d %d %d' % (qid, idx, hit))
    with open(out, 'w', newline='') as f:
        f.write('# quest, 2026 client reward index (its QuestReward row order), 2016 QUEST_DATA.Reward slot'
                + NL + '# for NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD. %d rows; %d client rows with no server slot.'
                % (len(lines), len(bad)) + NL + NL.join(lines) + NL)
    if bad:
        print('quest reward rows with no server slot (first 10): %s' % bad[:10])
    return len(lines)


def counter_rows(shine, client26, out):
    rows = {}
    kills = {}
    for r in shn.read(os.path.join(client26, 'ressystem', 'QuestEndNpc.shn'))['rows']:   # file order = client row
        rows.setdefault(r['ID'], []).append((r['MobID'], r['NpcMobActionType']))
        if r['IsEnabled'] and r['NpcMobActionType'] == 1 and r['Count']:
            kills.setdefault(r['ID'], []).append(len(rows[r['ID']]) - 1)
    lines = []
    for q in qd.read(os.path.join(shine, 'QuestData.shn')):
        client = rows.get(q['ID'], [])
        want, used = [], set()
        for k, m in enumerate(q['end']['NPCMobList']):
            if not m['bNPCMob']:
                want.append(k)
                continue
            hit = next((i for i, c in enumerate(client) if i not in used and c == (m['NPCMobID'], m['NPCMobAction'])), k)
            used.add(hit)
            want.append(hit)
        # quest_ext counts an untimed quest's kill rows past the 5 (file order, at most 2) in End_RunningTimeSec: zone
        # counter slots 5 and 6 (entry bytes 30, 31)
        if not q['end']['bTimeLimit'] and len(kills.get(q['ID'], [])) > 5:
            want += [i for i in kills[q['ID']] if i not in used][:2]
        if want != list(range(5)):
            lines.append('%d %s' % (q['ID'], ' '.join(str(i) for i in want)))
    with open(out, 'w', newline='') as f:
        f.write('# quest, then the 2026 client QuestEndNpc row of each zone counter slot (5, or 7 with the two extra kill rows quest_ext counts) (only quests where they'
                + NL + '# differ). %d quests.' % len(lines) + NL + NL.join(lines) + NL)
    return len(lines)


if __name__ == '__main__':
    main()
