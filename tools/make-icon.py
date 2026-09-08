#!/usr/bin/env python3
"""Generate the LabControl console icon.

Draws a sculpted white L with cyan classroom tiles on a cobalt rounded square
and writes every format the console needs:

    src/LabControl.Console/Assets/labcontrol.png    1024×1024 master, also the Linux icon
    src/LabControl.Console/Assets/labcontrol.ico    Windows exe icon (16…256 px)
    src/LabControl.Console/Assets/labcontrol.icns   macOS bundle icon (via iconutil)

Needs Pillow; `iconutil` only exists on macOS, the .icns step is skipped elsewhere.
Re-run after changing the drawing; the outputs are committed so a build never needs
Python.
"""
from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:  # pragma: no cover
    sys.exit("make-icon.py needs Pillow: python3 -m pip install pillow")

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "LabControl.Console" / "Assets"
SIZE = 1024
SCALE = 4  # draw at 4× and downsample for clean anti-aliased edges

def draw_master() -> Image.Image:
    """Render a bold, text-free mark with generous desktop-icon safe margins."""
    s = SIZE * SCALE

    def box(coords):
        return tuple(round(v * SCALE) for v in coords)

    # Work at master resolution for the smooth two-axis color field, then
    # supersample the contours. No font or external artwork is required.
    field = Image.new("RGBA", (SIZE, SIZE))
    pixels = field.load()
    for y in range(SIZE):
        for x in range(SIZE):
            u, v = x / SIZE, y / SIZE
            light = max(0.0, 1.0 - ((u - 0.12) ** 2 + (v - 0.05) ** 2) ** 0.5)
            pixels[x, y] = (
                round(29 + 20 * v + 6 * light),
                round(58 + 65 * light - 20 * v),
                round(204 + 43 * light - 21 * v),
                255,
            )
    field = field.resize((s, s), Image.Resampling.BICUBIC)
    mask = Image.new("L", (s, s))
    ImageDraw.Draw(mask).rounded_rectangle(box((64, 64, 960, 960)), radius=204 * SCALE, fill=255)
    img = Image.new("RGBA", (s, s))
    shadow = Image.new("RGBA", (s, s))
    ImageDraw.Draw(shadow).rounded_rectangle(
        box((78, 88, 946, 956)), radius=198 * SCALE, fill=(12, 24, 78, 80)
    )
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(15 * SCALE)))
    img.paste(field, (0, 0), mask)
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle(box((66, 66, 958, 958)), radius=202 * SCALE,
                           outline=(157, 194, 255, 90), width=2 * SCALE)

    # A continuous L anchors the mosaic. Rounded outer and inner corners give
    # the mark a single coherent silhouette, including at 16 px.
    mark = Image.new("L", (s, s))
    md = ImageDraw.Draw(mark)
    md.rounded_rectangle(box((256, 252, 416, 772)), radius=40 * SCALE, fill=255)
    md.rounded_rectangle(box((256, 612, 768, 772)), radius=40 * SCALE, fill=255)
    md.rectangle(box((336, 532, 416, 692)), fill=255)
    md.rectangle(box((336, 612, 496, 692)), fill=255)
    md.rectangle(box((416, 572, 456, 612)), fill=255)
    md.ellipse(box((416, 532, 496, 612)), fill=0)
    # Soft directional depth stays subordinate to the simple white shape.
    shade = Image.new("RGBA", (s, s), (9, 23, 104, 0))
    shifted = Image.new("L", (s, s))
    shifted.paste(mark, (0, 12 * SCALE))
    shade.putalpha(shifted.filter(ImageFilter.GaussianBlur(16 * SCALE)).point(lambda a: a // 3))
    img = Image.alpha_composite(img, shade)
    white = Image.new("RGBA", (s, s), (243, 251, 255, 255))
    img.paste(white, (0, 0), mark)

    # The two upper tiles and the wide lower screen suggest a flexible lab
    # layout rather than a fixed number of machines or an institutional seal.
    draw = ImageDraw.Draw(img)
    for rect, color in (
        ((464, 252, 592, 380), (151, 248, 248, 255)),
        ((640, 252, 768, 380), (89, 225, 246, 255)),
        ((464, 428, 768, 564), (73, 218, 239, 255)),
    ):
        draw.rounded_rectangle(box(rect), radius=30 * SCALE, fill=color)
        x0, y0, x1, _ = rect
        draw.line(box((x0 + 30, y0 + 2, x1 - 30, y0 + 2)),
                  fill=(209, 255, 255, 150), width=2 * SCALE)

    return img.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def write_ico(master: Image.Image, path: Path) -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    master.save(path, format="ICO", sizes=[(n, n) for n in sizes])


def write_icns(master: Image.Image, path: Path) -> bool:
    iconutil = shutil.which("iconutil")
    if iconutil is None:
        return False
    with tempfile.TemporaryDirectory() as tmp:
        iconset = Path(tmp) / "labcontrol.iconset"
        iconset.mkdir()
        for base in (16, 32, 128, 256, 512):
            master.resize((base, base), Image.LANCZOS).save(iconset / f"icon_{base}x{base}.png")
            master.resize((base * 2, base * 2), Image.LANCZOS).save(
                iconset / f"icon_{base}x{base}@2x.png"
            )
        result = subprocess.run(
            [iconutil, "-c", "icns", str(iconset), "-o", str(path)],
            capture_output=True, text=True,
        )
        if result.returncode:
            # Some macOS environments reject valid PNG iconsets. Pillow writes
            # the same standard PNG-backed ICNS representations without iconutil.
            master.save(path, format="ICNS")
            print("iconutil rejected the iconset; wrote ICNS with Pillow")
    return True


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    master = draw_master()
    master.save(ASSETS / "labcontrol.png")
    write_ico(master, ASSETS / "labcontrol.ico")
    got_icns = write_icns(master, ASSETS / "labcontrol.icns")
    print(f"wrote {ASSETS.relative_to(ROOT)}/labcontrol.{{png,ico{',icns' if got_icns else ''}}}")
    if not got_icns:
        print("iconutil not found (not macOS): labcontrol.icns not regenerated")


if __name__ == "__main__":
    main()
