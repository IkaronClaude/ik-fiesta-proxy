"""SHN table reader/writer (column format). Byte-identical round-trip on every 2016/2026 client table.

read(path) -> {'cryptHeader', 'header', 'recordCount', 'recordLength', 'columns': [(name, type, len)],
               'rawnames': [bytes], 'rows': [ {name: value} ]}
write(path, table)

Column widths come from the header's own length field, never from the type code: the 2026 client uses
type 29 with length 4 (a localisation id) where the 2016 files use 29 with length 8. Unknown numeric
types are read as little-endian unsigned ints of the declared width, so the writer can put them back.
"""
import hashlib
import os
import pickle
import struct
import sys

# string types (fixed padded) and the one variable-length string type
try:
    import numpy as _np
except ImportError:            # the pure-Python loop still works, just slower
    _np = None

PADDED = (9, 10, 24)
VARSTR = 26
FLOAT = 5
SIGNED = (13, 20, 21, 22)


def crypt(d):
    """the SHN XOR (symmetric). The key stream depends only on the length: key[n-1] = n & 0xFF and
    key[i-1] = key[i] ^ g(i), g(i) = ((i & 15) + 0x55) ^ (i * 11) ^ 0xAA (bytes) - so it is a reverse cumulative XOR,
    vectorised with numpy (13x faster, byte-identical; the loop below is the reference and the fallback)."""
    if _np is not None and len(d):
        n = len(d)
        i = _np.arange(n, dtype=_np.int64)
        g = ((((i & 0x0F) + 0x55) & 0xFF) ^ ((i * 11) & 0xFF) ^ 0xAA).astype(_np.uint8)
        rev = _np.bitwise_xor.accumulate(g[::-1])[::-1]
        key = _np.empty(n, dtype=_np.uint8)
        key[n - 1] = n & 0xFF
        key[:n - 1] = (n & 0xFF) ^ rev[1:]
        return (_np.frombuffer(bytes(d), dtype=_np.uint8) ^ key).tobytes()
    return crypt_py(d)


def crypt_py(d):
    d = bytearray(d)
    n = len(d)
    key = n & 0xFF
    for i in range(n - 1, -1, -1):
        d[i] ^= key
        nk = (i & 0xFF) & 0x0F
        nk = (nk + 0x55) & 0xFF
        nk ^= ((i & 0xFF) * 11) & 0xFF
        nk ^= key
        nk ^= 0xAA
        key = nk & 0xFF
    return bytes(d)


def _dec(b):
    return b.decode('cp949', 'surrogateescape')


def _enc(s):
    # collab's LosslessEucKr keeps a byte cp949 cannot decode (the 2026 client's cp1252 text, e.g. "D\xf6ner") as
    # U+F700 + byte; text copied from the collab JSON into a table written here carries those, so they go back to the byte
    if any('' <= c <= '' for c in s):
        out = bytearray()
        for c in s:
            out += bytes([ord(c) - 0xF700]) if '' <= c <= '' else c.encode('cp949', 'surrogateescape')
        return bytes(out)
    return s.encode('cp949', 'surrogateescape')




def read(path, rows=True):
    """read a column SHN (no cache in this copy)"""
    return _read(path, rows)


def _read(path, rows=True):
    b = open(path, 'rb').read()
    ch = b[:32]
    dl = struct.unpack_from('<I', b, 32)[0]
    if dl != len(b):
        raise ValueError('not a column shn (len %d != %d)' % (dl, len(b)))
    d = crypt(b[36:])
    hdr, rc, rl, cc = struct.unpack_from('<IIII', d, 0)
    o = 16
    cols = []
    raw = []
    unk = 0
    for i in range(cc):
        rn = d[o:o + 48]
        name = _dec(rn.split(b'\0')[0]).strip()
        tc, ln = struct.unpack_from('<Ii', d, o + 48)
        o += 56
        if len(name) < 2:
            name = 'Undefined%d' % unk
            unk += 1
        cols.append((name, tc, ln))
        raw.append(rn)
    out = {'cryptHeader': ch, 'header': hdr, 'recordCount': rc, 'recordLength': rl,
           'columns': cols, 'rawnames': raw, 'rows': []}
    if not rows:
        return out
    for r in range(rc):
        p = o + 2  # skip the row length prefix
        row = {}
        for name, tc, ln in cols:
            if tc in PADDED:
                v = _dec(d[p:p + ln].split(b'\0')[0])
                p += ln
            elif tc == VARSTR:
                e = d.index(b'\0', p)
                v = _dec(d[p:e])
                p = e + 1
            elif tc == FLOAT:
                v = struct.unpack_from('<f', d, p)[0]
                p += 4
            else:
                v = int.from_bytes(d[p:p + ln], 'little', signed=tc in SIGNED)
                p += ln
            row[name] = v
        out['rows'].append(row)
        o = p
    return out


def write(path, table):
    cols = table['columns']
    raw = table.get('rawnames')
    body = bytearray()
    body += struct.pack('<IIII', table['header'], len(table['rows']), 2 + sum(c[2] for c in cols), len(cols))
    for i, (name, tc, ln) in enumerate(cols):
        if raw and i < len(raw) and raw[i] is not None:
            nb = raw[i]
        else:
            nb = _enc(' ' if name.startswith('Undefined') else name)[:48]
            nb = nb + b'\0' * (48 - len(nb))
        body += nb + struct.pack('<Ii', tc, ln)
    for row in table['rows']:
        rb = bytearray()
        for name, tc, ln in cols:
            v = row[name]
            if tc in PADDED:
                sb = _enc(v)[:ln]
                rb += sb + b'\0' * (ln - len(sb))
            elif tc == VARSTR:
                rb += _enc(v) + b'\0'
            elif tc == FLOAT:
                rb += struct.pack('<f', v)
            else:
                rb += int(v).to_bytes(ln, 'little', signed=tc in SIGNED)
        body += struct.pack('<H', len(rb) + 2) + rb
    enc = crypt(bytes(body))
    with open(path, 'wb') as fh:
        fh.write(table['cryptHeader'] + struct.pack('<I', len(enc) + 36) + enc)


def new_like(src, columns=None, rawnames=None):
    """An empty table carrying src's crypt header and file header."""
    return {'cryptHeader': src['cryptHeader'], 'header': src['header'], 'recordCount': 0,
            'recordLength': 0, 'columns': columns or src['columns'],
            'rawnames': rawnames if rawnames is not None else src.get('rawnames'), 'rows': []}


if __name__ == '__main__':
    t = read(sys.argv[1], rows=False)
    print(t['recordCount'], 'rows', t['recordLength'], 'reclen')
    for c in t['columns']:
        print(' ', c)
