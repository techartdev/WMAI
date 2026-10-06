"""Copy the phone's .NET Compact Framework 3.5 assemblies into tools/cfref, for
tools/cfcheck.ps1 (which checks that WMAI only uses APIs NETCF 3.5 really has).

They are Microsoft binaries and are not in the repository; this pulls them from
your own device over RAPI (Windows Mobile Device Center must be connected).
NETCF 3.5 installs its GAC either in \\Windows or on the storage card.

  python tools/pull_cfref.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wm  # noqa: E402

NAMES = ["mscorlib", "System", "System.Windows.Forms", "System.Drawing"]
DEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cfref")


def exists(path):
    fd = wm.CE_FIND_DATA()
    h = wm.rapi.CeFindFirstFile(path, wm.ctypes.byref(fd))
    if h in (None, wm.INVALID_HANDLE):
        return False
    wm.rapi.CeFindClose(h)
    return True


def main():
    if wm.rapi.CeRapiInit() != 0:
        sys.exit("RAPI not connected - is the phone connected in Windows Mobile Device Center?")
    try:
        os.makedirs(DEST, exist_ok=True)
        roots = ["\\Windows"] + ["\\" + d + "\\Windows" for d in ("Storage Card", "SD Card", "Storage")]
        for n in NAMES:
            name = "GAC_%s_v3_5_0_0_cneutral_1.dll" % n
            src = next((r + "\\" + name for r in roots if exists(r + "\\" + name)), None)
            if src is None:
                sys.exit("not found on the device: %s (is .NET CF 3.5 installed?)" % name)
            wm.pull(src, os.path.join(DEST, n + ".dll"))
    finally:
        wm.rapi.CeRapiUninit()


if __name__ == "__main__":
    main()
