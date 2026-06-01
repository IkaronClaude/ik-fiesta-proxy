"""
Shared Fiesta protocol primitives for the inspector + replay tools.

Wire framing:
  * Length prefix is 1 byte (1..254) OR 0x00 + 2 bytes LE.
  * Frame body = opcode (LE u16) + payload.
  * The length prefix itself is plaintext; the body is XOR'd when the
    cipher is enabled on that direction.

Cipher (lifted from Ikaron/fiesta-filter):
  * 515-byte fixed XOR table.
  * Position is per-direction, wraps mod 515.
  * Server kicks the cipher on by sending a 4-byte plaintext frame
    [length=4][0x07 0x08 posLo posHi] S→C. After that, C→S bytes are
    XOR'd starting at the seed; S→C remains plaintext.
"""
from __future__ import annotations

import os


def _load_xor_table() -> bytes:
    """Bring-your-own c2s cipher table. Provide it via env -- it is NOT
    shipped (game-derived data). Priority:
      * XOR_TABLE_HEX  -- hex string (whitespace ignored)
      * XOR_TABLE_PATH -- path to a file of hex
    """
    hx = os.environ.get("XOR_TABLE_HEX")
    if not hx:
        path = os.environ.get("XOR_TABLE_PATH")
        if path:
            with open(path, "r", encoding="utf-8") as f:
                hx = f.read()
    if not hx:
        raise SystemExit(
            "XOR table not configured. Set XOR_TABLE_HEX or XOR_TABLE_PATH "
            "(the c2s cipher table is bring-your-own; it is not shipped)."
        )
    return bytes.fromhex("".join(hx.split()))


XOR_TABLE = _load_xor_table()


class XorCipher:
    __slots__ = ("pos",)

    def __init__(self, start_pos: int = 0) -> None:
        self.pos = start_pos % len(XOR_TABLE)

    def transform(self, data: bytes) -> bytes:
        n = len(XOR_TABLE)
        out = bytearray(len(data))
        pos = self.pos
        tbl = XOR_TABLE
        for i, b in enumerate(data):
            out[i] = b ^ tbl[pos]
            pos += 1
            if pos >= n:
                pos -= n
        self.pos = pos
        return bytes(out)


def is_handshake_body(body: bytes) -> tuple[bool, int]:
    """Return (is_handshake, seed). Body = opcode + payload, length-prefix already stripped."""
    if len(body) == 4 and body[0] == 0x07 and body[1] == 0x08:
        return True, body[2] | (body[3] << 8)
    return False, 0


def encode_frame(body: bytes) -> bytes:
    """body = opcode + payload (already cipher-applied if needed).
    Inline length is 1 byte = 1..255. Extended is [0x00][LO][HI] little-endian
    u16; 0x00 is reserved as the extension marker so it can't appear inline."""
    n = len(body)
    if n <= 0xFF:
        return bytes([n]) + body
    return bytes([0x00, n & 0xFF, (n >> 8) & 0xFF]) + body


def parse_frames(buf: bytes):
    """Yield (offset, length_prefix_bytes, body_bytes) until buf runs out."""
    i = 0
    n = len(buf)
    while i < n:
        start = i
        first = buf[i]
        if first != 0x00:
            blen = first
            i += 1
            prefix_len = 1
        else:
            if i + 2 >= n:
                return
            blen = buf[i + 1] | (buf[i + 2] << 8)
            i += 3
            prefix_len = 3
        if blen < 2 or i + blen > n:
            return
        body = bytes(buf[i:i + blen])
        yield start, prefix_len, body
        i += blen


def opcode_of(body: bytes) -> int:
    return body[0] | (body[1] << 8)


def payload_of(body: bytes) -> bytes:
    return body[2:]


def ip_to_str(b: bytes) -> str:
    return ".".join(str(x) for x in b)
