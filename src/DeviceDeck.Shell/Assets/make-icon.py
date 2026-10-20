"""Draws DeviceDeck.ico and DeviceDeck.png from the design of web/public/favicon.svg.

    python src/DeviceDeck.Shell/Assets/make-icon.py

Each size is drawn on its own (8x supersampled, then downscaled), so 16 px stays crisp.
Small sizes are stored as 32-bit bitmaps, 256 px as PNG, the way Windows' own icons are.
"""
import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
IRIS = (0x5B, 0x5B, 0xF6, 255)
WHITE = (255, 255, 255, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def draw(size: int) -> Image.Image:
    k = 8
    s = size * k
    unit = s / 32  # the SVG's viewBox is 32 x 32
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=8 * unit, fill=IRIS)
    # Phone outline: rect x=10 y=5 w=12 h=22 rx=3, stroke 2 centred on the path.
    stroke = 2 * unit
    d.rounded_rectangle([9 * unit, 4 * unit, 23 * unit, 28 * unit], radius=4 * unit, fill=WHITE)
    d.rounded_rectangle([11 * unit, 6 * unit, 21 * unit, 26 * unit], radius=2 * unit, fill=IRIS)
    # Home bar: M14.5 23 h3, round caps.
    d.rounded_rectangle([13.5 * unit, 22 * unit, 18.5 * unit, 24 * unit], radius=stroke / 2, fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)


def bmp_entry(img: Image.Image) -> bytes:
    w, h = img.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = img.getpixel((x, y))
            pixels += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    mask = bytes(mask_row * h)  # alpha carries transparency; the AND mask stays empty
    return header + bytes(pixels) + mask


def main() -> None:
    images = {size: draw(size) for size in SIZES}
    entries = []
    for size, img in images.items():
        if size >= 256:
            buf = io.BytesIO()
            img.save(buf, "PNG")
            entries.append((size, buf.getvalue()))
        else:
            entries.append((size, bmp_entry(img)))

    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dim = 0 if size >= 256 else size
        out.write(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in entries:
        out.write(data)

    with open(os.path.join(HERE, "DeviceDeck.ico"), "wb") as f:
        f.write(out.getvalue())
    images[256].save(os.path.join(HERE, "DeviceDeck.png"))


if __name__ == "__main__":
    main()
