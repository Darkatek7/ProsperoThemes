#!/usr/bin/env python3
"""Generate a distinctive 3840x2160 test wallpaper.

Deliberately unmistakable on a TV: a vertical gradient, wide diagonal bands and large text, so that a
glance at the console settles whether a written texture is being rendered -- and, when several screen
assets are labelled differently, which asset drives which screen.

    python gen_test_image.py out.png [label] [RRGGBB]

With no label the image reads PROSPEROTHEMES on a blue-to-magenta gradient (the original behaviour).
A label and colour let every screen asset be given a distinct, readable texture.
"""

import sys

from PIL import Image, ImageDraw, ImageFont

WIDTH, HEIGHT = 3840, 2160
OUT = sys.argv[1] if len(sys.argv) > 1 else "test-wallpaper.png"
LABEL = sys.argv[2] if len(sys.argv) > 2 else "PROSPEROTHEMES"
HUE = sys.argv[3] if len(sys.argv) > 3 else None


def parse_hex(value: str):
    value = value.lstrip("#")
    return int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16)


if HUE:
    r0, g0, b0 = parse_hex(HUE)
    # Darker at the top, the chosen colour at the bottom, so the gradient direction stays obvious.
    top = (int(r0 * 0.15), int(g0 * 0.15), int(b0 * 0.15))
    bottom = (r0, g0, b0)
else:
    top, bottom = (20, 10, 90), (240, 40, 210)

image = Image.new("RGB", (WIDTH, HEIGHT))
draw = ImageDraw.Draw(image)

for y in range(HEIGHT):
    t = y / (HEIGHT - 1)
    draw.line(
        [(0, y), (WIDTH, y)],
        fill=(
            int(top[0] + t * (bottom[0] - top[0])),
            int(top[1] + t * (bottom[1] - top[1])),
            int(top[2] + t * (bottom[2] - top[2])),
        ),
    )

# Wide diagonal bands, so orientation and scaling are obvious at a glance.
band = 160
for x in range(-HEIGHT, WIDTH, band * 2):
    draw.polygon(
        [(x, HEIGHT), (x + band, HEIGHT), (x + band + HEIGHT, 0), (x + HEIGHT, 0)],
        fill=(255, 255, 255),
    )


def font_at(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:  # older Pillow: no size argument
        return ImageFont.load_default()


font = font_at(260 if len(LABEL) > 18 else 340)
box = draw.textbbox((0, 0), LABEL, font=font)
text_w, text_h = box[2] - box[0], box[3] - box[1]
cx, cy = WIDTH // 2, HEIGHT // 2
draw.rectangle(
    [(cx - text_w // 2 - 70, cy - text_h // 2 - 60), (cx + text_w // 2 + 70, cy + text_h // 2 + 60)],
    fill=(0, 0, 0),
)
draw.text((cx - text_w // 2, cy - text_h // 2 - box[1]), LABEL, fill=(0, 255, 120), font=font)

image.save(OUT)
print(f"wrote {OUT} {image.size[0]}x{image.size[1]} label={LABEL!r} hue={HUE or 'default'}")
