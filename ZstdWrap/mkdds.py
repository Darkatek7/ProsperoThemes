#!/usr/bin/env python3
"""Build DDS wallpapers for the PS5's /system_ex/vsh_asset.

Established on firmware 13.00 by decoding the console's own bg_hub_default.dds.zst:

    magic            'DDS '
    dwSize           124
    dwFlags          0x00081007   (CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE)
    dwHeight/dwWidth 2160 x 3840
    dwPitchOrLinear  8294400      (3840 x 2160 -> 1 byte per pixel)
    dwFourCC         'DX10'
    dxgiFormat       98           (DXGI_FORMAT_BC7_UNORM)
    resourceDimension 3           (TEXTURE2D)
    header           148 bytes    (124-byte DDS_HEADER + 20-byte DX10 extension)
    total            8,294,548    (148 + 8,294,400)

So the system wallpaper is a **4K BC7 block-compressed** texture. BC7 is expensive to encode, which is
why these commands exist: they make structurally valid variants that isolate one question at a time.

    python mkdds.py info   <dds>
    python mkdds.py blank  <template.dds> <out.dds>
    python mkdds.py bc1    <template.dds> <out.dds> --rgb RRGGBB

`blank` keeps the template's header byte for byte and zeroes the pixel data (BC7 all-zero blocks decode
to transparent black, so it is a visibly different image with an unchanged structure).
`bc1` rewrites the header to dxgiFormat 71 (DXGI_FORMAT_BC1_UNORM) and fills solid-colour BC1 blocks --
a valid, trivially generated block format at half a byte per pixel, which tests whether the texture
loader honours the DDS header's format or insists on BC7.
"""

import argparse
import struct
import sys

HEADER_BYTES = 148
DXGI_FORMAT_OFFSET = 128
PITCH_OFFSET = 20
HEIGHT_OFFSET = 12
WIDTH_OFFSET = 16

DXGI_BC7_UNORM = 98
DXGI_BC1_UNORM = 71

# texconv's format names, which are what the DDS declares.
TEXCONV_FORMATS = {
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
    28: "R8G8B8A8_UNORM",
    87: "B8G8R8A8_UNORM",
}

LEGACY_FOURCC_FORMATS = {
    b"DXT1": "BC1_UNORM",
    b"DXT3": "BC2_UNORM",
    b"DXT5": "BC3_UNORM",
    b"ATI1": "BC4_UNORM",
    b"BC4U": "BC4_UNORM",
    b"ATI2": "BC5_UNORM",
    b"BC5U": "BC5_UNORM",
}


def header_length(data: bytes) -> int:
    """148 bytes when the pixel format is the DX10 extension, 128 for a legacy fourCC header.

    Not every asset uses the same header. Measured on 13.00: the 15 screen backgrounds are DX10 with a
    148-byte header, while bg_image_load_error is a legacy BC1 header of 128 bytes. Assuming one header
    size silently corrupts the other.
    """
    if len(data) >= 88 and data[84:88] == b"DX10":
        return 148
    return 128


def format_of(data: bytes) -> str:
    """The texture format as texconv names it, from the DX10 extension or a legacy fourCC."""
    if len(data) >= 132 and data[84:88] == b"DX10":
        dxgi = struct.unpack_from("<I", data, DXGI_FORMAT_OFFSET)[0]
        return TEXCONV_FORMATS.get(dxgi, f"dxgi{dxgi}")
    return LEGACY_FOURCC_FORMATS.get(bytes(data[84:88]), "unknown")


def read(path: str) -> bytes:
    with open(path, "rb") as handle:
        return handle.read()


def write(path: str, data: bytes) -> None:
    with open(path, "wb") as handle:
        handle.write(data)


def describe(path: str, data: bytes) -> None:
    if data[:4] != b"DDS ":
        sys.exit(f"{path}: not a DDS (magic {data[:4]!r})")
    height, width, pitch = struct.unpack_from("<III", data, HEIGHT_OFFSET)
    fcc = data[84:88]
    dxgi = struct.unpack_from("<I", data, DXGI_FORMAT_OFFSET)[0] if fcc == b"DX10" else None
    print(f"file             : {path}")
    print(f"total bytes      : {len(data)}")
    print(f"dimensions       : {width} x {height}")
    print(f"dwPitchOrLinear  : {pitch}")
    print(f"dwFourCC         : {fcc!r}")
    print(f"dxgiFormat       : {dxgi}  ({'BC7_UNORM' if dxgi == DXGI_BC7_UNORM else 'BC1_UNORM' if dxgi == DXGI_BC1_UNORM else 'other'})")
    header = header_length(data)
    print(f"header bytes     : {header}  ({'DX10 extension' if header == 148 else 'legacy fourCC'})")
    print(f"format (texconv) : {format_of(data)}")
    print(f"pixel bytes      : {len(data) - header}")
    print(f"bytes per pixel  : {(len(data) - header) / (width * height):.3f}")


def cmd_info(args: argparse.Namespace) -> int:
    describe(args.dds, read(args.dds))
    return 0


def cmd_blank(args: argparse.Namespace) -> int:
    template = read(args.template)
    if template[:4] != b"DDS ":
        sys.exit("template is not a DDS")
    header = header_length(template)
    out = bytearray(template[:header]) + bytearray(len(template) - header)
    write(args.out, bytes(out))
    print(f"blank: {len(template)} -> {len(out)} bytes, header preserved ({header} bytes), pixels zeroed -> {args.out}")
    return 0


def cmd_probe(args: argparse.Namespace) -> int:
    """Machine-readable one-line summary, for scripts that cannot parse the formatted output."""
    data = read(args.dds)
    if data[:4] != b"DDS ":
        sys.exit(f"{args.dds}: not a DDS")
    height, width = struct.unpack_from("<II", data, HEIGHT_OFFSET)
    header = header_length(data)
    print(
        f"WIDTH={width} HEIGHT={height} HEADER={header} FORMAT={format_of(data)} "
        f"TOTAL={len(data)} PIXELS={len(data) - header}"
    )
    return 0


def cmd_bc1(args: argparse.Namespace) -> int:
    template = read(args.template)
    if template[:4] != b"DDS ":
        sys.exit("template is not a DDS")
    height, width = struct.unpack_from("<II", template, HEIGHT_OFFSET)

    rgb = int(args.rgb, 16)
    r, g, b = (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF
    # BC1 endpoints are RGB565.
    endpoint = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)

    # A solid block: colour0 == colour1 == the endpoint, all 32 index bits zero. With equal endpoints
    # BC1 uses its 3-colour mode and index 0 selects colour0, so the block renders as that colour.
    block = struct.pack("<HHI", endpoint, endpoint, 0)
    blocks = (width // 4) * (height // 4)

    header = bytearray(template[:HEADER_BYTES])
    struct.pack_into("<I", header, DXGI_FORMAT_OFFSET, DXGI_BC1_UNORM)
    # BC1 is 8 bytes per 4x4 block.
    struct.pack_into("<I", header, PITCH_OFFSET, blocks * 8)

    out = bytes(header) + block * blocks
    write(args.out, out)
    print(f"bc1: {width}x{height}, rgb #{args.rgb}, rgb565 0x{endpoint:04X}, {blocks} solid blocks")
    print(f"     {len(out)} bytes -> {args.out}")
    return 0


def replace_pixels(reference: bytes, source: bytes) -> bytes:
    """The reference's header with the source's pixels.

    Compared by PIXEL length, not total length: a legacy 128-byte header and a DX10 148-byte header
    differ by 20 bytes while carrying an identical pixel payload, and the point is to keep the
    reference's header (and so its declared format) while taking the source's pixels. Shared by the
    `replace` command and ThemePackCreator so there is one implementation of this rule.
    """
    ref_header = header_length(reference)
    src_header = header_length(source)
    ref_pixels = len(reference) - ref_header
    src_pixels = len(source) - src_header
    if ref_pixels != src_pixels:
        raise ValueError(
            f"pixel-size mismatch: reference {ref_pixels} pixel bytes ({format_of(reference)}), "
            f"source {src_pixels} pixel bytes ({format_of(source)})"
        )
    return reference[:ref_header] + source[src_header:]


def cmd_replace(args: argparse.Namespace) -> int:
    """Take the header from a reference DDS and the pixels from another.

    This is the highest-fidelity way to build a replacement: the header is the console's own, byte for
    byte, so only the pixels differ. Encoders disagree on details such as DDSD_MIPMAPCOUNT (texconv sets
    it, the console's own wallpaper does not), and copying the header removes that whole class of doubt.
    """
    reference = read(args.reference)
    source = read(args.source)
    if reference[:4] != b"DDS ":
        sys.exit("reference is not a DDS")
    if source[:4] != b"DDS ":
        sys.exit("source is not a DDS")

    out = replace_pixels(reference, source)
    write(args.out, out)
    print(f"replace: header from {args.reference} ({header_length(reference)} bytes, {format_of(reference)}), pixels from {args.source} ({header_length(source)} bytes, {format_of(source)})")
    print(f"         {len(out)} bytes -> {args.out}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_info = sub.add_parser("info", help="describe a DDS")
    p_info.add_argument("dds")
    p_info.set_defaults(func=cmd_info)

    p_blank = sub.add_parser("blank", help="clone a template's header and zero the pixels")
    p_blank.add_argument("template")
    p_blank.add_argument("out")
    p_blank.set_defaults(func=cmd_blank)

    p_bc1 = sub.add_parser("bc1", help="rewrite a template as a solid-colour BC1 texture")
    p_bc1.add_argument("template")
    p_bc1.add_argument("out")
    p_bc1.add_argument("--rgb", required=True, help="solid colour as RRGGBB hex")
    p_bc1.set_defaults(func=cmd_bc1)

    p_probe = sub.add_parser("probe", help="machine-readable one-line summary")
    p_probe.add_argument("dds")
    p_probe.set_defaults(func=cmd_probe)

    p_replace = sub.add_parser("replace", help="header from a reference DDS, pixels from another")
    p_replace.add_argument("reference")
    p_replace.add_argument("source")
    p_replace.add_argument("out")
    p_replace.set_defaults(func=cmd_replace)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
