"""Builds windows/Swivel/Swivel.ico from the Mac app icon (Resources/Swivel.icns). Needs Pillow; run from the repo root:
python windows/tools/make_icon.py
"""
import io, pathlib, struct
from PIL import Image

root = pathlib.Path(__file__).resolve().parents[2]
data = (root / "Resources/Swivel.icns").read_bytes()
pngs, i = {}, 8
while i < len(data):
    kind, size = struct.unpack(">4sI", data[i:i + 8])
    chunk = data[i + 8:i + size]
    if chunk.startswith(b"\x89PNG"):
        pngs[kind] = chunk
    i += size

master = Image.open(io.BytesIO(pngs[b"ic10"])).convert("RGBA")
dest = root / "windows/Swivel/Swivel.ico"
master.save(dest, format="ICO", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", dest)
