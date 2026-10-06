"""cecab.py - show what a Windows CE .cab installs (files, folders, registry,
shortcuts) by parsing its binary "MSCE" manifest (the *.000 file inside).

  python tools/cecab.py <extracted-cab-dir>     (extract first: 7z x file.cab -odir)
"""
import glob
import os
import struct
import sys

ROOTS = {1: "HKCR", 2: "HKCU", 3: "HKLM", 4: "HKU"}


def parse(path):
    d = open(path, "rb").read()
    if d[:4] != b"MSCE":
        raise ValueError("not an MSCE manifest: " + path)
    (n_str, n_dir, n_file, n_hive, n_key, n_link) = struct.unpack_from("<6H", d, 48)
    (o_str, o_dir, o_file, o_hive, o_key, o_link) = struct.unpack_from("<6I", d, 60)
    o_app, l_app, o_prov, l_prov = struct.unpack_from("<4H", d, 84)
    arch, = struct.unpack_from("<I", d, 20)
    out = {"app": d[o_app:o_app + l_app].rstrip(b"\0").decode("latin-1"),
           "provider": d[o_prov:o_prov + l_prov].rstrip(b"\0").decode("latin-1"),
           "arch": arch}

    strings, p = {}, o_str
    for _ in range(n_str):
        sid, ln = struct.unpack_from("<2H", d, p)
        strings[sid] = d[p + 4:p + 4 + ln].rstrip(b"\0").decode("latin-1")
        p += 4 + ln

    def joined(p, ln):
        ids = struct.unpack_from("<%dH" % (ln // 2), d, p)
        return "\\".join(strings.get(i, "?") for i in ids if i)

    dirs, p = {}, o_dir
    for _ in range(n_dir):
        did, ln = struct.unpack_from("<2H", d, p)
        dirs[did] = joined(p + 4, ln)
        p += 4 + ln

    files, p = [], o_file
    for _ in range(n_file):
        fid, did, _unk, flags, ln = struct.unpack_from("<3HIH", d, p)
        name = d[p + 12:p + 12 + ln].rstrip(b"\0").decode("latin-1")
        files.append((fid, dirs.get(did, "?"), name, flags))
        p += 12 + ln

    hives, p = {}, o_hive
    for _ in range(n_hive):
        hid, root, _unk, ln = struct.unpack_from("<4H", d, p)
        hives[hid] = ROOTS.get(root, "?") + "\\" + joined(p + 8, ln)
        p += 8 + ln

    keys, p = [], o_key
    for _ in range(n_key):
        kid, hid, _sub, flags, ln = struct.unpack_from("<3HIH", d, p)
        data = d[p + 12:p + 12 + ln]
        name, _, value = data.partition(b"\0")
        kind = flags & 0x10001
        if kind == 0x10001:  # DWORD
            value = "dword:%d" % struct.unpack_from("<I", value)[0] if len(value) >= 4 else value
        else:
            value = repr(value.rstrip(b"\0").decode("latin-1"))
        keys.append((hives.get(hid, "?"), name.decode("latin-1") or "(default)", value))
        p += 12 + ln

    links, p = [], o_link
    for _ in range(n_link):
        lid, _unk, base, target, ltype, ln = struct.unpack_from("<6H", d, p)
        links.append((joined(p + 12, ln), "file" if ltype == 1 else "dir", target))
        p += 12 + ln

    out.update(dirs=dirs, files=files, keys=keys, links=links)
    return out


def main(folder):
    for man in glob.glob(os.path.join(folder, "*.000")):
        m = parse(man)
        print("== %s  (provider %s, arch %d)" % (m["app"], m["provider"], m["arch"]))
        byname = {}
        for fid, dirname, name, flags in m["files"]:
            byname[fid] = dirname + "\\" + name
            print("  file  %s\\%s" % (dirname, name))
        for hive, name, value in m["keys"]:
            print("  reg   %s : %s = %s" % (hive, name, value))
        for path, kind, target in m["links"]:
            print("  link  %s -> %s" % (path, byname.get(target, "#%d" % target)))


if __name__ == "__main__":
    main(sys.argv[1])
