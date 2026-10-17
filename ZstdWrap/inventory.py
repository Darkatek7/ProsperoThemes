#!/usr/bin/env python3
"""Inventory the console's wallpaper assets.

Decodes every *.dds.zst frame in a directory and reports what is really inside. This matters because
the assets are NOT uniform: on firmware 13.00 they differ in size, in dimensions and in block format.
Assuming one format for all of them is how a theme ends up unrenderable on some screens.

    python inventory.py <directory>
"""

import os
import struct
import sys

import zstandard as zstd

DXGI_NAMES = {
    71: "BC1_UNORM",
    72: "BC1_UNORM_SRGB",
    74: "BC2_UNORM",
    77: "BC3_UNORM",
    80: "BC4_UNORM",
    83: "BC5_UNORM",
    95: "BC6H_TYPELESS",
    96: "BC6H_UF16",
    98: "BC7_UNORM",
    99: "BC7_UNORM_SRGB",
    10: "R16G16B16A16_FLOAT",
    11: "R16G16B16A16_UNORM",
    28: "R8G8B8A8_UNORM",
    29: "R8G8B8A8_UNORM_SRGB",
    87: "B8G8R8A8_UNORM",
    88: "B8G8R8A8_UNORM_SRGB",
}


def describe(path: str) -> dict:
    raw = open(path, "rb").read()
    row = {"name": os.path.basename(path), "frame": len(raw), "content": None, "note": ""}

    if raw[:4] != bytes([0x28, 0xB5, 0x2F, 0xFD]):
        row["note"] = f"not a zstd frame (first bytes {raw[:4].hex(' ')})"
        return row

    reader = zstd.ZstdDecompressor().stream_reader(raw)
    try:
        content = reader.read()
    finally:
        reader.close()
    row["content"] = len(content)

    if content[:4] != b"DDS ":
        printable = "".join(chr(b) if 32 <= b < 127 else "." for b in content[:16])
        row["note"] = f"NOT a DDS (head {content[:4].hex(' ')}, ascii {printable!r})"
        return row

    height, width = struct.unpack_from("<II", content, 12)
    fcc = content[84:88]
    dxgi = struct.unpack_from("<I", content, 128)[0] if fcc == b"DX10" else None
    pixels = len(content) - 148

    row.update(
        width=width,
        height=height,
        fourcc=fcc.decode("ascii", "replace"),
        dxgi=dxgi,
        fmt=DXGI_NAMES.get(dxgi, f"dxgi {dxgi}"),
        pixels=pixels,
        bpp=(pixels / (width * height)) if width and height else 0,
    )
    return row


def main() -> int:
    directory = sys.argv[1] if len(sys.argv) > 1 else "."
    files = sorted(f for f in os.listdir(directory) if f.endswith(".dds.zst"))
    if not files:
        sys.exit(f"no *.dds.zst in {directory}")

    print(f"{'asset':<36} {'frame':>9} {'content':>9} {'size':>11} {'format':<14} {'B/px':>5}")
    print("-" * 92)
    for name in files:
        row = describe(os.path.join(directory, name))
        if row["note"]:
            print(f"{row['name']:<36} {row['frame']:>9} {str(row['content'] or '-'):>9} {'':>11} {'':<14} {'':>5}  {row['note']}")
        else:
            print(
                f"{row['name']:<36} {row['frame']:>9} {row['content']:>9} "
                f"{row['width']}x{row['height']:<6} {row['fmt']:<14} {row['bpp']:>5.3f}"
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
