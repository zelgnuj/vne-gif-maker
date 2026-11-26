#!/usr/bin/env python3
"""Packs PNGs from the macOS .iconset (tools/make-icon.swift) into a Windows .ico.
Usage: make-ico.py <AppIcon.iconset> <out.ico>"""
import struct, sys, os
src, out = sys.argv[1], sys.argv[2]
names = {16: "icon_16x16.png", 32: "icon_32x32.png", 48: None, 64: "icon_32x32@2x.png",
         128: "icon_128x128.png", 256: "icon_256x256.png"}
entries = [(s, open(os.path.join(src, n), "rb").read()) for s, n in names.items() if n]
header = struct.pack("<HHH", 0, 1, len(entries))
offset = 6 + 16 * len(entries)
dirs, blobs = b"", b""
for size, data in entries:
    dirs += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
open(out, "wb").write(header + dirs + blobs)
print(f"{out}: {len(entries)} sizes")
