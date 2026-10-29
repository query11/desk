// In-process XAML diagnostics initialization, as done by TranslucentTB's
// ExplorerTAP (GPL-3.0) and the Windows 11 Taskbar Styler mod for Windhawk
// (GPL-3.0).
#include "pch.h"
#include "install.h"
#include "config.h"
#include "engine.h"
#include "log.h"
#include "paths.h"
#include "regpatch.h"
#include "version.h"

namespace desk::install {
namespace {

using PFN_INITIALIZE_XAML_DIAGNOSTICS_EX = decltype(&InitializeXamlDiagnosticsEx);

HRESULT InitializeXamlDiagnostics() {
    std::wstring dllPath = paths::ModulePath();

    HMODULE xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr,
                                  LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!xaml) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    auto initialize = reinterpret_cast<PFN_INITIALIZE_XAML_DIAGNOSTICS_EX>(
        GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));
    if (!initialize) {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    HRESULT hr = E_FAIL;
    // XAML may not be ready yet right after the process started, so retry for
    // a while. Each attempt needs a fresh thread: XAML diagnostics can only be
    // initialized once per thread.
    for (int attempt = 1; attempt <= 60; attempt++) {
        std::thread([&] {
            // The endpoint name has to match an existing connection, and
            // there's no way to ask which one, so try them in order.
            for (int i = 1; i <= 10000; i++) {
                wchar_t connection[64];
                swprintf_s(connection, L"VisualDiagConnection%d", i);
                hr = initialize(connection, GetCurrentProcessId(), L"",
                                dllPath.c_str(), CLSID_DeskTap, nullptr);
                if (hr != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) {
                    break;
                }
            }
        }).join();

        if (SUCCEEDED(hr)) {
            return hr;
        }

        if (attempt == 1 || attempt % 10 == 0) {
            DLOG(L"InitializeXamlDiagnosticsEx attempt %d failed: %08X", attempt,
                 static_cast<uint32_t>(hr));
        }
        Sleep(500);
    }
    return hr;
}

}  // namespace

bool IsTargetProcess() {
    return paths::IsExplorer() || paths::IsStartMenu();
}

DWORD WINAPI InstallThread(LPVOID) {
    log::Init(paths::LogsDir() + L"\\tap-" + paths::ProcessName() + L".log");
    DLOG(L"DeskTap %s in %s (pid %lu), loaded from %s", DESKTAP_VERSION,
         paths::ProcessName().c_str(), GetCurrentProcessId(),
         paths::ModulePath().c_str());

    // One engine per process, even when another DeskTap build gets loaded.
    wchar_t mutexName[64];
    swprintf_s(mutexName, L"DeskTap.Engine.%lu", GetCurrentProcessId());
    HANDLE mutex = CreateMutexW(nullptr, FALSE, mutexName);
    if (!mutex) {
        DLOG(L"CreateMutex failed: %lu", GetLastError());
        return 0;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        DLOG(L"Another DeskTap engine is already running here");
        CloseHandle(mutex);
        return 0;
    }
    // The mutex stays open for the lifetime of the process.

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (...) {
    }

    regpatch::Install();
    config::Start([] { engine::OnConfigChanged(); });

    HRESULT hr = InitializeXamlDiagnostics();
    DLOG(L"InitializeXamlDiagnosticsEx: %08X", static_cast<uint32_t>(hr));
    return 0;
}

}  // namespace desk::install
