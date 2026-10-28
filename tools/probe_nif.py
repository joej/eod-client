#!/usr/bin/env python3
# Probe a 4.2.x NetImmerse NIF for the W3 question: do UV-less shapes reference a texture /
# a NiTextureEffect, or are they flat material-colored? Read-only. Usage: probe_nif.py FILE ...
import struct, sys, re
from collections import Counter, defaultdict

def load(path):
    b = open(path, 'rb').read()
    nl = b.index(b'\x0a')
    ver = struct.unpack_from('<I', b, nl+1)[0]
    nb  = struct.unpack_from('<I', b, nl+5)[0]
    return b, nl+1+8, ver, nb

def typeat(b, q):
    if q+6 > len(b): return None
    ln = struct.unpack_from('<I', b, q)[0]
    if ln < 3 or ln > 40 or q+4+ln > len(b): return None
    if b[q+4:q+6] != b'Ni': return None
    s = b[q+4:q+4+ln]
    if any(c < 0x20 or c > 0x7e for c in s): return None
    return s.decode('latin1')

def index_blocks(b, start, nb):
    blocks = []; q = start
    while len(blocks) < nb and q is not None and q < len(b):
        t = typeat(b, q)
        if t is None: break
        blocks.append([t, q]); after = q+4+len(t); nq = None
        for p in range(after, len(b)-5):
            if typeat(b, p) is not None: nq = p; break
        q = nq
    return blocks

class Cur:
    def __init__(s, b, o): s.b=b; s.o=o
    def u8(s): v=s.b[s.o]; s.o+=1; return v
    def u16(s): v=struct.unpack_from('<H',s.b,s.o)[0]; s.o+=2; return v
    def u32(s): v=struct.unpack_from('<I',s.b,s.o)[0]; s.o+=4; return v
    def i32(s): v=struct.unpack_from('<i',s.b,s.o)[0]; s.o+=4; return v
    def f32(s): v=struct.unpack_from('<f',s.b,s.o)[0]; s.o+=4; return v
    def sstr(s): ln=s.u32(); v=s.b[s.o:s.o+ln]; s.o+=ln; return v.decode('latin1')
    def vec3(s): return (s.f32(),s.f32(),s.f32())

def read_av(c):
    c.sstr(); c.i32(); c.i32(); c.u16(); c.vec3()
    [c.f32() for _ in range(9)]; c.f32(); c.vec3()
    npr = c.u32(); props = [c.i32() for _ in range(npr)]
    hasbv = c.u8()
    return props, hasbv

def data_uvsets(b, off):
    c = Cur(b, off); c.sstr(); nv = c.u16()
    if c.u8(): c.o += 12*nv            # verts
    if c.u8(): c.o += 12*nv            # normals
    c.vec3(); c.f32()                  # bsphere
    if c.u8(): c.o += 16*nv            # colors
    return nv, c.u16() & 0x3f

def read_mat(b, off):
    c = Cur(b, off); c.sstr(); nm = c.sstr(); c.i32(); c.i32(); c.u16()
    c.vec3(); dif = c.vec3(); c.vec3(); c.vec3(); c.f32(); al = c.f32()
    return nm, dif, al

def probe(path):
    b, start, ver, nb = load(path)
    vs = '%d.%d.%d.%d' % ((ver>>24)&255,(ver>>16)&255,(ver>>8)&255,ver&255)
    print(f"\n=== {path.split('/')[-1]}  version {vs}  numblocks(hdr)={nb} ===")
    # Version-agnostic substring census of the texture-relevant block names + external texture names.
    # (For 10.1.0.0/Gamebryo the type-string scanner below won't align, but these substrings still
    #  tell us whether any texture-effect / texturing property / source texture is present.)
    for name in (b'NiTextureEffect', b'NiTexturingProperty', b'NiSourceTexture', b'NiMaterialProperty'):
        print(f"  substr {name.decode():22} = {b.count(name)}")
    texnames = sorted(set(m.group(0).decode('latin1')
                          for m in re.finditer(rb'[ -~]{3,48}\.(dds|tga|bmp|DDS|TGA|BMP)', b)))
    print(f"  external texture names ({len(texnames)}): {texnames[:12]}{' …' if len(texnames)>12 else ''}")
    if ver >= 0x0A010000:
        print("  (10.1.0.0 Gamebryo: skipping per-shape crosstab — type-index layout)")
        return
    blocks = index_blocks(b, start, nb)
    census = Counter(t for t,_ in blocks)
    print(f"  indexed {len(blocks)}/{nb} blocks; census:", dict(census))

    # cross-tab: (has NiTexturingProperty) x (uvSets) ; material color on textureless shapes
    rows = Counter()
    matcolor_on_untex = Counter()
    bad = 0
    for i,(t,off) in enumerate(blocks):
        if t not in ('NiTriShape','NiTriStrips'): continue
        try:
            c = Cur(b, off); c.sstr(); props, hasbv = read_av(c)
            if hasbv: continue  # our reader bails on bounding volume; skip to avoid misparse
            dref = c.i32()
            dt = blocks[dref][0] if 0 <= dref < len(blocks) else '?'
            if dt == 'NiTriShapeData':
                nv, uv = data_uvsets(b, blocks[dref][1])
            else:
                uv = '?'  # strips: skip uv read here
            has_tex = any(0<=p<len(blocks) and blocks[p][0]=='NiTexturingProperty' for p in props)
            rows[(has_tex, uv)] += 1
            if not has_tex:
                mr = next((p for p in props if 0<=p<len(blocks) and blocks[p][0]=='NiMaterialProperty'), None)
                if mr is not None:
                    nm, dif, al = read_mat(b, blocks[mr][1])
                    matcolor_on_untex[(nm, 'rgb(%d,%d,%d)'%(round(dif[0]*255),round(dif[1]*255),round(dif[2]*255)), round(al,2))] += 1
        except Exception as e:
            bad += 1
    print("(has NiTexturingProperty, uvSets) -> count:")
    for (ht, uv), n in sorted(rows.items(), key=lambda x:(-x[1])):
        print(f"   texturing={ht!s:5}  uvSets={uv!s:3}  x{n}")
    if matcolor_on_untex:
        print("material color on shapes WITHOUT a texturing property:")
        for (nm, col, al), n in matcolor_on_untex.most_common():
            print(f"   x{n:3}  {col} alpha={al}  mat={nm!r}")
    if bad: print("skipped (parse/misalign):", bad)

for p in sys.argv[1:]:
    probe(p)
