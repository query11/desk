// Decisions without Windows dependencies, unit-tested in tests/core_tests.cpp.
#pragma once
#include <cstdint>
#include <optional>
#include <unordered_map>
#include <vector>

namespace desk::core {

// What the system figures island shows; each one is only sampled while some
// taskbar shows it.
enum Metric : uint8_t {
    kCpu = 1,
    kMemory = 2,
    kNetwork = 4,
};

struct Stats {
    std::optional<double> cpu;     // Percent.
    std::optional<double> memory;  // Percent.
    std::optional<double> down;    // Bytes per second.
    std::optional<double> up;
};

struct CpuTimes {
    uint64_t idle = 0;
    uint64_t kernel = 0;  // Includes idle time, as GetSystemTimes reports it.
    uint64_t user = 0;
};

struct InterfaceCounters {
    uint64_t id = 0;
    uint64_t received = 0;
    uint64_t sent = 0;
};

// Where the numbers come from (GetSystemTimes, GlobalMemoryStatusEx,
// GetIfTable2 on Windows).
class SystemSource {
public:
    virtual ~SystemSource() = default;
    virtual std::optional<CpuTimes> Cpu() = 0;
    virtual std::optional<double> MemoryLoad() = 0;
    virtual std::optional<std::vector<InterfaceCounters>> Network() = 0;
};

// Turns raw counters into figures. CPU and network are differences between
// two samples, so their baseline starts over whenever they weren't sampled
// last time (just turned on), after a gap (sleep, a stalled thread) and for
// an adapter that appeared or whose counters went back.
class SamplerCore {
public:
    static constexpr uint64_t kMaxGapMs = 5000;

    explicit SamplerCore(SystemSource& source) : m_source(source) {}

    Stats Sample(uint8_t needs, uint64_t nowMs) {
        Stats stats;
        bool gap = m_lastMs != 0 && nowMs - m_lastMs > kMaxGapMs;
        m_lastMs = nowMs;

        if (!(needs & kCpu) || gap) {
            m_cpu.reset();
        }
        if (!(needs & kNetwork) || gap) {
            m_interfaces.clear();
            m_networkMs = 0;
        }

        if (needs & kCpu) {
            SampleCpu(stats);
        }
        if (needs & kMemory) {
            stats.memory = m_source.MemoryLoad();
        }
        if (needs & kNetwork) {
            SampleNetwork(stats, nowMs);
        }
        return stats;
    }

private:
    void SampleCpu(Stats& stats) {
        auto times = m_source.Cpu();
        if (!times) {
            m_cpu.reset();
            return;
        }
        if (m_cpu && times->idle >= m_cpu->idle && times->kernel >= m_cpu->kernel &&
            times->user >= m_cpu->user) {
            uint64_t idle = times->idle - m_cpu->idle;
            uint64_t total = (times->kernel - m_cpu->kernel) + (times->user - m_cpu->user);
            if (total > 0 && idle <= total) {
                stats.cpu = 100.0 * static_cast<double>(total - idle) /
                            static_cast<double>(total);
            }
        }
        m_cpu = times;
    }

    void SampleNetwork(Stats& stats, uint64_t nowMs) {
        auto table = m_source.Network();
        if (!table) {
            m_interfaces.clear();
            m_networkMs = 0;
            return;
        }
        std::unordered_map<uint64_t, InterfaceCounters> current;
        double received = 0;
        double sent = 0;
        for (const auto& row : *table) {
            current[row.id] = row;
            auto previous = m_interfaces.find(row.id);
            // A new or reset adapter only sets a baseline.
            if (previous != m_interfaces.end() && row.received >= previous->second.received &&
                row.sent >= previous->second.sent) {
                received += static_cast<double>(row.received - previous->second.received);
                sent += static_cast<double>(row.sent - previous->second.sent);
            }
        }
        if (m_networkMs != 0 && nowMs > m_networkMs) {
            double seconds = static_cast<double>(nowMs - m_networkMs) / 1000.0;
            stats.down = received / seconds;
            stats.up = sent / seconds;
        }
        m_interfaces = std::move(current);
        m_networkMs = nowMs;
    }

    SystemSource& m_source;
    uint64_t m_lastMs = 0;
    std::optional<CpuTimes> m_cpu;
    std::unordered_map<uint64_t, InterfaceCounters> m_interfaces;
    uint64_t m_networkMs = 0;
};

}  // namespace desk::core
