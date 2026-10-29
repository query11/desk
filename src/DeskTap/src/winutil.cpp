#include "pch.h"
#include "winutil.h"

namespace desk::winutil {
namespace {

constexpr WPARAM kRunMagic = 0x4445534B;  // "DESK"

UINT RunMessage() {
    static UINT message = RegisterWindowMessageW(L"DeskTap_RunOnWindowThread");
    return message;
}

LRESULT CALLBACK CallWndProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code == HC_ACTION) {
        auto* message = reinterpret_cast<const CWPSTRUCT*>(lParam);
        if (message->message == RunMessage() && message->wParam == kRunMagic) {
            auto* callback =
                reinterpret_cast<const std::function<void()>*>(message->lParam);
            try {
                (*callback)();
            } catch (...) {
            }
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

}  // namespace

bool RunOnWindowThread(HWND window, const std::function<void()>& callback) {
    DWORD threadId = GetWindowThreadProcessId(window, nullptr);
    if (!threadId) {
        return false;
    }
    if (threadId == GetCurrentThreadId()) {
        callback();
        return true;
    }

    HHOOK hook = SetWindowsHookExW(WH_CALLWNDPROC, CallWndProc, nullptr, threadId);
    if (!hook) {
        return false;
    }
    SendMessageW(window, RunMessage(), kRunMagic,
                 reinterpret_cast<LPARAM>(&callback));
    UnhookWindowsHookEx(hook);
    return true;
}

bool SetWindowSubclassFromAnyThread(HWND window,
                                    SUBCLASSPROC proc,
                                    UINT_PTR id,
                                    DWORD_PTR data) {
    bool result = false;
    RunOnWindowThread(window, [&] {
        result = SetWindowSubclass(window, proc, id, data) != FALSE;
    });
    return result;
}

bool IsTaskbarWindow(HWND window) {
    wchar_t className[64];
    if (!GetClassNameW(window, className, ARRAYSIZE(className))) {
        return false;
    }
    return _wcsicmp(className, L"Shell_TrayWnd") == 0 ||
           _wcsicmp(className, L"Shell_SecondaryTrayWnd") == 0;
}

}  // namespace desk::winutil
