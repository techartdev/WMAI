"""Draw WMAI's icon and write app/WMAI.ico (embedded into WMAI.exe by
app/build.cmd via csc -win32icon).

Windows Mobile 5 reads classic BMP-based icons only (not the PNG-compressed
entries modern tools write), so the .ico is written by hand: 24-bit color plus
a 1-bit transparency mask, at 16x16, 32x32 and 48x48. Each size is drawn at its
own resolution so the small ones stay crisp. Needs Pillow.

  python tools/icon/make_icon.py
"""
import os
import struct

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SIZES = [16, 32, 48]
TOP, BOTTOM = (40, 120, 220), (10, 60, 150)  # Windows Mobile-ish blue gradient


def font(px):
    for name in ("segoeuib.ttf", "arialbd.ttf", "verdanab.ttf"):
        try:
            return ImageFont.truetype(name, px)
        except OSError:
            continue
    return ImageFont.load_default()


def draw(size):
    scale = 4  # draw large, then downsample once for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(int(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (s, s), 0)
    radius = s // 5
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    f = font(int(s * (0.62 if size <= 16 else 0.56)))
    box = d.textbbox((0, 0), "AI", font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((s - w) / 2 - box[0], (s - h) / 2 - box[1]), "AI", font=f, fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


def ico_entry(img):
    """BITMAPINFOHEADER + 24-bit XOR bitmap (bottom-up) + 1-bit AND mask."""
    w, h = img.size
    px = img.load()
    xor_stride = (w * 3 + 3) & ~3
    and_stride = ((w + 31) // 32) * 4
    xor = bytearray()
    andm = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray()
        bits = bytearray(and_stride)
        for x in range(w):
            r, g, b, a = px[x, y]
            if a < 128:
                row += b"\0\0\0"
                bits[x // 8] |= 0x80 >> (x % 8)  # transparent
            else:
                row += bytes((b, g, r))
        row += b"\0" * (xor_stride - len(row))
        xor += row
        andm += bits
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 24, 0, len(xor) + len(andm), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(andm)


def main():
    images = [draw(s) for s in SIZES]
    entries = [ico_entry(i) for i in images]
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for img, data in zip(images, entries):
        w = img.size[0]
        out += struct.pack("<BBBBHHII", w % 256, w % 256, 0, 0, 1, 24, len(data), offset)
        offset += len(data)
    for data in entries:
        out += data
    path = os.path.join(ROOT, "app", "WMAI.ico")
    open(path, "wb").write(out)
    # Preview for humans: each size at 8x, nearest-neighbour, side by side.
    prev = Image.new("RGBA", (sum(s * 8 for s in SIZES) + 40 * len(SIZES), max(SIZES) * 8 + 40), (240, 240, 240, 255))
    x = 20
    for img in images:
        big = img.resize((img.size[0] * 8, img.size[1] * 8), Image.NEAREST)
        prev.paste(big, (x, 20), big)
        x += big.size[0] + 40
    prev.save(os.path.join(ROOT, "tools", "icon", "preview.png"))
    print("wrote %s (%d bytes, sizes %s)" % (path, len(out), SIZES))


if __name__ == "__main__":
    main()
