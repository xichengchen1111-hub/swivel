# Generates the 7z test fixtures. libarchive (bsdtar) writes the real-world ones; the
# small writer below hand-assembles BCJ and LZMA-variant archives from Python's lzma
# (liblzma) raw streams. bsdtar reads every output back as an independent check.
import lzma, os, struct, subprocess, sys, zlib, shutil, tempfile

out = sys.argv[1]; os.makedirs(out, exist_ok=True)

def lcg_bytes(n, seed=12345):
    x = seed; b = bytearray(n)
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        b[i] = (x >> 16) & 0xFF
    return bytes(b)

def pattern(n): return bytes(((i * 7) + (i >> 8)) & 0xFF for i in range(n))
def lines(n): return "".join(f"line {i:06d} swivel\n" for i in range(n)).encode()

def x86_like(n, seed=777):
    x = seed; b = bytearray()
    def nxt():
        nonlocal x; x = (x * 1103515245 + 12345) & 0x7FFFFFFF; return x
    while len(b) < n:
        r = nxt()
        if r % 5 == 0:
            v = nxt()
            b += bytes([0xE8 | ((v >> 3) & 1), v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0x0F, 0xFF if (v >> 20) & 1 else 0x00])
        else:
            b.append((r >> 16) % 24 + 0x40)
    return bytes(b[:n])

# --- libarchive fixtures ---
tree = tempfile.mkdtemp()
def put(rel, data):
    p = os.path.join(tree, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, "wb").write(data); os.utime(p, (1600000000, 1600000000))
put("a.txt", b"hello 7z\n")
put("子目录/数据 🦜.bin", pattern(70000))
put("empty.txt", b"")
os.makedirs(os.path.join(tree, "空文件夹"))
small = tempfile.mkdtemp(); shutil.copytree(tree, small, dirs_exist_ok=True)
put("large.txt", lines(150000))
put("noise.bin", lcg_bytes(30000))
def bsd(name, src, comp):
    path = os.path.join(out, name)
    if os.path.exists(path): os.remove(path)
    subprocess.check_call(["bsdtar", "--format", "7zip", "--options", "7zip:compression=" + comp, "-cf", path, "-C", src, "."])
bsd("libarchive-lzma2.7z", tree, "lzma2")
bsd("libarchive-lzma1.7z", tree, "lzma1")
bsd("libarchive-copy.7z", small, "copy")
one = tempfile.mkdtemp(); open(os.path.join(one, "a.txt"), "wb").write(b"hello 7z\n")
for c in ["deflate", "bzip2", "ppmd"]: bsd(f"libarchive-{c}.7z", one, c)
os.symlink("a.txt", os.path.join(one, "link"))
bsd("libarchive-symlink.7z", one, "lzma2")

# --- hand-assembled ---
def num(v):
    for n in range(9):
        if n == 8: return bytes([0xFF]) + v.to_bytes(8, "little")
        if v < (1 << (7 * (n + 1))):
            first = ((0xFF << (8 - n)) & 0xFF) | (v >> (8 * n))
            return bytes([first]) + (v & ((1 << (8 * n)) - 1)).to_bytes(n, "little")
def bits(flags):
    b = bytearray((len(flags) + 7) // 8)
    for i, f in enumerate(flags):
        if f: b[i // 8] |= 0x80 >> (i % 8)
    return bytes(b)
def crc(d): return zlib.crc32(d) & 0xFFFFFFFF

def write7z(path, folders, files, reverse=False):
    """folders: list of (coders, packed, out_sizes, substream_sizes) where coders=[(id_bytes, props)]
    packed side first (coder i+1 reads coder i, as libarchive expects); files: list of (name, data_or_None_for_dir)."""
    packed = b"".join(f[1] for f in folders)
    h = bytearray([0x01, 0x04, 0x06]) + num(0) + num(len(folders)) + bytes([0x09])
    for f in folders: h += num(len(f[1]))
    h += bytes([0x00, 0x07, 0x0B]) + num(len(folders)) + b"\x00"
    for coders, _, _, _ in folders:
        h += num(len(coders))
        for cid, props in (coders[::-1] if reverse else coders):
            h += bytes([len(cid) | (0x20 if props else 0)]) + cid
            if props: h += num(len(props)) + props
        for i in range(len(coders) - 1): h += num(i) + num(i + 1) if reverse else num(i + 1) + num(i)
    h += bytes([0x0C])
    for f in folders:
        for s in (f[2][::-1] if reverse else f[2]): h += num(s)
    h += bytes([0x00, 0x08, 0x0D])
    for f in folders: h += num(len(f[3]))
    h += bytes([0x09])
    for f in folders:
        for s in f[3][:-1]: h += num(s)
    datas = [d for _, d in files if d is not None and len(d) > 0]
    h += bytes([0x0A, 0x01]) + b"".join(struct.pack("<I", crc(d)) for d in datas)
    h += bytes([0x00, 0x00])
    h += bytes([0x05]) + num(len(files))
    empty = [d is None or len(d) == 0 for _, d in files]
    if any(empty):
        e = bits(empty); h += bytes([0x0E]) + num(len(e)) + e
        ef = bits([d is not None for (_, d), em in zip(files, empty) if em]); h += bytes([0x0F]) + num(len(ef)) + ef
    names = b"\x00" + b"".join(n.encode("utf-16-le") + b"\0\0" for n, _ in files)
    h += bytes([0x11]) + num(len(names)) + names
    times = b"\x01\x00" + b"".join(struct.pack("<Q", 132000000000000000) for _ in files)
    h += bytes([0x14]) + num(len(times)) + times
    h += bytes([0x00, 0x00])
    start = struct.pack("<QQI", len(packed), len(h), crc(h))
    with open(path, "wb") as f:
        f.write(b"7z\xbc\xaf\x27\x1c\x00\x04" + struct.pack("<I", crc(start)) + start + packed + h)

LZMA2 = b"\x21"; LZMA = b"\x03\x01\x01"; BCJ = b"\x03\x03\x01\x03"
def lzma2_props(dict_size):
    for p in range(41):
        if ((2 | (p & 1)) << (p // 2 + 11)) >= dict_size: return bytes([p])

code1 = x86_like(120000); code2 = x86_like(5000, seed=99)
joined = code1 + code2
raw = lzma.compress(joined, format=lzma.FORMAT_RAW, filters=[{"id": lzma.FILTER_X86}, {"id": lzma.FILTER_LZMA2, "dict_size": 1 << 20}])
write7z(os.path.join(out, "handmade-bcj.7z"), [([(LZMA2, lzma2_props(1 << 20)), (BCJ, b"")], raw, [len(joined), len(joined)], [len(code1), len(code2)])],
        [("code/one.exe", code1), ("code/two.dll", code2)])
# The same folder with the filter listed first and the bind pair the other way round.
write7z(os.path.join(out, "handmade-bcj-filter-first.7z"), [([(LZMA2, lzma2_props(1 << 20)), (BCJ, b"")], raw, [len(joined), len(joined)], [len(code1), len(code2)])],
        [("code/one.exe", code1), ("code/two.dll", code2)], reverse=True)

folders, files = [], []
for i, (lc, lp, pb, d) in enumerate([(3, 0, 2, 1 << 16), (0, 2, 0, 4096), (1, 3, 4, 4096), (4, 0, 0, 1 << 20), (0, 0, 0, 4096)]):
    data = lines(3000 + i * 500) + lcg_bytes(2000, seed=i + 1) + pattern(20000 + i)
    raw = lzma.compress(data, format=lzma.FORMAT_RAW, filters=[{"id": lzma.FILTER_LZMA1, "lc": lc, "lp": lp, "pb": pb, "dict_size": d}])
    folders.append(([(LZMA, bytes([(pb * 5 + lp) * 9 + lc]) + struct.pack("<I", d))], raw, [len(data)], [len(data)]))
    files.append((f"lzma {lc}{lp}{pb}.dat", data))
files.insert(2, ("folder", None)); files.insert(3, ("folder/zero.txt", b""))
write7z(os.path.join(out, "handmade-lzma.7z"), folders, files)

for name in sorted(os.listdir(out)):
    dest = tempfile.mkdtemp()
    r = subprocess.run(["bsdtar", "-xf", os.path.join(out, name), "-C", dest], capture_output=True)
    print(name, os.path.getsize(os.path.join(out, name)), "bsdtar:", r.returncode, r.stderr.decode()[:120].strip())
# Cross-check hand-made contents against the generator.
for name, expected in [("handmade-bcj.7z", {"code/one.exe": code1, "code/two.dll": code2})]:
    dest = tempfile.mkdtemp(); subprocess.check_call(["bsdtar", "-xf", os.path.join(out, name), "-C", dest])
    for rel, data in expected.items(): assert open(os.path.join(dest, rel), "rb").read() == data, rel
print("bcj crosscheck ok")
