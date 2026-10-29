"""Renders the app icon: a green V/F curve that rises and flattens at a cap point.

Usage: python make_icon.py <output-dir>
Writes AppIcon.png (256 px), AppIcon.ico (16-256 px) and icon-1024.png (README).
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

GREEN = (118, 185, 0)
BG_TOP = (30, 34, 27)
BG_BOTTOM = (8, 9, 7)
SIZE = 1024


def curve_points():
    """Stock-like V/F curve: steep rise, knee, gentle slope up to the cap."""
    pts = []
    for i in range(0, 101):
        t = i / 100
        x = 150 + t * 470
        y = 820 - 470 * (1 - math.exp(-4.2 * t)) - 60 * t
        pts.append((x, y))
    return pts


def stroke(draw, points, width, fill):
    """Draws a smooth thick polyline by stamping circles densely along it."""
    r = width / 2
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        steps = max(1, int(math.hypot(x1 - x0, y1 - y0) / 2))
        for i in range(steps + 1):
            x, y = x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps
            draw.ellipse([x - r, y - r, x + r, y + r], fill=fill)


def render(size, detail):
    """Draws the icon at 1024 px and scales it down to the requested size."""
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

    bg = Image.new("RGBA", (SIZE, SIZE))
    bd = ImageDraw.Draw(bg)
    for y in range(SIZE):
        f = y / (SIZE - 1)
        bd.line([(0, y), (SIZE, y)], fill=tuple(int(a + (b - a) * f) for a, b in zip(BG_TOP, BG_BOTTOM)) + (255,))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, SIZE - 24, SIZE - 24], radius=210, fill=255)
    img.paste(bg, (0, 0), mask)

    d = ImageDraw.Draw(img)
    d.rounded_rectangle([24, 24, SIZE - 24, SIZE - 24], radius=210, outline=GREEN + (60,), width=8)

    if detail:
        grid = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        gd = ImageDraw.Draw(grid)
        for v in range(190, 900, 140):
            gd.line([(v, 140), (v, 884)], fill=GREEN + (26,), width=4)
            gd.line([(140, v), (884, v)], fill=GREEN + (26,), width=4)
        img = Image.alpha_composite(img, Image.composite(grid, Image.new("RGBA", (SIZE, SIZE)), mask))
        d = ImageDraw.Draw(img)

    pts = curve_points()
    cap = pts[-1]
    flat_end = (880, cap[1])

    if detail:
        ghost = [cap]
        for i in range(1, 30):
            t = i / 29
            ghost.append((cap[0] + t * 260, cap[1] - 150 * (1 - math.exp(-2.5 * t))))
        for a, b in zip(ghost[::2], ghost[1::2]):
            d.line([a, b], fill=(200, 205, 195, 120), width=18)

    path = pts + [flat_end]
    glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    stroke(ImageDraw.Draw(glow), path, 120, GREEN + (170,))
    glow = glow.filter(ImageFilter.GaussianBlur(38))
    img = Image.alpha_composite(img, Image.composite(glow, Image.new("RGBA", (SIZE, SIZE)), mask))

    d = ImageDraw.Draw(img)
    stroke(d, path, 64 if detail else 120, GREEN + (255,))

    r_outer, r_inner = (78, 44) if detail else (120, 64)
    d.ellipse([cap[0] - r_outer, cap[1] - r_outer, cap[0] + r_outer, cap[1] + r_outer], fill=GREEN + (255,))
    d.ellipse([cap[0] - r_inner, cap[1] - r_inner, cap[0] + r_inner, cap[1] + r_inner], fill=(245, 248, 240, 255))

    return img.resize((size, size), Image.LANCZOS)


def main():
    out = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    out.mkdir(parents=True, exist_ok=True)
    render(1024, True).save(out / "icon-1024.png")
    render(256, True).save(out / "AppIcon.png")
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [render(s, s >= 48) for s in sizes]
    frames[-1].save(out / "AppIcon.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])


if __name__ == "__main__":
    main()
