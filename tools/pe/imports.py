"""List a WinCE PE's imports per DLL (by name or ordinal)."""
import struct
import sys

d = open(sys.argv[1], "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
optsz = struct.unpack_from("<H", d, pe + 20)[0]
nsec = struct.unpack_from("<H", d, pe + 6)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + 40 * i
    vsize, va, rawsize, rawptr = struct.unpack_from("<4I", d, o + 8)
    secs.append((va, max(vsize, rawsize), rawptr))


def off(rva):
    for va, vs, raw in secs:
        if va <= rva < va + vs:
            return rva - va + raw
    raise ValueError(hex(rva))


def cstr(o):
    return d[o:d.index(b"\0", o)].decode("latin-1")


imp_rva = struct.unpack_from("<I", d, pe + 24 + 104)[0]
o = off(imp_rva)
while True:
    ilt, _ts, _fc, name_rva, iat = struct.unpack_from("<5I", d, o)
    if name_rva == 0:
        break
    dll = cstr(off(name_rva))
    names = []
    t = off(ilt or iat)
    while True:
        v, = struct.unpack_from("<I", d, t)
        if v == 0:
            break
        names.append("#%d" % (v & 0xFFFF) if v & 0x80000000 else cstr(off(v) + 2))
        t += 4
    print("%s (%d): %s" % (dll, len(names), ", ".join(names)))
    o += 20
