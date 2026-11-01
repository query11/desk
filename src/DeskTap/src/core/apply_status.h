// Decisions without Windows dependencies, unit-tested in tests/core_tests.cpp.
#pragma once
#include <cstdint>
#include <map>

namespace desk::core {

// What one XAML UI thread last reported after applying a configuration.
struct ThreadReport {
    uint64_t revision = 0;  // The engine's effective configuration revision.
    uint64_t serial = 0;    // The serial Desk.exe gave that configuration.
    uint32_t matched = 0;   // Elements with at least one property set.
    uint32_t errors = 0;    // Styles that failed on this thread.
};

enum class ApplyResult {
    // The current configuration turns styling off and every UI thread
    // restored its elements (or there are none).
    Disabled,
    // Styling is on, but no XAML UI thread showed up yet (the Start menu
    // before it was opened).
    NoUi,
    // Some UI thread hasn't applied the current configuration yet.
    Pending,
    // Applied everywhere, but no rule matched any element.
    NoMatch,
    // Applied everywhere and at least one element is styled.
    Applied,
};

struct ApplySummary {
    ApplyResult result = ApplyResult::NoUi;
    // The serial of the configuration that is fully processed, 0 if none.
    uint64_t appliedSerial = 0;
    uint32_t threads = 0;
    uint32_t matched = 0;
    uint32_t errors = 0;
};

// "Applied" means: every live UI thread processed the current revision and
// something matched. Receiving a configuration is not enough.
inline ApplySummary Summarize(const std::map<uint32_t, ThreadReport>& threads,
                              uint64_t revision,
                              uint64_t serial,
                              bool enabled) {
    ApplySummary summary;
    summary.threads = static_cast<uint32_t>(threads.size());
    bool pending = false;
    for (const auto& [id, report] : threads) {
        if (report.revision != revision) {
            pending = true;
            continue;
        }
        summary.matched += report.matched;
        summary.errors += report.errors;
    }

    if (pending) {
        summary.result = ApplyResult::Pending;
        return summary;
    }
    if (!enabled) {
        summary.result = ApplyResult::Disabled;
        summary.appliedSerial = serial;
        return summary;
    }
    if (threads.empty()) {
        summary.result = ApplyResult::NoUi;
        return summary;
    }
    summary.appliedSerial = serial;
    summary.result =
        summary.matched > 0 ? ApplyResult::Applied : ApplyResult::NoMatch;
    return summary;
}

}  // namespace desk::core
