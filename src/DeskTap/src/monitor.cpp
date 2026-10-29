// Live system figures in a taskbar island. Sampling follows the Taskbar
// System Info mod for Windhawk (https://github.com/ramensoftware/windhawk-mods,
// GPL-3.0): only hardware network interfaces, per-interface byte deltas, and
// GetSystemTimes for the CPU load.
#include "pch.h"
#include <winsock2.h>
#include <ws2ipdef.h>
#include <iphlpapi.h>
#include "monitor.h"
#include "config.h"
#include "log.h"

namespace desk::monitor {
namespace {

using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::System::DispatcherQueuePriority;
using winrt::Windows::System::DispatcherQueueTimer;

constexpr wchar_t kIslandName[] = L"DeskMonitorIsland";
// CPU and memory figures at or above this are shown in the warning color.
constexpr double kWarningPercent = 85.0;

struct Stats {
    std::optional<double> cpu;
    std::optional<double> memory;
    std::optional<double> down;  // Bytes per second.
    std::optional<double> up;
};

uint64_t FileTimeValue(const FILETIME& time) {
    return (static_cast<uint64_t>(time.dwHighDateTime) << 32) |
           time.dwLowDateTime;
}

// One sampler for the process: every taskbar thread asks once a second and
// they share the result.
class Sampler {
public:
    Stats Get() {
        std::lock_guard lock(m_mutex);
        ULONGLONG now = GetTickCount64();
        if (m_sampledAt == 0 || now - m_sampledAt >= 900) {
            m_stats = {};
            SampleCpu();
            SampleMemory();
            SampleNetwork(now);
            m_sampledAt = now;
        }
        return m_stats;
    }

private:
    void SampleCpu() {
        FILETIME idleTime{};
        FILETIME kernelTime{};
        FILETIME userTime{};
        if (!GetSystemTimes(&idleTime, &kernelTime, &userTime)) {
            return;
        }
        uint64_t idle = FileTimeValue(idleTime);
        uint64_t kernel = FileTimeValue(kernelTime);  // Includes idle time.
        uint64_t user = FileTimeValue(userTime);
        if (m_cpuReady) {
            uint64_t idleDelta = idle - m_idle;
            uint64_t total = (kernel - m_kernel) + (user - m_user);
            if (total > 0 && idleDelta <= total) {
                m_stats.cpu = 100.0 * static_cast<double>(total - idleDelta) /
                              static_cast<double>(total);
            }
        }
        m_idle = idle;
        m_kernel = kernel;
        m_user = user;
        m_cpuReady = true;
    }

    void SampleMemory() {
        MEMORYSTATUSEX memory{};
        memory.dwLength = sizeof(memory);
        if (GlobalMemoryStatusEx(&memory)) {
            m_stats.memory = static_cast<double>(memory.dwMemoryLoad);
        }
    }

    void SampleNetwork(ULONGLONG now) {
        MIB_IF_TABLE2* table = nullptr;
        if (GetIfTable2(&table) != NO_ERROR || !table) {
            m_interfaces.clear();
            m_networkAt = 0;
            return;
        }

        std::unordered_map<uint64_t, std::pair<uint64_t, uint64_t>> current;
        double received = 0;
        double sent = 0;
        for (ULONG i = 0; i < table->NumEntries; i++) {
            const MIB_IF_ROW2& row = table->Table[i];
            // Virtual adapters and filter drivers repeat the same traffic.
            if (!row.InterfaceAndOperStatusFlags.HardwareInterface ||
                row.InterfaceAndOperStatusFlags.FilterInterface ||
                row.OperStatus != IfOperStatusUp ||
                row.Type == IF_TYPE_SOFTWARE_LOOPBACK) {
                continue;
            }
            current[row.InterfaceLuid.Value] = {row.InOctets, row.OutOctets};
            auto previous = m_interfaces.find(row.InterfaceLuid.Value);
            // A new or reset adapter only sets a baseline.
            if (previous != m_interfaces.end() &&
                row.InOctets >= previous->second.first &&
                row.OutOctets >= previous->second.second) {
                received += static_cast<double>(row.InOctets - previous->second.first);
                sent += static_cast<double>(row.OutOctets - previous->second.second);
            }
        }
        FreeMibTable(table);

        if (m_networkAt != 0 && now > m_networkAt) {
            double seconds = static_cast<double>(now - m_networkAt) / 1000.0;
            m_stats.down = received / seconds;
            m_stats.up = sent / seconds;
        }
        m_interfaces = std::move(current);
        m_networkAt = now;
    }

    std::mutex m_mutex;
    ULONGLONG m_sampledAt = 0;
    Stats m_stats;
    bool m_cpuReady = false;
    uint64_t m_idle = 0;
    uint64_t m_kernel = 0;
    uint64_t m_user = 0;
    std::unordered_map<uint64_t, std::pair<uint64_t, uint64_t>> m_interfaces;
    ULONGLONG m_networkAt = 0;
};

Sampler g_sampler;

std::wstring FormatSpeed(std::optional<double> bytesPerSecond) {
    if (!bytesPerSecond) {
        return L"--";
    }
    static constexpr const wchar_t* kUnits[] = {L"B/s", L"KB/s", L"MB/s", L"GB/s"};
    double value = std::max(*bytesPerSecond, 0.0);
    int unit = 0;
    while (value >= 1000.0 && unit < 3) {
        value /= 1024.0;
        unit++;
    }
    wchar_t text[32];
    if (unit == 0 || value >= 100.0) {
        swprintf_s(text, L"%.0f %s", value, kUnits[unit]);
    } else if (value >= 10.0) {
        swprintf_s(text, L"%.1f %s", value, kUnits[unit]);
    } else {
        swprintf_s(text, L"%.2f %s", value, kUnits[unit]);
    }
    return text;
}

std::wstring FormatPercent(std::optional<double> percent) {
    if (!percent) {
        return L"--";
    }
    wchar_t text[16];
    swprintf_s(text, L"%.0f%%", std::clamp(*percent, 0.0, 100.0));
    return text;
}

struct Island {
    winrt::weak_ref<wux::FrameworkElement> frame;
    winrt::weak_ref<wuxc::Panel> host;
    wuxc::Border border{nullptr};
    wuxc::StackPanel network{nullptr};
    wuxc::StackPanel system{nullptr};
    wuxc::StackPanel cpuRow{nullptr};
    wuxc::StackPanel memoryRow{nullptr};
    wuxc::TextBlock cpuLabel{nullptr};
    wuxc::TextBlock memoryLabel{nullptr};
    wuxc::TextBlock upValue{nullptr};
    wuxc::TextBlock downValue{nullptr};
    wuxc::TextBlock cpuValue{nullptr};
    wuxc::TextBlock memoryValue{nullptr};
    std::vector<wuxc::TextBlock> secondaryTexts;
    std::vector<wuxc::TextBlock> primaryTexts;
};

struct ThreadState {
    std::vector<winrt::weak_ref<wux::FrameworkElement>> frames;
    std::vector<Island> islands;
    DispatcherQueueTimer timer{nullptr};
    MonitorConfig config;
    bool enabled = false;
    wuxm::SolidColorBrush text{nullptr};
    wuxm::SolidColorBrush secondaryText{nullptr};
    wuxm::SolidColorBrush warning{nullptr};
};

thread_local ThreadState* t_state = nullptr;

ThreadState& State() {
    if (!t_state) {
        t_state = new ThreadState();
    }
    return *t_state;
}

wuxc::TextBlock MakeText(double minWidth, bool alignRight) {
    wuxc::TextBlock text;
    text.FontSize(11);
    text.LineHeight(14);
    text.LineStackingStrategy(wux::LineStackingStrategy::BlockLineHeight);
    text.VerticalAlignment(wux::VerticalAlignment::Center);
    text.IsHitTestVisible(false);
    if (minWidth > 0) {
        text.MinWidth(minWidth);
    }
    if (alignRight) {
        text.TextAlignment(wux::TextAlignment::Right);
    }
    return text;
}

// An arrow glyph from the system icon font.
wuxc::TextBlock MakeArrow(const wchar_t* glyph) {
    auto text = MakeText(0, false);
    text.FontFamily(wuxm::FontFamily(L"Segoe Fluent Icons, Segoe MDL2 Assets"));
    text.FontSize(9);
    text.Text(glyph);
    text.Margin({0, 0, 4, 0});
    return text;
}

wuxc::StackPanel MakeRow(wuxc::TextBlock const& label, wuxc::TextBlock const& value) {
    wuxc::StackPanel row;
    row.Orientation(wuxc::Orientation::Horizontal);
    row.VerticalAlignment(wux::VerticalAlignment::Center);
    row.Children().Append(label);
    row.Children().Append(value);
    return row;
}

wuxc::StackPanel MakeColumn() {
    wuxc::StackPanel column;
    column.Orientation(wuxc::Orientation::Vertical);
    column.VerticalAlignment(wux::VerticalAlignment::Center);
    return column;
}

Island Create() {
    Island island;

    island.upValue = MakeText(60, true);
    island.downValue = MakeText(60, true);
    auto up = MakeArrow(L"");
    auto down = MakeArrow(L"");
    island.network = MakeColumn();
    island.network.Children().Append(MakeRow(up, island.upValue));
    island.network.Children().Append(MakeRow(down, island.downValue));

    island.cpuLabel = MakeText(28, false);
    island.memoryLabel = MakeText(28, false);
    island.cpuValue = MakeText(30, true);
    island.memoryValue = MakeText(30, true);
    island.cpuRow = MakeRow(island.cpuLabel, island.cpuValue);
    island.memoryRow = MakeRow(island.memoryLabel, island.memoryValue);
    island.system = MakeColumn();
    island.system.Children().Append(island.cpuRow);
    island.system.Children().Append(island.memoryRow);

    wuxc::StackPanel content;
    content.Orientation(wuxc::Orientation::Horizontal);
    content.VerticalAlignment(wux::VerticalAlignment::Center);
    content.Spacing(14);
    content.Children().Append(island.network);
    content.Children().Append(island.system);

    island.border = wuxc::Border();
    island.border.Name(kIslandName);
    island.border.HorizontalAlignment(wux::HorizontalAlignment::Left);
    island.border.VerticalAlignment(wux::VerticalAlignment::Stretch);
    // The theme normally sets these together with the material.
    island.border.Margin({8, 0, 0, 0});
    island.border.Padding({10, 0, 12, 0});
    island.border.IsHitTestVisible(false);
    island.border.Child(content);
    wuxc::Canvas::SetZIndex(island.border, 10);

    island.secondaryTexts = {up, down, island.cpuLabel, island.memoryLabel};
    island.primaryTexts = {island.upValue, island.downValue, island.cpuValue,
                           island.memoryValue};
    return island;
}

void Configure(Island& island, const ThreadState& state) {
    const auto& config = state.config;
    island.network.Visibility(config.network ? wux::Visibility::Visible
                                             : wux::Visibility::Collapsed);
    island.cpuRow.Visibility(config.cpu ? wux::Visibility::Visible
                                        : wux::Visibility::Collapsed);
    island.memoryRow.Visibility(config.memory ? wux::Visibility::Visible
                                              : wux::Visibility::Collapsed);
    island.system.Visibility(config.cpu || config.memory
                                 ? wux::Visibility::Visible
                                 : wux::Visibility::Collapsed);
    island.cpuLabel.Text(config.cpuLabel);
    island.memoryLabel.Text(config.memoryLabel);
    for (auto& text : island.secondaryTexts) {
        text.Foreground(state.secondaryText);
    }
    for (auto& text : island.primaryTexts) {
        text.Foreground(state.text);
    }
}

void Show(Island& island, const ThreadState& state, const Stats& stats) {
    island.upValue.Text(FormatSpeed(stats.up));
    island.downValue.Text(FormatSpeed(stats.down));
    island.cpuValue.Text(FormatPercent(stats.cpu));
    island.memoryValue.Text(FormatPercent(stats.memory));

    auto load = [&state](std::optional<double> percent) {
        return percent && *percent >= kWarningPercent ? state.warning : state.text;
    };
    island.cpuValue.Foreground(load(stats.cpu));
    island.memoryValue.Foreground(load(stats.memory));
}

void Remove(Island& island) {
    auto host = island.host.get();
    if (!host || !island.border) {
        return;
    }
    uint32_t index = 0;
    if (host.Children().IndexOf(island.border, index)) {
        host.Children().RemoveAt(index);
    }
}

void Tick(ThreadState& state) {
    auto stats = g_sampler.Get();
    for (auto& island : state.islands) {
        try {
            Show(island, state, stats);
        } catch (...) {
        }
    }
}

void StartTimer(ThreadState& state) {
    if (state.timer) {
        if (!state.timer.IsRunning()) {
            state.timer.Start();
        }
        return;
    }
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        return;
    }
    state.timer = queue.CreateTimer();
    state.timer.Interval(std::chrono::seconds(1));
    state.timer.IsRepeating(true);
    state.timer.Tick([](auto&&, auto&&) {
        try {
            if (t_state) {
                Tick(*t_state);
            }
        } catch (...) {
        }
    });
    state.timer.Start();
}

void RemoveAll(ThreadState& state) {
    for (auto& island : state.islands) {
        try {
            Remove(island);
        } catch (...) {
        }
    }
    if (!state.islands.empty()) {
        DLOG(L"Removed %zu monitor islands", state.islands.size());
    }
    state.islands.clear();
    if (state.timer) {
        state.timer.Stop();
    }
}

void Ensure(ThreadState& state, wux::FrameworkElement const& frame) {
    auto host = wuxm::VisualTreeHelper::GetParent(frame).try_as<wuxc::Panel>();
    if (!host) {
        DLOG(L"Monitor: the taskbar frame has no panel parent");
        return;
    }

    for (auto it = state.islands.begin(); it != state.islands.end();) {
        auto islandFrame = it->frame.get();
        if (!islandFrame) {
            it = state.islands.erase(it);
            continue;
        }
        if (islandFrame == frame) {
            uint32_t index = 0;
            if (it->host.get() == host && host.Children().IndexOf(it->border, index)) {
                Configure(*it, state);
                return;
            }
            // The frame moved to another parent: start over.
            Remove(*it);
            it = state.islands.erase(it);
            continue;
        }
        ++it;
    }

    auto children = host.Children();
    for (uint32_t i = 0; i < children.Size();) {
        auto element = children.GetAt(i).try_as<wux::FrameworkElement>();
        if (element && element.Name() == kIslandName) {
            children.RemoveAt(i);
        } else {
            i++;
        }
    }

    auto island = Create();
    island.frame = winrt::make_weak(frame);
    island.host = winrt::make_weak(host);
    if (auto grid = host.try_as<wuxc::Grid>()) {
        auto columns = static_cast<int32_t>(grid.ColumnDefinitions().Size());
        auto rows = static_cast<int32_t>(grid.RowDefinitions().Size());
        wuxc::Grid::SetColumnSpan(island.border, std::max(1, columns));
        wuxc::Grid::SetRowSpan(island.border, std::max(1, rows));
    }
    Configure(island, state);
    Show(island, state, g_sampler.Get());
    children.Append(island.border);
    DLOG(L"Monitor island added to %s", winrt::get_class_name(host).c_str());

    state.islands.push_back(std::move(island));
    StartTimer(state);
}

void Refresh(ThreadState& state, const Config& config) {
    state.enabled = config.enabled && config.monitor.enabled;
    state.config = config.monitor;
    state.text = wuxm::SolidColorBrush(state.config.text);
    state.secondaryText = wuxm::SolidColorBrush(state.config.secondaryText);
    state.warning = wuxm::SolidColorBrush(state.config.warning);

    state.frames.erase(std::remove_if(state.frames.begin(), state.frames.end(),
                                      [](const auto& weak) { return !weak.get(); }),
                       state.frames.end());

    if (!state.enabled) {
        RemoveAll(state);
        return;
    }
    for (const auto& weak : state.frames) {
        if (auto frame = weak.get()) {
            try {
                Ensure(state, frame);
            } catch (...) {
                DLOG(L"Monitor island failed: %08X", winrt::to_hresult().value);
            }
        }
    }
}

}  // namespace

void OnTaskbarFrameAdded(wux::FrameworkElement const& frame) {
    auto& state = State();
    state.frames.push_back(winrt::make_weak(frame));

    // The island goes in once the frame is settled in its parent.
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        return;
    }
    queue.TryEnqueue(DispatcherQueuePriority::Low, [] {
        try {
            if (t_state) {
                Refresh(*t_state, *config::Current());
            }
        } catch (...) {
            DLOG(L"Monitor refresh failed: %08X", winrt::to_hresult().value);
        }
    });
}

void OnConfigApplied(const Config& config) {
    try {
        Refresh(State(), config);
    } catch (...) {
        DLOG(L"Monitor refresh failed: %08X", winrt::to_hresult().value);
    }
}

}  // namespace desk::monitor
