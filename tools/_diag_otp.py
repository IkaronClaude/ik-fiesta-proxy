"""Diagnostic: extract Login [0C0C] OTP and search for it inside the captured
WM [0C0F] decrypted payload. Confirms whether the OTP is relayed byte-for-byte
(in which case chained replay with OTP patching is a clean fix)."""
import collections
import sys

import dpkt

from _fiesta_proto import XorCipher, opcode_of, parse_frames, payload_of


def load_streams(path):
    bufs = collections.defaultdict(bytearray)
    seqs = {}
    with open(path, "rb") as f:
        magic = f.read(4)
        f.seek(0)
        rdr = dpkt.pcapng.Reader(f) if magic == b"\x0a\x0d\x0d\x0a" else dpkt.pcap.Reader(f)
        for _ts, raw in rdr:
            try:
                eth = dpkt.ethernet.Ethernet(raw)
                ip = eth.data
            except Exception:
                continue
            if not isinstance(ip, dpkt.ip.IP) or not isinstance(ip.data, dpkt.tcp.TCP):
                continue
            tcp = ip.data
            data = bytes(tcp.data)
            if not data:
                continue
            key = (bytes(ip.src), tcp.sport, bytes(ip.dst), tcp.dport)
            if key not in seqs:
                seqs[key] = tcp.seq
            off = (tcp.seq - seqs[key]) & 0xFFFFFFFF
            buf = bufs[key]
            end = off + len(data)
            if end > len(buf):
                buf.extend(b"\x00" * (end - len(buf)))
            buf[off:end] = data
    return {k: bytes(v) for k, v in bufs.items()}


def first_seed(data):
    for _off, _plen, body in parse_frames(data):
        if opcode_of(body) == 0x0807 and len(body) >= 4:
            return body[2] | (body[3] << 8)
    return None


def find_frame(data, opcode):
    for _off, _plen, body in parse_frames(data):
        if opcode_of(body) == opcode:
            return payload_of(body)
    return None


def main(path):
    streams = load_streams(path)
    login_s2c = max((v for k, v in streams.items() if k[1] == 9010), key=len, default=None)
    wm_s2c = max((v for k, v in streams.items() if k[1] == 9013), key=len, default=None)
    wm_c2s = max((v for k, v in streams.items() if k[3] == 9013), key=len, default=None)
    print(f"Login S->C: {len(login_s2c) if login_s2c else 0} bytes")
    print(f"WM    S->C: {len(wm_s2c) if wm_s2c else 0} bytes")
    print(f"WM    C->S: {len(wm_c2s) if wm_c2s else 0} bytes")

    pl_0c0c = find_frame(login_s2c, 0x0C0C)
    if not pl_0c0c:
        print("No [0C0C] in Login S->C")
        return
    print(f"Login [0C0C]: status={pl_0c0c[0]} payload_len={len(pl_0c0c)}")
    # status(1) + ip(16) + port(2) + OTP
    otp = pl_0c0c[19:]
    print(f"OTP ({len(otp)} bytes): {otp.hex()}")

    wm_seed = first_seed(wm_s2c)
    print(f"WM handshake seed: 0x{wm_seed:04X}")
    wm_c2s_plain = XorCipher(wm_seed).transform(wm_c2s)
    pl_0c0f = find_frame(wm_c2s_plain, 0x0C0F)
    if not pl_0c0f:
        print("No [0C0F] in decrypted WM C->S — looking for any auth-shaped frame:")
        for _off, _plen, body in parse_frames(wm_c2s_plain):
            print(f"  C->S decrypted opcode=0x{opcode_of(body):04X} len={len(payload_of(body))}")
        return
    print(f"WM [0C0F] payload_len={len(pl_0c0f)}")
    idx = pl_0c0f.find(otp)
    print(f"OTP byte-substring offset in [0C0F]: {idx}")
    if idx >= 0:
        before = pl_0c0f[max(0, idx - 16):idx].hex()
        after = pl_0c0f[idx + len(otp):idx + len(otp) + 16].hex()
        print(f"  ...{before} [OTP {idx}:{idx + len(otp)}] {after}...")
    else:
        # try shorter prefixes
        for n in (32, 16, 8):
            idx2 = pl_0c0f.find(otp[:n])
            print(f"  first {n} bytes match: {idx2}")


if __name__ == "__main__":
    main(sys.argv[1])
