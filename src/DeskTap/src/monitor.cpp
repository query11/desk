// Live system figures in a taskbar island. Sampling follows the Taskbar
// System Info mod for Windhawk (https://github.com/ramensoftware/windhawk-mods,
// GPL-3.0): only hardware network interfaces, per-interface byte deltas, and
// GetSystemTimes for the CPU load.
//
// One sampler thread serves the process: it runs only while some taskbar
// shows figures, reads only the figures shown, and hands each UI thread the
// newest sample (a sample that wasn't picked up yet is replaced, not queued).
// The UI threads only touch text whose value or color changed.
#include "pch.h"
#include <condition_variable>
#include <winsock2.h>
#include <ws2ipdef.h>
#include <iphlpapi.h>
#include "monitor.h"
#include "config.h"
#include "core/sampler_core.h"
#include "log.h"
#include "status.h"

namespace desk::monitor {
namespace {

using core::Stats;
using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::System::DispatcherQueuePriority;

constexpr wchar_t kIslandName[] = L"DeskMonitorIsland";
// CPU and memory figures at or above this are shown in the warning color.
constexpr double kWarningPercent = 85.0;
constexpr DWORD kIntervalMs = 1000;

uint64_t FileTimeValue(const FILETIME& time) {
    return (static_cast<uint64_t>(time.dwHighDateTime) << 32) |
           time.dwLowDateTime;
}

class WindowsSource : public core::SystemSource {
public:
    std::optional<core::CpuTimes> Cpu() override {
        status::Count(status::kSamplesCpu);
        FILETIME idle{}, kernel{}, user{};
        if (!GetSystemTimes(&idle, &kernel, &user)) {
            return std::nullopt;
        }
        return core::CpuTimes{FileTimeValue(idle), FileTimeValue(kernel),
                              FileTimeValue(user)};
    }

    std::optional<double> MemoryLoad() override {
        status::Count(status::kSamplesMemory);
        MEMORYSTATUSEX memory{};
        memory.dwLength = sizeof(memory);
        if (!GlobalMemoryStatusEx(&memory)) {
            return std::nullopt;
        }
        return static_cast<double>(memory.dwMemoryLoad);
    }

    std::optional<std::vector<core::InterfaceCounters>> Network() override {
        status::Count(status::kSamplesNetwork);
        MIB_IF_TABLE2* table = nullptr;
        if (GetIfTable2(&table) != NO_ERROR || !table) {
            return std::nullopt;
        }
        std::vector<core::InterfaceCounters> rows;
        for (ULONG i = 0; i < table->NumEntries; i++) {
            const MIB_IF_ROW2& row = table->Table[i];
            // Virtual adapters and filter drivers repeat the same traffic.
            if (!row.InterfaceAndOperStatusFlags.HardwareInterface ||
                row.InterfaceAndOperStatusFlags.FilterInterface ||
                row.OperStatus != IfOperStatusUp ||
                row.Type == IF_TYPE_SOFTWARE_LOOPBACK) {
                continue;
            }
            rows.push_back({row.InterfaceLuid.Value, row.InOctets, row.OutOctets});
        }
        FreeMibTable(table);
        return rows;
    }
};

// A UI thread that shows figures.
class Subscriber {
public:
    explicit Subscriber(DispatcherQueue queue) : m_queue(std::move(queue)) {}

    void SetNeeds(uint8_t needs) { m_needs = needs; }
    uint8_t Needs() const { return m_needs; }

    Stats Latest() {
        std::lock_guard lock(m_mutex);
        return m_latest;
    }

    // Sampler thread: keeps the newest sample and wakes the UI thread unless
    // it hasn't picked up the previous one yet. False when the dispatcher is
    // gone.
    bool Deliver(const Stats& stats, std::function<void()> const& handler) {
        {
            std::lock_guard lock(m_mutex);
            m_latest = stats;
        }
        if (m_posted.exchange(true)) {
            status::Count(status::kMonitorPostsCoalesced);
            return true;
        }
        bool queued = false;
        try {
            queued = m_queue.TryEnqueue(DispatcherQueuePriority::Low, handler);
        } catch (...) {
        }
        if (!queued) {
            m_posted = false;
            status::Count(status::kDispatcherPostFailures);
            return false;
        }
        status::Count(status::kMonitorPosts);
        return true;
    }

    // UI thread, when the posted handler runs.
    Stats Take() {
        m_posted = false;
        return Latest();
    }

private:
    DispatcherQueue m_queue;
    std::atomic<uint8_t> m_needs{0};
    std::atomic<bool> m_posted{false};
    std::mutex m_mutex;
    Stats m_latest;
};

void OnSample();

// The process-wide sampler. Lives as long as the process (the DLL is pinned);
// its thread exits when the last subscriber leaves and starts again with
// the next one.
class Sampler {
public:
    void Subscribe(const std::shared_ptr<Subscriber>& subscriber) {
        std::lock_guard lock(m_mutex);
        if (std::find(m_subscribers.begin(), m_subscribers.end(), subscriber) ==
            m_subscribers.end()) {
            m_subscribers.push_back(subscriber);
        }
        if (!m_running) {
            m_running = true;
            HANDLE thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
            if (thread) {
                CloseHandle(thread);
                status::Count(status::kSamplerThreadStarts);
            } else {
                m_running = false;
                DLOG(L"Monitor: could not start the sampler: %lu", GetLastError());
            }
        }
        m_changed.notify_all();
    }

    void Unsubscribe(const std::shared_ptr<Subscriber>& subscriber) {
        std::lock_guard lock(m_mutex);
        m_subscribers.erase(
            std::remove(m_subscribers.begin(), m_subscribers.end(), subscriber),
            m_subscribers.end());
        m_changed.notify_all();
    }

    // Needs changed: sample right away instead of at the next interval.
    void Wake() {
        std::lock_guard lock(m_mutex);
        m_wake = true;
        m_changed.notify_all();
    }

private:
    static DWORD WINAPI ThreadProc(LPVOID parameter) {
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
        } catch (...) {
        }
        static_cast<Sampler*>(parameter)->Run();
        status::Count(status::kSamplerThreadStops);
        winrt::uninit_apartment();
        return 0;
    }

    void Run() {
        WindowsSource source;
        core::SamplerCore core(source);
        std::function<void()> handler = [] {
            try {
                OnSample();
            } catch (...) {
            }
        };

        std::unique_lock lock(m_mutex);
        for (;;) {
            if (m_subscribers.empty()) {
                m_running = false;
                return;
            }
            uint8_t needs = 0;
            for (const auto& subscriber : m_subscribers) {
                needs |= subscriber->Needs();
            }
            auto subscribers = m_subscribers;
            lock.unlock();

            Stats stats;
            if (needs) {
                try {
                    stats = core.Sample(needs, GetTickCount64());
                } catch (...) {
                }
            }
            std::vector<std::shared_ptr<Subscriber>> gone;
            for (const auto& subscriber : subscribers) {
                if (subscriber->Needs() && !subscriber->Deliver(stats, handler)) {
                    gone.push_back(subscriber);
                }
            }

            lock.lock();
            for (const auto& subscriber : gone) {
                // Its dispatcher shut down without telling us.
                m_subscribers.erase(
                    std::remove(m_subscribers.begin(), m_subscribers.end(), subscriber),
                    m_subscribers.end());
            }
            m_wake = false;
            m_changed.wait_for(lock, std::chrono::milliseconds(kIntervalMs), [this] {
                return m_wake || m_subscribers.empty();
            });
        }
    }

    std::mutex m_mutex;
    std::condition_variable m_changed;
    std::vector<std::shared_ptr<Subscriber>> m_subscribers;
    bool m_running = false;
    bool m_wake = false;
};

Sampler& TheSampler() {
    // Never destroyed: the DLL stays loaded for the life of the process.
    static Sampler* sampler = new Sampler();
    return *sampler;
}

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
    // What is shown, to skip writes that change nothing.
    std::wstring shownUp, shownDown, shownCpu, shownMemory;
    int cpuWarning = -1;
    int memoryWarning = -1;
};

struct ThreadState {
    std::vector<winrt::weak_ref<wux::FrameworkElement>> frames;
    std::vector<Island> islands;
    std::shared_ptr<Subscriber> subscriber;
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

uint8_t NeedsFor(const MonitorConfig& config) {
    return static_cast<uint8_t>((config.cpu ? core::kCpu : 0) |
                                (config.memory ? core::kMemory : 0) |
                                (config.network ? core::kNetwork : 0));
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

void SetText(wuxc::TextBlock const& block, std::wstring& shown, std::wstring text) {
    if (text != shown) {
        block.Text(text);
        shown = std::move(text);
        status::Count(status::kMonitorTextUpdates);
    }
}

void SetWarning(wuxc::TextBlock const& block, int& shown, bool warning,
                const ThreadState& state, bool force) {
    if (force || shown != static_cast<int>(warning)) {
        block.Foreground(warning ? state.warning : state.text);
        shown = warning;
    }
}

// Only the parts that are shown, and only what changed. `force` after the
// colors changed.
void Show(Island& island, const ThreadState& state, const Stats& stats, bool force) {
    const auto& config = state.config;
    if (config.network) {
        SetText(island.upValue, island.shownUp, FormatSpeed(stats.up));
        SetText(island.downValue, island.shownDown, FormatSpeed(stats.down));
    }
    auto high = [](std::optional<double> percent) {
        return percent && *percent >= kWarningPercent;
    };
    if (config.cpu) {
        SetText(island.cpuValue, island.shownCpu, FormatPercent(stats.cpu));
        SetWarning(island.cpuValue, island.cpuWarning, high(stats.cpu), state, force);
    }
    if (config.memory) {
        SetText(island.memoryValue, island.shownMemory, FormatPercent(stats.memory));
        SetWarning(island.memoryValue, island.memoryWarning, high(stats.memory), state,
                   force);
    }
}

void OnSample() {
    if (!t_state || !t_state->subscriber || !t_state->enabled) {
        return;
    }
    LARGE_INTEGER start{}, end{}, frequency{};
    QueryPerformanceCounter(&start);
    auto stats = t_state->subscriber->Take();
    for (auto& island : t_state->islands) {
        try {
            Show(island, *t_state, stats, false);
        } catch (...) {
        }
    }
    QueryPerformanceCounter(&end);
    QueryPerformanceFrequency(&frequency);
    if (frequency.QuadPart > 0) {
        status::Count(status::kMonitorUiMicros,
                      (end.QuadPart - start.QuadPart) * 1'000'000 / frequency.QuadPart);
    }
}

void Unsubscribe(ThreadState& state) {
    if (state.subscriber) {
        TheSampler().Unsubscribe(state.subscriber);
        state.subscriber = nullptr;
    }
}

void Subscribe(ThreadState& state) {
    uint8_t needs = NeedsFor(state.config);
    if (!needs) {
        Unsubscribe(state);
        return;
    }
    if (!state.subscriber) {
        auto queue = DispatcherQueue::GetForCurrentThread();
        if (!queue) {
            return;
        }
        state.subscriber = std::make_shared<Subscriber>(queue);
        state.subscriber->SetNeeds(needs);
        TheSampler().Subscribe(state.subscriber);
        return;
    }
    if (state.subscriber->Needs() != needs) {
        state.subscriber->SetNeeds(needs);
        TheSampler().Wake();
    }
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

void RemoveAll(ThreadState& state) {
    for (auto& island : state.islands) {
        try {
            Remove(island);
        } catch (...) {
        }
    }
    if (!state.islands.empty()) {
        DLOG(L"Removed %zu monitor islands", state.islands.size());
        status::Count(status::kMonitorIslandsReleased,
                      static_cast<int64_t>(state.islands.size()));
    }
    state.islands.clear();
    Unsubscribe(state);
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
    Show(island, state, state.subscriber ? state.subscriber->Latest() : Stats{}, true);
    children.Append(island.border);
    DLOG(L"Monitor island added to %s", winrt::get_class_name(host).c_str());

    state.islands.push_back(std::move(island));
    status::Count(status::kMonitorIslands);
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
    // Islands whose frame is gone (the taskbar was rebuilt, a display removed).
    auto before = state.islands.size();
    state.islands.erase(
        std::remove_if(state.islands.begin(), state.islands.end(),
                       [](Island& island) {
                           if (island.frame.get()) {
                               return false;
                           }
                           try {
                               Remove(island);
                           } catch (...) {
                           }
                           return true;
                       }),
        state.islands.end());
    if (state.islands.size() < before) {
        status::Count(status::kMonitorIslandsReleased,
                      static_cast<int64_t>(before - state.islands.size()));
    }

    if (!state.enabled) {
        RemoveAll(state);
        return;
    }
    Subscribe(state);
    for (const auto& weak : state.frames) {
        if (auto frame = weak.get()) {
            try {
                Ensure(state, frame);
            } catch (...) {
                DLOG(L"Monitor island failed: %08X", winrt::to_hresult().value);
            }
        }
    }
    // New colors or newly shown parts: write everything once.
    auto stats = state.subscriber ? state.subscriber->Latest() : Stats{};
    for (auto& island : state.islands) {
        try {
            Show(island, state, stats, true);
        } catch (...) {
        }
    }
}

}  // namespace

void OnTaskbarFrameAdded(wux::FrameworkElement const& frame) {
    auto& state = State();
    for (const auto& weak : state.frames) {
        if (weak.get() == frame) {
            return;  // Reported again.
        }
    }
    state.frames.push_back(winrt::make_weak(frame));

    // The island goes in once the frame is settled in its parent.
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        return;
    }
    bool queued = queue.TryEnqueue(DispatcherQueuePriority::Low, [] {
        try {
            if (t_state) {
                Refresh(*t_state, *config::Current());
            }
        } catch (...) {
            DLOG(L"Monitor refresh failed: %08X", winrt::to_hresult().value);
        }
    });
    if (!queued) {
        status::Count(status::kDispatcherPostFailures);
    }
}

void OnConfigApplied(const Config& config) {
    try {
        Refresh(State(), config);
    } catch (...) {
        DLOG(L"Monitor refresh failed: %08X", winrt::to_hresult().value);
    }
}

void OnThreadShutdown() {
    if (!t_state) {
        return;
    }
    try {
        RemoveAll(*t_state);
    } catch (...) {
    }
    delete t_state;
    t_state = nullptr;
}

}  // namespace desk::monitor
