#include "pch.h"
#include "control.h"
#include "config.h"
#include "core/host_watch.h"
#include "core/init_policy.h"
#include "engine.h"
#include "install.h"
#include "log.h"
#include "paths.h"
#include "status.h"

namespace desk::control {
namespace {

using core::HostWatch;
using core::InitPolicy;

HANDLE g_wake = nullptr;

// Control thread only.
InitPolicy g_init;
HostWatch g_host;
bool g_lastLost = false;
std::wstring g_probedSession;
HANDLE g_hostProcess = nullptr;
bool g_hostGone = false;
DWORD g_probeError = 0;

// Shared with the initialization worker.
std::atomic<bool> g_initRunning{false};
std::atomic<bool> g_initFinished{false};
install::InitResult g_initResult;

// Milliseconds the system was awake: sleep and hibernation don't count, so a
// long sleep never looks like a missed heartbeat.
uint64_t AwakeMs() {
    ULONGLONG time = 0;
    QueryUnbiasedInterruptTime(&time);
    return time / 10'000;
}

void CloseHostProcess() {
    if (g_hostProcess) {
        CloseHandle(g_hostProcess);
        g_hostProcess = nullptr;
    }
}

uint64_t CreationTime(HANDLE process) {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) {
        return 0;
    }
    return (static_cast<uint64_t>(creation.dwHighDateTime) << 32) |
           creation.dwLowDateTime;
}

// Looks at the owner's process. Opened once per session; the creation time
// tells a reused process id apart.
HostWatch::Probe ProbeHost(const Config& config) {
    if (g_probedSession != config.hostSession) {
        CloseHostProcess();
        g_probedSession = config.hostSession;
        g_hostGone = false;
        g_probeError = 0;
        if (config.hostPid != 0) {
            g_hostProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
                                        FALSE, config.hostPid);
            if (!g_hostProcess) {
                g_probeError = GetLastError();
                // No such process. Access denied (an app container) only
                // means the heartbeat has to tell.
                g_hostGone = g_probeError == ERROR_INVALID_PARAMETER;
            } else if (config.hostStart != 0 &&
                       CreationTime(g_hostProcess) != config.hostStart) {
                g_hostGone = true;
                CloseHostProcess();
            }
            DLOG(L"Watching Desk.exe pid %lu: %s", config.hostPid,
                 g_hostGone        ? L"already gone"
                 : g_hostProcess   ? L"process handle"
                                   : L"heartbeat only");
        }
    }
    if (g_hostGone) {
        return HostWatch::Probe::Gone;
    }
    if (!g_hostProcess) {
        return HostWatch::Probe::Unknown;
    }
    if (WaitForSingleObject(g_hostProcess, 0) == WAIT_OBJECT_0) {
        g_hostGone = true;
        CloseHostProcess();
        return HostWatch::Probe::Gone;
    }
    return HostWatch::Probe::Alive;
}

DWORD WINAPI InitWorker(LPVOID) {
    install::InitResult result;
    try {
        result = install::Initialize();
    } catch (...) {
        result = {false, L"Exception", winrt::to_hresult().value};
    }
    g_initResult = result;
    g_initFinished.store(true, std::memory_order_release);
    g_initRunning.store(false, std::memory_order_release);
    Wake();
    return 0;
}

void StartInit() {
    if (!g_init.OnStarted()) {
        return;
    }
    status::Count(status::kInitAttempts);
    status::SetInitAttempts(g_init.attempts());
    status::SetEngine(status::Engine::Initializing);
    g_initRunning = true;
    HANDLE thread = CreateThread(nullptr, 0, InitWorker, nullptr, 0, nullptr);
    if (thread) {
        CloseHandle(thread);
    } else {
        g_initRunning = false;
        g_init.OnFailed(AwakeMs());
        status::SetInitFailure(L"CreateThread", HRESULT_FROM_WIN32(GetLastError()),
                               g_init.attempts(),
                               g_init.nextAttemptMs() - AwakeMs());
    }
}

void FinishInit(uint64_t now) {
    if (!g_initFinished.exchange(false, std::memory_order_acquire)) {
        return;
    }
    auto result = g_initResult;
    if (result.ok) {
        g_init.OnSucceeded();
        status::SetEngine(status::Engine::Ready);
        DLOG(L"Engine ready after %u attempt(s)", g_init.attempts());
        return;
    }
    g_init.OnFailed(now);
    uint64_t retryIn = g_init.nextAttemptMs() - now;
    status::SetInitFailure(result.stage, result.hr, g_init.attempts(), retryIn);
    DLOG(L"Initialization failed at %s: %08X; retrying in %llu s",
         result.stage ? result.stage : L"?", static_cast<uint32_t>(result.hr),
         retryIn / 1000);
}

void Step() {
    uint64_t now = AwakeMs();

    if (config::ReloadIfChanged()) {
        status::Count(status::kConfigLoads);
        auto parsed = config::Parsed();
        if (g_init.OnRecoverRequest(parsed->recover, now)) {
            DLOG(L"Desk.exe asked to retry the initialization");
        }
        if (g_host.Attach(parsed->hostSession, now)) {
            DLOG(L"Configuration owner: %s",
                 parsed->hostSession.empty() ? L"none" : parsed->hostSession.c_str());
        }
    }

    auto parsed = config::Parsed();
    HostWatch::Verdict verdict = HostWatch::Verdict::Unowned;
    if (!parsed->hostSession.empty()) {
        auto probe = ProbeHost(*parsed);
        bool beatRead = false;
        uint64_t seq = 0;
        if (probe == HostWatch::Probe::Unknown) {
            if (auto beat = config::ReadHeartbeat();
                beat && beat->session == parsed->hostSession) {
                beatRead = true;
                seq = beat->seq;
            }
        }
        verdict = g_host.Observe(probe, beatRead, seq, now);
    } else {
        verdict = g_host.Observe(HostWatch::Probe::Unknown, false, 0, now);
    }

    bool lost = verdict == HostWatch::Verdict::Lost;
    if (lost != g_lastLost) {
        g_lastLost = lost;
        if (lost) {
            DLOG(L"Desk.exe (session %s) is gone (%s): restoring",
                 parsed->hostSession.c_str(),
                 g_host.reason() == HostWatch::Reason::ProcessGone
                     ? L"process ended"
                     : L"no heartbeat");
        } else {
            DLOG(L"Desk.exe (session %s) is back", parsed->hostSession.c_str());
        }
    }
    status::SetHost(verdict == HostWatch::Verdict::Unowned ? status::Host::None
                    : lost                                ? status::Host::Lost
                                                          : status::Host::Attached,
                    parsed->hostSession,
                    !lost ? nullptr
                    : g_host.reason() == HostWatch::Reason::ProcessGone
                        ? L"processGone"
                        : L"noHeartbeat");

    if (config::UpdateEffective(lost)) {
        auto current = config::Current();
        status::SetConfig(current->serial, current->revision, current->enabled);
        engine::OnConfigChanged();
    }

    FinishInit(now);
    bool wanted = config::Current()->enabled;
    if (!g_initRunning.load(std::memory_order_acquire) && g_init.Due(now, wanted)) {
        StartInit();
    }

    status::Flush();
}

DWORD WINAPI ControlThread(LPVOID) {
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (...) {
    }

    std::wstring dir = paths::RuntimeDir();
    HANDLE change = FindFirstChangeNotificationW(
        dir.c_str(), FALSE,
        FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_FILE_NAME |
            FILE_NOTIFY_CHANGE_SIZE);
    if (change == INVALID_HANDLE_VALUE) {
        DLOG(L"FindFirstChangeNotification failed (%lu), polling instead",
             GetLastError());
    }

    for (;;) {
        try {
            Step();
        } catch (...) {
            DLOG(L"Control step failed: %08X", winrt::to_hresult().value);
        }

        HANDLE handles[2] = {g_wake, change};
        DWORD count = change != INVALID_HANDLE_VALUE ? 2 : 1;
        DWORD wait = WaitForMultipleObjects(count, handles, FALSE, 1000);
        if (wait == WAIT_OBJECT_0 + 1) {
            // Let the writer finish replacing the file.
            Sleep(50);
            FindNextChangeNotification(change);
        }
    }
}

}  // namespace

void Start() {
    static std::once_flag once;
    std::call_once(once, [] {
        g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        HANDLE thread = CreateThread(nullptr, 0, ControlThread, nullptr, 0, nullptr);
        if (thread) {
            CloseHandle(thread);
        } else {
            DLOG(L"Could not start the control thread: %lu", GetLastError());
        }
    });
}

void Wake() {
    if (g_wake) {
        SetEvent(g_wake);
    }
}

}  // namespace desk::control
