// Unit tests for the engine's Windows-independent decisions (src/core).
// Built by CMake as DeskTapCoreTests (MSVC on the build machine) and with any
// C++20 compiler elsewhere:
//   g++ -std=c++20 -I src/DeskTap/src src/DeskTap/tests/core_tests.cpp -o core_tests
#include <cstdio>
#include <functional>
#include <string>
#include <vector>

#include "core/apply_status.h"
#include "core/clickthrough_gate.h"
#include "core/host_watch.h"
#include "core/init_policy.h"
#include "core/sampler_core.h"

namespace {

struct TestCase {
    const char* name;
    std::function<void()> body;
};

std::vector<TestCase>& Tests() {
    static std::vector<TestCase> tests;
    return tests;
}

int g_failures = 0;

struct Register {
    Register(const char* name, std::function<void()> body) {
        Tests().push_back({name, std::move(body)});
    }
};

#define TEST(name)                                     \
    void name();                                       \
    const Register register_##name(#name, name);       \
    void name()

#define CHECK(condition)                                                    \
    do {                                                                    \
        if (!(condition)) {                                                 \
            std::printf("    %s:%d: CHECK(%s) failed\n", __FILE__, __LINE__, \
                        #condition);                                        \
            g_failures++;                                                   \
        }                                                                   \
    } while (false)

using desk::core::ApplyResult;
using desk::core::ClickThroughGate;
using desk::core::HostWatch;
using desk::core::InitPolicy;
using desk::core::Summarize;
using desk::core::ThreadReport;

// ----------------------------------------------------------- InitPolicy

TEST(InitWaitsUntilStylingIsWanted) {
    InitPolicy policy;
    CHECK(!policy.Due(0, false));
    CHECK(policy.Due(0, true));
}

TEST(InitRunsOneAttemptAtATime) {
    InitPolicy policy;
    CHECK(policy.OnStarted());
    CHECK(!policy.OnStarted());
    CHECK(!policy.Due(0, true));
    policy.OnSucceeded();
    CHECK(!policy.OnStarted());
    CHECK(!policy.Due(1'000'000, true));
    CHECK(policy.attempts() == 1);
}

TEST(InitBacksOffAfterFailures) {
    InitPolicy policy;
    uint64_t now = 0;
    std::vector<uint64_t> delays;
    for (int i = 0; i < 7; i++) {
        CHECK(policy.Due(now, true));
        CHECK(policy.OnStarted());
        policy.OnFailed(now);
        delays.push_back(policy.nextAttemptMs() - now);
        CHECK(!policy.Due(policy.nextAttemptMs() - 1, true));
        now = policy.nextAttemptMs();
    }
    std::vector<uint64_t> expected = {15'000, 30'000, 60'000, 120'000,
                                      240'000, 300'000, 300'000};
    CHECK(delays == expected);
}

TEST(InitRecoverRequestRetriesAFailureNow) {
    InitPolicy policy;
    CHECK(!policy.OnRecoverRequest(7, 0));  // Baseline.
    policy.OnStarted();
    policy.OnFailed(1000);
    CHECK(!policy.Due(2000, true));
    CHECK(!policy.OnRecoverRequest(7, 2000));  // Same token: nothing new.
    CHECK(!policy.Due(2000, true));
    CHECK(policy.OnRecoverRequest(8, 2000));
    CHECK(policy.Due(2000, true));
    // Not while it runs, nor once it worked.
    policy.OnStarted();
    CHECK(!policy.OnRecoverRequest(9, 3000));
    policy.OnSucceeded();
    CHECK(!policy.OnRecoverRequest(10, 4000));
    CHECK(!policy.Due(4000, true));
}

// ------------------------------------------------------------ HostWatch

constexpr uint64_t kGrace = HostWatch::kGraceMs;

TEST(HostWithoutSessionIsNeverLost) {
    HostWatch watch;
    watch.Attach(L"", 0);
    CHECK(watch.Observe(HostWatch::Probe::Gone, false, 0, 1'000'000) ==
          HostWatch::Verdict::Unowned);
}

TEST(HostProcessGoneIsLostAtOnceAndForGood) {
    HostWatch watch;
    watch.Attach(L"a", 0);
    CHECK(watch.Observe(HostWatch::Probe::Alive, false, 0, 10) ==
          HostWatch::Verdict::Attached);
    CHECK(watch.Observe(HostWatch::Probe::Gone, false, 0, 20) ==
          HostWatch::Verdict::Lost);
    CHECK(watch.reason() == HostWatch::Reason::ProcessGone);
    // A late heartbeat from the same session doesn't bring it back.
    CHECK(watch.Observe(HostWatch::Probe::Unknown, true, 5, 30) ==
          HostWatch::Verdict::Lost);
}

TEST(HostAliveButSilentIsKept) {
    HostWatch watch;
    watch.Attach(L"a", 0);
    watch.Observe(HostWatch::Probe::Unknown, true, 1, 0);
    CHECK(watch.Observe(HostWatch::Probe::Alive, false, 0, 10 * kGrace) ==
          HostWatch::Verdict::Attached);
}

TEST(HostUnprovenHeartbeatNeverDeclaresLoss) {
    // No access to the process and the heartbeat never readable: can't tell,
    // so the styling stays.
    HostWatch watch;
    watch.Attach(L"a", 0);
    for (uint64_t t = 0; t < 10 * kGrace; t += 1000) {
        CHECK(watch.Observe(HostWatch::Probe::Unknown, false, 0, t) ==
              HostWatch::Verdict::Attached);
    }
}

TEST(HostHeartbeatTimeoutAfterGrace) {
    HostWatch watch;
    watch.Attach(L"a", 0);
    watch.Observe(HostWatch::Probe::Unknown, true, 1, 0);
    watch.Observe(HostWatch::Probe::Unknown, true, 2, 5000);
    // Read but not advancing (stuck), or unreadable: only the grace counts.
    CHECK(watch.Observe(HostWatch::Probe::Unknown, true, 2, 5000 + kGrace) ==
          HostWatch::Verdict::Attached);
    CHECK(watch.Observe(HostWatch::Probe::Unknown, false, 0,
                        5000 + kGrace + 1) == HostWatch::Verdict::Lost);
    CHECK(watch.reason() == HostWatch::Reason::NoHeartbeat);
}

TEST(HostSleepDoesNotCount) {
    // The caller passes time the system was awake: a long sleep between two
    // checks is a small step, and the next heartbeat arrives in time.
    HostWatch watch;
    watch.Attach(L"a", 0);
    watch.Observe(HostWatch::Probe::Unknown, true, 1, 1000);
    CHECK(watch.Observe(HostWatch::Probe::Unknown, false, 0, 3000) ==
          HostWatch::Verdict::Attached);
    CHECK(watch.Observe(HostWatch::Probe::Unknown, true, 2, 8000) ==
          HostWatch::Verdict::Attached);
}

TEST(HostHeartbeatResumingReattaches) {
    HostWatch watch;
    watch.Attach(L"a", 0);
    watch.Observe(HostWatch::Probe::Unknown, true, 1, 0);
    CHECK(watch.Observe(HostWatch::Probe::Unknown, false, 0, kGrace + 1) ==
          HostWatch::Verdict::Lost);
    CHECK(watch.Observe(HostWatch::Probe::Unknown, true, 1, kGrace + 2) ==
          HostWatch::Verdict::Lost);  // Same beat as before: still silent.
    CHECK(watch.Observe(HostWatch::Probe::Unknown, true, 2, kGrace + 3) ==
          HostWatch::Verdict::Attached);
}

TEST(HostNewSessionStartsOver) {
    HostWatch watch;
    watch.Attach(L"a", 0);
    watch.Observe(HostWatch::Probe::Gone, false, 0, 10);
    CHECK(!watch.Attach(L"a", 20));  // The same owner again: no change.
    CHECK(watch.verdict() == HostWatch::Verdict::Lost);
    CHECK(watch.Attach(L"b", 30));
    CHECK(watch.verdict() == HostWatch::Verdict::Attached);
    CHECK(watch.Observe(HostWatch::Probe::Alive, false, 0, 40) ==
          HostWatch::Verdict::Attached);
    CHECK(!watch.heartbeatSeen());
}

// ---------------------------------------------------------- ApplyStatus

TEST(ApplyWithoutUiThreads) {
    std::map<uint32_t, ThreadReport> threads;
    auto on = Summarize(threads, 3, 300, true);
    CHECK(on.result == ApplyResult::NoUi);
    CHECK(on.appliedSerial == 0);
    auto off = Summarize(threads, 3, 300, false);
    CHECK(off.result == ApplyResult::Disabled);
    CHECK(off.appliedSerial == 300);
}

TEST(ApplyPendingUntilEveryThreadIsDone) {
    std::map<uint32_t, ThreadReport> threads;
    threads[1] = {3, 300, 10, 0};
    threads[2] = {2, 200, 4, 0};
    auto summary = Summarize(threads, 3, 300, true);
    CHECK(summary.result == ApplyResult::Pending);
    CHECK(summary.appliedSerial == 0);
    threads[2] = {3, 300, 4, 1};
    summary = Summarize(threads, 3, 300, true);
    CHECK(summary.result == ApplyResult::Applied);
    CHECK(summary.appliedSerial == 300);
    CHECK(summary.matched == 14);
    CHECK(summary.errors == 1);
}

TEST(ApplyWithNothingMatchedIsNotSuccess) {
    std::map<uint32_t, ThreadReport> threads;
    threads[1] = {3, 300, 0, 0};
    auto summary = Summarize(threads, 3, 300, true);
    CHECK(summary.result == ApplyResult::NoMatch);
    CHECK(summary.appliedSerial == 300);
}

TEST(ApplyRestoreForTheSameSerialIsANewRevision) {
    // Losing the host disables the configuration without a new serial; the
    // restore counts only once every thread ran it.
    std::map<uint32_t, ThreadReport> threads;
    threads[1] = {3, 300, 10, 0};
    auto summary = Summarize(threads, 4, 300, false);
    CHECK(summary.result == ApplyResult::Pending);
    threads[1] = {4, 300, 0, 0};
    summary = Summarize(threads, 4, 300, false);
    CHECK(summary.result == ApplyResult::Disabled);
}

// ------------------------------------------------------ ClickThroughGate

TEST(ClickThroughOffPublishedBeforeAnElementArrives) {
    // On and applied; then "off" is published; a new taskbar frame arrives
    // before the thread re-applies; then the re-apply runs.
    ClickThroughGate gate;
    CHECK(gate.OnConfigApplied(true) == ClickThroughGate::Action::Apply);
    CHECK(gate.EnabledForNewElement(false));  // Follows the applied state.
    CHECK(gate.OnConfigApplied(false) == ClickThroughGate::Action::Clear);
    CHECK(!gate.Enabled());
}

TEST(ClickThroughOnPublishedBeforeAnElementArrives) {
    ClickThroughGate gate;
    gate.OnConfigApplied(false);
    CHECK(!gate.EnabledForNewElement(true));
    CHECK(gate.OnConfigApplied(true) == ClickThroughGate::Action::Apply);
}

TEST(ClickThroughOffIsIdempotent) {
    ClickThroughGate gate;
    gate.OnConfigApplied(true);
    CHECK(gate.OnConfigApplied(false) == ClickThroughGate::Action::Clear);
    CHECK(gate.OnConfigApplied(false) == ClickThroughGate::Action::Clear);
    CHECK(gate.OnConfigApplied(true) == ClickThroughGate::Action::Apply);
}

TEST(ClickThroughNewThreadUsesTheConfiguration) {
    ClickThroughGate gate;
    CHECK(gate.EnabledForNewElement(true));
    CHECK(!gate.EnabledForNewElement(false));
    CHECK(!gate.Enabled());
}

// ----------------------------------------------------------- SamplerCore

struct FakeSource : desk::core::SystemSource {
    int cpuReads = 0;
    int memoryReads = 0;
    int networkReads = 0;
    desk::core::CpuTimes cpu;
    double memory = 40;
    std::vector<desk::core::InterfaceCounters> interfaces = {{1, 0, 0}};

    std::optional<desk::core::CpuTimes> Cpu() override {
        cpuReads++;
        return cpu;
    }
    std::optional<double> MemoryLoad() override {
        memoryReads++;
        return memory;
    }
    std::optional<std::vector<desk::core::InterfaceCounters>> Network() override {
        networkReads++;
        return interfaces;
    }
};

using desk::core::SamplerCore;

TEST(SamplerOnlyReadsWhatIsShown) {
    FakeSource source;
    SamplerCore sampler(source);
    for (uint64_t t = 1000; t <= 10'000; t += 1000) {
        auto stats = sampler.Sample(desk::core::kMemory, t);
        CHECK(stats.memory == 40.0);
        CHECK(!stats.cpu && !stats.down);
    }
    CHECK(source.memoryReads == 10);
    CHECK(source.cpuReads == 0);
    CHECK(source.networkReads == 0);
}

TEST(SamplerRatesNeedTwoSamples) {
    FakeSource source;
    SamplerCore sampler(source);
    uint8_t all = desk::core::kCpu | desk::core::kMemory | desk::core::kNetwork;
    source.cpu = {100, 200, 100};  // 300 total, 100 idle.
    auto first = sampler.Sample(all, 1000);
    CHECK(!first.cpu && !first.down);  // Only a baseline.

    source.cpu = {150, 300, 200};      // +200 total, +50 idle: 75%.
    source.interfaces = {{1, 2048, 1024}};
    auto second = sampler.Sample(all, 2000);
    CHECK(second.cpu && *second.cpu == 75.0);
    CHECK(second.down && *second.down == 2048.0);
    CHECK(second.up && *second.up == 1024.0);
}

TEST(SamplerTurningNetworkOnStartsANewBaseline) {
    FakeSource source;
    SamplerCore sampler(source);
    sampler.Sample(desk::core::kNetwork, 1000);
    source.interfaces = {{1, 1000, 0}};
    CHECK(sampler.Sample(desk::core::kNetwork, 2000).down == 1000.0);

    // Off for a while: the traffic meanwhile must not show up as one spike.
    sampler.Sample(desk::core::kMemory, 3000);
    source.interfaces = {{1, 900'000'000, 0}};
    CHECK(!sampler.Sample(desk::core::kNetwork, 4000).down);
    source.interfaces = {{1, 900'000'500, 0}};
    CHECK(sampler.Sample(desk::core::kNetwork, 5000).down == 500.0);
}

TEST(SamplerAGapStartsANewBaseline) {
    // Sleep or a stalled thread: no huge or tiny average over the gap.
    FakeSource source;
    SamplerCore sampler(source);
    uint8_t needs = desk::core::kCpu | desk::core::kNetwork;
    source.cpu = {0, 100, 0};
    sampler.Sample(needs, 1000);
    source.cpu = {1000, 2000, 500};
    source.interfaces = {{1, 50'000'000, 0}};
    auto afterGap = sampler.Sample(needs, 1000 + SamplerCore::kMaxGapMs + 1);
    CHECK(!afterGap.cpu && !afterGap.down);
}

TEST(SamplerAdapterChanges) {
    FakeSource source;
    SamplerCore sampler(source);
    source.interfaces = {{1, 1000, 1000}};
    sampler.Sample(desk::core::kNetwork, 1000);
    // Adapter 1 reset its counters, adapter 2 is new: neither counts yet.
    source.interfaces = {{1, 10, 10}, {2, 5000, 5000}};
    auto stats = sampler.Sample(desk::core::kNetwork, 2000);
    CHECK(stats.down == 0.0);
    source.interfaces = {{1, 110, 10}, {2, 5100, 5000}};
    CHECK(sampler.Sample(desk::core::kNetwork, 3000).down == 200.0);
}

}  // namespace

int main() {
    int failedTests = 0;
    for (const auto& test : Tests()) {
        int before = g_failures;
        test.body();
        bool ok = g_failures == before;
        failedTests += ok ? 0 : 1;
        std::printf("%s %s\n", ok ? "PASS" : "FAIL", test.name);
    }
    std::printf("%zu tests, %d failed\n", Tests().size(), failedTests);
    return failedTests == 0 ? 0 : 1;
}
