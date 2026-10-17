"""Package artwork and Inter glyph atlases without requiring console font services.

Source artwork is retained unchanged; runtime copies are resized/format-converted.
Run from any directory: python ThemeApp/tools/bake-assets.py [--fonts-only] [--only inter-48,inter-28-semibold]
--only bakes just the named atlases (their file stems) and never touches the artwork.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageOps
import math
import struct
import sys

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
# New characters go at the end, so the glyphs already in an atlas keep their cells.
CHARS = "".join(chr(i) for i in range(32, 127)) + "äöüÄÖÜßéèàáçñ…·–×" + "„“”‚‘’"
# (size, weight, file stem). The stem is inter-<size> unless two weights of one size exist, which need their own name.
FONTS = [(22, 400, None), (28, 400, None), (36, 600, None), (76, 600, None), (48, 600, None), (28, 600, "inter-28-semibold")]


def bake_font(size: int, weight: int, stem: str | None = None):
    font = ImageFont.truetype(str(ASSETS / "source" / "Inter.ttf"), size)
    font.set_variation_by_axes([14, weight])
    cell = math.ceil(size * 1.5)
    height = sum(font.getmetrics()) + 4
    width = cell * 16
    atlas = Image.new("L", (width, height * math.ceil(len(CHARS) / 16)))
    records = bytearray()
    for i, c in enumerate(CHARS):
        x, y = (i % 16) * cell, (i // 16) * height
        # Each glyph is drawn into its own cell and pasted: a glyph taller than the line ('|' reaches above
        # the ascent) otherwise leaves a stroke in the cell above it. The x offset leaves room for negative
        # side bearings.
        glyph = Image.new("L", (cell, height))
        ImageDraw.Draw(glyph).text((4, 0), c, font=font, fill=255)
        atlas.paste(glyph, (x, y))
        records.extend(struct.pack("<5H", ord(c), x, y, cell, round(font.getlength(c))))
    header = struct.pack("<4s4H", b"FNT1", width, atlas.height, height, len(CHARS))
    (ASSETS / f"{stem or f'inter-{size}'}.p5font").write_bytes(header + records + atlas.tobytes())


def main():
    args = sys.argv[1:]
    only = set(args[args.index("--only") + 1].split(",")) if "--only" in args else None
    fonts_only = "--fonts-only" in args or only is not None
    if not fonts_only:
        bake_artwork()
    baked = 0
    for size, weight, stem in FONTS:
        if only is None or (stem or f"inter-{size}") in only:
            bake_font(size, weight, stem)
            baked += 1
    print(f"Packaged {baked} Inter font atlases." if fonts_only else f"Packaged Blue Hour and {baked} Inter font atlases.")


def bake_artwork():
    artwork = Image.open(ASSETS / "source" / "blue-hour.png").convert("RGB")
    ImageOps.fit(artwork, (960, 540), method=Image.Resampling.LANCZOS).save(ASSETS / "blue-hour-preview.png")
    ImageOps.fit(artwork, (1920, 1080), method=Image.Resampling.LANCZOS).save(ASSETS / "blue-hour.bmp")


if __name__ == "__main__":
    main()
