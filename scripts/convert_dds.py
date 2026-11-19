"""Batch DDS -> opaque RGB PNG for one folder (alpha dropped, transparent texels bled).

Usage:
  blender -b --factory-startup --python convert_dds.py -- <tex_dir> [--keep-dds]

Why Blender: the Sekiro DDS set uses BC1/BC3/BC4/BC5 plus a few odd header variants;
Blender's image loader handles them all, a hand written decoder does not.
"""
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    raise SystemExit("usage: convert_dds.py -- <tex_dir> [--keep-dds]")
tex_dir = argv[0]
keep_dds = "--keep-dds" in argv[1:]


def write_png_rgb(path, w, h, arr):
    """arr: uint8 (h, w, 3), row order top -> bottom."""
    import struct
    import zlib
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += arr[y].tobytes()

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        f.write(chunk(b"IEND", b""))


def alpha_bleed(rgb, alpha, threshold=0.5, rounds=8):
    """Fill texels with alpha < threshold using the average of already valid neighbours."""
    h, w, _ = rgb.shape
    valid = alpha >= threshold
    if valid.all() or not valid.any():
        if not valid.any():
            rgb[:] = 0.5
        return rgb
    out = rgb.copy()
    filled = valid.copy()
    for _ in range(rounds):
        if filled.all():
            break
        acc = np.zeros_like(out)
        cnt = np.zeros((h, w), dtype=np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                sy = slice(max(0, dy), h + min(0, dy))
                sx = slice(max(0, dx), w + min(0, dx))
                ty = slice(max(0, -dy), h + min(0, -dy))
                tx = slice(max(0, -dx), w + min(0, -dx))
                m = filled[sy, sx]
                acc[ty, tx] += out[sy, sx] * m[:, :, None]
                cnt[ty, tx] += m
        grow = (~filled) & (cnt > 0)
        if not grow.any():
            break
        out[grow] = acc[grow] / cnt[grow][:, None]
        filled |= grow
    if not filled.all():
        ref = out[valid].mean(axis=0)
        out[~filled] = ref
    return out


def srgb_encode(x):
    return np.where(x <= 0.0031308, x * 12.92,
                    1.055 * np.power(np.maximum(x, 0.0), 1.0 / 2.4) - 0.055)


def convert_one(src, dst, non_color):
    img = bpy.data.images.load(src, check_existing=False)
    try:
        w, h = img.size
        if w == 0 or h == 0:
            img.reload()
            w, h = img.size
        if w == 0 or h == 0:
            raise RuntimeError("unreadable image")
        buf = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(buf)
        px = buf.reshape(h, w, 4)[::-1]            # top-down
        rgb = np.clip(px[:, :, :3], 0.0, 1.0)
        alpha = px[:, :, 3]
        if not non_color:
            rgb = srgb_encode(rgb)
        rgb = alpha_bleed(rgb, alpha)
        arr = np.clip(rgb * 255.0 + 0.5, 0, 255).astype(np.uint8)
        write_png_rgb(dst, w, h, arr)
    finally:
        bpy.data.images.remove(img)


def main():
    if not os.path.isdir(tex_dir):
        raise SystemExit("no such folder: " + tex_dir)
    names = [n for n in sorted(os.listdir(tex_dir)) if n.lower().endswith(".dds")]
    made = failed = 0
    for i, name in enumerate(names, 1):
        src = os.path.join(tex_dir, name)
        dst = os.path.join(tex_dir, name[:-4] + ".png")
        non_color = name.lower().endswith("_n.dds") or name.lower().endswith("_n2.dds")
        try:
            convert_one(src, dst, non_color)
            if not keep_dds:
                try:
                    os.remove(src)
                except OSError:
                    pass
            made += 1
        except Exception as e:
            failed += 1
            print("DDS_FAIL %s: %s" % (name, e))
        if i % 50 == 0:
            print(".. %d/%d" % (i, len(names)))
    print("DDS_DONE made=%d failed=%d dir=%s" % (made, failed, tex_dir))


main()