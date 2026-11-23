#!/usr/bin/env python3
"""
seedkey_reference.py - Ford EEC-VI (Spanish Oak) service 0x27 seed/key generator.

This is the algorithm recovered from the reference tool by static analysis and then
verified instruction-by-instruction against an emulation of the original method body
(see docs/REVERSE_ENGINEERING.md). The PCM sends a three byte seed, the tester answers
0x27 0x02 followed by the three byte key.

The five application constants below are the ones the Falcon (BF/FG/FGII/FGX) Spanish Oak
1024k PCM expects: 0x08 0x30 0x61 0xA4 0xC5.

Usage:
    python3 seedkey_reference.py 6A3F12
    python3 seedkey_reference.py --self-test
"""

import random
import sys

M32 = 0xFFFFFFFF


def _lfsr(state, word, rounds=32):
    """24 bit Galois LFSR, tap mask 0xEF6FD7, one input bit per round (LSB first)."""
    for i in range(rounds):
        feedback = ((word >> (i & 0x1F)) & 1) ^ (state & 1)
        t = ((feedback << 23) | (state >> 1)) & M32
        f = (t & 0x800000) >> 23
        nxt = t & 0xEF6FD7
        nxt |= (((t & 0x100000) >> 20) ^ f) << 20
        nxt |= ((((state >> 1) & 0x8000) >> 15) ^ f) << 15
        nxt |= ((((state >> 1) & 0x1000) >> 12) ^ f) << 12
        nxt |= 0x20 * ((((state >> 1) & 0x20) >> 5) ^ f)
        nxt |= 8 * ((((state >> 1) & 0x08) >> 3) ^ f)
        state = nxt & M32
    return state


def keygen(seed, k0=0x08, k1=0x30, k2=0x61, k3=0xA4, k4=0xC5):
    """Returns the 24 bit key for a 24 bit seed."""
    b0 = (seed >> 16) & 0xFF
    b1 = (seed >> 8) & 0xFF
    b2 = seed & 0xFF
    s = (b0 << 16) + (b1 << 8) + b2

    word1 = ((s & 0xFF0000) >> 16) | (s & 0xFF00) | ((k0 & 0xFF) << 24) | ((s & 0xFF) << 16)
    word2 = ((k4 & 0xFF) << 24) | ((k3 & 0xFF) << 16) | ((k2 & 0xFF) << 8) | (k1 & 0xFF)

    state = _lfsr(0xC541A9, word1 & M32)
    state = _lfsr(state, word2 & M32)

    out = (state & 0x0F0000) >> 16
    out |= 0x10 * (state & 0x0F)
    out |= (((state & 0xF00000) >> 20) | ((state & 0xF000) >> 8)) << 8
    out |= ((state & 0x0FF0) >> 4) << 16
    return out & 0xFFFFFF


# Vectors cross-checked against the emulated original IL (see recover_il.py --emulate).
VECTORS = [
    (0x000000, 0xDAA9C2),
    (0x000001, 0x9BC78F),
    (0x123456, 0x1DADBF),
    (0xFFFFFF, 0xB1541B),
    (0xA5C3F1, 0xBBA402),
    (0x43DC13, 0xF7E84D),
    (0x0B522C, 0x8090D9),
    (0xDE6616, 0xADEB39),
    (0xC42ACD, 0x1EE582),
    (0x7F4150, 0xFFC5B6),
    (0x25BA23, 0x515B55),
    (0x9E48D1, 0xB7CB43),
]


def self_test():
    failures = 0
    for seed, expected in VECTORS:
        got = keygen(seed)
        ok = got == expected
        failures += not ok
        print(f"  seed 0x{seed:06X} -> 0x{got:06X} (expected 0x{expected:06X}) {'OK' if ok else 'MISMATCH'}")

    # The output must be a pure function of the seed and stay in 24 bits.
    for _ in range(1000):
        seed = random.randrange(0x1000000)
        key = keygen(seed)
        if key > 0xFFFFFF or key != keygen(seed):
            failures += 1
            print(f"  property check failed for 0x{seed:06X}")
    print(f"\n{len(VECTORS) + 1000} checks, {failures} failures")
    return 0 if failures == 0 else 1


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if argv[0] == "--self-test":
        return self_test()

    text = argv[0].lower().replace("0x", "").replace(" ", "").replace("-", "")
    seed = int(text, 16)
    key = keygen(seed)
    print(f"seed 0x{seed:06X}  ->  key 0x{key:06X}   (send: 27 02 {key >> 16 & 0xFF:02X} {key >> 8 & 0xFF:02X} {key & 0xFF:02X})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
