# The on-device compiler: PocketGCC-WMAI

WMAI's coding tools build native ARM apps on the phone with **PocketGCC-WMAI**,
WMAI's own build of [PocketGCC](https://sourceforge.net/projects/pocketgcc/)
1.50 — GCC 3.2.2 and binutils 2.13.2.1 ported to run on Windows CE (2003).

It ships ready to use in the release kit (`pgcc` folder: PocketGCC's headers,
libraries and samples, with the rebuilt tools in `pgcc\wmai`). **The original
PocketGCC tools do not work with WMAI** — they cannot report errors to a
calling program (below) — so don't replace the kit's compiler with the
SourceForge package.

## Why the tools are rebuilt

PocketGCC prints its errors to the console. On Windows Mobile that output is
lost when another program starts the compiler:

* Windows Mobile 5 has no console by default
  (`HKLM\Drivers\Console\OutputTo` = -1). PocketConsole can show one, but only
  as a window on screen.
* Windows CE redirects standard I/O per process (`SetStdioPathW`), and a child
  process does **not** inherit its parent's redirection (verified on the
  device). So WMAI cannot capture the compiler's messages from outside.
* With nowhere to write, `cc1plus` also exits with code 1 even after a
  successful compile, so the exit code is useless too.

The fix is to let each tool redirect itself. `wce-stdio.c` adds two options
to `cc1plus`, `cpp0`, `as`, `ld`, `windres` and `ar`:

```
--stdout=FILE   --stderr=FILE
```

They are removed from `argv` and applied with `SetStdioPathW` at the very start
of `main()`. WMAI's `build` tool passes them, so it gets real messages
(`main.cpp:27: parse error before ';' token`) and correct exit codes. The
paths in these options must not contain spaces.

Also learned on the device: **never pass `-quiet`** to this `cc1plus` — it
then exits 1 without output.

## Files

| File | Purpose |
|---|---|
| `wce-stdio.c` | The redirection code (goes into `libiberty`, which every tool links) |
| `apply_wce_stdio.py` | Applies the change to PocketGCC's source tree: copies `wce-stdio.c`, inserts the call at the top of each tool's `main()`, adds it to `makefile.native` |
| `xp-agent.bat` | Optional job runner for building inside a Windows XP virtual machine (below) |

## Rebuilding the tools

The release kit already contains the rebuilt tools. To build them yourself:

1. Get the source. Easiest: `pocketgcc-wmai-src-<version>.zip` from WMAI's
   [releases](https://github.com/techartdev/WMAI/releases) — PocketGCC's source tree with this patch
   already applied; unpack it and use its `pgcc-src` folder (skip step 2).
   Or start from the upstream package,
   `pocketgcc-3.2.2-binutils-2.13.2.1-20031121-src` on
   [SourceForge](https://sourceforge.net/projects/pocketgcc/files/).
2. Upstream source only — apply the change:
   ```
   python compiler\apply_wce_stdio.py path\to\pgcc-src
   ```
3. Build on **Windows XP** (a virtual machine is fine). The package brings its
   own x86 cross toolchain in `pgcc-src\bin-native` — a 2003 Cygwin build that
   crashes on modern Windows (`STATUS_ACCESS_VIOLATION` at startup), but works
   on XP. In XP, from `pgcc-src`:
   ```
   nmake /f makefile.native gcc binutils
   ```
   The source package includes prebuilt object files, so only the changed
   files are recompiled; it takes seconds.
4. The results are `gcc\gcc\cc1plus.n.exe`, `gcc\gcc\cpp0.n.exe`,
   `binutils\gas\as.n.exe`, `binutils\ld\ld.n.exe`,
   `binutils\binutils\windres.n.exe` and `binutils\binutils\ar.n.exe`.
   Copy them to a folder and pass it to `tools\make_release.py --tools`, or
   put them on the phone as `\Storage Card\pgcc\wmai\<name>.exe`.

### Building in a VirtualBox XP machine from the host

`xp-agent.bat` lets a script on the host drive the build without remote
desktop or passwords:

1. Share a host folder with the VM, e.g. (transient, gone at VM shutdown):
   `VBoxManage sharedfolder add "WindowsXP" --name wmai --hostpath D:\wmai-vm --transient`
2. Copy `xp-agent.bat` and the patched `pgcc-src` into that folder and start
   `\\vboxsvr\wmai\xp-agent.bat` in XP (Start > Run). Close its window to stop.
3. Drop a batch file at `jobs\job.bat` (write it under another name and rename
   it, so the agent never sees a half-written file). The agent runs it and
   writes its output to `jobs\job.log`. For example: copy `pgcc-src` to
   `C:\pgcc-src`, run nmake there, copy the six `*.n.exe` back to the share.
