// In-process XAML diagnostics initialization, as done by TranslucentTB's
// ExplorerTAP (GPL-3.0) and the Windows 11 Taskbar Styler mod for Windhawk
// (GPL-3.0).
#include "pch.h"
#include "install.h"
#include "config.h"
#include "control.h"
#include "log.h"
#include "paths.h"
#include "regpatch.h"
#include "status.h"
#include "version.h"
#include "watcher.h"

namespace desk::install {
namespace {

using PFN_INITIALIZE_XAML_DIAGNOSTICS_EX = decltype(&InitializeXamlDiagnosticsEx);

constexpr int kAttemptsPerRound = 20;  // 500 ms apart: 10 s per round.
constexpr DWORD kAdviseTimeoutMs = 15'000;

// Initialization progress, shared by the worker and the XAML callbacks.
std::atomic<bool> g_diagnosticsUp{false};
std::atomic<bool> g_siteSet{false};
std::atomic<bool> g_advised{false};
std::atomic<HRESULT> g_adviseHr{E_PENDING};
HANDLE g_adviseEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

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
    for (int attempt = 1; attempt <= kAttemptsPerRound; attempt++) {
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

// The current process holds this for as long as it lives: one engine per
// process, even when another DeskTap build gets loaded.
bool ClaimProcess() {
    wchar_t mutexName[64];
    swprintf_s(mutexName, L"DeskTap.Engine.%lu", GetCurrentProcessId());
    HANDLE mutex = CreateMutexW(nullptr, FALSE, mutexName);
    if (!mutex) {
        DLOG(L"CreateMutex failed: %lu", GetLastError());
        return false;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(mutex);
        return false;
    }
    return true;
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

    status::Init();
    if (!ClaimProcess()) {
        // Desk.exe sees this and asks for a restart of the process.
        DLOG(L"Another DeskTap engine is already running here");
        status::SetEngine(status::Engine::Blocked);
        status::Flush(true);
        return 0;
    }

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (...) {
    }

    // Initialization itself waits for a configuration that turns styling on:
    // an engine that arrives after Desk was turned off (or after the crash
    // protection tripped) stays dormant.
    regpatch::Install();
    config::Init();
    status::SetEngine(status::Engine::Loaded);
    control::Start();
    return 0;
}

// For testing recovery on a real system: while
// <base>\state\fault.engine-init.<process> exists, initialization fails.
// The folder is readable from the Start menu's app container; the file
// never exists unless a tester creates it.
bool InitFaultInjected() {
    std::wstring path = paths::StateDir() + L"\\fault.engine-init." + paths::ProcessName();
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

InitResult Initialize() {
    if (InitFaultInjected()) {
        DLOG(L"Initialization fails on purpose (fault file present)");
        return {false, L"FaultInjection", E_FAIL};
    }
    if (!regpatch::Install()) {
        // Without it, composition diagnostics may corrupt explorer's heap.
        return {false, L"RegistryHooks", E_FAIL};
    }

    if (!g_diagnosticsUp) {
        ResetEvent(g_adviseEvent);
        g_siteSet = false;
        g_advised = false;
        g_adviseHr = E_PENDING;
        HRESULT hr = InitializeXamlDiagnostics();
        DLOG(L"InitializeXamlDiagnosticsEx: %08X", static_cast<uint32_t>(hr));
        if (FAILED(hr)) {
            return {false, L"InitializeXamlDiagnosticsEx", hr};
        }
        g_diagnosticsUp = true;
    } else if (g_advised && FAILED(g_adviseHr.load())) {
        // The site is there but watching failed: advise again, no new TAP.
        ResetEvent(g_adviseEvent);
        g_advised = false;
        g_adviseHr = E_PENDING;
        if (!RetryAdvise()) {
            return {false, L"AdviseVisualTreeChange", E_FAIL};
        }
    }

    if (WaitForSingleObject(g_adviseEvent, kAdviseTimeoutMs) != WAIT_OBJECT_0) {
        if (!g_siteSet) {
            // XAML never created the TAP: diagnostics may be initialized again.
            g_diagnosticsUp = false;
            return {false, L"SetSite", HRESULT_FROM_WIN32(ERROR_TIMEOUT)};
        }
        // An advise may still be running; the next round waits for it.
        return {false, L"AdviseVisualTreeChange", HRESULT_FROM_WIN32(ERROR_TIMEOUT)};
    }
    HRESULT hr = g_adviseHr;
    if (FAILED(hr)) {
        return {false, L"AdviseVisualTreeChange", hr};
    }
    return {true, nullptr, S_OK};
}

void OnSiteSet() {
    g_siteSet = true;
}

void OnAdviseResult(HRESULT hr) {
    g_adviseHr = hr;
    g_advised = true;
    SetEvent(g_adviseEvent);
    control::Wake();
}

}  // namespace desk::install
