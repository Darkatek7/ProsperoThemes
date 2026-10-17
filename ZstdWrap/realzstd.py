#!/usr/bin/env python3
"""PC-side Zstandard frame tool (real compression).

The C# code beside this file (ZstdRawFrame.cs) is deliberately dependency-free so it can ship inside
the PS5 app, but it only emits Raw and RLE blocks -- it cannot compress. This script is the PC-side
authoring and verification path: it uses python-zstandard for real compression and decompression, so a
theme frame can be built at a sensible size and any console frame can be decoded and inspected.

    python realzstd.py info    <frame>
    python realzstd.py decode  <frame> <out>
    python realzstd.py encode  <in> <frame> [--level N]

Requires: python -m pip install zstandard
"""

import argparse
import sys

try:
    import zstandard as zstd
except ImportError:
    sys.exit("python-zstandard is not installed. Run: python -m pip install zstandard")


def read(path: str) -> bytes:
    with open(path, "rb") as handle:
        return handle.read()


def encode_bytes(content: bytes, level: int = 19) -> bytes:
    """Compress bytes into a frame shaped like the console's own.

    Shared with ThemePackCreator so there is one implementation of "what a PS5 wallpaper frame looks
    like": a declared content size, no checksum, no dictionary id, and a 4 MiB window to match the
    descriptor (0x60) the system wallpapers use.
    """
    params = zstd.ZstdCompressionParameters.from_level(
        level, window_log=22, write_content_size=1, write_checksum=0, write_dict_id=0
    )
    return zstd.ZstdCompressor(compression_params=params).compress(content)


def decode_bytes(frame: bytes) -> bytes:
    """Decompress a complete frame; see decompress() for why a stream reader is used."""
    return decompress(frame)


def decompress(frame: bytes) -> bytes:
    """Decompress a complete frame.

    A stream reader is used rather than ZstdDecompressor.decompress because the latter refuses frames
    that do not declare a content size -- and the console's own wallpaper frames are not guaranteed to.
    """
    reader = zstd.ZstdDecompressor().stream_reader(frame)
    try:
        return reader.read()
    finally:
        reader.close()


def cmd_info(args: argparse.Namespace) -> int:
    frame = read(args.frame)
    print(f"file            : {args.frame}")
    print(f"frame bytes     : {len(frame)}")
    print(f"magic           : {frame[:4].hex(' ')}"
          f"  ({'ok' if frame[:4] == bytes([0x28, 0xB5, 0x2F, 0xFD]) else 'NOT a zstd frame'})")
    try:
        print(f"declared size   : {zstd.frame_content_size(frame)}")
    except Exception as exc:  # noqa: BLE001 - any failure here just means "not declared"
        print(f"declared size   : unavailable ({exc})")
    try:
        content = decompress(frame)
        print(f"actual size     : {len(content)}")
        print(f"content head    : {content[:4]!r}")
    except Exception as exc:  # noqa: BLE001
        print(f"actual size     : could not decompress ({exc})")
    return 0


def cmd_decode(args: argparse.Namespace) -> int:
    frame = read(args.frame)
    content = decompress(frame)
    with open(args.out, "wb") as handle:
        handle.write(content)
    print(f"decoded {len(frame)} frame bytes -> {len(content)} content bytes -> {args.out}")
    return 0


def cmd_encode(args: argparse.Namespace) -> int:
    content = read(args.input)
    frame = encode_bytes(content, args.level)
    with open(args.out, "wb") as handle:
        handle.write(frame)
    ratio = len(content) / len(frame) if frame else 0
    print(f"encoded {len(content)} -> {len(frame)} bytes (level {args.level}, ratio {ratio:.2f}x) -> {args.out}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_info = sub.add_parser("info", help="describe a frame")
    p_info.add_argument("frame")
    p_info.set_defaults(func=cmd_info)

    p_decode = sub.add_parser("decode", help="decompress a frame to a file")
    p_decode.add_argument("frame")
    p_decode.add_argument("out")
    p_decode.set_defaults(func=cmd_decode)

    p_encode = sub.add_parser("encode", help="compress a file into a frame")
    p_encode.add_argument("input")
    p_encode.add_argument("out")
    p_encode.add_argument("--level", type=int, default=19, help="compression level (default 19)")
    p_encode.set_defaults(func=cmd_encode)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
