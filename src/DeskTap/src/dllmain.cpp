#include "pch.h"
#include "install.h"
#include "version.h"

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH && desk::install::IsTargetProcess()) {
        // Desk.exe loads the DLL with a window hook. Pin it, so that removing
        // the hook doesn't unload it again.
        HMODULE pinned = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&DllMain), &pinned);

        if (HANDLE thread = CreateThread(nullptr, 0, desk::install::InstallThread,
                                         nullptr, 0, nullptr)) {
            CloseHandle(thread);
        }
    }
    return TRUE;
}

// Desk.exe installs this as a WH_GETMESSAGE hook on a thread of the target
// process, which makes Windows load the DLL there.
extern "C" LRESULT CALLBACK DeskTap_HookProc(int code, WPARAM wParam, LPARAM lParam) {
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

extern "C" const wchar_t* WINAPI DeskTap_Version() {
    return DESKTAP_VERSION;
}
