// Ported from the "Click-through taskbar" option of the Windows 11 Taskbar
// Styler mod for Windhawk (https://github.com/ramensoftware/windhawk-mods,
// GPL-3.0).
#include "pch.h"
#include "clickthrough.h"
#include "log.h"
#include "winutil.h"

namespace desk::clickthrough {
namespace {

constexpr UINT_PTR kSubclassId = 0x4445534C;

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
    wux::FrameworkElement::LayoutUpdated_revoker layoutRevoker;
    std::vector<long long> lastSignature;
};

thread_local std::list<IslandRoot> t_islands;
thread_local std::list<TaskbarState> t_taskbars;
// Bounding box of the region we applied, per top-level window. Explorer can
// replace or clear the region (e.g. when toggling auto-hide), and this tells
// whether the current region is still ours.
thread_local std::unordered_map<HWND, RECT> t_appliedBoxes;
thread_local std::unordered_set<HWND> t_subclassed;
thread_local bool t_applying = false;
thread_local bool t_enabled = false;

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

    // A new island reports its root before its content is attached, so the
    // window is resolved lazily.
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

    RECT currentBox;
    bool hasRegion = GetWindowRgnBox(topLevel, &currentBox) != ERROR;
    auto applied = t_appliedBoxes.find(topLevel);
    bool regionIsOurs = hasRegion && applied != t_appliedBoxes.end() &&
                        EqualRect(&currentBox, &applied->second);

    // Explorer clips the window itself in some modes (e.g. the collapsed
    // tablet taskbar). Only take over when there's no region or a full-window
    // one.
    if (hasRegion && !regionIsOurs) {
        RECT full = {0, 0, width, height};
        if (!EqualRect(&currentBox, &full)) {
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
            UnionRect(&box, &box, &rect);
        }
    }

    // SetWindowRgn owns the region on success and synchronously sends
    // WM_WINDOWPOSCHANGED, which the subclass must ignore.
    t_applying = true;
    BOOL ok = SetWindowRgn(topLevel, region, TRUE);
    t_applying = false;
    if (ok) {
        state.lastSignature = std::move(signature);
        t_appliedBoxes[topLevel] = box;
    } else {
        DeleteObject(region);
    }
}

void ReapplyForTopLevel(HWND topLevel) {
    if (!t_enabled) {
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
            if (!t_applying) {
                ReapplyForTopLevel(window);
            }
            return result;
        }
        case WM_NCDESTROY:
            t_subclassed.erase(window);
            t_appliedBoxes.erase(window);
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
    if (!topLevel || !winutil::IsTaskbarWindow(topLevel) ||
        t_subclassed.contains(topLevel)) {
        return;
    }
    if (winutil::SetWindowSubclassFromAnyThread(topLevel, SubclassProc,
                                                kSubclassId)) {
        t_subclassed.insert(topLevel);
    }
}

void ClearRegions() {
    for (const auto& [window, box] : t_appliedBoxes) {
        if (!winutil::IsTaskbarWindow(window)) {
            continue;
        }
        RECT current;
        if (GetWindowRgnBox(window, &current) == ERROR ||
            !EqualRect(&current, &box)) {
            continue;
        }
        t_applying = true;
        SetWindowRgn(window, nullptr, TRUE);
        t_applying = false;
    }
    t_appliedBoxes.clear();
    for (auto& state : t_taskbars) {
        state.lastSignature.clear();
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

void OnElementAdded(wux::FrameworkElement const& element, bool enabled) {
    auto className = winrt::get_class_name(element);
    bool isTaskbarFrame = className == L"Taskbar.TaskbarFrame";
    bool isTrayFrame = className == L"SystemTray.SystemTrayFrame";
    bool isMonitorIsland = !isTaskbarFrame && !isTrayFrame &&
                           element.Name() == L"DeskMonitorIsland";
    if (!isTaskbarFrame && !isTrayFrame && !isMonitorIsland) {
        return;
    }

    t_enabled = enabled;

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
    } else if (isTrayFrame) {
        state->systemTrayFrame = winrt::make_weak(element);
    } else {
        state->monitorIsland = winrt::make_weak(element);
    }

    // The taskbar frame's layout passes cover tray changes too.
    if (isTaskbarFrame && !state->layoutRevoker) {
        auto weakRoot = winrt::make_weak(root);
        state->layoutRevoker = element.LayoutUpdated(
            winrt::auto_revoke, [weakRoot](auto&&, auto&&) {
                if (!t_enabled) {
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
    }

    if (t_enabled) {
        try {
            Update(*state);
            EnsureSubclass(*state);
        } catch (...) {
        }
    }
}

void OnConfigApplied(bool enabled) {
    bool wasEnabled = t_enabled;
    t_enabled = enabled;
    if (enabled) {
        for (auto& state : t_taskbars) {
            state.lastSignature.clear();
            try {
                Update(state);
                EnsureSubclass(state);
            } catch (...) {
            }
        }
        DLOG(L"Click-through enabled for %zu taskbars", t_taskbars.size());
    } else if (wasEnabled) {
        ClearRegions();
        DLOG(L"Click-through disabled");
    }
}

}  // namespace desk::clickthrough
