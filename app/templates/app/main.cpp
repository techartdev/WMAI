// {{TITLE}} - Windows Mobile 5 app built on the phone with GCC 3.2.2.
// Skeleton from WMAI's project_new: a full-screen window, a soft-key menu bar
// (main.rc) and a multi-line edit control. Keep this file ASCII.
#include <windows.h>
#include <aygshell.h>
#include <string.h>
#include "resource.h"

static HINSTANCE g_inst;
static HWND g_menubar;
static HWND g_edit;
static const WCHAR APP_CLASS[] = L"{{NAME}}_wnd";
static const WCHAR APP_TITLE[] = L"{{TITLE}}";

static void Layout(HWND hwnd)
{
    RECT rc;
    GetClientRect(hwnd, &rc);
    MoveWindow(g_edit, 0, 0, rc.right, rc.bottom, TRUE);
}

static LRESULT OnCommand(HWND hwnd, int id)
{
    switch (id)
    {
    case IDM_LEFT:
        SetWindowTextW(g_edit, L"");
        return 0;
    case IDM_ABOUT:
        MessageBoxW(hwnd, L"Built on this phone with GCC 3.2.2.", APP_TITLE, MB_OK);
        return 0;
    case IDM_EXIT:
        DestroyWindow(hwnd);
        return 0;
    }
    return 0;
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_CREATE:
    {
        SHMENUBARINFO mbi;
        memset(&mbi, 0, sizeof(mbi));
        mbi.cbSize = sizeof(mbi);
        mbi.hwndParent = hwnd;
        mbi.nToolBarId = IDR_MENUBAR;
        mbi.hInstRes = g_inst;
        if (SHCreateMenuBar(&mbi)) g_menubar = mbi.hwndMB;
        g_edit = CreateWindowExW(0, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
            0, 0, 0, 0, hwnd, (HMENU)IDC_EDIT, g_inst, NULL);
        return 0;
    }
    case WM_SIZE:
        Layout(hwnd);
        return 0;
    case WM_ACTIVATE:
        if (LOWORD(wp) != WA_INACTIVE) SetFocus(g_edit);
        return 0;
    case WM_COMMAND:
        return OnCommand(hwnd, LOWORD(wp));
    case WM_DESTROY:
        if (g_menubar) DestroyWindow(g_menubar);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int WINAPI WinMain(HINSTANCE inst, HINSTANCE prev, LPWSTR cmdLine, int show)
{
    g_inst = inst;

    // Windows Mobile convention: one instance; bring the running one forward.
    HWND running = FindWindowW(APP_CLASS, NULL);
    if (running)
    {
        SetForegroundWindow((HWND)((DWORD)running | 1));
        return 0;
    }

    WNDCLASSW wc;
    memset(&wc, 0, sizeof(wc));
    wc.lpfnWndProc = (WNDPROC)WndProc;
    wc.hInstance = inst;
    wc.hbrBackground = (HBRUSH)GetStockObject(WHITE_BRUSH);
    wc.lpszClassName = APP_CLASS;
    if (!RegisterClassW(&wc)) return 0;

    HWND hwnd = CreateWindowExW(0, APP_CLASS, APP_TITLE, WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, NULL, NULL, inst, NULL);
    if (!hwnd) return 0;

    // Fit the window between the title bar and the soft-key menu bar.
    if (g_menubar)
    {
        RECT rc, mb;
        GetWindowRect(hwnd, &rc);
        GetWindowRect(g_menubar, &mb);
        MoveWindow(hwnd, rc.left, rc.top, rc.right - rc.left, mb.top - rc.top, FALSE);
    }

    ShowWindow(hwnd, show);
    UpdateWindow(hwnd);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return (int)msg.wParam;
}
