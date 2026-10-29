// Ported from the Windows 11 Taskbar Styler mod for Windhawk
// (https://github.com/ramensoftware/windhawk-mods, GPL-3.0).
#include "pch.h"
#include "surface.h"
#include "log.h"
#include "winutil.h"

namespace desk::surface {
namespace {

constexpr UINT_PTR kSubclassId = 0x4445534B;  // "DESK"

thread_local std::unordered_set<HWND> t_subclassed;

// Taskbar windows use a NULL class brush and nothing paints into them, so
// whatever DWM leaves in the redirection surface stays. Filling with black
// resets it: GDI leaves alpha at zero, which composites as transparent. The DC
// must not clip children, since the XAML island covers the whole window.
void ResetSurface(HWND window) {
    RECT rect;
    if (!GetWindowRect(window, &rect)) {
        return;
    }
    HDC dc = GetDCEx(window, nullptr, DCX_WINDOW | DCX_CACHE);
    if (!dc) {
        return;
    }
    RECT fill = {0, 0, rect.right - rect.left, rect.bottom - rect.top};
    FillRect(dc, &fill, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
    ReleaseDC(window, dc);
}

LRESULT CALLBACK SubclassProc(HWND window,
                              UINT message,
                              WPARAM wParam,
                              LPARAM lParam,
                              UINT_PTR,
                              DWORD_PTR) {
    switch (message) {
        case WM_PAINT:
            ResetSurface(window);
            break;
        case WM_NCDESTROY:
            t_subclassed.erase(window);
            RemoveWindowSubclass(window, SubclassProc, kSubclassId);
            break;
    }
    return DefSubclassProc(window, message, wParam, lParam);
}

}  // namespace

void EnsureForCurrentThread() {
    EnumThreadWindows(
        GetCurrentThreadId(),
        [](HWND window, LPARAM) -> BOOL {
            if (winutil::IsTaskbarWindow(window) &&
                !t_subclassed.contains(window) &&
                SetWindowSubclass(window, SubclassProc, kSubclassId, 0)) {
                t_subclassed.insert(window);
                ResetSurface(window);
                DLOG(L"Taskbar surface reset installed for %p", window);
            }
            return TRUE;
        },
        0);
}

}  // namespace desk::surface
