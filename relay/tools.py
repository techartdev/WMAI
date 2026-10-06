"""Agent definition shared by the relay and the phone app: system prompt and the
tools offered to the model (the phone app executes them).
Export for the app: python relay/tools.py app/WMAI.agent.json

Each tool is (name, description, {param: (json_type, description)}, required).
The phone asks the user Yes/No before anything that sends, deletes,
overwrites, launches or kills (see Tools.cs).
"""

SYSTEM_PROMPT = (
    "You are WMAI, an assistant running on a Windows Mobile Pocket PC phone - typically "
    "an old device with a small screen (often 240x320), a slow CPU, little memory and a "
    "slow keyboard; device_info tells you about this one. "
    "Keep answers short and plain: no markdown tables, no headings, minimal "
    "formatting. Use short paragraphs or simple '-' lists.\n"
    "You have tools that run on the phone itself: files, registry, processes, "
    "contacts, SMS, calls, device status. Use them when they help; don't ask "
    "permission first - the phone shows the user a Yes/No prompt for anything risky. "
    "If the user declines, accept it. There is no command shell on Windows Mobile; "
    "launch programs with run_program. File paths use backslashes; the storage card "
    "is usually \\Storage Card (fs_list \\ shows the root). Phone time: use device_info.\n"
    "You can write and compile native Windows Mobile apps on the phone: start with project_new, edit with "
    "project_read/project_edit/project_write, compile with build (its description is the compiler manual), "
    "then run the .exe with run_program.\n"
    "You can see: screenshot shows you the screen (check apps you built this way), view_image shows image "
    "files. You can use the web: web_search, then web_fetch pages."
)
MAX_STEPS = 30  # model calls per user message (coding needs write/build/fix loops)

# The compiler manual for the model: every rule here was verified on the phone
# (PocketGCC 1.50 rebuilt with --stdout/--stderr, see compiler/README.md).
BUILD_GUIDE = (
    "Compile a project into a native Windows Mobile 5 app (ARM .exe) with the on-device compiler, GCC 3.2.2 "
    "from 2003. Compiles each changed .cpp/.c (all as C++) plus the project's one .rc file, links "
    "<project>.exe in the project folder and returns compiler messages as 'file:line: message'. "
    "On error: read the reported lines with project_read, fix with project_edit, build again. "
    "When it succeeds, start it with run_program (close a running copy first, or linking fails).\n"
    "Speed: every .cpp costs about 45 s on a 200 MHz phone (mostly parsing windows.h), so use one or two "
    "source files; only changed files are recompiled; linking takes ~10 s.\n"
    "Language: C++98 only. No STL, no std::string/vector/iostream, no exceptions; no auto, nullptr, lambdas, "
    "range-for, override or C++11 headers. Use plain structs, fixed arrays, new/delete, WCHAR buffers and the C "
    "runtime in <stdlib.h>/<string.h> (wcslen, wcscpy, wcscat, wcscmp, _wtoi, swprintf, malloc/free).\n"
    "Platform: Windows CE 5 Win32 API, Unicode only (UNICODE is predefined): L\"text\" strings, WCHAR/TCHAR, "
    "W functions (CreateWindowExW, MessageBoxW, SendMessageW...). There is no console: printf output is "
    "invisible, so show results in controls or MessageBoxW, or write a file. Entry point: "
    "int WINAPI WinMain(HINSTANCE inst, HINSTANCE prev, LPWSTR cmdLine, int show). Keep the skeleton's "
    "single-instance check and window sizing.\n"
    "Headers: windows.h, aygshell.h (SHCreateMenuBar, SHInitDialog, SHSipPreference), commctrl.h and "
    "windowsx.h (constants/macros), string.h, stdlib.h. Linked libraries: coredll, aygshell and the C/C++ "
    "runtime only. Functions in other DLLs (commctrl.dll, commdlg.dll, phone.dll...) must be loaded with "
    "LoadLibraryW + GetProcAddressW.\n"
    "UI: small touch screen (often 240x320; never hard-code the size - lay out from GetClientRect) with two "
    "soft keys. Child controls from coredll work: EDIT (ES_MULTILINE | "
    "ES_WANTRETURN | WS_VSCROLL for editors), LISTBOX (LBS_NOTIFY), BUTTON, STATIC, COMBOBOX. Lay them out in "
    "WM_SIZE. Soft keys come from the SHMENUBAR resource in main.rc: the left key sends its WM_COMMAND id, the "
    "right key opens popup 0 of the MENU resource with the same id; captions are in the STRINGTABLE; keep ids "
    "in resource.h. Files: CreateFileW/ReadFile/WriteFile with full paths such as \\My Documents\\notes.txt "
    "(no current directory on Windows CE).\n"
    "Text: source files must be ASCII. Write other characters as escapes inside wide strings, e.g. "
    "L\"\\x0417\\x0434\\x0440\\x0430\\x0432\\x0435\\x0439\". Never pass -quiet or other compiler flags."
)

_TOOLS = [
    ("device_info", "Battery, memory, storage, signal/operator, unread SMS count, OS version and time of the phone.", {}, []),

    ("fs_list", "List a directory on the phone. Paths use backslashes, e.g. \\, \\Storage Card, \\My Documents, \\Windows.",
     {"path": ("string", "Directory path"), "pattern": ("string", "Wildcard filter, default *")}, ["path"]),
    ("fs_read", "Read a text file (UTF-8/UTF-16 detected). Binary files return a hex dump.",
     {"path": ("string", "File path"), "offset": ("integer", "Start character, default 0"),
      "max_chars": ("integer", "Max characters to return, default 4000, max 12000")}, ["path"]),
    ("fs_write", "Write text to a file (UTF-8). Overwrites unless append=true. The user must approve.",
     {"path": ("string", "File path"), "content": ("string", "Text to write"),
      "append": ("boolean", "Append instead of overwrite")}, ["path", "content"]),
    ("fs_delete", "Delete a file or an empty directory. The user must approve.",
     {"path": ("string", "Path")}, ["path"]),
    ("fs_move", "Move or rename a file or directory. The user must approve.",
     {"src": ("string", "Source path"), "dst": ("string", "Destination path")}, ["src", "dst"]),
    ("fs_copy", "Copy a file (never overwrites).",
     {"src": ("string", "Source file"), "dst": ("string", "Destination file")}, ["src", "dst"]),
    ("fs_mkdir", "Create a directory.", {"path": ("string", "Directory path")}, ["path"]),

    ("registry_read", "Read the phone registry. Without 'value' lists subkeys and values of the key. "
     "Key like HKLM\\System\\State\\Phone (roots: HKLM, HKCU, HKCR, HKU).",
     {"key": ("string", "Registry key"), "value": ("string", "Value name; omit to list")}, ["key"]),

    ("process_list", "List running processes (pid, threads, exe).", {}, []),
    ("run_program", "Launch a program (.exe) on the phone, e.g. \\Windows\\calc.exe. There is no command shell. The user must approve.",
     {"path": ("string", "Program path"), "args": ("string", "Command-line arguments")}, ["path"]),
    ("kill_process", "Terminate a process by pid (see process_list). The user must approve.",
     {"pid": ("integer", "Process id")}, ["pid"]),

    ("contacts_search", "Search the phone's contacts by name, company, phone or email. Empty query lists all.",
     {"query": ("string", "Text to search for"), "max": ("integer", "Max results, default 20")}, []),
    ("sms_list", "List recent SMS messages in a folder, newest first.",
     {"folder": ("string", "Inbox (default), Sent Items, Drafts, Outbox or Deleted Items"),
      "count": ("integer", "How many, default 10, max 50")}, []),
    ("sms_send", "Send an SMS. The user must approve on the phone.",
     {"to": ("string", "Phone number, e.g. +359888123456"), "text": ("string", "Message text")}, ["to", "text"]),
    ("phone_call", "Dial a phone number. The user must approve.",
     {"number": ("string", "Phone number")}, ["number"]),

    # --- coding: native apps built on the phone (app/CodeTools.cs) ---
    ("project_new", "Create a new native app project in \\Storage Card\\Projects\\<project> from a skeleton that "
     "already compiles: main.cpp (window + soft-key menu bar + multi-line text box), main.rc (menu bar), resource.h. "
     "Always start new apps with this, then read and change the files. The first change to a project asks the "
     "user once for permission.",
     {"project": ("string", "Project name: letters, digits, _ or -; also the .exe name"),
      "title": ("string", "Window title shown to the user")}, ["project"]),
    ("project_list", "List projects, or the files of one project.",
     {"project": ("string", "Project name; omit to list all projects")}, []),
    ("project_read", "Read a project file with line numbers (compiler errors refer to these).",
     {"project": ("string", "Project name"), "path": ("string", "File path inside the project, e.g. main.cpp"),
      "start_line": ("integer", "First line, default 1"), "max_lines": ("integer", "Default 200, max 400")},
     ["project", "path"]),
    ("project_write", "Create or overwrite a file in a project. Prefer project_edit for small changes to existing "
     "files (less data on a slow link). Source files must be ASCII.",
     {"project": ("string", "Project name"), "path": ("string", "File path inside the project"),
      "content": ("string", "Full file content")}, ["project", "path", "content"]),
    ("project_edit", "Replace one exact snippet in a project file. old_text must match exactly once "
     "(copy it from project_read output without the line-number prefix).",
     {"project": ("string", "Project name"), "path": ("string", "File path inside the project"),
      "old_text": ("string", "Exact text to replace"), "new_text": ("string", "Replacement text")},
     ["project", "path", "old_text", "new_text"]),
    # --- web and vision (app/Web.cs, app/ImageTools.cs) ---
    ("web_search", "Search the web (DuckDuckGo). Returns titles, URLs and snippets; open pages with web_fetch.",
     {"query": ("string", "Search terms"), "count": ("integer", "Results, default 6, max 10")}, ["query"]),
    ("web_fetch", "Download a web page (http or https) and return it as readable text (HTML is converted; "
     "scripts and styling dropped). Long pages are paged: call again with the offset it suggests. "
     "links=true numbers the links in the text [n] and lists their URLs, for following them. "
     "The phone is slow and memory is small: prefer specific pages over large ones.",
     {"url": ("string", "Full URL"), "offset": ("integer", "Start character, default 0"),
      "max_chars": ("integer", "Default 6000, max 20000"), "links": ("boolean", "Also list links")}, ["url"]),
    ("screenshot", "Capture the phone's screen as a PNG (saved in \\My Documents\\WMAI) and look at it - the "
     "image is attached for you to see. WMAI hides itself during the capture so you see the app underneath; "
     "use delay_seconds to wait for an app you just launched.",
     {"delay_seconds": ("integer", "Wait before capturing, 0-30, default 0"),
      "hide_wmai": ("boolean", "Hide WMAI during the capture, default true")}, []),
    ("view_image", "Look at an image file on the phone (PNG, JPEG, GIF or BMP, up to 600 KB). The image is "
     "attached for you to see.",
     {"path": ("string", "Image file path")}, ["path"]),

    ("build", BUILD_GUIDE,
     {"project": ("string", "Project name"),
      "clean": ("boolean", "Recompile everything instead of only changed files")}, ["project"]),
]

SCHEMAS = [
    {
        "type": "function",
        "function": {
            "name": name,
            "description": desc,
            "parameters": {
                "type": "object",
                "properties": {k: {"type": t, "description": d} for k, (t, d) in params.items()},
                "required": required,
            },
        },
    }
    for name, desc, params, required in _TOOLS
]


if __name__ == "__main__":
    import json
    import sys

    with open(sys.argv[1], "w", encoding="utf-8") as f:
        json.dump({"system": SYSTEM_PROMPT, "max_steps": MAX_STEPS, "tools": SCHEMAS}, f, ensure_ascii=False, indent=1)
