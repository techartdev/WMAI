"""Build the WMAI release kit: an SD-card layout ready to copy to the phone,
plus the GPL source archive of the compiler tools it ships.

  python tools/make_release.py [--pgcc-zip PATH] [--pgcc-src PATH] [--tools DIR]

  --pgcc-zip  PocketGCC 1.50 device package (pocketgcc-1.50-20031108-arm.zip,
              from https://sourceforge.net/projects/pocketgcc/): only its
              headers, libraries and samples are used, not its 2003 tools
  --pgcc-src  PocketGCC source tree folder "pgcc-src": from a WMAI release's
              pocketgcc-wmai-src zip, or the upstream
              pocketgcc-3.2.2-binutils-2.13.2.1-20031121-src (the WMAI patch
              is applied either way; it is idempotent)
  --tools     the six tools rebuilt with compiler/apply_wce_stdio.py
              (cc1plus.n.exe, cpp0.n.exe, as.n.exe, ld.n.exe, windres.n.exe,
              ar.n.exe); see compiler/README.md

Builds the app first (app/build.cmd). Output in release/.
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import cecab  # noqa: E402

TOOLS = ["cc1plus", "cpp0", "as", "ld", "windres", "ar"]


def version():
    s = open(os.path.join(ROOT, "app", "WMAI.cs"), encoding="utf-8-sig").read()
    return re.search(r'const string Version = "([^"]+)"', s).group(1)


def expand_cab(cab, dest):
    os.makedirs(dest, exist_ok=True)
    subprocess.run(["expand", "-F:*", cab, dest], check=True, stdout=subprocess.DEVNULL)


def build_pgcc(pgcc_zip, tools_dir, dest):
    """pgcc tree from the PocketGCC 1.50 CAB, minus the 2003 tools, plus ours."""
    tmp = tempfile.mkdtemp(prefix="wmai_pgcc_")
    zipfile.ZipFile(pgcc_zip).extractall(tmp)
    cab = [p for p in glob.glob(os.path.join(tmp, "**", "*.CAB"), recursive=True) if "pgcc" in os.path.basename(p).lower()][0]
    files = os.path.join(tmp, "cab")
    expand_cab(cab, files)
    man = cecab.parse(glob.glob(os.path.join(files, "*.000"))[0])
    members = {int(f.rsplit(".", 1)[1]): os.path.join(files, f) for f in os.listdir(files)
               if f.rsplit(".", 1)[-1].isdigit() and not f.endswith(".000")}
    for fid, dirname, name, _flags in man["files"]:
        rel = dirname[len("\\pgcc"):].strip("\\")
        if rel == "" and name.lower().endswith(".exe"):
            continue  # the 2003 tools: replaced by the rebuilt ones in pgcc\wmai
        out = os.path.join(dest, rel)
        os.makedirs(out, exist_ok=True)
        shutil.copyfile(members[fid], os.path.join(out, name))
    # newres.h (needed by .rc files) only ships with the sample; make it global.
    shutil.copyfile(os.path.join(dest, "samp", "menu", "newres.h"), os.path.join(dest, "include", "newres.h"))
    os.makedirs(os.path.join(dest, "wmai"), exist_ok=True)
    for t in TOOLS:
        shutil.copyfile(os.path.join(tools_dir, t + ".n.exe"), os.path.join(dest, "wmai", t + ".exe"))
    shutil.rmtree(tmp, ignore_errors=True)


def zip_dir(folder, zpath, arc_root):
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for base, _dirs, names in os.walk(folder):
            for n in names:
                full = os.path.join(base, n)
                z.write(full, os.path.join(arc_root, os.path.relpath(full, folder)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pgcc-zip", default=os.path.join(ROOT, "pocketgcc-1.50-20031108-arm.zip"))
    ap.add_argument("--pgcc-src", default=os.path.join(ROOT, "pocketgcc-3.2.2-binutils-2.13.2.1-20031121-src", "pgcc-src"))
    ap.add_argument("--tools", default=os.path.join(ROOT, "vm", "out"))
    a = ap.parse_args()

    if not os.path.exists(os.path.join(ROOT, "app", "WMAI.roots.idx")):
        subprocess.run(["cmd", "/c", os.path.join(ROOT, "tools", "rootindex", "build.cmd")], check=True)
    subprocess.run(["cmd", "/c", os.path.join(ROOT, "app", "build.cmd")], check=True)
    v = version()
    rel = os.path.join(ROOT, "release")
    kit = os.path.join(rel, "WMAI-" + v)
    if os.path.isdir(kit):
        shutil.rmtree(kit)
    card = os.path.join(kit, "Storage Card")

    app = os.path.join(card, "WMAI")
    os.makedirs(app)
    for f in ["WMAI.exe", "WMAI.roots", "WMAI.roots.idx", "WMAI.agent.json", "WMAI.config.example"]:
        shutil.copyfile(os.path.join(ROOT, "app", f), os.path.join(app, f))
    shutil.copyfile(os.path.join(ROOT, "vendor", "out", "cf", "BouncyCastle.Crypto.dll"), os.path.join(app, "BouncyCastle.Crypto.dll"))
    shutil.copytree(os.path.join(ROOT, "app", "templates"), os.path.join(app, "templates"))

    build_pgcc(a.pgcc_zip, a.tools, os.path.join(card, "pgcc"))
    for f in ["INSTALL.txt"]:
        shutil.copyfile(os.path.join(ROOT, "docs", f), os.path.join(kit, f))
    for f in ["LICENSE", "THIRD_PARTY.md"]:
        shutil.copyfile(os.path.join(ROOT, f), os.path.join(kit, f))

    zip_dir(kit, os.path.join(rel, "WMAI-%s-sdcard.zip" % v), "WMAI-" + v)

    # GPL: the exact source of the shipped compiler tools = PocketGCC's source
    # tree with compiler/apply_wce_stdio.py applied.
    src_tmp = tempfile.mkdtemp(prefix="wmai_pgccsrc_")
    tree = os.path.join(src_tmp, "pgcc-src")
    shutil.copytree(a.pgcc_src, tree)
    subprocess.run([sys.executable, os.path.join(ROOT, "compiler", "apply_wce_stdio.py"), tree], check=True, stdout=subprocess.DEVNULL)
    for f in ["README.md", "wce-stdio.c", "apply_wce_stdio.py", "xp-agent.bat"]:
        shutil.copyfile(os.path.join(ROOT, "compiler", f), os.path.join(src_tmp, "WMAI-" + f))
    zip_dir(src_tmp, os.path.join(rel, "pocketgcc-wmai-src-%s.zip" % v), "pocketgcc-wmai-src")
    shutil.rmtree(src_tmp, ignore_errors=True)

    for f in sorted(os.listdir(rel)):
        p = os.path.join(rel, f)
        if os.path.isfile(p):
            print("%-40s %8d KB" % (f, os.path.getsize(p) // 1024))


if __name__ == "__main__":
    main()
