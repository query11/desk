#include "pch.h"
#include "status.h"
#include "log.h"
#include "paths.h"
#include "version.h"

namespace desk::status {
namespace {

constexpr int kProtocol = 1;

const wchar_t* const kCounterNames[] = {
    L"uiThreadsSeen",          L"uiThreadsReleased",
    L"configLoads",            L"statusWrites",
    L"initAttempts",           L"propertyCallbacks",
    L"propertyCallbacksReleased", L"styleResolves",
    L"styleCacheHits",         L"blurBrushes",
    L"blurBrushesReleased",    L"blurInPlaceUpdates",
    L"effectFactories",        L"effectFactoryHits",
    L"noiseSurfaces",          L"noiseSurfaceHits",
    L"samplerThreadStarts",    L"samplerThreadStops",
    L"samplesCpu",             L"samplesMemory",
    L"samplesNetwork",         L"monitorPosts",
    L"monitorPostsCoalesced",  L"monitorTextUpdates",
    L"monitorUiMicros",        L"monitorIslands",
    L"monitorIslandsReleased", L"layoutSubscriptions",
    L"layoutSubscriptionsReleased", L"subclasses",
    L"subclassesReleased",     L"regionsSet",
    L"regionsCleared",         L"dispatcherPostFailures",
};
static_assert(std::size(kCounterNames) == kCounterCount);

std::atomic<int64_t> g_counters[kCounterCount];

struct State {
    bool initialized = false;
    std::wstring path;
    std::wstring instance;
    uint64_t processStart = 0;

    Engine engine = Engine::Loaded;
    std::wstring failStage;
    HRESULT failHr = S_OK;
    uint32_t attempts = 0;
    uint64_t retryAt = 0;  // FILETIME (UTC) of the next automatic attempt.

    uint64_t serial = 0;
    uint64_t revision = 0;
    bool enabled = false;

    Host host = Host::None;
    std::wstring session;
    std::wstring lostReason;

    std::map<uint32_t, core::ThreadReport> threads;

    std::string lastContent;
    int64_t lastCounters[kCounterCount] = {};
    ULONGLONG lastCountersWrite = 0;
    bool writeFailedLogged = false;
};

std::mutex g_mutex;
State g_state;

std::wstring Escape(std::wstring_view text) {
    std::wstring result;
    result.reserve(text.size() + 8);
    for (wchar_t c : text) {
        switch (c) {
            case L'"':
                result += L"\\\"";
                break;
            case L'\\':
                result += L"\\\\";
                break;
            case L'\n':
                result += L"\\n";
                break;
            case L'\r':
                result += L"\\r";
                break;
            case L'\t':
                result += L"\\t";
                break;
            default:
                if (c < 0x20) {
                    wchar_t escaped[8];
                    swprintf_s(escaped, L"\\u%04x", static_cast<unsigned>(c));
                    result += escaped;
                } else {
                    result += c;
                }
        }
    }
    return result;
}

std::wstring Quote(std::wstring_view text) {
    return L"\"" + Escape(text) + L"\"";
}

const wchar_t* EngineName(Engine state) {
    switch (state) {
        case Engine::Loaded:
            return L"loaded";
        case Engine::Initializing:
            return L"initializing";
        case Engine::Ready:
            return L"ready";
        case Engine::Failed:
            return L"failed";
        case Engine::Blocked:
            return L"blocked";
    }
    return L"unknown";
}

const wchar_t* HostName(Host host) {
    switch (host) {
        case Host::None:
            return L"none";
        case Host::Attached:
            return L"attached";
        case Host::Lost:
            return L"lost";
    }
    return L"unknown";
}

const wchar_t* ApplyName(core::ApplyResult result) {
    switch (result) {
        case core::ApplyResult::Disabled:
            return L"disabled";
        case core::ApplyResult::NoUi:
            return L"noUi";
        case core::ApplyResult::Pending:
            return L"pending";
        case core::ApplyResult::NoMatch:
            return L"noMatch";
        case core::ApplyResult::Applied:
            return L"applied";
    }
    return L"unknown";
}

std::wstring NewInstanceId() {
    GUID guid{};
    if (FAILED(CoCreateGuid(&guid))) {
        wchar_t fallback[32];
        swprintf_s(fallback, L"%lu-%llu", GetCurrentProcessId(),
                   static_cast<unsigned long long>(GetTickCount64()));
        return fallback;
    }
    wchar_t text[40];
    swprintf_s(text, L"%08lx%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x",
               guid.Data1, guid.Data2, guid.Data3, guid.Data4[0], guid.Data4[1],
               guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5],
               guid.Data4[6], guid.Data4[7]);
    return text;
}

uint64_t ProcessStartTime() {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user)) {
        return 0;
    }
    return (static_cast<uint64_t>(creation.dwHighDateTime) << 32) |
           creation.dwLowDateTime;
}

std::string ToUtf8(const std::wstring& text) {
    int length = WideCharToMultiByte(CP_UTF8, 0, text.data(),
                                     static_cast<int>(text.size()), nullptr, 0,
                                     nullptr, nullptr);
    std::string utf8(static_cast<size_t>(std::max(length, 0)), '\0');
    if (length > 0) {
        WideCharToMultiByte(CP_UTF8, 0, text.data(),
                            static_cast<int>(text.size()), utf8.data(), length,
                            nullptr, nullptr);
    }
    return utf8;
}

// The part of the report that matters for Desk.exe; written whenever it
// changes. Caller holds the mutex.
std::wstring Body(const State& state) {
    auto summary = core::Summarize(state.threads, state.revision, state.serial,
                                   state.enabled);
    std::wstring json = L"{";
    json += L"\"protocol\":" + std::to_wstring(kProtocol);
    json += L",\"version\":" + Quote(DESKTAP_VERSION);
    json += L",\"process\":" + Quote(paths::ProcessName());
    json += L",\"pid\":" + std::to_wstring(GetCurrentProcessId());
    json += L",\"processStart\":" + std::to_wstring(state.processStart);
    json += L",\"module\":" + Quote(paths::ModulePath());
    json += L",\"instance\":" + Quote(state.instance);
    json += L",\"state\":" + Quote(EngineName(state.engine));
    json += L",\"initAttempts\":" + std::to_wstring(state.attempts);
    if (state.engine == Engine::Failed) {
        wchar_t hr[16];
        swprintf_s(hr, L"%08X", static_cast<uint32_t>(state.failHr));
        json += L",\"error\":{\"stage\":" + Quote(state.failStage) +
                L",\"hr\":" + Quote(hr) + L"}";
        json += L",\"retryAt\":" + std::to_wstring(state.retryAt);
    }
    json += L",\"configSerial\":" + std::to_wstring(state.serial);
    json += L",\"enabled\":" + std::wstring(state.enabled ? L"true" : L"false");
    json += L",\"appliedSerial\":" + std::to_wstring(summary.appliedSerial);
    json += L",\"apply\":" + Quote(ApplyName(summary.result));
    json += L",\"uiThreads\":" + std::to_wstring(summary.threads);
    json += L",\"matched\":" + std::to_wstring(summary.matched);
    json += L",\"errors\":" + std::to_wstring(summary.errors);
    json += L",\"host\":{\"state\":" + Quote(HostName(state.host)) +
            L",\"session\":" + Quote(state.session);
    if (!state.lostReason.empty()) {
        json += L",\"reason\":" + Quote(state.lostReason);
    }
    json += L"}";
    return json;
}

bool WriteAtomically(const std::wstring& path, const std::string& content) {
    std::wstring temp = path + L".tmp";
    HANDLE file = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    DWORD written = 0;
    BOOL ok = WriteFile(file, content.data(), static_cast<DWORD>(content.size()),
                        &written, nullptr) &&
              written == content.size();
    CloseHandle(file);
    if (!ok || !MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        DeleteFileW(temp.c_str());
        return false;
    }
    return true;
}

}  // namespace

void Init() {
    std::lock_guard lock(g_mutex);
    if (g_state.initialized) {
        return;
    }
    g_state.initialized = true;
    g_state.instance = NewInstanceId();
    g_state.processStart = ProcessStartTime();
    g_state.path = paths::StateDir() + L"\\" + paths::ProcessName() + L"." +
                   std::to_wstring(GetCurrentProcessId()) + L".json";
}

void SetEngine(Engine state) {
    std::lock_guard lock(g_mutex);
    g_state.engine = state;
    if (state != Engine::Failed) {
        g_state.failStage.clear();
        g_state.failHr = S_OK;
    }
}

void SetInitFailure(const wchar_t* stage, HRESULT hr, uint32_t attempts,
                    uint64_t retryInMs) {
    std::lock_guard lock(g_mutex);
    g_state.engine = Engine::Failed;
    g_state.failStage = stage ? stage : L"";
    g_state.failHr = hr;
    g_state.attempts = attempts;
    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    g_state.retryAt = ((static_cast<uint64_t>(now.dwHighDateTime) << 32) |
                       now.dwLowDateTime) +
                      retryInMs * 10'000;
}

void SetInitAttempts(uint32_t attempts) {
    std::lock_guard lock(g_mutex);
    g_state.attempts = attempts;
}

void SetConfig(uint64_t serial, uint64_t revision, bool enabled) {
    std::lock_guard lock(g_mutex);
    g_state.serial = serial;
    g_state.revision = revision;
    g_state.enabled = enabled;
}

void SetHost(Host host, const std::wstring& session, const wchar_t* reason) {
    std::lock_guard lock(g_mutex);
    g_state.host = host;
    g_state.session = session;
    g_state.lostReason = reason ? reason : L"";
}

void ReportThread(DWORD threadId, const core::ThreadReport& report) {
    std::lock_guard lock(g_mutex);
    g_state.threads[threadId] = report;
}

void ForgetThread(DWORD threadId) {
    std::lock_guard lock(g_mutex);
    g_state.threads.erase(threadId);
}

void Flush(bool force) {
    std::wstring path;
    std::wstring json;
    {
        std::lock_guard lock(g_mutex);
        if (!g_state.initialized) {
            return;
        }
        path = g_state.path;
        json = Body(g_state);

        int64_t counters[kCounterCount];
        bool countersChanged = false;
        for (int i = 0; i < kCounterCount; i++) {
            counters[i] = g_counters[i].load(std::memory_order_relaxed);
            countersChanged |= counters[i] != g_state.lastCounters[i];
        }
        std::string body = ToUtf8(json);
        ULONGLONG now = GetTickCount64();
        bool bodyChanged = body != g_state.lastContent;
        bool countersDue = countersChanged && now - g_state.lastCountersWrite >= 15'000;
        if (!force && !bodyChanged && !countersDue) {
            return;
        }
        g_state.lastContent = std::move(body);
        g_state.lastCountersWrite = now;
        std::copy(std::begin(counters), std::end(counters),
                  std::begin(g_state.lastCounters));

        json += L",\"counters\":{";
        for (int i = 0; i < kCounterCount; i++) {
            if (i > 0) {
                json += L",";
            }
            json += Quote(kCounterNames[i]) + L":" + std::to_wstring(counters[i]);
        }
        json += L"}}";
    }

    Count(kStatusWrites);
    if (!WriteAtomically(path, ToUtf8(json))) {
        std::lock_guard lock(g_mutex);
        if (!g_state.writeFailedLogged) {
            g_state.writeFailedLogged = true;
            DLOG(L"Could not write the status file %s: %lu", path.c_str(),
                 GetLastError());
        }
        // Try again next time.
        g_state.lastContent.clear();
    }
}

void Count(Counter counter, int64_t amount) {
    if (counter >= 0 && counter < kCounterCount) {
        g_counters[counter].fetch_add(amount, std::memory_order_relaxed);
    }
}

int64_t CounterValue(Counter counter) {
    return counter >= 0 && counter < kCounterCount
               ? g_counters[counter].load(std::memory_order_relaxed)
               : 0;
}

}  // namespace desk::status
