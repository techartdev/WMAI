# WMAI — an AI agent on a 2007 Windows Mobile phone

WMAI runs a modern AI agent on a **Windows Mobile 5 Pocket PC** (ARM, 200 MHz,
44 MB RAM, .NET Compact Framework 3.5). The phone talks to the model API
**directly**, with TLS 1.2 implemented inside the app, and the agent uses the
phone itself through 26 tools: files, registry, processes, contacts, SMS,
calls, the web, the screen (vision) — and an **on-device C++ compiler**, so you
can ask it to write, build, debug and run native apps on the phone.

```
You: Make me a notes app: a text area, the left soft key saves to
     \My Documents\notes.txt, the menu has Load and Clear.
AI:  [tool] project_new(project=notes, title=Notes) - ok
     [tool] project_edit(project=notes, path=main.cpp, ...) - ok
     [tool] build(project=notes) - error        <- reads GCC's errors,
     [tool] project_edit(...) - ok                 fixes them,
     [tool] build(project=notes) - ok              builds again
     [tool] run_program(path=\Storage Card\Projects\notes\notes.exe) - ok
```

Everything above runs on the phone: the model call, the agent loop, the file
edits and GCC 3.2.2.

## What it can do

| Area | Tools |
|---|---|
| Device | `device_info` (battery, memory, storage, signal, unread SMS), `registry_read` |
| Files | `fs_list`, `fs_read`, `fs_write`, `fs_delete`, `fs_move`, `fs_copy`, `fs_mkdir` |
| Programs | `process_list`, `run_program`, `kill_process` |
| Phone | `contacts_search`, `sms_list`, `sms_send`, `phone_call` |
| Web | `web_search` (DuckDuckGo), `web_fetch` (http/https pages as text, links) |
| Vision | `screenshot` (the model sees the screen), `view_image` (PNG/JPEG/GIF/BMP) |
| Coding | `project_new`, `project_list`, `project_read`, `project_write`, `project_edit`, `build` |

Anything that sends, dials, deletes, overwrites, launches or kills asks you on
the phone first (Yes/No). The coding tools ask once per project and session.

## How it works

```
 Windows Mobile 5 phone                                    Internet
┌──────────────────────────────────────────┐
│ WMAI.exe (C#, .NET CF 3.5)               │
│  chat UI ── agent loop (Agent.cs) ───────┼── TLS 1.2 ──▶ OpenAI-compatible API
│              │  streaming, tool calls,   │  (Bouncy      (DeepSeek by default,
│              │  images, reasoning        │   Castle)      any vision model)
│              ▼                           │
│  26 tools: P/Invoke into coredll, sms,   │
│  pimstore/cemapi, GDI; web; compiler     │
│              │                           │
│              ▼                           │
│  PocketGCC 3.2.2 (cc1plus, as, ld, ...)  │
│  rebuilt with --stdout/--stderr          │
└──────────────────────────────────────────┘
```

* **TLS 1.2 on Windows Mobile 5.** WM5's own TLS stops at 1.0 and today's APIs
  need TLS 1.2 with ECDHE, so WMAI ships Bouncy Castle C# 1.8.10 compiled for
  the Compact Framework, validates certificates against Mozilla's root store
  and keeps one connection alive (first handshake ~5 s, later requests ~0.3 s).
* **Vision.** With a vision model (default: `deepseek-flash`), `screenshot`
  captures the screen to PNG and attaches it to the conversation.
* **On-device compiler.** PocketGCC (GCC 3.2.2/binutils 2.13 for ARM WinCE,
  2003) runs on the phone. Windows CE cannot redirect a child process's output,
  so its error messages were invisible; WMAI ships the tools **rebuilt from
  source** with `--stdout=FILE` / `--stderr=FILE` options (see
  [compiler/](compiler/README.md)), which gives the agent real compiler errors
  to fix.
* **No Visual Studio needed to build.** The app is compiled with the desktop C#
  compiler against .NET 2.0 and retargeted to NETCF 3.5 by rewriting its
  assembly references; `tools/cfcheck.ps1` checks every API it uses against the
  phone's real framework.

## Try it

You need:

* A **Windows Mobile 5 or 6 Pocket PC** with an ARM CPU (developed on WM5;
  WM6 untested), an SD card and **.NET Compact Framework 3.5** installed
  (Microsoft's "NET Compact Framework 3.5 Redistributable").
* Internet on the phone: Wi-Fi, GPRS/3G, Bluetooth PAN, or USB pass-through via
  ActiveSync / Windows Mobile Device Center.
* An API key for an OpenAI-compatible chat completions endpoint — DeepSeek by
  default (`deepseek-flash` supports images), or OpenRouter, etc.
* A correct date on the phone (certificate checks fail otherwise).

Then:

1. Download `WMAI-<version>-sdcard.zip` from the
   [releases](../../releases) and copy the contents of its `Storage Card`
   folder to the root of the phone's SD card (you get `WMAI\` and `pgcc\`).
2. In `WMAI\`, copy `WMAI.config.example` to `WMAI.config` and put your API key
   in it.
3. Start `WMAI\WMAI.exe` from File Explorer. Type, press Enter or **Send**.

Details, including other storage card names and a shortcut, are in
[docs/INSTALL.txt](docs/INSTALL.txt).

## Repository layout

| Path | Contents |
|---|---|
| `app/` | The phone app: `WMAI.cs` (UI), `Agent.cs` (agent loop), `Https.cs` (TLS + HTTP), `Tools.cs`, `CodeTools.cs`, `Web.cs`, `ImageTools.cs`, `Json.cs`; project templates; build scripts |
| `relay/` | Optional PC relay (Python, stdlib only): plain-HTTP chat for USB development, over-the-air app updates. `tools.py` holds the system prompt and all tool definitions, exported to `WMAI.agent.json` for the phone |
| `compiler/` | The PocketGCC `--stdout/--stderr` patch and how to rebuild the compiler (needs Windows XP) |
| `vendor/` | Bouncy Castle: download + NETCF patch + build script |
| `tools/` | PC tooling: `wm.py` (files/registry/processes on the phone over RAPI), `cfcheck.ps1`, `make_release.py`, root bundle builder, tests |
| `docs/` | Install guide and development notes |

## Building from source

On Windows 10/11 with .NET Framework 3.5 (for the 2.0 reference assemblies),
PowerShell 7 (`pwsh`) and Python 3:

```
app\build.cmd                    builds app\WMAI.exe (fetches + builds Bouncy Castle first)
tools\rootindex\build.cmd        builds app\WMAI.roots.idx from Mozilla's CA bundle
python tools\make_release.py     builds the SD-card kit in release\
```

`make_release.py` also needs PocketGCC 1.50 and its source package from
[SourceForge](https://sourceforge.net/projects/pocketgcc/), and the rebuilt
compiler tools (see [compiler/README.md](compiler/README.md)).

With the phone connected through Windows Mobile Device Center,
`python tools\wm.py push|pull|ls|run ...` deploys and inspects without
touching the phone, and `python tools\pull_cfref.py` copies the phone's
framework assemblies so `cfcheck` can verify each build.
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) has the full engineering notes:
the relay protocol, every NETCF pitfall found, TLS and vision details, and how
the compiler was rebuilt.

## Limitations

* The API key is stored in plain text in `WMAI.config` on the SD card.
* The first message of a session waits 5–8 s for the TLS handshake; every
  `.cpp` file takes ~45 s to compile (mostly `windows.h`).
* Images work in direct mode only (not through the relay).
* Free RAM is tight (~10 MB with the crypto library loaded).

## License

WMAI is licensed under the [GNU General Public License v3.0](LICENSE).
Third-party components and their licenses are listed in
[THIRD_PARTY.md](THIRD_PARTY.md).
