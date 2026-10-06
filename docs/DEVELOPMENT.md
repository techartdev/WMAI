# WMAI development notes

Engineering notes from building WMAI, roughly in the order things were done.
They record what was verified on the device and every pitfall found, so they
don't have to be rediscovered. For using WMAI, see the [README](../README.md).

## Development setup

- **Device:** a Windows Mobile 5 Pocket PC Phone Edition (OS 5.1.525, CE 5
  based), ARM926T (OMAP730) at 200 MHz, 44 MB RAM, .NET Compact Framework 3.5,
  storage card at `\Storage Card`. No Wi-Fi; GPRS, Bluetooth, USB.
- **PC:** Windows 11 with .NET Framework 2.0/3.5/4, PowerShell 7 and Python 3.
  No Visual Studio and no Windows CE SDK are needed to build.
- **USB link:** the phone enumerates as RNDIS (`USB\VID_045E&PID_0301`). Without
  Windows Mobile Device Center (WMDC), forcing the "Remote NDIS Compatible
  Device" driver gives an IP link: phone `169.254.2.1`, PC `169.254.2.2`
  (DHCP from the phone; Windows treats it as a Public network, so allow
  TCP 8080 from `169.254.2.1` in the firewall for the relay). The phone's
  Connection Manager then shows the link as disconnected, so Pocket IE and
  HttpWebRequest try GPRS - raw `TcpClient` sockets work regardless, and are
  what WMAI uses.
- **With WMDC** on Windows 11, the adapter becomes "Microsoft Windows Mobile
  Remote Adapter" (same IPs), the phone gets internet through USB
  pass-through, and RAPI works from the PC: `python tools/wm.py
  info|ls|push|pull|mkdir|run|regget|regdword` (64-bit
  `C:\Windows\System32\rapi.dll` via ctypes). `push` fails while the target
  exe is running (file locked) - use the app's Menu > Update app then.
  RAPI may not write protected registry keys (error 5); run
  `tools/devtools/RegDword.exe` on the device instead.

## The optional PC relay

```
python relay\relay.py
```

Binds `169.254.2.2:8080` (`WMAI_BIND`/`WMAI_PORT` in `relay\.env`, key in
`DEEPSEEK_API_KEY`; see `relay\.env.example`). It serves a plain HTML chat for
Pocket IE, the phone app's relay mode (used when there is no `WMAI.config`) and
`/api/app` for Menu > Update app. It was the first architecture: before WMAI
could do TLS 1.2 itself, all model traffic went through it.

## Relay HTTP API (plain text, UTF-8, HTTP/1.0, one request per connection)

| Request | Response |
|---|---|
| `POST /api/send` body = message | `OK` or `BUSY` |
| `GET /api/reply?from=N` | line 1 status, rest = reply display text from offset N |
| `GET /api/tool` | waiting tool call (see below), empty body if none |
| `POST /api/tool?id=ID` body = result text | `OK` |
| `GET /api/history` | whole conversation, `You: ...` / `AI: ...` blocks separated by blank lines |
| `GET /api/new` | `OK`, clears the conversation (ignored while busy) |
| `GET /api/app` | the current `app\WMAI.exe` bytes (used by "Update app") |
| `GET /` | server-rendered HTML chat for Pocket IE (no tools) |

Status line values: `B` busy (model streaming), `T` busy and a tool call is
waiting for the phone, `D` done, `E <message>` error.

**Offsets are UTF-16 code units** (matches .NET `string.Length`), so the client
can keep `got += text.Length`.

The display text contains the model's text plus lines like
`[tool] fs_list(path=\Storage Card) - ok`, so the chat log shows tool activity
without any extra client work.

### Tool call format (`GET /api/tool`)

```
<call id>
<tool name>
key=value
key=value
```
Values are escaped: `\\` = backslash, `\n` = newline, `\r` = CR. Unescape in one
left-to-right pass. Non-string JSON values arrive as JSON text (`true`, `42`).
Example on the wire: `path=\\Storage Card` means the path `\Storage Card`.

The relay waits up to 180 s per tool (`TOOL_TIMEOUT`) and runs up to 12 model
calls per user message (`MAX_STEPS`). Result text conventions: start with
`error:` for failures or `denied` if the user declined; anything else counts
as ok. Keep results compact (they go back into the model context).

Verified with a fake model: send -> `T` -> tool fetched -> result posted -> `D`,
history renders the tool line, `chat.json` gets `user, assistant(tool_calls),
tool, assistant`.

## Phone app: tool loop and tools (v0.3)

`Converse()` in `WMAI.cs` now handles the full loop: append new text, and if the
status is `T`, `GET /api/tool`, parse the block (`ParseTool`), execute it once
per id, `POST /api/tool?id=<id>` with the result, and keep polling; `B` polls,
`D`/`E` finish. Parsing/unescaping matches the relay's format (`\\`, `\n`,
`\r`, one left-to-right pass).

`Tools.cs` implements the tools named in `relay/tools.py`. `Execute(name, args)`
dispatches on the tool name; anything that mutates, launches, dials or sends
asks the user with a Yes/No `MessageBox` (marshalled to the UI thread through
`ApproveHandler`). Implemented:

- `device_info` (OS/memory/storage/battery via coredll; signal/operator/unread
  SMS read from the `System\State` registry keys, best effort)
- `fs_list`, `fs_read` (256 KB read cap, binary -> hex dump), `fs_write`,
  `fs_delete`, `fs_move`, `fs_copy`, `fs_mkdir`
- `registry_read` (`Microsoft.Win32.Registry`)
- `process_list` (toolhelp), `run_program`, `kill_process`
- `contacts_search` (POOM flat helpers in `pimstore.dll`), `sms_list` and
  `sms_send` (CEMAPI flat helpers in `cemapi.dll`), `phone_call`
  (PhoneMakeCall, coredll then phone.dll)

### contacts_search / sms_list (v0.4)

Both talk to the device DLLs the same way Microsoft's own managed wrapper does:
calling the flat C helper exports (`pimstore.dll` and `cemapi.dll`) by ordinal
with raw `IntPtr`s. No managed assembly has to be deployed, and the CEDB buffer
parsing is avoided entirely.

- `contacts_search`: `IPOutlookApp_Create` (#78) -> `IPOutlookApp_Logon` (#85) ->
  `IPOutlookApp_GetDefaultFolder` (#81, 10 = contacts) -> `IFolder_get_Items`
  (#60) -> `IPOutlookItemCollection_get_Count`/`_Item` (#70/#66) ->
  `IItem_get_StringPropertyFromPropId` (#39) for the PIM property IDs
  (`PIM_PROP_TAG(CEVT_LPWSTR, id) = id << 16 | 0x1F`). Strings come back in the
  `cePropVal` buffer, which is freed with `IPOutlookApp_HeapFree` (#83).
- `sms_list` / `sms_send`: `MAPIInitialize` -> `CEMAPILogonEx` (#20) ->
  `IMAPISession_get_MsgStoresTable` (#23) -> `IMAPITable_get_NextEntryID` (#27)
  -> `IMAPISession_OpenMsgStore` (#24); the store whose
  `PR_DISPLAY_NAME`-style name is `"SMS"` is the SMS account. Then
  `IMsgStore_get_Folder` (#29, Inbox = 2164654338) ->
  `IMAPIContainer_get_ContentsTable` (#43) -> `IMAPITable_get_NextEntryID` ->
  `IMAPISession_OpenEntry` (#25) and `IMAPIProp_get_StringProperty` (#38) /
  `IMAPIProp_get_DateProperty` (#40) / `IMessage_get_Body` (#34). Sending uses
  `SendSMSMessage` (#48), which is what the managed wrapper calls (it loads
  `sms.dll` itself and returns `0x80004002`-style errors).

Ordinals were taken from `pimstore.lib` / `cemapi.lib` in the WM5 SDK and
cross-checked by decompiling `Microsoft.WindowsMobile.PocketOutlook.dll`
(`ilspycmd`) - that is also where the property tags, folder constants and the
`GetStringProperty` free rule come from. The SDK (downloaded from the Internet
Archive, unpacked locally; see "Windows Mobile 5.0 SDK" below) is the
source of truth for these helpers.

The WM5 managed `Microsoft.WindowsMobile.PocketOutlook` exposes `Contact`
reading but only `SmsAccount.Drafts`/`Send` - no SMS message collection (that
arrived in the WM6 SDK), which is why SMS goes through CEMAPI directly.

Review of v0.4.0 (mechanical, against the SDK's `Microsoft.WindowsMobile.PocketOutlook.dll`):
all 24 `pimstore.dll`/`cemapi.dll` imports exist in Microsoft's assembly with the
same DLL, ordinal and full parameter signature (the folder parameter is the
`FolderType` enum, underlying `Int32`); `#38` is declared there as both
`DoUnmanagedDestructor` and `DoReleaseCOMPtr`, so releasing MAPI objects with it
matches Microsoft; the five SMS folder constants appear in Microsoft's DLL; new
framework APIs (`DateTime` ctor/`MinValue`/`!=`, `IntPtr !=`,
`Marshal.PtrToStringUni`) all exist in NETCF 3.5.

**Device-verified 2026-10-06 (v0.4.0):** `contacts_search` and `sms_list` work on
the phone. `sms_send` is still untested (no SIM in the phone at the time). Failures return an `error:` result (the
calls sit in separate methods, per the NETCF rules below), and each result is
kept small because it goes back into the model context.

`device_info`, `sms_send` and `phone_call` use P/Invoke that has **not been
run on the device yet** - verify on the phone. A wrong struct or DLL name shows
up as an `error:` result (the calls sit in separate methods so a bad P/Invoke is
catchable, per the NETCF rules below), not a crash, but a bad native struct
still could. Keep the tool result compact; it goes back into context.

Bump `Version` in `WMAI.cs`, run `app\build.cmd`, restart the relay, then on the
phone use Menu > Update app (no SD card copy needed any more).

File members the exe references are checked after a build with the PowerShell
snippet in "Building the app" below; keep only APIs that exist in NETCF 3.5.

## Windows Mobile 5.0 SDK (reference for the native helpers)

The WM5 Pocket PC SDK was downloaded from the Internet Archive
(`archive.org/download/windows-mobile-5.0-pocket-pc-sdk_202305`) and unpacked
without installing: `7z x WM5SDK.msi` extracts the payload CAB directly, then
Python's `msilib` reads the MSI `File`/`Component`/`Directory` tables to restore
the real file names (the CAB entries are mangled). It provided `windbase.h`, `pimstore.h`, `sms.h`, `cemapi.h`, `mapidefs.h`,
`cemapi.lib`/`pimstore.lib` and the managed `Microsoft.WindowsMobile.*.dll`.

Confirmed from the official `sms.h`: `SMS_ADDRESS` is just
`{ SMS_ADDRESS_TYPE smsatAddressType; TCHAR ptsAddress[256]; }` (no
`cbSize`/`dwParams`/`dwIndex`) and `SMS_MESSAGE_ID` is a `DWORD`. The old
`sms_send` struct was indeed wrong; v0.4 sidesteps it by using CEMAPI's
`SendSMSMessage`.

To re-derive an ordinal or property tag in future, read the `.lib` export list
(`7z x cemapi.lib -o...` and open `1.txt`) or decompile the managed wrapper with
`ilspycmd -o out Microsoft.WindowsMobile.PocketOutlook.dll`.

## Building the app (no Visual Studio)

`app\build.cmd` compiles with the desktop C# compiler
(`C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe`, C# 5 syntax) against
**desktop .NET 2.0** reference assemblies, then `retarget.ps1` (needs `pwsh`)
patches the exe's AssemblyRef rows to NETCF 3.5: version `3.5.0.0`, public key
token `969db8053d3322ac`. Without that patch the phone says
"System.Windows.Forms 2.0.0.0 was not found".

To compile more files, add them to the csc line in `build.cmd`. If you
reference other on-device assemblies, add their name and real device identity
(version, token) to `retarget.ps1`, which currently gives every listed name the
NETCF identity.

Constraints that come from this approach:

- Only use APIs that exist in **both** desktop .NET 2.0 and NETCF 3.5. Anything
  desktop-only compiles fine and then throws `MissingMethodException` on the
  phone when the calling method is JIT-compiled. Keep risky code in separate
  methods so failures are catchable at the call site.
- C# 2.0 style is safest: anonymous delegates are fine (already used); avoid
  LINQ, extension methods, `Func`/`Action` (not in mscorlib 2.0), and generic
  BCL helpers such as `Array.IndexOf<T>`.
- **Confirmed missing on the device:** `Int32.TryParse` (use `int.Parse` in a
  try/catch). Device-verified working: `Dictionary<,>`, `Microsoft.Win32.Registry`,
  `Directory`/`File`/`FileInfo`, `Array.Sort`, ToolHelp from `toolhelp.dll`.
- UI updates only on the UI thread: `Control.Invoke(new EventHandler(...))`
  with shared fields under a lock (see `Post`/`Flush`).
- Commands go in the `MainMenu` soft keys, not Buttons.
- After building, list every framework member the exe references and check
  each exists in NETCF 3.5:

```powershell
$pe = [Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead('app\WMAI.exe'))
$md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
foreach ($h in $md.MemberReferences) { $m = $md.GetMemberReference($h)
  if ($m.Parent.Kind -eq 'TypeReference') { $t = $md.GetTypeReference($m.Parent)
    "{0}.{1}::{2}" -f $md.GetString($t.Namespace), $md.GetString($t.Name), $md.GetString($m.Name) } } | Sort -Unique
$pe.Dispose()
```

Confirmed working on the device so far: WinForms TextBox/MainMenu/Timer/Font,
`TcpClient` + `IPEndPoint`, threads, `Control.Invoke`, file I/O,
`Process.Start`, P/Invoke into `coredll.dll` (SIP functions), self-update.

## API compatibility checker (run on every build)

`tools/cfref/` holds the phone's real NETCF 3.5 assemblies (pulled from
`\Storage Card\Windows\GAC_*_v3_5_0_0_cneutral_1.dll`). `pwsh tools/cfcheck.ps1
<assemblies>` lists every framework type/member an assembly uses that the
device lacks, with full signatures (overloads included). `app/build.cmd` runs
it and fails on any finding. Attribute-only references are ignored (attributes
are only loaded via reflection). It found: `Process.Start(string)` (absent in
NETCF; `run_program` could never JIT - fixed in v0.4.1), plus the two Bouncy
Castle issues below. Trust it over the hand-written lists further down.

## Standalone direction: in-app TLS 1.2 (Bouncy Castle)

Goal: the phone holds key/provider/history and calls the API itself (over
WMDC pass-through, GPRS or Bluetooth PAN). WM5's own TLS stops at TLS 1.0 and
both api.deepseek.com and openrouter.ai require ECDHE (plain RSA key exchange
rejected - verified with `openssl s_client`), so the app brings its own TLS.

- `vendor/bc-csharp-release-1.8.10` (official GitHub tag zip), built with
  `-define:NETCF_2_0` by `vendor/build-bc.cmd` (out\ref = compile-against
  copy, out\ = retargeted copy to deploy; compiling against the retargeted dll
  duplicates mscorlib refs). 2.4 MB.
- WMAI patches (marked `WMAI patch`): `security/SecureRandom.cs` (lock instead
  of `Interlocked.Increment(ref long)`, keeping the CryptoAPI-seeded branch, not
  the weak NETCF_1_0 one), `crypto/tls/AbstractTlsContext.cs` (same),
  `crypto/generators/OpenBsdBCrypt.cs` (3-arg ArgumentException).
- No X25519 in 1.8.10's TLS; P-256 is used.
- **Measured on the device** (a TLS speed test, via WMDC pass-through, no cert
  validation): process start + BC load 3.7 s, SecureRandom 0.8 s, DNS 0.9 s,
  first handshake 4.3 s (JIT), later handshakes 1.3-1.5 s, HTTP round trip
  ~0.3 s; cipher 0xC02F; api.deepseek.com answered 401 (no key) as expected.
- Deploy/run/log loop without touching the phone: `tools/wm.py push/run/pull`.

### Step 1 done: `app/Https.cs` (verified HTTPS client)

- `TrustStore` loads `WMAI.roots` (PEM next to the exe): Amazon Root CA 1/3,
  GTS Root R1/R4, ISRG Root X1. Amazon CA 1 and GTS R4 were downloaded from
  amazontrust.com / pki.goog and their keys matched the copies the live
  servers send. DeepSeek chains to Amazon Root CA 1 (RSA), OpenRouter to GTS
  Root R4 (ECDSA P-384). Validity checks use max(UTC now, 2026-10-01) in case
  the phone clock resets again.
- `ChainValidator`: SAN hostname (single-label wildcards), validity dates,
  signature chain until a cert is issued by a bundled root (cross-signed root
  copies sent by servers are fine), CA basic constraints. SNI always uses the
  real host.
- `HttpsConnection`: keep-alive HTTP/1.1, Content-Length / chunked / until-close
  bodies, one retry on a stale reused connection, `ServerDate` from the Date
  header, ECDHE-only cipher suites, one shared SecureRandom per process.
- `tools/nettest` (build.cmd makes out\pc and out\phone): 6 cases incl. two
  negative ones. **All pass on the PC and on the phone.** Phone timings: load
  roots 1.6 s; DeepSeek first verified handshake 5.2 s; keep-alive request
  0.34 s; OpenRouter (ECDSA chain) handshake 6.3 s; body throughput ~48k
  chars/s. Phone clock matched the server Date (0 s skew).
- Ideas if the first-message cost matters: TLS session resumption, caching
  the validated leaf for reconnects.

### Step 2 done: agent loop on the phone (app v0.5.0, "direct mode")

- `relay/tools.py` now holds the system prompt + MAX_STEPS + tool schemas;
  `python relay/tools.py app/WMAI.agent.json` exports them (run by
  `app/build.cmd`), so relay and phone share one definition.
- `app/Json.cs` (parser/writer, culture-free numbers), `app/Agent.cs`
  (`AgentConfig` from `WMAI.config`; streamed chat completions with tool calls;
  tools run locally via `Tools.Execute`; history in `WMAI.chat.json`, capped at
  60 messages; same display/transcript format and unanswered-tool-call cleanup
  as the relay).
- `WMAI.cs`: if `WMAI.config` has a `key`, the app runs the agent itself
  ("direct: <model>" in the first line); otherwise relay mode as before.
  Streaming text reaches the UI at most every 300 ms.
- Phone files in `\Storage Card\WMAI`: WMAI.EXE, BouncyCastle.Crypto.dll
  (`vendor/out/cf`), WMAI.roots, WMAI.agent.json, WMAI.config
  (`host`, `path`, `model`, `key`; plain text on the SD card - a known trade-off). `vendor/build-bc.cmd` builds BC once into `vendor/out/{ref,cf}`.
- Tests: `tools/agenttest` - AgentTest.exe (PC, real DeepSeek, 4 checks incl.
  tool round trip and history) and DeviceAgentTest.exe (phone, no UI). **On the
  phone:** first turn 8.7 s (5.3 s handshake), a `device_info` tool turn 3.1 s
  on the open connection; free RAM dropped from ~22 MB to ~10.5 MB with BC
  loaded - memory is the tight resource now.

## On-device native compiler: PocketGCC (works, with readable errors)

- PocketGCC 1.50 (GCC 3.2.2 + binutils 2.13, 2003) runs on the phone. CABs in
  `pocketgcc-1.50-20031108-arm/`; `tools/cecab.py` shows what a CE CAB installs.
  Compiler files live in `\Storage Card\pgcc` (the release kit's `pgcc` folder, built from the CAB by `tools/make_release.py`;
  the CAB wants `\pgcc` in RAM, which has only ~2.7 MB free). PocketConsole and
  CMD were installed with wceload but are not needed by the build.
- Pipeline (see `tools/pgcc_build.py`): cc1plus (.cpp -> .s), as, cpp0 + windres
  (.rc), ld with `-l cpplib -l corelibc -l coredll -l aygshell -l runtime -l
  portlib`. The sample `samp\menu` GUI app builds in ~62 s of process time
  (cc1plus 47 s) and runs.
- **Never pass `-quiet` to this cc1plus** (it exits 1 with no output).
- Windows CE does not let a parent redirect a child's stdio (SetStdioPathW is
  per-process, not inherited), and the 2003 tools print errors to the console.
  Fix: the tools were **rebuilt from source** with `--stdout=FILE` and
  `--stderr=FILE` options (`compiler/wce-stdio.c`, applied to each
  tool's main() by `compiler/apply_wce_stdio.py`; originals untouched in
  `pocketgcc-3.2.2-binutils-2.13.2.1-20031121-src`). Rebuild steps: `compiler/README.md`. On the phone they are in `\Storage Card\pgcc\wmai`; `pgcc_build.py` uses
  them by default (`--orig` = old ones). With them, exit codes are right and
  error text lands in the file, e.g.
  `/pgcc/bad.cpp:2: 'undefined_thing' undeclared`.
- Rebuilding the tools: the 2003 Cygwin cross toolchain in `pgcc-src/bin-native`
  crashes on Windows 11, so builds run in a **Windows XP VirtualBox VM**. Bridge: transient shared folder `wmai` -> a host folder (see `compiler/README.md`)
  (`VBoxManage sharedfolder add ... --transient`, gone after VM shutdown);
  start `\\vboxsvr\wmai\agent.bat` in XP, which runs
  `vm\jobs\job.bat` when it appears (write it as job.tmp, then rename) and
  writes `job.log`. Source copy in the VM: `C:\pgcc-src`; `nmake /f
  makefile.native gcc binutils` takes seconds because prebuilt objects ship
  with the source. (`job.done` exit code is always empty: batch expands
  %errorlevel% too early there - jobs echo their own exit codes.)
- `HKLM\Drivers\Console\OutputTo` is back at the default -1 (console off). With
  0 PocketConsole/CMD work but Messaging (tmail.exe) crashed once at boot.
  RAPI may not write protected keys (error 5): use
  `tools/devtools/RegDword.exe` on the device.
- Other helpers: `tools/devtools/Run.exe` (runs a program on the phone, waits,
  logs the exit code), `tools/pe/*.py` (PE header and import inspection).

## Coding harness on the phone (app v0.6.0)

- `app/CodeTools.cs`: tools `project_new`, `project_list`, `project_read`
  (line numbers), `project_write`, `project_edit` (exact single-match replace),
  `build`. Projects in `\Storage Card\Projects\<name>`; build output in
  `<project>\build`, exe at `<project>\<project>.exe`. Incremental: a .cpp is
  recompiled when it or any project .h is newer than its .o. Compiler messages
  go through `\Temp\wmai_<tool>.err.txt` (no spaces inside `--stderr=`).
- First change to a project per WMAI session asks the user once; launching the
  result uses run_program (asks).
- `project_new` copies `app/templates/app` (deployed to
  `\Storage Card\WMAI\templates\app`): main.cpp + main.rc (SHMENUBAR soft keys)
  + resource.h; verified to compile (~45 s) and run. `newres.h` was added to
  `\Storage Card\pgcc\include`.
- The compiler manual is `BUILD_GUIDE` in `relay/tools.py` (the `build` tool's
  description). MAX_STEPS raised to 30 for write/build/fix loops.
- `tools/agenttest/CodeTest.cs` (on the phone, auto-approving): new -> build ->
  break -> error with file:line -> fix -> rebuild -> relink-only. All 9 checks
  pass: first build 57 s, incremental rebuild 49 s, relink 3 s.
- Not yet verified: compiling while WMAI (with Bouncy Castle loaded, ~10 MB
  free RAM) is running - the first real agent session will show it. `build`
  reports "no messages - possibly out of memory" if cc1plus dies silently.

## Vision and web (app v0.7.1)

- Model: `deepseek-flash` (DeepSeek-V4.1-Flash, accepts images; `/models` on
  a DeepSeek key lists it with input_modalities text+image). It is a thinking
  model: `reasoning_content` must be sent back with tool calls within a turn
  (HTTP 400 otherwise). Agent.cs and relay.py keep it during the turn and strip
  it afterwards. Images go as an OpenAI-style user message
  (`image_url` data URL) after the tool results - an image inside a tool
  message is rejected. Image messages are removed at the end of the turn.
  `tools/vision_probe.py` reproduces these checks.
- `app/ImageTools.cs`: `screenshot` (GDI BitBlt into a 24-bit DIB, PNG via
  Bouncy Castle zlib, saved to `\My Documents\WMAI`, WMAI hides itself during
  the capture) and `view_image` (png/jpg/gif sent as-is, bmp converted).
  On the phone: 240x320 capture + PNG in ~2.4 s, 87 KB.
- `app/Web.cs`: `web_fetch` (http/https, redirects, gzip, charset, HTML to
  text, paging, numbered links) and `web_search` (DuckDuckGo lite; the html
  endpoint returns a bot check). Roots: `WMAI.roots.idx` = Mozilla bundle from
  curl.se (121 roots, 2026-09-25) indexed by `tools/rootindex` with the same
  Bouncy Castle build, so the DN strings match; parsed lazily on the phone.
  On the phone: example.com 17 s (first TLS use), Wikipedia 27 s (large page),
  search 4.4 s.
- NETCF on the phone throws on `SetSocketOption(ReceiveTimeout)` (now best
  effort), and its exception texts are not installed - `Tools.Describe(ex)`
  reports the exception type instead.
- Chat box (WMAI.cs `Append`): appended with EM_REPLACESEL instead of
  resetting `log.Text`, which re-rendered the whole chat on every chunk.
- `kill_process`: CE pids exceed int.MaxValue; parsed as long now.
- Images do not pass through relay mode (text-only protocol).
- Tests: `tools/agenttest/WebVisionTest.cs` (PC, 7 checks) and
  `DeviceWebVisionTest.cs` (phone, 5 checks, uses copies of the app files in
  its own folder so the real chat history is untouched). All pass.

## Gotchas already solved (don't re-debug)

- `TcpClient.Connect(string host, ...)` costs ~5 s on WM5 (name resolution);
  connect with `IPEndPoint` when the host is numeric (done in `Http()`).
- Python `HTTPServer` does a reverse DNS lookup on bind, ~10 s on link-local
  addresses; `relay.Server.server_bind` skips it.
- Windows console code page breaks printing Cyrillic from test scripts; set
  `PYTHONIOENCODING=utf-8`.
- Testing the relay without the phone: drive it over raw sockets from Python
  (see the HTTP/1.0 helper pattern in the API above), monkeypatch
  `relay.stream_completion` to fake the model, and point `relay.CHAT_FILE` at a
  temp file so the real conversation isn't touched.
- Self-update: "Update app" downloads `/api/app` to `WMAI_new.exe`, starts it
  with `/install "<path>"`; it waits for the old process to exit, copies itself
  over `WMAI.exe`, relaunches it, and the relaunched app deletes `WMAI_new.exe`.
  `/api/app` serves whatever `app\WMAI.exe` is on disk, so build before updating.
