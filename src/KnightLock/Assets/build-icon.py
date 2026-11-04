#!/usr/bin/env python3
"""
KnightLock icon builder  (official artwork pipeline)

Source art: Assets/icon-source.png -- the 1024x1024 official KnightLock mark
(navy rounded tile, layered blue folder, silver padlock with the white
knight-helmet emblem).

The raw render sits on a white matte inside a pure-black frame. Windows icons
must never ship that matte (it reads as a white square on a dark taskbar), so the
pipeline:

    1. locates the navy tile inside the white matte (auto-detected),
    2. crops a hair inside the tile edge to drop the matte's anti-aliased rim,
    3. rebuilds the tile squircle as a clean, super-sampled alpha mask,
    4. downsamples in premultiplied space (no dark/light fringing),
    5. writes the .ico (10 sizes, PNG-compressed entries) and the PNG exports.

Run:  python3 build-icon.py
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "icon-source.png"

MASTER = 1024
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
PNG_SIZES = [96, 48]   # only the two PNGs the app embeds; every other size lives in the .ico

# Corner radius of the tile as a fraction of its width. Measured off the source
# art (~20.8%), which matches the Windows 11 app-icon squircle ratio.
RADIUS_RATIO = 0.2078

# At or below this size the icon is rendered from a slightly zoomed crop so the
# padlock silhouette stays readable in the taskbar / title bar / Alt-Tab.
SMALL_CUTOFF = 24
SMALL_ZOOM = 1.32

SUPERSAMPLE = 4


def find_tile(im: Image.Image) -> tuple[int, int, int, int]:
    """Bounding box (left, top, right, bottom) of the navy tile inside the white matte."""
    a = np.asarray(im.convert("RGB"), dtype=np.int16)
    r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
    navy = (b > r + 8) & ((r + g + b) / 3.0 < 205)
    rows = np.where(navy.sum(axis=1) > 20)[0]
    cols = np.where(navy.sum(axis=0) > 20)[0]
    return int(cols.min()), int(rows.min()), int(cols.max()) + 1, int(rows.max()) + 1


def rounded_mask(size: int, radius: float) -> Image.Image:
    """Super-sampled, anti-aliased squircle mask. radius = corner radius in px."""
    big = size * SUPERSAMPLE
    m = Image.new("L", (big, big), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, big - 1, big - 1), radius=radius * SUPERSAMPLE, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def resize_rgba(im: Image.Image, size: int) -> Image.Image:
    """Resize RGBA in premultiplied space so transparent pixels never bleed into edges."""
    src = np.asarray(im.convert("RGBA"), dtype=np.uint16)
    a = src[:, :, 3]
    scale = (a.astype(np.float32) / 255.0)[:, :, None]
    pm = np.dstack([src[:, :, :3].astype(np.float32) * scale, a.astype(np.float32)])
    small = np.asarray(
        Image.fromarray(np.clip(pm, 0, 255).astype(np.uint8), "RGBA").resize((size, size), Image.LANCZOS),
        dtype=np.float32,
    )
    sa = small[:, :, 3]
    safe = np.where(sa == 0, 1.0, sa)[:, :, None]
    rgb = np.clip(small[:, :, :3] * 255.0 / safe, 0, 255)
    return Image.fromarray(np.dstack([rgb, sa]).astype(np.uint8), "RGBA")


def build_master(src: Path) -> Image.Image:
    im = Image.open(src).convert("RGB")
    left, top, right, bottom = find_tile(im)
    side = min(right - left, bottom - top)
    inset = max(3, round(side * 0.005))

    tile = im.crop((left + inset, top + inset, left + inset + side - 2 * inset,
                    top + inset + side - 2 * inset)).convert("RGBA")
    tile.putalpha(rounded_mask(tile.width, RADIUS_RATIO * side - inset))
    return resize_rgba(tile, MASTER)


def render(master: Image.Image, size: int) -> Image.Image:
    if size <= SMALL_CUTOFF:
        crop = int(round(MASTER / SMALL_ZOOM / 2)) * 2
        off = (MASTER - crop) // 2
        sub = master.crop((off, off, off + crop, off + crop))
        sub_radius = (RADIUS_RATIO * MASTER - 4) * (crop / MASTER)
        keep = sub.getchannel("A").point(lambda v: 255 if v else 0)
        sub.putalpha(Image.composite(keep, Image.new("L", sub.size, 0), rounded_mask(crop, sub_radius)))
        return resize_rgba(sub, size).filter(ImageFilter.UnsharpMask(radius=0.6, percent=70, threshold=1))

    out = resize_rgba(master, size)
    if size <= 96:
        out = out.filter(ImageFilter.UnsharpMask(radius=0.7, percent=45, threshold=2))
    return out


def main() -> int:
    if not SOURCE.exists():
        print(f"missing source art: {SOURCE}", file=sys.stderr)
        return 1

    master = build_master(SOURCE)
    for s in PNG_SIZES:
        render(master, s).save(HERE / f"KnightLock-{s}.png")

    frames = [render(master, s) for s in ICO_SIZES]
    frames[-1].save(
        HERE / "KnightLock.ico",
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=frames[:-1],
        bitmap_format="png",
    )

    print(f"source tile: {find_tile(Image.open(SOURCE))}")
    print(f"wrote KnightLock.ico ({len(ICO_SIZES)} sizes: {ICO_SIZES})")
    print(f"wrote PNG exports: {PNG_SIZES}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
