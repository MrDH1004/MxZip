"""Generate three original, multi-resolution archiver icon concepts.

Usage:
    python make_icon_concepts.py OUTDIR

Only Pillow and the Python standard library are required.  Every bitmap is
drawn independently; the small ICO frames use deliberately simplified,
pixel-snapped geometry instead of reductions of the 512 px artwork.
"""

from __future__ import annotations

import io
import math
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
SS = 4


def rgba(hex_color: str, alpha: int = 255) -> tuple[int, int, int, int]:
    h = hex_color.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), alpha)


def mix(a, b, t: float):
    return tuple(round(a[i] * (1 - t) + b[i] * t) for i in range(4))


def vertical_gradient(size: tuple[int, int], top, bottom) -> Image.Image:
    w, h = size
    strip = Image.new("RGBA", (1, h))
    strip.putdata([mix(top, bottom, y / max(1, h - 1)) for y in range(h)])
    return strip.resize((w, h))


def diagonal_gradient(size: tuple[int, int], c0, c1, c2=None) -> Image.Image:
    w, h = size
    c2 = c1 if c2 is None else c2
    # A compact smooth ramp is visually identical after compositing and keeps
    # 512 px supersampled renders fast on ordinary build machines.
    gw, gh = min(w, 128), min(h, 128)
    im = Image.new("RGBA", (gw, gh))
    pix = im.load()
    for y in range(gh):
        for x in range(gw):
            t = (x / max(1, gw - 1) + y / max(1, gh - 1)) * 0.5
            pix[x, y] = mix(c0, c1, t * 2) if t <= .5 else mix(c1, c2, (t - .5) * 2)
    return im if (gw, gh) == (w, h) else im.resize((w, h), Image.Resampling.BICUBIC)


def paste_mask(dst: Image.Image, fill: Image.Image | tuple, mask: Image.Image) -> None:
    if isinstance(fill, tuple):
        layer = Image.new("RGBA", dst.size, fill)
    else:
        layer = fill
    dst.alpha_composite(Image.composite(layer, Image.new("RGBA", dst.size), mask))


def polygon_mask(size, points) -> Image.Image:
    m = Image.new("L", size)
    ImageDraw.Draw(m).polygon(points, fill=255)
    return m


def rounded_mask(size, box, radius) -> Image.Image:
    m = Image.new("L", size)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m


def glow(dst, box, color, blur):
    layer = Image.new("RGBA", dst.size)
    d = ImageDraw.Draw(layer)
    d.ellipse(box, fill=color)
    dst.alpha_composite(layer.filter(ImageFilter.GaussianBlur(blur)))


def shadow(dst, mask, offset, blur, alpha=105):
    sh = Image.new("RGBA", dst.size, (0, 0, 0, alpha))
    shifted = ImageChops.offset(mask, offset[0], offset[1])
    # Clear wrapped portions introduced by ImageChops.offset.
    if offset[1] > 0:
        ImageDraw.Draw(shifted).rectangle((0, 0, shifted.width, offset[1] - 1), fill=0)
    if offset[0] > 0:
        ImageDraw.Draw(shifted).rectangle((0, 0, offset[0] - 1, shifted.height), fill=0)
    shifted = shifted.filter(ImageFilter.GaussianBlur(blur))
    dst.alpha_composite(Image.composite(sh, Image.new("RGBA", dst.size), shifted))


def base_tile(n: int, palette: str) -> tuple[Image.Image, tuple[int, int, int, int]]:
    """Return a transparent canvas and its inset rounded-square bounds."""
    im = Image.new("RGBA", (n, n))
    inset = round(n * .075)
    box = (inset, inset, n - inset - 1, n - inset - 1)
    radius = round(n * .215)
    mask = rounded_mask(im.size, box, radius)

    shadow(im, mask, (0, round(n * .025)), max(1, round(n * .04)), 118)
    if palette == "indigo":
        bg = diagonal_gradient(im.size, rgba("#12152B"), rgba("#25204B"), rgba("#0B3B43"))
        glow(bg, (n * .25, -n * .18, n * 1.05, n * .58), (91, 93, 215, 75), n * .10)
    elif palette == "glass":
        bg = diagonal_gradient(im.size, rgba("#101620"), rgba("#18313A"), rgba("#182027"))
        glow(bg, (n * .45, -n * .2, n * 1.15, n * .55), (28, 184, 167, 65), n * .11)
    else:
        bg = diagonal_gradient(im.size, rgba("#25172B"), rgba("#442338"), rgba("#183948"))
        glow(bg, (-n * .12, n * .18, n * .72, n * .95), (234, 94, 96, 62), n * .12)
    paste_mask(im, bg, mask)

    # Restrained glass rim separates the tile on both light and dark shells.
    rim = Image.new("RGBA", im.size)
    rd = ImageDraw.Draw(rim)
    width = max(1, round(n * .008))
    rd.rounded_rectangle(box, radius=radius, outline=(255, 255, 255, 42), width=width)
    im.alpha_composite(rim)
    return im, box


def concept_a(size: int, small: bool) -> Image.Image:
    """Foldstack: broad sheets fold inward and settle into a compact block."""
    n = size * SS
    im, _ = base_tile(n, "indigo")
    q = SS

    def P(points):
        return [(round(x * size) * q if small else round(x * n),
                 round(y * size) * q if small else round(y * n)) for x, y in points]

    # Three distinct folded strata; fewer bevels in small frames.
    back = P(((.22, .34), (.50, .20), (.78, .34), (.51, .48)))
    mid = P(((.22, .45), (.51, .31), (.78, .45), (.51, .60)))
    front = P(((.22, .57), (.51, .43), (.78, .57), (.51, .75)))
    colors = [
        (rgba("#69D2C5"), rgba("#279E9B")),
        (rgba("#8F91F4"), rgba("#5554C8")),
        (rgba("#FF9B73"), rgba("#E45E67")),
    ]
    for idx, (poly, cs) in enumerate(zip((back, mid, front), colors)):
        mask = polygon_mask(im.size, poly)
        shadow(im, mask, (0, max(q, round(n * .015))), max(q, round(n * .018)), 90)
        fill = diagonal_gradient(im.size, cs[0], cs[1], mix(cs[1], rgba("#171C35"), .28))
        paste_mask(im, fill, mask)
        if not small:
            hi = Image.new("RGBA", im.size)
            hd = ImageDraw.Draw(hi)
            hd.line(poly[:3], fill=(255, 255, 255, 75), width=max(1, round(n * .009)), joint="curve")
            im.alpha_composite(hi)

    # Central compression seam: a dark inset diamond, not a zipper or letterform.
    core = P(((.42, .50), (.51, .455), (.60, .50), (.51, .548)))
    cm = polygon_mask(im.size, core)
    paste_mask(im, rgba("#17182D", 235), cm)
    inner = P(((.465, .50), (.51, .478), (.555, .50), (.51, .523)))
    paste_mask(im, rgba("#FFD18A"), polygon_mask(im.size, inner))
    return im.resize((size, size), Image.Resampling.LANCZOS)


def concept_b(size: int, small: bool) -> Image.Image:
    """Corewell: nested rounded contours visually collapse into a warm core."""
    n = size * SS
    im, _ = base_tile(n, "glass")
    q = SS
    layer = Image.new("RGBA", im.size)
    d = ImageDraw.Draw(layer)

    # Pixel-snapped bounds at small sizes. Open corners make the nested forms
    # read as kinetic contours rather than a folder or target.
    levels = [
        (.22, .22, .78, .78, "#37C6B2", .068),
        (.32, .32, .68, .68, "#7188EB", .060),
    ]
    for i, (x0, y0, x1, y1, col, widthf) in enumerate(levels):
        box = tuple(round(v * size) * q if small else round(v * n) for v in (x0, y0, x1, y1))
        w = max(q, round((widthf * size) if small else widthf * n / q) * q) if small else max(1, round(widthf * n))
        r = round((x1 - x0) * n * .27)
        # Each contour has a different aperture, implying material flowing in.
        starts = (205, 25)
        ends = (515, 335)
        d.rounded_rectangle(box, radius=r, outline=(0, 0, 0, 70), width=w + max(q, round(n * .012)))
        d.arc(box, starts[i], ends[i], fill=rgba(col), width=w)
        if not small:
            hb = (box[0] + w // 3, box[1] + w // 3, box[2] - w // 3, box[3] - w // 3)
            d.arc(hb, starts[i] + 12, starts[i] + 92, fill=(255, 255, 255, 70), width=max(1, round(n * .008)))
    im.alpha_composite(layer)

    # Dense faceted core: the terminal compressed unit.
    c = n // 2
    r = (max(1, round(.070 * size)) * q if small else round(n * .070))
    glow(im, (c - r * 2, c - r * 2, c + r * 2, c + r * 2), (255, 170, 78, 70), max(q, n * .045))
    core = Image.new("RGBA", im.size)
    cd = ImageDraw.Draw(core)
    diamond = ((c, c-r), (c+r, c), (c, c+r), (c-r, c))
    cd.polygon(diamond, fill=rgba("#F3A34E"))
    if not small:
        cd.polygon(((c, c-r), (c+r, c), (c, c)), fill=rgba("#FFD98A"))
        cd.polygon(((c-r, c), (c, c-r), (c, c)), fill=rgba("#FFE7A7"))
        cd.polygon(((c-r, c), (c, c), (c, c+r)), fill=rgba("#E78343"))
        cd.polygon(((c, c), (c+r, c), (c, c+r)), fill=rgba("#C95D45"))
    im.alpha_composite(core)
    return im.resize((size, size), Image.Resampling.LANCZOS)


def concept_c(size: int, small: bool) -> Image.Image:
    """Bound prism: compressed mineral-like slabs held by one orbital ribbon."""
    n = size * SS
    im, _ = base_tile(n, "coral")
    q = SS

    def P(points):
        return [(round(x * size) * q if small else round(x * n),
                 round(y * size) * q if small else round(y * n)) for x, y in points]

    # Rear half of the orbit disappears behind the prism, establishing a true
    # wrap rather than a checkmark-like foreground stripe.
    rear = Image.new("RGBA", im.size)
    rear_d = ImageDraw.Draw(rear)
    rear_w = (max(1, round(size * .065)) * q if small else round(n * .065))
    rear_pts = P(((.25, .50), (.30, .37), (.46, .29), (.65, .35), (.75, .49)))
    rear_d.line(rear_pts, fill=(178, 54, 76, 210), width=rear_w, joint="curve")
    im.alpha_composite(rear)

    # A squat hexagonal prism built from dense horizontal mineral layers.
    top = P(((.29, .35), (.45, .24), (.69, .31), (.54, .42)))
    left = P(((.29, .35), (.54, .42), (.54, .72), (.29, .62)))
    right = P(((.54, .42), (.69, .31), (.69, .61), (.54, .72)))
    for poly, cs in ((left, (rgba("#344E72"), rgba("#172B46"))),
                     (right, (rgba("#2E8C8C"), rgba("#1C515E"))),
                     (top, (rgba("#8FD2C5"), rgba("#5369A4")))):
        m = polygon_mask(im.size, poly)
        shadow(im, m, (0, max(q, round(n * .018))), max(q, round(n * .02)), 85)
        paste_mask(im, diagonal_gradient(im.size, cs[0], cs[1]), m)

    # Layer lines are structural facets, not filing or paper imagery.
    facets = Image.new("RGBA", im.size)
    fd = ImageDraw.Draw(facets)
    ys = (.47, .55, .63) if not small else (.50, .61)
    for y in ys:
        yy = round(y * size) * q if small else round(y * n)
        xl = round((.29 + max(0, y-.35) * .12) * n)
        xr = round((.69 - max(0, y-.31) * .03) * n)
        fd.line((xl, yy, round(.54*n), yy + round(.035*n)), fill=(255, 255, 255, 48), width=max(q, round(n*.008)))
        fd.line((round(.54*n), yy + round(.035*n), xr, yy), fill=(4, 15, 30, 72), width=max(q, round(n*.008)))
    im.alpha_composite(facets)

    # A single diagonal orbit tightly binds the prism; its asymmetric crossing
    # creates a memorable silhouette without resembling a literal strap/zipper.
    ribbon = Image.new("RGBA", im.size)
    rd = ImageDraw.Draw(ribbon)
    width = (max(1, round(size * .072)) * q if small else round(n * .072))
    pts = P(((.23, .49), (.39, .61), (.61, .64), (.76, .49)))
    rd.line(pts, fill=(77, 19, 37, 125), width=width + max(q, round(n*.018)), joint="curve")
    rd.line(pts, fill=rgba("#FF756D"), width=width, joint="curve")
    if not small:
        rd.line(P(((.25, .475), (.40, .585), (.61, .615), (.74, .475))),
                fill=(255, 181, 139, 145), width=max(1, round(n * .013)), joint="curve")
    im.alpha_composite(ribbon)
    return im.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(path: Path, frames: list[Image.Image]) -> None:
    """Write exact, independently rendered RGBA frames into an ICO container."""
    blobs = []
    for frame in frames:
        buf = io.BytesIO()
        frame.convert("RGBA").save(buf, "PNG", optimize=True)
        blobs.append(buf.getvalue())

    count = len(frames)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = []
    for frame, blob in zip(frames, blobs):
        w, h = frame.size
        entries.append(struct.pack(
            "<BBBBHHII", 0 if w == 256 else w, 0 if h == 256 else h,
            0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    path.write_bytes(header + b"".join(entries) + b"".join(blobs))


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python make_icon_concepts.py OUTDIR", file=sys.stderr)
        return 2
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)

    concepts = {"concept_a": concept_a, "concept_b": concept_b, "concept_c": concept_c}
    for name, renderer in concepts.items():
        frames = [renderer(s, s <= 32) for s in SIZES]
        write_ico(out / f"{name}.ico", frames)
        renderer(512, False).save(out / f"{name}.png", "PNG", optimize=True)
        print(f"wrote {name}.ico and {name}.png")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
