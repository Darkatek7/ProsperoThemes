"""Package artwork and Inter glyph atlases without requiring console font services.

Source artwork is retained unchanged; runtime copies are resized/format-converted.
Run from any directory: python ThemeApp/tools/bake-assets.py [--fonts-only]
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


def bake_font(size: int, weight: int):
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
    (ASSETS / f"inter-{size}.p5font").write_bytes(header + records + atlas.tobytes())


def main():
    if "--fonts-only" not in sys.argv[1:]:
        bake_artwork()
    for size, weight in [(22, 400), (28, 400), (36, 600), (76, 600)]:
        bake_font(size, weight)
    print("Packaged four Inter font sizes." if "--fonts-only" in sys.argv[1:] else "Packaged Blue Hour and four Inter font sizes.")


def bake_artwork():
    artwork = Image.open(ASSETS / "source" / "blue-hour.png").convert("RGB")
    ImageOps.fit(artwork, (960, 540), method=Image.Resampling.LANCZOS).save(ASSETS / "blue-hour-preview.png")
    ImageOps.fit(artwork, (1920, 1080), method=Image.Resampling.LANCZOS).save(ASSETS / "blue-hour.bmp")


if __name__ == "__main__":
    main()
