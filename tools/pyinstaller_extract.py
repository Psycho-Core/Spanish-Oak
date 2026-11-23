#!/usr/bin/env python3
"""
pyinstaller_extract.py - unpack a PyInstaller one-file bundle.

Written for the reverse-engineering write-up of "Spanish Oak Flash Tool.exe", which turned
out to be a PyInstaller (Python 3.10) dropper rather than the .NET program it appears to be.

The bundle layout is:

    [PE stub][-- PKG archive --][cookie (88 bytes)]

where the cookie is

    b'MEI\\x0c\\x0b\\x0a\\x0b\\x0e'
    uint32 package_length        (big endian, counts back from the cookie)
    uint32 toc_offset            (relative to the start of the PKG archive)
    uint32 toc_length
    uint32 python_version        (310 for Python 3.10)
    char[64] python_library      ("python310.dll")

and every TOC entry is

    uint32 entry_length
    uint32 offset                (relative to the start of the PKG archive)
    uint32 compressed_length
    uint32 uncompressed_length
    uint8  compression_flag      (1 = zlib deflate)
    char   type_code             b'm' module, b's' script, b'b' binary, b'z' PYZ, b'o' option
    char[] name                   NUL terminated

Usage:
    python3 pyinstaller_extract.py "Spanish Oak Flash Tool.exe" -o out/

No third-party modules are required.
"""

import argparse
import os
import struct
import sys
import zlib

COOKIE_MAGIC = b"MEI\x0c\x0b\x0a\x0b\x0e"


def parse_bundle(path):
    with open(path, "rb") as handle:
        blob = handle.read()

    cookie_pos = blob.rfind(COOKIE_MAGIC)
    if cookie_pos < 0:
        raise SystemExit("No PyInstaller cookie found - this is not a one-file bundle.")

    (pkg_len, toc_off, toc_len, pyver) = struct.unpack_from("!IIII", blob, cookie_pos + 8)
    pylib = blob[cookie_pos + 24:cookie_pos + 88].split(b"\x00")[0].decode("ascii", "replace")

    start = len(blob) - pkg_len
    if start < 0:
        raise SystemExit("Cookie reports a package larger than the file.")

    toc = blob[start + toc_off:start + toc_off + toc_len]
    entries = []
    pos = 0
    while pos < len(toc):
        entry_len, offset, clen, ulen, cflag = struct.unpack_from("!IIIIB", toc, pos)
        typecode = chr(toc[pos + 17])
        name = toc[pos + 18:pos + entry_len].split(b"\x00")[0].decode("utf-8", "replace")
        entries.append({
            "type": typecode,
            "name": name,
            "offset": offset,
            "compressed": clen,
            "uncompressed": ulen,
            "flag": cflag,
        })
        pos += entry_len

    return blob, start, entries, pyver, pylib


def main(argv):
    parser = argparse.ArgumentParser(description="Unpack a PyInstaller one-file bundle.")
    parser.add_argument("exe", help="the PyInstaller executable")
    parser.add_argument("-o", "--out", default="extracted", help="output directory")
    parser.add_argument("--list-only", action="store_true", help="only print the table of contents")
    args = parser.parse_args(argv)

    blob, start, entries, pyver, pylib = parse_bundle(args.exe)

    print(f"archive start : 0x{start:X}")
    print(f"python        : {pyver // 100}.{pyver % 100:02d}    runtime: {pylib}")
    print(f"entries       : {len(entries)}")
    print()

    for entry in entries:
        print(f"  [{entry['type']}] {entry['name']:<40} {entry['uncompressed']:>10} bytes")
    if args.list_only:
        return 0

    os.makedirs(args.out, exist_ok=True)
    for entry in entries:
        raw = blob[start + entry["offset"]:start + entry["offset"] + entry["compressed"]]
        data = zlib.decompress(raw) if entry["flag"] else raw
        safe = entry["name"].replace("/", "_").replace("\\", "_")
        target = os.path.join(args.out, safe)
        with open(target, "wb") as handle:
            handle.write(data)
        print(f"wrote {target} ({len(data)} bytes)")

    print()
    print("Notes for reversing:")
    print("  * 's' entries are raw marshalled code objects (no .pyc header). Rebuild one with")
    print("    importlib.util.MAGIC_NUMBER + b'\\0'*12, then load it with xdis for Python 3.10.")
    print("  * A 'Spanish' script entry that is ~35 MB decompressed usually means it embeds a")
    print("    base64 PE image (look for 'TVqQAAMAAAA' at the start of a constant).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
