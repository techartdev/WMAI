"""Print the PE header essentials of a WinCE binary."""
import struct
import sys

d = open(sys.argv[1], "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
machine, nsec, _ts, _sym, _nsym, optsz, chars = struct.unpack_from("<HHIIIHH", d, pe + 4)
o = pe + 24
entry, = struct.unpack_from("<I", d, o + 16)
base, = struct.unpack_from("<I", d, o + 28)
imagesz, = struct.unpack_from("<I", d, o + 56)
subsys, dllchars = struct.unpack_from("<HH", d, o + 68)
ndirs, = struct.unpack_from("<I", d, o + 92)
dirs = [struct.unpack_from("<II", d, o + 96 + 8 * i) for i in range(ndirs)]
names = ["export", "import", "resource", "exception", "security", "basereloc"]
print("characteristics 0x%04X (DLL=%s, relocs stripped=%s)" % (chars, bool(chars & 0x2000), bool(chars & 0x0001)))
print("entry RVA 0x%X, image base 0x%X, image size 0x%X, subsystem %d" % (entry, base, imagesz, subsys))
for i, n in enumerate(names):
    if i < ndirs:
        print("  dir %-10s rva 0x%06X size %d" % (n, dirs[i][0], dirs[i][1]))
for i in range(nsec):
    s = pe + 24 + optsz + 40 * i
    name = d[s:s + 8].rstrip(b"\0").decode("latin-1")
    vsize, va, rawsize, rawptr = struct.unpack_from("<4I", d, s + 8)
    print("  section %-8s va 0x%06X vsize %6d raw %6d @0x%X" % (name, va, vsize, rawsize, rawptr))
