"""wm.py - talk to the Windows Mobile phone from the PC over RAPI (needs WMDC connected).

  python tools/wm.py info
  python tools/wm.py ls "\\Storage Card"
  python tools/wm.py push app/WMAI.exe "\\Storage Card\\WMAI.exe"
  python tools/wm.py pull "\\My Documents\\test.txt" test.txt
  python tools/wm.py mkdir "\\Storage Card\\New"
  python tools/wm.py run "\\Windows\\calc.exe" [args]

Device paths use backslashes; quote them in the shell.
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys

rapi = ctypes.WinDLL(r"C:\Windows\System32\rapi.dll")

INVALID_HANDLE = wt.HANDLE(-1).value
GENERIC_READ, GENERIC_WRITE = 0x80000000, 0x40000000
CREATE_ALWAYS, OPEN_EXISTING = 2, 3
FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_DIRECTORY = 0x80, 0x10


class CEOSVERSIONINFO(ctypes.Structure):
    _fields_ = [("size", wt.DWORD), ("major", wt.DWORD), ("minor", wt.DWORD), ("build", wt.DWORD),
                ("platform", wt.DWORD), ("csd", wt.WCHAR * 128)]


class CE_FIND_DATA(ctypes.Structure):
    _fields_ = [("attrs", wt.DWORD), ("ctime", wt.FILETIME), ("atime", wt.FILETIME), ("wtime", wt.FILETIME),
                ("size_hi", wt.DWORD), ("size_lo", wt.DWORD), ("oid", wt.DWORD), ("name", wt.WCHAR * 260)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE), ("pid", wt.DWORD), ("tid", wt.DWORD)]


rapi.CeRapiInit.restype = ctypes.c_long
rapi.CeGetLastError.restype = wt.DWORD
rapi.CeFindFirstFile.restype = wt.HANDLE
rapi.CeFindFirstFile.argtypes = [wt.LPCWSTR, ctypes.POINTER(CE_FIND_DATA)]
rapi.CeFindNextFile.argtypes = [wt.HANDLE, ctypes.POINTER(CE_FIND_DATA)]
rapi.CeFindClose.argtypes = [wt.HANDLE]
rapi.CeCreateFile.restype = wt.HANDLE
rapi.CeCreateFile.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
rapi.CeWriteFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
rapi.CeReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
rapi.CeCloseHandle.argtypes = [wt.HANDLE]
rapi.CeCreateProcess.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL, wt.DWORD,
                                 ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                 ctypes.POINTER(PROCESS_INFORMATION)]
rapi.CeGetSpecialFolderPath.argtypes = [ctypes.c_int, wt.DWORD, wt.LPWSTR]
rapi.CeCreateDirectory.argtypes = [wt.LPCWSTR, ctypes.c_void_p]
rapi.CeRegOpenKeyEx.argtypes = [wt.HKEY, wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.POINTER(wt.HKEY)]
rapi.CeRegQueryValueEx.argtypes = [wt.HKEY, wt.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(wt.DWORD), ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
rapi.CeRegCreateKeyEx.argtypes = [wt.HKEY, wt.LPCWSTR, wt.DWORD, wt.LPWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, ctypes.POINTER(wt.HKEY), ctypes.POINTER(wt.DWORD)]
rapi.CeRegSetValueEx.argtypes = [wt.HKEY, wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD]
rapi.CeRegCloseKey.argtypes = [wt.HKEY]


def fail(what):
    sys.exit("%s failed (device error %d)" % (what, rapi.CeGetLastError()))


def info():
    v = CEOSVERSIONINFO()
    v.size = ctypes.sizeof(v)
    rapi.CeGetVersionEx(ctypes.byref(v))
    print("Windows CE %d.%d build %d" % (v.major, v.minor, v.build))
    buf = ctypes.create_unicode_buffer(260)
    rapi.CeGetSpecialFolderPath(5, 260, buf)  # CSIDL_PERSONAL
    print("My Documents:", buf.value)


def ls(path):
    fd = CE_FIND_DATA()
    h = rapi.CeFindFirstFile(path.rstrip("\\") + "\\*", ctypes.byref(fd))
    if h in (None, INVALID_HANDLE):
        fail("list " + path)
    try:
        while True:
            if fd.attrs & FILE_ATTRIBUTE_DIRECTORY:
                print("<dir>       %s" % fd.name)
            else:
                print("%10d  %s" % ((fd.size_hi << 32) | fd.size_lo, fd.name))
            if not rapi.CeFindNextFile(h, ctypes.byref(fd)):
                break
    finally:
        rapi.CeFindClose(h)


def push(local, remote):
    data = open(local, "rb").read()
    h = rapi.CeCreateFile(remote, GENERIC_WRITE, 0, None, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, None)
    if h in (None, INVALID_HANDLE):
        fail("create " + remote + " (is the app running and holding the file?)")
    try:
        written = wt.DWORD()
        if not rapi.CeWriteFile(h, data, len(data), ctypes.byref(written), None) or written.value != len(data):
            fail("write " + remote)
    finally:
        rapi.CeCloseHandle(h)
    print("pushed %d bytes -> %s" % (len(data), remote))


def pull(remote, local):
    h = rapi.CeCreateFile(remote, GENERIC_READ, 1, None, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None)
    if h in (None, INVALID_HANDLE):
        fail("open " + remote)
    chunks = []
    try:
        buf = ctypes.create_string_buffer(65536)
        got = wt.DWORD()
        while rapi.CeReadFile(h, buf, len(buf), ctypes.byref(got), None) and got.value:
            chunks.append(buf.raw[:got.value])
    finally:
        rapi.CeCloseHandle(h)
    data = b"".join(chunks)
    with open(local, "wb") as f:
        f.write(data)
    print("pulled %d bytes -> %s" % (len(data), os.path.abspath(local)))


HKEYS = {"HKCR": 0x80000000, "HKCU": 0x80000001, "HKLM": 0x80000002, "HKU": 0x80000003}
REG_SZ, REG_DWORD = 1, 4


def _hkey(root):
    return wt.HKEY(HKEYS[root.upper()])


def reg_get(root, key, name):
    h = wt.HKEY()
    rc = rapi.CeRegOpenKeyEx(_hkey(root), key, 0, 0, ctypes.byref(h))
    if rc != 0:
        print("%s\\%s: key not found (error %d)" % (root, key, rc))
        return
    try:
        typ, size = wt.DWORD(), wt.DWORD(1024)
        buf = ctypes.create_string_buffer(1024)
        rc = rapi.CeRegQueryValueEx(h, name, None, ctypes.byref(typ), buf, ctypes.byref(size))
        if rc != 0:
            print("%s\\%s : %s not set (error %d)" % (root, key, name, rc))
        elif typ.value == REG_DWORD:
            print("%s\\%s : %s = dword:%d" % (root, key, name, ctypes.c_uint32.from_buffer(buf).value))
        else:
            print("%s\\%s : %s = %r (type %d)" % (root, key, name, buf.raw[:size.value].decode("utf-16-le").rstrip("\0"), typ.value))
    finally:
        rapi.CeRegCloseKey(h)


def reg_setdword(root, key, name, value):
    h, disp = wt.HKEY(), wt.DWORD()
    rc = rapi.CeRegCreateKeyEx(_hkey(root), key, 0, None, 0, 0, None, ctypes.byref(h), ctypes.byref(disp))
    if rc != 0:
        sys.exit("create key failed (error %d)" % rc)
    try:
        v = ctypes.c_uint32(int(value))
        rc = rapi.CeRegSetValueEx(h, name, 0, REG_DWORD, ctypes.byref(v), 4)
        if rc != 0:
            sys.exit("set value failed (error %d)" % rc)
    finally:
        rapi.CeRegCloseKey(h)
    reg_get(root, key, name)


def mkdir(path):
    if not rapi.CeCreateDirectory(path, None) and rapi.CeGetLastError() != 183:  # 183 = already exists
        fail("mkdir " + path)
    print("directory " + path)


def run(exe, args=""):
    pi = PROCESS_INFORMATION()
    if not rapi.CeCreateProcess(exe, args or None, None, None, False, 0, None, None, None, ctypes.byref(pi)):
        fail("start " + exe)
    rapi.CeCloseHandle(pi.hProcess)
    rapi.CeCloseHandle(pi.hThread)
    print("started %s (pid %d)" % (exe, pi.pid))


def main(argv):
    if len(argv) < 2 or argv[1] not in ("info", "ls", "push", "pull", "mkdir", "run", "regget", "regdword"):
        sys.exit(__doc__)
    if rapi.CeRapiInit() != 0:
        sys.exit("RAPI not connected - is the phone connected in Windows Mobile Device Center?")
    try:
        cmd, args = argv[1], argv[2:]
        if cmd == "info":
            info()
        elif cmd == "ls":
            ls(args[0] if args else "\\")
        elif cmd == "push":
            push(args[0], args[1])
        elif cmd == "pull":
            pull(args[0], args[1])
        elif cmd == "regget":
            reg_get(args[0], args[1], args[2])
        elif cmd == "regdword":
            reg_setdword(args[0], args[1], args[2], args[3])
        elif cmd == "mkdir":
            mkdir(args[0])
        elif cmd == "run":
            run(args[0], " ".join(args[1:]))
    finally:
        rapi.CeRapiUninit()


if __name__ == "__main__":
    main(sys.argv)
