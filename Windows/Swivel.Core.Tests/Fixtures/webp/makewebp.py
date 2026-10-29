# Regenerates the WebP fixtures and their expected pixels: python3 -I makewebp.py <dir>
# Needs Pillow with WebP support (libwebp). <name>.rgba holds Pillow's decoded RGBA rows.
import sys, os, numpy as np
from PIL import Image
out = sys.argv[1]

def photo(w, h, alpha=False, seed=0):
    r = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w]
    img = np.stack([(x * 255 // max(w - 1, 1)), (y * 255 // max(h - 1, 1)), ((x + y) * 3) % 256], -1).astype(np.int32)
    img += (128 * np.sin(x / 7.0) * np.cos(y / 5.0)).astype(np.int32)[..., None]
    img += r.integers(-30, 30, img.shape)
    img = np.clip(img, 0, 255).astype(np.uint8)
    if alpha:
        a = np.clip((x * 3 + y * 2) % 300, 0, 255).astype(np.uint8)
        a[(x // 9 + y // 9) % 4 == 0] = 0
        img = np.dstack([img, a])
    return Image.fromarray(img, 'RGBA' if alpha else 'RGB')

def palette(n, seed):
    r = np.random.default_rng(seed)
    pal = r.integers(0, 256, (n, 4)).astype(np.uint8)
    pal[:, 3] = np.where(r.random(n) < .3, r.integers(0, 256, n), 255)
    return Image.fromarray(pal[r.integers(0, n, (23, 19))], 'RGBA')

items = [
    ("ll-1x1", photo(1, 1), dict(lossless=True)),
    ("ll-photo-17x9-alpha", photo(17, 9, True, 1), dict(lossless=True, method=6, exact=True)),
    ("ll-photo-64x48", photo(64, 48, False, 2), dict(lossless=True, method=0)),
    ("ll-photo-61x33-alpha-m4", photo(61, 33, True, 3), dict(lossless=True, method=4, exact=True)),
    ("ll-photo-40x30-q0", photo(40, 30, False, 4), dict(lossless=True, quality=0, method=2)),
    ("ll-pal2", palette(2, 5), dict(lossless=True, method=5, exact=True)),
    ("ll-pal4", palette(4, 6), dict(lossless=True, method=5, exact=True)),
    ("ll-pal11", palette(11, 7), dict(lossless=True, method=5, exact=True)),
    ("ll-pal200", palette(200, 8), dict(lossless=True, method=5, exact=True)),
    ("ll-grey-31x7", photo(31, 7, False, 9).convert('L').convert('RGB'), dict(lossless=True)),
    ("ll-vp8x-exif", photo(21, 13, True, 10), dict(lossless=True, exact=True, exif=b"Exif\x00\x00MM\x00\x2a\x00\x00\x00\x08\x00\x00")),
    ("ly-1x1", photo(1, 1), dict(quality=80)),
    ("ly-photo-17x9-q5", photo(17, 9, False, 11), dict(quality=5)),
    ("ly-photo-64x48-q75", photo(64, 48, False, 12), dict(quality=75, method=4)),
    ("ly-photo-63x47-q95-alpha", photo(63, 47, True, 13), dict(quality=95, method=6)),
    ("ly-photo-33x65-q40-alpha", photo(33, 65, True, 14), dict(quality=40, method=0, alpha_quality=50)),
    ("ly-photo-48x40-q100", photo(48, 40, False, 15), dict(quality=100, method=6)),
    ("ly-vp8x-icc", photo(30, 20, False, 16), dict(quality=60, icc_profile=b"\x00" * 128)),
]
for name, im, kw in items:
    path = os.path.join(out, name + ".webp")
    im.save(path, "WEBP", **kw)
    open(os.path.join(out, name + ".rgba"), "wb").write(np.asarray(Image.open(path).convert("RGBA")).tobytes())
frames = [photo(16, 16, False, 20), photo(16, 16, False, 21)]
frames[0].save(os.path.join(out, "animated.webp"), "WEBP", save_all=True, append_images=frames[1:], duration=100, lossless=True)
