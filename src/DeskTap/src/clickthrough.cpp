// Ported from the "Click-through taskbar" option of the Windows 11 Taskbar
// Styler mod for Windhawk (https://github.com/ramensoftware/windhawk-mods,
// GPL-3.0).
#include "pch.h"
#include "clickthrough.h"
#include "core/clickthrough_gate.h"
#include "log.h"
#include "status.h"
#include "winutil.h"

namespace desk::clickthrough {
namespace {

using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::System::DispatcherQueuePriority;

constexpr UINT_PTR kSubclassId = 0x4445534C;

// ---------------------------------------------------------- XAML threads
// Everything here belongs to the XAML thread that reported the elements.

struct IslandRoot {
    winrt::weak_ref<wuxh::DesktopWindowXamlSource> source;
    HWND hwnd = nullptr;
};

struct TaskbarState {
    winrt::weak_ref<wux::XamlRoot> xamlRoot;
    HWND islandHwnd = nullptr;
    winrt::weak_ref<wux::FrameworkElement> taskbarFrame;
    winrt::weak_ref<wux::FrameworkElement> systemTrayFrame;
    // Desk's own island with the system figures, outside the frames.
    winrt::weak_ref<wux::FrameworkElement> monitorIsland;
    // The frame the layout handler is attached to; it moves to a new frame
    // when the taskbar frame is replaced.
    winrt::weak_ref<wux::FrameworkElement> layoutFrame;
    wux::FrameworkElement::LayoutUpdated_revoker layoutRevoker;
    std::vector<long long> lastSignature;

    TaskbarState() = default;
    TaskbarState(const TaskbarState&) = delete;
    TaskbarState& operator=(const TaskbarState&) = delete;
    ~TaskbarState() { Unsubscribe(); }

    void Unsubscribe() {
        if (layoutRevoker) {
            layoutRevoker.revoke();
            status::Count(status::kLayoutSubscriptionsReleased);
        }
        layoutFrame = nullptr;
    }
};

thread_local std::list<IslandRoot> t_islands;
thread_local std::list<TaskbarState> t_taskbars;
thread_local core::ClickThroughGate t_gate;

// ----------------------------------------------------------- all threads
// The windows Desk clipped. A window's subclass procedure runs on the
// window's thread, which needn't be the XAML thread that clipped it, so the
// record says which thread owns it and how to reach it. Never held across
// SetWindowRgn or SendMessage: those wait for the window's thread, which may
// be waiting for this lock.

struct RegionRecord {
    HRGN applied = nullptr;  // A copy of the region Desk set.
    DWORD ownerThread = 0;   // The XAML thread that set it.
    DispatcherQueue ownerQueue{nullptr};
    DWORD windowThread = 0;  // Tells a reused window handle apart.
    bool applying = false;   // Desk is setting it right now.
    bool reapplyQueued = false;
};

std::mutex g_mutex;
std::unordered_map<HWND, RegionRecord> g_regions;
std::unordered_set<HWND> g_subclassed;

HRGN CopyRegion(HRGN region) {
    HRGN copy = CreateRectRgn(0, 0, 0, 0);
    if (copy && CombineRgn(copy, region, nullptr, RGN_COPY) == ERROR) {
        DeleteObject(copy);
        return nullptr;
    }
    return copy;
}

// Whether the window's current region is exactly the one Desk set: a region
// with the same bounding box set by Explorer doesn't count.
bool RegionIsOurs(HWND window, bool* hasRegion) {
    HRGN current = CreateRectRgn(0, 0, 0, 0);
    if (!current) {
        *hasRegion = false;
        return false;
    }
    *hasRegion = GetWindowRgn(window, current) != ERROR;
    bool ours = false;
    if (*hasRegion) {
        std::lock_guard lock(g_mutex);
        auto it = g_regions.find(window);
        ours = it != g_regions.end() && it->second.applied &&
               EqualRgn(current, it->second.applied);
    }
    DeleteObject(current);
    return ours;
}

void ReapplyForTopLevel(HWND topLevel);

// The window moved or Explorer reset its region (auto-hide, a display
// change): the XAML thread that owns the record checks it again.
void RequestReapply(HWND window) {
    DispatcherQueue queue{nullptr};
    bool here = false;
    {
        std::lock_guard lock(g_mutex);
        auto it = g_regions.find(window);
        if (it == g_regions.end() || it->second.applying || it->second.reapplyQueued) {
            return;
        }
        if (it->second.ownerThread == GetCurrentThreadId()) {
            here = true;
        } else {
            queue = it->second.ownerQueue;
            it->second.reapplyQueued = queue != nullptr;
        }
    }
    if (here) {
        ReapplyForTopLevel(window);
        return;
    }
    if (!queue) {
        return;
    }
    bool queued = false;
    try {
        queued = queue.TryEnqueue(DispatcherQueuePriority::Normal, [window] {
            {
                std::lock_guard lock(g_mutex);
                if (auto it = g_regions.find(window); it != g_regions.end()) {
                    it->second.reapplyQueued = false;
                }
            }
            try {
                ReapplyForTopLevel(window);
            } catch (...) {
            }
        });
    } catch (...) {
    }
    if (!queued) {
        // The owner's dispatcher is shutting down.
        status::Count(status::kDispatcherPostFailures);
        std::lock_guard lock(g_mutex);
        if (auto it = g_regions.find(window); it != g_regions.end()) {
            it->second.reapplyQueued = false;
        }
    }
}

void Forget(HWND window) {
    std::lock_guard lock(g_mutex);
    if (auto it = g_regions.find(window); it != g_regions.end()) {
        if (it->second.applied) {
            DeleteObject(it->second.applied);
        }
        g_regions.erase(it);
    }
    if (g_subclassed.erase(window)) {
        status::Count(status::kSubclassesReleased);
    }
}

LRESULT CALLBACK SubclassProc(HWND window,
                              UINT message,
                              WPARAM wParam,
                              LPARAM lParam,
                              UINT_PTR,
                              DWORD_PTR) {
    switch (message) {
        case WM_WINDOWPOSCHANGED: {
            // Explorer resets the region when the taskbar is shown or
            // auto-hide is toggled.
            LRESULT result = DefSubclassProc(window, message, wParam, lParam);
            try {
                RequestReapply(window);
            } catch (...) {
            }
            return result;
        }
        case WM_NCDESTROY:
            Forget(window);
            RemoveWindowSubclass(window, SubclassProc, kSubclassId);
            break;
    }
    return DefSubclassProc(window, message, wParam, lParam);
}

void EnsureSubclass(TaskbarState& state) {
    if (!state.islandHwnd) {
        return;
    }
    HWND topLevel = GetAncestor(state.islandHwnd, GA_ROOT);
    if (!topLevel || !winutil::IsTaskbarWindow(topLevel)) {
        return;
    }
    {
        std::lock_guard lock(g_mutex);
        if (g_subclassed.contains(topLevel)) {
            return;
        }
    }
    if (winutil::SetWindowSubclassFromAnyThread(topLevel, SubclassProc, kSubclassId)) {
        std::lock_guard lock(g_mutex);
        if (g_subclassed.insert(topLevel).second) {
            status::Count(status::kSubclasses);
        }
    }
}

// Sets the region and records it as Desk's. Takes ownership of `region`.
void SetRegion(HWND topLevel, HRGN region) {
    HRGN copy = CopyRegion(region);
    if (!copy) {
        DeleteObject(region);
        return;
    }
    DWORD windowThread = GetWindowThreadProcessId(topLevel, nullptr);
    bool created = false;
    {
        std::lock_guard lock(g_mutex);
        auto [it, inserted] = g_regions.try_emplace(topLevel);
        created = inserted;
        auto& record = it->second;
        if (inserted || record.ownerThread != GetCurrentThreadId()) {
            record.ownerThread = GetCurrentThreadId();
            record.ownerQueue = DispatcherQueue::GetForCurrentThread();
        }
        record.windowThread = windowThread;
        record.applying = true;
    }

    // Owns the region on success, and synchronously sends
    // WM_WINDOWPOSCHANGED, which the subclass ignores while `applying`.
    BOOL ok = SetWindowRgn(topLevel, region, TRUE);

    std::lock_guard lock(g_mutex);
    auto it = g_regions.find(topLevel);
    if (it == g_regions.end()) {
        // Destroyed meanwhile.
        DeleteObject(copy);
        return;
    }
    it->second.applying = false;
    if (ok) {
        if (it->second.applied) {
            DeleteObject(it->second.applied);
        }
        it->second.applied = copy;
        if (created) {
            status::Count(status::kRegionsSet);
        }
    } else {
        DeleteObject(copy);
        DeleteObject(region);
        if (created) {
            g_regions.erase(it);
        }
    }
}

// Removes the regions this thread set, where they are still exactly Desk's.
// A window that is gone, whose handle now belongs to another window, or
// whose region somebody else replaced is left alone.
void ClearOwnedRegions() {
    std::vector<std::pair<HWND, RegionRecord>> owned;
    {
        std::lock_guard lock(g_mutex);
        for (auto it = g_regions.begin(); it != g_regions.end();) {
            if (it->second.ownerThread == GetCurrentThreadId() && !it->second.applying) {
                owned.emplace_back(it->first, std::move(it->second));
                it = g_regions.erase(it);
            } else {
                ++it;
            }
        }
    }

    for (auto& [window, record] : owned) {
        bool clear = IsWindow(window) && winutil::IsTaskbarWindow(window) &&
                     GetWindowThreadProcessId(window, nullptr) == record.windowThread;
        if (clear) {
            HRGN current = CreateRectRgn(0, 0, 0, 0);
            clear = current && GetWindowRgn(window, current) != ERROR &&
                    EqualRgn(current, record.applied);
            if (current) {
                DeleteObject(current);
            }
        }
        if (clear) {
            // No record any more, so the subclass ignores the resulting
            // WM_WINDOWPOSCHANGED.
            SetWindowRgn(window, nullptr, TRUE);
            status::Count(status::kRegionsCleared);
        }
        if (record.applied) {
            DeleteObject(record.applied);
        }
    }
    for (auto& state : t_taskbars) {
        state.lastSignature.clear();
    }
    if (!owned.empty()) {
        DLOG(L"Click-through regions cleared for %zu windows", owned.size());
    }
}

// ----------------------------------------------------------- the regions

long long Quantize(double value) {
    if (value < 0 || std::isnan(value)) {
        return 0;
    }
    return static_cast<long long>(value * 2 + 0.5);
}

TaskbarState* FindState(wux::XamlRoot const& root, bool create) {
    if (!root) {
        return nullptr;
    }
    // A removed display takes its root with it.
    t_taskbars.remove_if(
        [](const TaskbarState& state) { return !state.xamlRoot.get(); });
    for (auto& state : t_taskbars) {
        if (state.xamlRoot.get() == root) {
            return &state;
        }
    }
    if (!create) {
        return nullptr;
    }
    auto& state = t_taskbars.emplace_back();
    state.xamlRoot = winrt::make_weak(root);
    return &state;
}

HWND ResolveIslandHwnd(wux::XamlRoot const& root) {
    t_islands.remove_if(
        [](const IslandRoot& island) { return !island.source.get(); });
    for (auto& island : t_islands) {
        auto source = island.source.get();
        if (!source) {
            continue;
        }
        try {
            auto content = source.Content();
            if (content && content.XamlRoot() == root) {
                return island.hwnd;
            }
        } catch (...) {
        }
    }
    return nullptr;
}

void Update(TaskbarState& state) {
    auto taskbarFrame = state.taskbarFrame.get();
    if (!taskbarFrame) {
        return;
    }

    wux::XamlRoot root{nullptr};
    try {
        root = taskbarFrame.XamlRoot();
    } catch (...) {
    }
    if (!root) {
        return;
    }
    if (root != state.xamlRoot.get()) {
        // The frame moved to another root (display); that root's state
        // takes it over.
        state.taskbarFrame = nullptr;
        state.Unsubscribe();
        return;
    }

    // A new island reports its root before its content is attached, so the
    // window is resolved lazily (and again if it was destroyed).
    if (state.islandHwnd && !IsWindow(state.islandHwnd)) {
        state.islandHwnd = nullptr;
        state.lastSignature.clear();
    }
    if (!state.islandHwnd) {
        state.islandHwnd = ResolveIslandHwnd(root);
    }
    if (!state.islandHwnd) {
        return;
    }

    // RasterizationScale is snapped to standard values; the island's DPI is
    // the real DIP-to-pixel ratio (e.g. at 110% scaling).
    UINT dpi = GetDpiForWindow(state.islandHwnd);
    double scale = dpi ? dpi / 96.0 : root.RasterizationScale();
    if (scale <= 0) {
        return;
    }

    auto content = root.Content();
    if (!content) {
        return;
    }

    HWND topLevel = GetAncestor(state.islandHwnd, GA_ROOT);
    if (!topLevel || !winutil::IsTaskbarWindow(topLevel)) {
        return;
    }

    auto frameRect = [&content](wux::FrameworkElement const& frame)
        -> std::optional<wf::Rect> {
        if (!frame) {
            return std::nullopt;
        }
        auto rect = frame.TransformToVisual(content).TransformBounds(
            {0, 0, static_cast<float>(frame.ActualWidth()),
             static_cast<float>(frame.ActualHeight())});
        if (rect.Width <= 0 || rect.Height <= 0) {
            return std::nullopt;
        }
        return rect;
    };

    auto taskbarRect = frameRect(taskbarFrame);
    if (!taskbarRect) {
        // An empty region would hide the whole taskbar; retry later.
        return;
    }
    auto trayRect = frameRect(state.systemTrayFrame.get());
    auto monitorRect = frameRect(state.monitorIsland.get());

    std::vector<long long> signature;
    auto append = [&signature](const std::optional<wf::Rect>& rect) {
        if (rect) {
            signature.push_back(Quantize(rect->X));
            signature.push_back(Quantize(rect->Y));
            signature.push_back(Quantize(rect->Width));
            signature.push_back(Quantize(rect->Height));
        } else {
            signature.push_back(-1);
        }
    };
    signature.push_back(reinterpret_cast<long long>(topLevel));
    signature.push_back(Quantize(scale * 100));
    append(taskbarRect);
    append(trayRect);
    append(monitorRect);

    RECT windowRect;
    if (!GetWindowRect(topLevel, &windowRect)) {
        return;
    }
    int width = windowRect.right - windowRect.left;
    int height = windowRect.bottom - windowRect.top;

    bool hasRegion = false;
    bool regionIsOurs = RegionIsOurs(topLevel, &hasRegion);

    // Explorer clips the window itself in some modes (e.g. the collapsed
    // tablet taskbar). Only take over when there's no region or a full-window
    // one.
    if (hasRegion && !regionIsOurs) {
        RECT currentBox;
        RECT full = {0, 0, width, height};
        if (GetWindowRgnBox(topLevel, &currentBox) == ERROR ||
            !EqualRect(&currentBox, &full)) {
            return;
        }
    }

    if (signature == state.lastSignature && regionIsOurs) {
        return;
    }

    POINT origin = {0, 0};
    ClientToScreen(state.islandHwnd, &origin);
    auto toWindow = [&](const wf::Rect& dip) {
        int left = (origin.x - windowRect.left) + std::lround(dip.X * scale);
        int top = (origin.y - windowRect.top) + std::lround(dip.Y * scale);
        int right = left + std::lround(dip.Width * scale);
        int bottom = top + std::lround(dip.Height * scale);
        return RECT{std::clamp(left, 0, width), std::clamp(top, 0, height),
                    std::clamp(right, 0, width), std::clamp(bottom, 0, height)};
    };

    RECT box = toWindow(*taskbarRect);
    HRGN region = CreateRectRgn(box.left, box.top, box.right, box.bottom);
    if (!region) {
        return;
    }
    for (const auto& extra : {trayRect, monitorRect}) {
        if (!extra) {
            continue;
        }
        RECT rect = toWindow(*extra);
        if (HRGN extraRegion =
                CreateRectRgn(rect.left, rect.top, rect.right, rect.bottom)) {
            CombineRgn(region, region, extraRegion, RGN_OR);
            DeleteObject(extraRegion);
        }
    }

    SetRegion(topLevel, region);
    state.lastSignature = std::move(signature);
}

void ReapplyForTopLevel(HWND topLevel) {
    if (!t_gate.Enabled()) {
        return;
    }
    for (auto& state : t_taskbars) {
        if (state.islandHwnd &&
            GetAncestor(state.islandHwnd, GA_ROOT) == topLevel) {
            try {
                Update(state);
            } catch (...) {
            }
        }
    }
}

}  // namespace

void OnIslandRoot(wf::IInspectable const& inspectable) {
    auto source = inspectable.try_as<wuxh::DesktopWindowXamlSource>();
    if (!source) {
        return;
    }
    HWND hwnd = nullptr;
    if (auto native = source.try_as<IDesktopWindowXamlSourceNative>()) {
        native->get_WindowHandle(&hwnd);
    }
    if (!hwnd) {
        return;
    }
    t_islands.remove_if([hwnd](const IslandRoot& island) {
        return !island.source.get() || island.hwnd == hwnd;
    });
    t_islands.push_back({winrt::make_weak(source), hwnd});
}

void OnElementAdded(wux::FrameworkElement const& element, bool configured) {
    auto className = winrt::get_class_name(element);
    bool isTaskbarFrame = className == L"Taskbar.TaskbarFrame";
    bool isTrayFrame = className == L"SystemTray.SystemTrayFrame";
    bool isMonitorIsland = !isTaskbarFrame && !isTrayFrame &&
                           element.Name() == L"DeskMonitorIsland";
    if (!isTaskbarFrame && !isTrayFrame && !isMonitorIsland) {
        return;
    }

    // What this thread applied last, not the newest configuration (see
    // core/clickthrough_gate.h).
    bool enabled = t_gate.EnabledForNewElement(configured);

    wux::XamlRoot root{nullptr};
    try {
        root = element.XamlRoot();
    } catch (...) {
    }
    auto* state = FindState(root, true);
    if (!state) {
        return;
    }

    if (isTaskbarFrame) {
        state->taskbarFrame = winrt::make_weak(element);
        // A replaced frame: follow the new one's layout, not the old one's.
        // The same frame reported again keeps its handler.
        if (state->layoutFrame.get() != element) {
            state->Unsubscribe();
            state->layoutFrame = winrt::make_weak(element);
            auto weakRoot = winrt::make_weak(root);
            // The taskbar frame's layout passes cover tray changes too.
            state->layoutRevoker = element.LayoutUpdated(
                winrt::auto_revoke, [weakRoot](auto&&, auto&&) {
                    if (!t_gate.Enabled()) {
                        return;
                    }
                    try {
                        if (auto* state = FindState(weakRoot.get(), false)) {
                            Update(*state);
                            EnsureSubclass(*state);
                        }
                    } catch (...) {
                    }
                });
            status::Count(status::kLayoutSubscriptions);
        }
    } else if (isTrayFrame) {
        state->systemTrayFrame = winrt::make_weak(element);
    } else {
        state->monitorIsland = winrt::make_weak(element);
    }

    if (enabled) {
        try {
            Update(*state);
            EnsureSubclass(*state);
        } catch (...) {
        }
    }
}

void OnConfigApplied(bool enabled) {
    if (t_gate.OnConfigApplied(enabled) == core::ClickThroughGate::Action::Apply) {
        for (auto& state : t_taskbars) {
            state.lastSignature.clear();
            try {
                Update(state);
                EnsureSubclass(state);
            } catch (...) {
            }
        }
        DLOG(L"Click-through enabled for %zu taskbars", t_taskbars.size());
        return;
    }
    // Every time, not only when it was on: whatever this thread clipped goes.
    ClearOwnedRegions();
}

void OnThreadShutdown() {
    try {
        ClearOwnedRegions();
    } catch (...) {
    }
    t_taskbars.clear();
    t_islands.clear();
}

}  // namespace desk::clickthrough
