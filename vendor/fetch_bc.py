"""Download Bouncy Castle C# 1.8.10 (official GitHub tag) into
vendor/bc-csharp-release-1.8.10 and apply WMAI's three NETCF patches.

NETCF 3.5 lacks Interlocked.Increment(ref long) and the 3-argument
ArgumentException constructor (found by tools/cfcheck.ps1). Bouncy Castle
already has lock-based fallbacks for older platforms; the patches enable them
for NETCF_2_0 - except SecureRandom, which keeps its CryptoAPI-seeded branch
(the NETCF_1_0 one seeds weakly) and only swaps the counter increment.

  python vendor/fetch_bc.py
"""
import io
import os
import sys
import urllib.request
import zipfile

URL = "https://github.com/bcgit/bc-csharp/archive/refs/tags/release-1.8.10.zip"
HERE = os.path.dirname(os.path.abspath(__file__))
TREE = os.path.join(HERE, "bc-csharp-release-1.8.10")

PATCHES = [
    ("crypto/src/security/SecureRandom.cs",
     "#else\n        private static long NextCounterValue()\n        {\n            return Interlocked.Increment(ref counter);\n        }",
     "#else\n#if NETCF_2_0\n        // WMAI patch: NETCF has no Interlocked.Increment(ref long). Keep this\n"
     "        // branch (OS CryptoAPI seeding) rather than the weak NETCF_1_0 one.\n"
     "        private static readonly object counterLock = new object();\n\n"
     "        private static long NextCounterValue()\n        {\n            lock (counterLock)\n            {\n"
     "                return ++counter;\n            }\n        }\n#else\n"
     "        private static long NextCounterValue()\n        {\n            return Interlocked.Increment(ref counter);\n        }\n#endif"),
    ("crypto/src/crypto/tls/AbstractTlsContext.cs",
     "#if NETCF_1_0\n",
     "#if NETCF_1_0 || NETCF_2_0 // WMAI patch: NETCF has no Interlocked.Increment(ref long)\n"),
    ("crypto/src/crypto/generators/OpenBsdBCrypt.cs",
     "#if PORTABLE\n",
     "#if PORTABLE || NETCF_2_0 // WMAI patch: NETCF lacks ArgumentException(string, string, Exception)\n"),
]


def main():
    if os.path.isdir(os.path.join(TREE, "crypto", "src")):
        print("already present:", TREE)
        return
    print("downloading", URL)
    data = urllib.request.urlopen(URL, timeout=120).read()
    z = zipfile.ZipFile(io.BytesIO(data))
    prefix = "bc-csharp-release-1.8.10/crypto/"
    names = [n for n in z.namelist() if n.startswith(prefix) and ("/src/" in n or "/bzip2/" in n)]
    z.extractall(HERE, names)
    print("extracted %d entries" % len(names))
    for rel, old, new in PATCHES:
        p = os.path.join(TREE, rel)
        data = open(p, "rb").read()
        bom = data.startswith(b"\xef\xbb\xbf")
        raw = data.decode("utf-8-sig")
        crlf = "\r\n" in raw
        text = raw.replace("\r\n", "\n")
        if old not in text:
            sys.exit("patch target not found in " + rel)
        text = text.replace(old, new, 1)
        if crlf:
            text = text.replace("\n", "\r\n")
        open(p, "w", encoding="utf-8-sig" if bom else "utf-8", newline="").write(text)
        print("patched:", rel)


if __name__ == "__main__":
    main()
