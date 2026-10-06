"""Apply WMAI's --stdout=FILE / --stderr=FILE change to a PocketGCC source tree.

Copies wce-stdio.c into libiberty (linked into every tool), adds a call to it
at the top of each tool's main() - as the first declaration's initializer, so
it runs before anything else and stays valid C89 - and adds the object to
makefile.native. Idempotent.

  python compiler/apply_wce_stdio.py <pgcc-src>

<pgcc-src> is the "pgcc-src" folder of PocketGCC's source package
(pocketgcc-3.2.2-binutils-2.13.2.1-20031121-src). See compiler/README.md.
"""
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
root = sys.argv[1] if len(sys.argv) > 1 else "pgcc-src"

shutil.copyfile(os.path.join(HERE, "wce-stdio.c"), os.path.join(root, "libiberty", "wce-stdio.c"))
print("copied: libiberty/wce-stdio.c")

mains = ["gcc/gcc/main.c", "gcc/gcc/cppmain.c", "binutils/gas/as.c", "binutils/ld/ldmain.c",
         "binutils/binutils/windres.c", "binutils/binutils/ar.c"]
for rel in mains:
    p = os.path.join(root, rel)
    s = open(p, encoding="latin-1").read()
    if "wce_stdio_redirect" in s:
        print("already patched:", rel)
        continue
    # The return type may sit on its own line above "main (argc, argv)"; the
    # prototype must go above that line, not between it and main.
    m = re.search(r"\n((?:int\n)?)main \(argc, argv\)\n(\s+int argc;\n\s+char \*\*argv;\n)\{\n", s)
    if not m:
        sys.exit("main() not found in " + rel)
    proto = "\n/* WMAI: --stdout=FILE / --stderr=FILE, see libiberty/wce-stdio.c */\nextern int wce_stdio_redirect PARAMS ((int *, char **));\n"
    body = "{\n  int wce_redirected = wce_stdio_redirect (&argc, argv); /* WMAI: must run first */\n"
    s = s[:m.start()] + proto + s[m.start():m.end() - 2] + body + s[m.end():]
    open(p, "w", encoding="latin-1", newline="").write(s)
    print("patched:", rel)

mk = os.path.join(root, "makefile.native")
s = open(mk, encoding="latin-1").read()
if "wce-stdio.no" not in s:
    s = s.replace("libiberty_objs=libiberty/asprintf.no", "libiberty_objs=libiberty/wce-stdio.no libiberty/asprintf.no", 1)
    open(mk, "w", encoding="latin-1", newline="").write(s)
    print("patched: makefile.native")
