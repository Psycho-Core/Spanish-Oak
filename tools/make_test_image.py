#!/usr/bin/env python3
"""Generate a *synthetic* Spanish Oak 1024k test image, plus a modified copy.

Why this exists: `psychoflash info --in`, `diff` and `checksums` can all be exercised without a
PCM, but they need a file of exactly 0x100000 bytes that is not blank and has calibration-looking
strings in it. No genuine Ford calibration is redistributed with this project (they are licensed
data), so this script builds a stand-in with the right shape:

  * 1,048,576 bytes, no 0x00/0xFF-only content
  * boot block 0x000000-0x00FFFF mostly erased, as a real read of the boot area is
  * calibration strings at offsets where a real image carries them (part number, strategy, catch
    code, VIN)
  * a 16 bit sum16 field at 0x0FFFE0 over the calibration area, so `checksums --b` has something
    real to find
  * a second file that differs only in a 16 byte calibration table, for `diff` and `checksums`

It is NOT a calibration: never flash it. It will not run an engine.

Usage:
    python3 tools/make_test_image.py [--out DIR]
"""
import argparse
import hashlib
import os
import sys

IMAGE_SIZE = 0x100000
BOOT_END = 0x010000
CAL_START = 0x010000
CAL_LENGTH = 0x0F0000
CHECKSUM_OFFSET = 0x0FFE00          # inside the calibration area, near the end
CHECKSUM_SIZE = 2
PAD_OFFSET = CHECKSUM_OFFSET + CHECKSUM_SIZE
PAD_LENGTH = 0x101                  # 257 bytes: any 16 bit sum is reachable with these
if PAD_OFFSET + PAD_LENGTH > IMAGE_SIZE:
    raise SystemExit("the checksum pad does not fit in the image")

STRINGS = [
    (0x000100, "SPANISH OAK 1024K  MCP565  BOOT"),
    (0x000140, "SYNTHETIC TEST IMAGE - NOT A CALIBRATION"),
    (0x010000, "5R29-12A650-CC"),
    (0x010040, "ASU-152  STRATEGY ASU-152"),
    (0x010080, "CATCH CODE HACCKX5  ADR"),
    (0x0100C0, "6FPAAAJGSW4A12345"),
    (0x020000, "FORD MOTOR COMPANY OF AUSTRALIA"),
    (0x030000, "PSYCHOFLASH OFFLINE TEST FIXTURE"),
]


def lcg_bytes(start_seed, length):
    """Deterministic pseudo random filler with the top bit always set.

    High-bit-set bytes are not printable ASCII, so the filler does not produce the accidental
    "strings" that random low bytes would, and `psychoflash info` reports only the strings this
    script actually placed. Real calibration data looks like this too - plenty of 0x80..0xFF.
    """
    out = bytearray(length)
    x = start_seed & 0x7FFFFFFF
    for i in range(length):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = ((x >> 16) & 0x7F) | 0x80
    return out


def write_string(image, offset, text):
    """Writes a NUL padded 64 byte slot, so the string does not run into the next byte."""
    data = text.encode("ascii")
    if len(data) > 63:
        raise SystemExit("string too long for its slot: " + text)
    image[offset:offset + 64] = b"\x00" * 64
    image[offset:offset + len(data)] = data


def total16(image, start, length):
    return sum(image[start:start + length]) & 0xFFFF


def fit_checksum(image, start, length, field, wanted):
    """Store `wanted` at `field` and make the region's sum16 actually equal it.

    The condition is sum16(region, field included) == wanted, i.e.
        rest + (wanted >> 8) + (wanted & 0xFF) + pad == wanted   (mod 65536)
    so the pad has to carry the difference. 257 bytes can hold any 16 bit sum, which is why the
    pad is that length.
    """
    lo = wanted & 0xFF
    hi = (wanted >> 8) & 0xFF
    image[field:field + 2] = bytes([hi, lo])

    rest = total16(image, start, length) - hi - lo
    rest -= sum(image[PAD_OFFSET:PAD_OFFSET + PAD_LENGTH])
    pad_sum = (wanted - hi - lo - rest) % 65536

    image[PAD_OFFSET:PAD_OFFSET + PAD_LENGTH] = b"\x00" * PAD_LENGTH
    remaining = pad_sum
    for i in range(PAD_LENGTH):
        take = min(255, remaining)
        image[PAD_OFFSET + i] = take
        remaining -= take
    if remaining:
        raise SystemExit("pad could not express the checksum")

    if total16(image, start, length) != wanted:
        raise SystemExit("fixture checksum is not self consistent")
    return wanted


def build_stock():
    image = bytearray(lcg_bytes(0x5A5A5A, IMAGE_SIZE))

    # Boot block: a real read is mostly erased there, with the boot loader around the start.
    image[0:BOOT_END] = b"\xFF" * BOOT_END
    image[0x000200:0x002000] = lcg_bytes(0x1234, 0x002000 - 0x000200)

    # The last part of the calibration area is unused flash on a real PCM.
    image[0x0F8000:CAL_START + CAL_LENGTH] = b"\xFF" * (CAL_START + CAL_LENGTH - 0x0F8000)

    for offset, text in STRINGS:
        write_string(image, offset, text)

    value = fit_checksum(image, CAL_START, CAL_LENGTH, CHECKSUM_OFFSET, 0x3C2A)
    return image, value


def build_modified(stock):
    image = bytearray(stock)
    # A 16 byte "calibration table" change, well inside the write window and well under the 1%
    # difference guard (a tune pair differs by a fraction of a percent).
    for i in range(16):
        image[0x0A0100 + i] ^= 0x5A

    value = fit_checksum(image, CAL_START, CAL_LENGTH, CHECKSUM_OFFSET, 0x71B4)
    return image, value


def report(name, image, checksum):
    digest = hashlib.sha256(bytes(image)).hexdigest()
    print("  %-34s %s bytes  sum16 field 0x%04X  sha256 %s" % (name, "{:,}".format(len(image)), checksum, digest))
    return digest


def main():
    parser = argparse.ArgumentParser(description="Generate synthetic Spanish Oak test images")
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "testdata"))
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    stock, stock_sum = build_stock()
    modified, modified_sum = build_modified(stock)

    if len(stock) != IMAGE_SIZE or len(modified) != IMAGE_SIZE:
        raise SystemExit("wrong image size")

    changed = sum(1 for a, b in zip(stock, modified) if a != b)
    if changed > IMAGE_SIZE // 100:
        raise SystemExit("the pair differs more than 1% - checksum discovery would refuse it")

    print("Wrote to " + args.out)
    stock_path = os.path.join(args.out, "spanish-oak-SYNTHETIC-stock.bin")
    tuned_path = os.path.join(args.out, "spanish-oak-SYNTHETIC-modified.bin")
    open(stock_path, "wb").write(stock)
    open(tuned_path, "wb").write(modified)

    report("spanish-oak-SYNTHETIC-stock.bin", stock, stock_sum)
    report("spanish-oak-SYNTHETIC-modified.bin", modified, modified_sum)
    print("  differing bytes: %d (%.4f%% of the image)" % (changed, 100.0 * changed / IMAGE_SIZE))
    print()
    print("These are fixtures, not calibrations. Never flash them.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
