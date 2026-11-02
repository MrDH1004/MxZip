"""Generate the MxZip app icon (MxZip.ico) with Pillow only.

The mark is YMX's chevron cube in the logo's own colours (traced from ymx.co.kr/media/logo-dark.png) on a white
tile: yellow diamond, sky-to-blue upper chevron, blue side bar, navy lower chevron. A zipper closes the gap
between the two chevrons: navy interlocking teeth under a yellow slider, so the cube reads as a packed (zipped) box.

Usage: python make_icon.py [preview.png]
"""
from __future__ import annotations

import io
import struct
import sys
from pathlib import Path
from typing import Iterable

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
SS = 4  # supersampling factor

TILE = (255, 255, 255)
TILE_EDGE = (214, 222, 234)  # keeps the white tile visible on white backgrounds
YELLOW = (254, 207, 25)
SKY, BLUE = (61, 162, 219), (16, 100, 174)
BAR = (30, 76, 154)
LOWER_C = (29, 63, 121)
TEETH = (29, 63, 121)

# YMX mark, mark box 202 x 229
DIAMOND = [(100, 2), (148, 27), (100, 52), (54, 27)]
UPPER = [(0, 58), (40, 36), (100, 70), (162, 36), (202, 58), (100, 114)]
RIGHT_BAR = [(150, 87), (202, 58), (202, 155), (150, 127)]
LOWER = [(0, 174), (50, 146), (100, 174), (152, 146), (202, 174), (104, 229), (96, 229)]
SLIDER = [(84, 108), (116, 108), (108, 122), (92, 122)]


def _horizontal_gradient_poly(img: Image.Image, points, c0, c1):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    x0, x1 = min(p[0] for p in points), max(p[0] for p in points)
    ramp = Image.new("RGBA", img.size)
    draw = ImageDraw.Draw(ramp)
    for x in range(int(x0), int(x1) + 1):
        t = (x - x0) / max(1.0, x1 - x0)
        draw.line([(x, 0), (x, img.height)], fill=tuple(round(a + (b - a) * t) for a, b in zip(c0, c1)) + (255,))
    img.paste(ramp, (0, 0), mask)


def render(size: int) -> Image.Image:
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    pad = (0.03 if size <= 24 else 0.06) * n  # small sizes use more of the square
    draw.rounded_rectangle([pad, pad, n - pad, n - pad], radius=0.2 * n, fill=TILE + (255,),
                           outline=TILE_EDGE + (255,), width=max(SS, n // 128))
    fill = 0.72 if size <= 24 else 0.66
    scale = (n - 2 * pad) * fill / 229
    ox, oy = n / 2 - 101 * scale, n / 2 - 114.5 * scale

    def tf(points):
        return [(ox + x * scale, oy + y * scale) for x, y in points]

    draw.polygon(tf(DIAMOND), fill=YELLOW + (255,))
    _horizontal_gradient_poly(img, tf(UPPER), SKY, BLUE)
    draw.polygon(tf(RIGHT_BAR), fill=BAR + (255,))
    draw.polygon(tf(LOWER), fill=LOWER_C + (255,))
    teeth = 2 if size <= 24 else 4
    top, bottom = 118, 166
    step = (bottom - top) / teeth
    for i in range(teeth):
        y = top + i * step
        x0, x1 = (80, 104) if i % 2 == 0 else (96, 120)
        draw.polygon(tf([(x0, y + step * .15), (x1, y + step * .15), (x1, y + step * .85), (x0, y + step * .85)]),
                     fill=TEETH + (255,))
    draw.polygon(tf(SLIDER), fill=YELLOW + (255,))
    return img.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(path: Path, images: Iterable[Image.Image]):
    images = [image.convert("RGBA") for image in images]
    blobs = []
    for image in images:
        stream = io.BytesIO()
        image.save(stream, "PNG", optimize=True)
        blobs.append(stream.getvalue())
    offset = 6 + 16 * len(images)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    for image, blob in zip(images, blobs):
        side = 0 if image.width == 256 else image.width
        directory.extend(struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    path.write_bytes(directory + b"".join(blobs))


def write_preview(path: Path, images):
    canvas = Image.new("RGB", (1200, 720), (243, 243, 243))
    canvas.paste(Image.new("RGB", (1200, 330), (32, 32, 32)), (0, 390))
    draw = ImageDraw.Draw(canvas)
    for top, label in ((0, (45, 45, 45)), (390, (225, 225, 225))):
        x = 28
        for image in images:
            canvas.paste(image, (x, top + 90), image)
            draw.text((x, top + 90 + image.height + 12), f"{image.width}", fill=label)
            x += max(image.width, 44) + 28
    canvas.save(path, "PNG", optimize=True)


def main():
    images = [render(size) for size in SIZES]
    write_ico(HERE / "MxZip.ico", images)  # file name kept: build.ps1, the installer and the GUI load it by name
    if len(sys.argv) > 1:
        write_preview(Path(sys.argv[1]), images)


if __name__ == "__main__":
    main()
