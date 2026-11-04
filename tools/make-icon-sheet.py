#!/usr/bin/env python3
"""
Generates docs/icons.png — a design sheet of the shipped application icon (all sizes, on
light and dark) plus the in-app vector icon set, read straight from Themes/Icons.xaml so
the sheet can never drift from what the app actually draws.
"""

import io
import os
import re

import cairosvg
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ICONS_XAML = os.path.join(ROOT, "src", "KnightLock", "Themes", "Icons.xaml")
ICO = os.path.join(ROOT, "src", "KnightLock", "Assets", "KnightLock.ico")
OUT = os.path.join(ROOT, "docs", "icons.png")

BG_LIGHT = (248, 248, 249)
BG_DARK = (32, 32, 32)
INK_LIGHT = (26, 26, 26)
INK_DARK = (243, 243, 243)


def load_geometries():
    text = open(ICONS_XAML, encoding="utf-8").read()
    items = re.findall(r'<StreamGeometry x:Key="(Geo[A-Za-z]+)"[^>]*>(.*?)</StreamGeometry>',
                       text, flags=re.S)
    result = []
    for key, data in items:
        path = data.strip()
        path = re.sub(r"^F0\s+", "", path)   # even-odd is applied separately in the SVG
        result.append((key.replace("Geo", ""), path))
    return result


def render_path(data, size, colour):
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" '
        f'width="{size}" height="{size}"><path d="{data}" fill="{colour}" '
        f'fill-rule="evenodd"/></svg>'
    )
    png = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)

    app_sizes = [256, 128, 64, 48, 32, 16]
    ico = Image.open(ICO)
    app_images = []
    for size in app_sizes:
        ico.size = (size, size)
        app_images.append((size, ico.copy().convert("RGBA")))

    icons = load_geometries()

    width, height = 900, 1040
    sheet = Image.new("RGB", (width, height), BG_LIGHT)
    draw = ImageDraw.Draw(sheet)

    # ---- header
    draw.text((28, 22), "KnightLock  /  icon system", fill=INK_LIGHT)
    draw.text((28, 42), "Application icon (transparent, per-size artwork) and the in-app vector set",
              fill=(110, 110, 112))

    # ---- app icon row (light + dark strips)
    y = 80
    draw.text((28, y), "APP ICON", fill=(120, 120, 122))
    y += 22

    strip_h = 300  # tall enough for the 256 px tile plus its size label
    for row, bg in ((0, BG_LIGHT), (1, BG_DARK)):
        strip = Image.new("RGB", (width - 56, strip_h), bg)
        strip_draw = ImageDraw.Draw(strip)
        label_ink = (90, 90, 92) if bg == BG_LIGHT else (170, 170, 172)
        x = 16
        for size, img in app_images:
            # bottom-aligned size ladder, each tile on its own baseline
            strip.paste(img, (x, strip_h - 30 - size), img)
            tag = str(size)
            tw = strip_draw.textlength(tag)
            strip_draw.text((x + (size - tw) / 2, strip_h - 22), tag, fill=label_ink)
            x += size + 26
            if size == 32:
                x += 6
        sheet.paste(strip, (28, y))
        y += strip_h + 8

    # ---- in-app icon grid
    draw.text((28, y + 4), "IN-APP ICONS  (24x24 grid, filled paths)", fill=(120, 120, 122))
    y += 26

    cols, cell = 8, 96
    for idx, (name, data) in enumerate(icons):
        cx = 28 + (idx % cols) * cell
        cy = y + (idx // cols) * cell
        tile = Image.new("RGB", (cell - 12, cell - 12), BG_DARK)
        tile.paste(render_path(data, 30, "#F3F3F3"), (18, 12), render_path(data, 30, "#F3F3F3"))
        sheet.paste(tile, (cx, cy))
        draw.text((cx + 2, cy + 62), name[:13], fill=(110, 110, 112))

    sheet.save(OUT)
    print("wrote", OUT, sheet.size, "| icons:", len(icons))


if __name__ == "__main__":
    main()
