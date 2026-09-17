#!/usr/bin/env python3
"""Generate the four WinLocalASR tray icons (16x16 + 32x32, 32bpp BGRA) as .ico files.

Shapes (circle color coding, distinct per phase):
  tray-idle.ico        green hollow ring            (Idle / Loading)
  tray-recording.ico   red filled disc              (Recording)
  tray-processing.ico  amber disc + 3 white dots    (Processing)
  tray-error.ico       dark-red disc + white X      (Error)

Pure stdlib: shapes are evaluated per subpixel (4x supersampling) and packed into
ICO containers (ICONDIR + ICONDIRENTRY + BITMAPINFOHEADER + bottom-up BGRA rows +
zero AND mask). Regenerate with:  python3 tools/generate_tray_icons.py
"""

import os
import struct

SIZES = (16, 32)

COLORS = {
    "idle": (22, 163, 74),        # green
    "recording": (220, 38, 38),   # red
    "processing": (245, 158, 11), # amber
    "error": (185, 28, 28),       # dark red
}

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "WinLocalASR.App", "Assets")


def dist(px, py, qx, qy):
    return ((px - qx) ** 2 + (py - qy) ** 2) ** 0.5


def make_pixels(size, kind):
    """Top-down RGBA pixel grid for one icon kind, 4x supersampled coverage."""

    def disc(x, y):
        r = size * 0.40
        return dist(x, y, size / 2, size / 2) <= r

    def ring(x, y):
        outer = size * 0.42
        inner = outer - max(1.8, size * 0.11)
        d = dist(x, y, size / 2, size / 2)
        return d <= outer and d >= inner

    def dot(x, y, cx, cy):
        return dist(x, y, cx, cy) <= max(0.9, size * 0.055)

    def seg(x, y, ax, ay, bx, by):
        # distance from point to segment < half width
        abx, aby = bx - ax, by - ay
        t = max(0.0, min(1.0, ((x - ax) * abx + (y - ay) * aby) / (abx * abx + aby * aby)))
        return dist(x, y, ax + t * abx, ay + t * aby) <= max(0.9, size * 0.075)

    base = ring if kind == "idle" else disc
    overlay = None
    if kind == "processing":
        s = size / 2
        d = size * 0.155
        overlay = lambda x, y: dot(x, y, s - d, s) or dot(x, y, s, s) or dot(x, y, s + d, s)
    elif kind == "error":
        s = size / 2
        d = size * 0.19
        overlay = lambda x, y: seg(x, y, s - d, s - d, s + d, s + d) or seg(x, y, s - d, s + d, s + d, s - d)

    color = COLORS[kind]
    ss = 4  # supersample factor
    pixels = []
    for py in range(size):
        row = []
        for px in range(size):
            # premultiplied accumulators
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    x = px + (sx + 0.5) / ss
                    y = py + (sy + 0.5) / ss
                    if overlay is not None and overlay(x, y):
                        acc[0] += 255
                        acc[1] += 255
                        acc[2] += 255
                        acc[3] += 255
                    elif base(x, y):
                        acc[0] += color[0]
                        acc[1] += color[1]
                        acc[2] += color[2]
                        acc[3] += 255
            n = ss * ss
            a = acc[3] / n
            if a > 0:
                row.append((acc[0] / n / a, acc[1] / n / a, acc[2] / n / a, a))
            else:
                row.append((0, 0, 0, 0))
        pixels.append(row)
    return pixels


def bmp_entry(size, pixels):
    rows = bytearray()
    for y in range(size - 1, -1, -1):  # bottom-up
        for x in range(size):
            r, g, b, a = pixels[y][x]
            rows += struct.pack("<BBBB", int(b + 0.5), int(g + 0.5), int(r + 0.5), int(a + 0.5))
    and_row = (size + 31) // 32 * 4
    mask = b"\x00" * (and_row * size)
    # BITMAPINFOHEADER: biHeight = 2*height (XOR + AND), biCompression = BI_RGB
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(rows), 0, 0, 0, 0)
    return header + bytes(rows) + mask


def write_ico(path, kind):
    images = []
    for size in SIZES:
        images.append((size, bmp_entry(size, make_pixels(size, kind))))

    header = struct.pack("<HHH", 0, 1, len(images))
    entries = b""
    offset = 6 + 16 * len(images)
    blobs = b""
    for size, blob in images:
        entries += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(blob), offset)
        blobs += blob
        offset += len(blob)

    with open(path, "wb") as f:
        f.write(header + entries + blobs)
    print(f"{path}: {os.path.getsize(path)} bytes")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    for kind in ("idle", "recording", "processing", "error"):
        write_ico(os.path.join(OUT_DIR, f"tray-{kind}.ico"), kind)


if __name__ == "__main__":
    main()
