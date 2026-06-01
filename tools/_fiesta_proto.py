"""
Shared Fiesta protocol primitives for the inspector + replay tools.

Wire framing:
  * Length prefix is 1 byte (1..254) OR 0x00 + 2 bytes LE.
  * Frame body = opcode (LE u16) + payload.
  * The length prefix itself is plaintext; the body is XOR'd when the
    cipher is enabled on that direction.

Cipher (same shape as Ikaron/fiesta-filter):
  * Fixed XOR table, bring-your-own (see load_xor_table below) — this repo
    ships no table.
  * Position is per-direction, wraps mod len(table).
  * Server kicks the cipher on by sending a 4-byte plaintext frame
    [length=4][0x07 0x08 posLo posHi] S→C. After that, C→S bytes are
    XOR'd starting at the seed; S→C remains plaintext.
"""
from __future__ import annotations

import os


_XOR_TABLE: bytes | None = None


def _parse_hex(s: str) -> bytes | None:
    """Parse a hex string, tolerating whitespace, commas and 0x prefixes."""
    out: list[str] = []
    i = 0
    while i < len(s):
        c = s[i]
        if c.isspace() or c == ",":
            i += 1
            continue
        if c == "0" and i + 1 < len(s) and s[i + 1] in "xX":
            i += 2
            continue
        out.append(c)
        i += 1
    h = "".join(out)
    if not h or len(h) % 2 != 0:
        return None
    try:
        return bytes.fromhex(h)
    except ValueError:
        return None


def load_xor_table() -> bytes:
    """
    Load the bring-your-own C->S XOR cipher table. Cached after first call.

    Sources, in priority order (matches the C# XorTableLoader contract):
      1. XOR_TABLE_HEX  -- inline hex string (whitespace / commas / 0x ok).
      2. XOR_TABLE_PATH -- file containing hex text, or the raw binary table.

    This repo ships no table: different server builds may use different
    tables, and the table is part of the protocol-licensing question this
    project deliberately takes no stance on. Tools that decrypt C->S
    traffic require the operator to supply one.
    """
    global _XOR_TABLE
    if _XOR_TABLE is not None:
        return _XOR_TABLE

    hex_env = os.environ.get("XOR_TABLE_HEX")
    if hex_env and hex_env.strip():
        parsed = _parse_hex(hex_env)
        if parsed is None:
            raise ValueError("XOR_TABLE_HEX is set but not valid hex")
        _XOR_TABLE = parsed
        return _XOR_TABLE

    path = os.environ.get("XOR_TABLE_PATH")
    if path and path.strip():
        if not os.path.isfile(path):
            raise FileNotFoundError(f"XOR_TABLE_PATH '{path}' does not exist")
        with open(path, "rb") as fh:
            raw = fh.read()
        # Try hex-as-text first; fall back to treating the file as raw binary.
        try:
            parsed = _parse_hex(raw.decode("ascii"))
        except UnicodeDecodeError:
            parsed = None
        _XOR_TABLE = parsed if parsed is not None else raw
        return _XOR_TABLE

    raise RuntimeError(
        "No XOR table configured. This tool needs the C->S cipher table; "
        "supply it via XOR_TABLE_HEX or XOR_TABLE_PATH (bring-your-own -- "
        "see lib/fiesta-proxy/README.md)."
    )


class XorCipher:
    __slots__ = ("pos", "_tbl")

    def __init__(self, start_pos: int = 0) -> None:
        self._tbl = load_xor_table()
        self.pos = start_pos % len(self._tbl)

    def transform(self, data: bytes) -> bytes:
        tbl = self._tbl
        n = len(tbl)
        out = bytearray(len(data))
        pos = self.pos
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
