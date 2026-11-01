#pragma once
#include "core/apply_status.h"

namespace desk::status {

// The engine's report to Desk.exe: <base>\state\<process>.<pid>.json,
// replaced atomically. It names this process instance (id and creation
// time), the engine build and instance, how far initialization got, the
// configuration serial last seen and the one fully applied, and diagnostic
// counters. Desk.exe only shows "active" when this says so for the process
// and configuration it expects.

enum class Engine {
    Loaded,        // DLL loaded; waiting for a configuration that turns styling on.
    Initializing,  // Setting up XAML diagnostics.
    Ready,         // Watching the XAML tree.
    Failed,        // Initialization failed; retried automatically.
    Blocked,       // Another DeskTap build owns this process.
};

enum class Host {
    None,      // The configuration names no Desk.exe (older Desk, restore).
    Attached,  // The owner is there.
    Lost,      // The owner is gone; everything was restored.
};

void Init();

void SetEngine(Engine state);
void SetInitFailure(const wchar_t* stage, HRESULT hr, uint32_t attempts,
                    uint64_t retryInMs);
void SetInitAttempts(uint32_t attempts);
void SetConfig(uint64_t serial, uint64_t revision, bool enabled);
void SetHost(Host host, const std::wstring& session, const wchar_t* reason);

// Called on a UI thread after it applied a revision (or later, when more
// elements matched).
void ReportThread(DWORD threadId, const core::ThreadReport& report);
// The UI thread is shutting down.
void ForgetThread(DWORD threadId);

// Writes the file if something changed (counters alone at most every 15 s).
// Called from the control thread.
void Flush(bool force = false);

// Diagnostic counters, for leaks and repeated work. Cheap to bump anywhere.
enum Counter : int {
    kUiThreadsSeen,
    kUiThreadsReleased,
    kConfigLoads,
    kStatusWrites,
    kInitAttempts,
    kPropertyCallbacks,
    kPropertyCallbacksReleased,
    kStyleResolves,
    kStyleCacheHits,
    kBlurBrushes,
    kBlurBrushesReleased,
    kBlurInPlaceUpdates,
    kEffectFactories,
    kEffectFactoryHits,
    kNoiseSurfaces,
    kNoiseSurfaceHits,
    kSamplerThreadStarts,
    kSamplerThreadStops,
    kSamplesCpu,
    kSamplesMemory,
    kSamplesNetwork,
    kMonitorPosts,
    kMonitorPostsCoalesced,
    kMonitorTextUpdates,
    kMonitorUiMicros,
    kMonitorIslands,
    kMonitorIslandsReleased,
    kLayoutSubscriptions,
    kLayoutSubscriptionsReleased,
    kSubclasses,
    kSubclassesReleased,
    kRegionsSet,
    kRegionsCleared,
    kDispatcherPostFailures,
    kCounterCount,
};

void Count(Counter counter, int64_t amount = 1);
int64_t CounterValue(Counter counter);

}  // namespace desk::status
