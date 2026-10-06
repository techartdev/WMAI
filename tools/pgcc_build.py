"""Drive the PocketGCC pipeline on the phone over RAPI, stage by stage.

Each stage runs under tools/devtools/Run.exe on the phone, which waits for the
process and writes its exit code to run.log (an output file that stops growing
is NOT a completion signal: cc1plus pauses between functions). PocketGCC tools
print errors to a PocketConsole window on the phone, which is not captured.

  python tools/pgcc_build.py "\\Storage Card\\pgcc\\samp\\menu" menu
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import wm  # noqa: E402

PGCC = "\\Storage Card\\pgcc"


def q(path):
    return '"%s"' % path


def size_of(path):
    folder, name = path.rsplit("\\", 1)
    fd = wm.CE_FIND_DATA()
    h = wm.rapi.CeFindFirstFile(path, wm.ctypes.byref(fd))
    if h in (None, wm.INVALID_HANDLE):
        return None
    wm.rapi.CeFindClose(h)
    return fd.size_lo


LENIENT = set()  # stages whose exit code is ignored if they produced output (--lenient-cc1)
# Tools rebuilt with --stdout/--stderr support (vm/apply_wce_stdio.py); the
# original 2003 tools stay in PGCC itself. --orig selects those.
BIN = PGCC + "\\wmai"
MSG = "\\Temp\\pgcc_err.txt"      # the tool's own stderr (--stderr=)
MSG_OUT = "\\Temp\\pgcc_out.txt"  # the tool's own stdout (--stdout=)
RUN = "\\Storage Card\\WMAI\\devtools\\Run.exe"
RUN_LOG = "\\Storage Card\\WMAI\\devtools\\run.log"


def read_text(path):
    h = wm.rapi.CeCreateFile(path, wm.GENERIC_READ, 1, None, wm.OPEN_EXISTING, wm.FILE_ATTRIBUTE_NORMAL, None)
    if h in (None, wm.INVALID_HANDLE):
        return ""
    try:
        buf = wm.ctypes.create_string_buffer(4096)
        got = wm.wt.DWORD()
        wm.rapi.CeReadFile(h, buf, 4096, wm.ctypes.byref(got), None)
        return buf.raw[:got.value].decode("utf-8-sig", "replace")
    finally:
        wm.rapi.CeCloseHandle(h)


def stage(name, exe, args, output, timeout):
    for p in (output, RUN_LOG, MSG, MSG_OUT):
        if size_of(p) is not None:
            wm.rapi.CeDeleteFile(p)
    if BIN != PGCC:  # rebuilt tools capture their own messages
        args += " --stderr=%s --stdout=%s" % (MSG, MSG_OUT)
    t = time.time()
    wm.run(RUN, '\\Temp\\run_out.txt \\Temp\\run_err.txt "%s\\%s" %s' % (BIN, exe, args))
    while time.time() - t < timeout:
        time.sleep(2)
        log = read_text(RUN_LOG)
        if "exit code" in log or "FAILED" in log:
            line = [l for l in log.splitlines() if "exit code" in l or "FAILED" in l][0]
            ok = (line.startswith("exit code 0 ") or name in LENIENT) and size_of(output)
            print("  %-8s %-4s %6.1f s  %s  (%s)" % (name, "ok" if ok else "FAIL", time.time() - t,
                                                  output.rsplit("\\", 1)[1] + (" %d bytes" % size_of(output) if size_of(output) else ""), line))
            msg = (read_text(MSG_OUT) + read_text(MSG)).strip() if BIN != PGCC else ""
            if msg:
                print("           | " + msg.replace("\n", "\n           | "))
            return bool(ok)
    print("  %-8s TIMEOUT after %.0f s" % (name, time.time() - t))
    return False


def main(src, base, timeout=300):
    s = src + "\\" + base
    inc = '-I %s -I %s -include %s' % (q(PGCC + "\\include"), q(src), q(PGCC + "\\fixincl.h"))
    steps = [
        # Never pass -quiet: this cc1plus then exits 1 without output. Without it,
        # progress goes to stderr; if stderr has nowhere to go (console off, no
        # redirect) the write fails and cc1plus exits 1 despite a complete .s.
        ("cc1plus", "cc1plus.exe", '%s -o %s %s -fms-extensions' % (q(s + ".cpp"), q(s + ".s"), inc), s + ".s"),
        ("as", "as.exe", '%s -o %s' % (q(s + ".s"), q(s + ".o")), s + ".o"),
    ]
    if size_of(s + ".rc") is not None:
        steps += [
            ("cpp0", "cpp0.exe", '%s -o %s %s -DRC_INVOKED' % (q(s + ".rc"), q(s + ".rc.p"), inc.replace("-include", "-include")), s + ".rc.p"),
            ("windres", "windres.exe", '%s -o %s --include-dir %s' % (q(s + ".rc.p"), q(s + ".rc.o"), q(src)), s + ".rc.o"),
        ]
    objs = q(s + ".o") + (" " + q(s + ".rc.o") if size_of(s + ".rc") is not None else "")
    steps.append(("ld", "ld.exe", '%s -o %s -L %s -l cpplib -l corelibc -l coredll -l aygshell -l runtime -l portlib'
                  % (objs, q(s + ".exe"), q(PGCC + "\\lib")), s + ".exe"))
    t = time.time()
    for name, exe, args, out in steps:
        if not stage(name, exe, args, out, timeout):
            return False
    print("built %s.exe in %.1f s" % (s, time.time() - t))
    return True


if __name__ == "__main__":
    wm.rapi.CeDeleteFile.argtypes = [wm.wt.LPCWSTR]
    if wm.rapi.CeRapiInit() != 0:
        sys.exit("RAPI not connected")
    if "--lenient-cc1" in sys.argv:
        LENIENT.add("cc1plus")
    if "--orig" in sys.argv:
        BIN = PGCC
    try:
        sys.exit(0 if main(sys.argv[1], sys.argv[2]) else 1)
    finally:
        wm.rapi.CeRapiUninit()
