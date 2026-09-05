#!/usr/bin/env python3
"""Generate the LabControl console icon.

Draws the icon procedurally (a mosaic of student screens, one of them selected — the
lab view the console exists for) and writes every format the console needs:

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

# Palette: dark slate background, the console's blue accent for the selected tile,
# muted "screens" for the rest.
BG_TOP = (30, 36, 48)
BG_BOTTOM = (16, 20, 28)
TILE = (58, 68, 88)
TILE_EDGE = (86, 98, 122)
ACCENT = (0, 122, 255)
ACCENT_EDGE = (120, 184, 255)
GLOW = (0, 122, 255, 110)
TEXT_LINE = (150, 160, 180)


def lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def draw_master() -> Image.Image:
    s = SIZE * SCALE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Rounded-square background with a vertical gradient (macOS-style squircle-ish).
    grad = Image.new("RGBA", (s, s))
    gdraw = ImageDraw.Draw(grad)
    for y in range(s):
        gdraw.line([(0, y), (s, y)], fill=lerp(BG_TOP, BG_BOTTOM, y / s) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [(0, 0), (s - 1, s - 1)], radius=int(s * 0.22), fill=255
    )
    img.paste(grad, (0, 0), mask)

    # 3×3 mosaic of screens; the centre one is the selected, live tile.
    margin = int(s * 0.17)
    gap = int(s * 0.045)
    cell = (s - 2 * margin - 2 * gap) // 3
    radius = int(cell * 0.14)

    # Glow behind the selected tile.
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gx = margin + cell + gap
    ImageDraw.Draw(glow).rounded_rectangle(
        [(gx - gap, gx - gap), (gx + cell + gap, gx + cell + gap)],
        radius=radius * 2,
        fill=GLOW,
    )
    glow = glow.filter(ImageFilter.GaussianBlur(int(s * 0.03)))
    img = Image.alpha_composite(img, glow)

    draw = ImageDraw.Draw(img)
    for row in range(3):
        for col in range(3):
            x0 = margin + col * (cell + gap)
            y0 = margin + row * (cell + gap)
            x1, y1 = x0 + cell, y0 + cell
            selected = row == 1 and col == 1
            fill = ACCENT if selected else TILE
            edge = ACCENT_EDGE if selected else TILE_EDGE
            draw.rounded_rectangle(
                [(x0, y0), (x1, y1)],
                radius=radius,
                fill=fill,
                outline=edge,
                width=max(1, int(s * 0.006)),
            )
            # A few "lines of content" so tiles read as screens at large sizes.
            if not selected:
                lx0 = x0 + int(cell * 0.18)
                lw = int(cell * 0.64)
                lh = max(1, int(cell * 0.06))
                for k, frac in enumerate((0.30, 0.48, 0.66)):
                    ly = y0 + int(cell * frac)
                    w = lw if k != 2 else int(lw * 0.6)
                    draw.rounded_rectangle(
                        [(lx0, ly), (lx0 + w, ly + lh)], radius=lh // 2, fill=TEXT_LINE
                    )
            else:
                # Cursor arrow on the selected tile: the teacher is driving this PC.
                cx = x0 + int(cell * 0.40)
                cy = y0 + int(cell * 0.32)
                a = int(cell * 0.34)
                arrow = [
                    (cx, cy),
                    (cx, cy + a),
                    (cx + int(a * 0.26), cy + int(a * 0.76)),
                    (cx + int(a * 0.44), cy + a),
                    (cx + int(a * 0.60), cy + int(a * 0.92)),
                    (cx + int(a * 0.42), cy + int(a * 0.68)),
                    (cx + int(a * 0.72), cy + int(a * 0.68)),
                ]
                draw.polygon(arrow, fill=(255, 255, 255), outline=(20, 40, 80))

    return img.resize((SIZE, SIZE), Image.LANCZOS)


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
        subprocess.run([iconutil, "-c", "icns", str(iconset), "-o", str(path)], check=True)
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
