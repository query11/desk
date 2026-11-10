#include "pch.h"
#include "adaptive.h"
#include "config.h"
#include "core/adaptive_layout.h"
#include "engine.h"
#include "log.h"
#include "monitor.h"
#include "status.h"

namespace desk::adaptive {
namespace {
enum class Kind { Frame, Tray, Button, Overflow };
struct Entry {
    Kind kind;
    winrt::weak_ref<wux::FrameworkElement> element;
    wux::FrameworkElement::LayoutUpdated_revoker layout;
};
struct Bar {
    InstanceHandle handle;
    winrt::weak_ref<wux::XamlRoot> root;
    core::LayoutPlan plan;
    double measuredWidth = 0;
    bool overflow = false;
};
thread_local std::unordered_map<InstanceHandle, Entry> t_entries;
thread_local std::vector<Bar> t_bars;
thread_local winrt::Windows::System::DispatcherQueueTimer t_timer{nullptr};
thread_local bool t_pending = false;
thread_local bool t_dirty = false;

bool Active() {
    auto config = config::Current();
    return config->enabled && (config->adaptiveLayout || std::any_of(config->displays.begin(), config->displays.end(),
        [](auto const& display) { return display.adaptiveLayout; }));
}

bool DescendsFrom(wux::FrameworkElement element, wux::FrameworkElement const& frame) {
    for (int depth = 0; element && depth < 32; depth++) {
        if (element == frame) return true;
        element = wuxm::VisualTreeHelper::GetParent(element).try_as<wux::FrameworkElement>();
    }
    return false;
}

void Update() {
    bool dirty = t_dirty;
    t_dirty = false;
    auto config = config::Current();
    // Snapshot handles: changing the layout can synchronously add/remove elements.
    std::vector<InstanceHandle> frames;
    for (auto const& [handle, entry] : t_entries)
        if (entry.kind == Kind::Frame) frames.push_back(handle);
    for (auto frameHandle : frames) {
        auto found = t_entries.find(frameHandle);
        if (found == t_entries.end()) continue;
        auto frame = found->second.element.get();
        if (!frame) continue;
        auto display = config::ForElement(*config, frame);
        bool enabled = config->enabled && (display ? display->adaptiveLayout : config->adaptiveLayout);
        auto root = frame.XamlRoot();
        if (!root || root.Size().Width <= 0 || frame.ActualWidth() <= 0) continue;
        auto bar = std::find_if(t_bars.begin(), t_bars.end(), [&](auto const& b) { return b.handle == frameHandle; });
        core::LayoutInput input;
        input.width = root.Size().Width;
        input.icon = display ? display->iconSize : config->iconSize;
        input.requestedButton = display ? display->buttonWidth : config->buttonWidth;
        input.previousLevel = bar == t_bars.end() ? 0 : bar->plan.monitorLevel;
        std::tie(input.monitor, input.compactMonitor) = monitor::LayoutWidths(frame);
        std::vector<InstanceHandle> affected{frameHandle};
        double actualButtons = 0;
        bool overflow = false;
        for (auto const& [handle, entry] : t_entries) {
            auto element = entry.element.get();
            if (!element || element.XamlRoot() != root) continue;
            if (entry.kind == Kind::Tray) {
                // Use the occupied area from the tray's left edge, including clock and show-desktop.
                auto rect = element.TransformToVisual(root.Content()).TransformBounds(
                    {0, 0, static_cast<float>(element.ActualWidth()), static_cast<float>(element.ActualHeight())});
                if (rect.Width > 0) input.tray = std::max(input.tray, input.width - rect.X);
            } else if (entry.kind == Kind::Button && DescendsFrom(element, frame)) {
                affected.push_back(handle);
                if (element.Visibility() == wux::Visibility::Visible && element.ActualWidth() > 0) {
                    input.buttons++;
                    actualButtons += element.ActualWidth();
                }
            } else if (entry.kind == Kind::Overflow && DescendsFrom(element, frame)) {
                overflow |= element.Visibility() == wux::Visibility::Visible && element.ActualWidth() > 0;
            }
        }
        // Wait for the primary tray to be measured; secondary taskbars may have no tray.
        if (input.buttons == 0) continue;
        input.extra = std::clamp(std::round((frame.ActualWidth() - actualButtons) / 4) * 4, 24.0, 400.0);
        auto plan = core::FitTaskbar(input);
        bool changed = bar == t_bars.end() || bar->plan != plan;
        bool measured = bar == t_bars.end() || bar->measuredWidth != frame.ActualWidth() || bar->overflow != overflow;
        if (changed || measured) {
            DLOG(L"Taskbar layout: adaptive=%d screen=%.0f frame=%.0f buttons=%d spacing=%.0f overflow=%d monitor=%d",
                 enabled, input.width, frame.ActualWidth(), input.buttons,
                 plan.button, overflow, plan.monitorLevel);
        }
        if (!enabled) continue;
        if (!changed && !dirty && !measured) continue;
        if (bar == t_bars.end()) t_bars.push_back({frameHandle, winrt::make_weak(root), plan, frame.ActualWidth(), overflow});
        else { bar->root = winrt::make_weak(root); bar->plan = plan;
               bar->measuredWidth = frame.ActualWidth(); bar->overflow = overflow; }
        if (!changed && !dirty) continue;
        monitor::SetCompactLevel(frame, plan.monitorLevel);
        engine::RefreshLayout(affected);
    }
}

void Schedule() {
    if (t_pending || (!t_dirty && !Active())) return;
    if (!t_timer) {
        auto queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
        if (!queue) return;
        t_timer = queue.CreateTimer();
        t_timer.IsRepeating(false);
        t_timer.Interval(std::chrono::milliseconds(120));
        t_timer.Tick([](auto&&, auto&&) {
            t_pending = false;
            try { Update(); }
            catch (...) { DLOG(L"Adaptive layout skipped: %08X", winrt::to_hresult().value); }
        });
    }
    t_pending = true;
    t_timer.Start();
}
} // namespace

void OnElementAdded(InstanceHandle handle, wux::FrameworkElement const& element) {
    auto type = winrt::get_class_name(element);
    std::optional<Kind> kind;
    if (type == L"Taskbar.TaskbarFrame") kind = Kind::Frame;
    else if (type == L"SystemTray.SystemTrayFrame") kind = Kind::Tray;
    else if (type == L"Taskbar.OverflowToggleButton") kind = Kind::Overflow;
    else {
        auto parent = wuxm::VisualTreeHelper::GetParent(element).try_as<wux::FrameworkElement>();
        if (parent && ((element.Name() == L"IconPanel" && winrt::get_class_name(parent) == L"Taskbar.TaskListButton") ||
            (type == L"Taskbar.TaskListButtonPanel" && parent.Name() == L"LaunchListButton"))) kind = Kind::Button;
    }
    if (!kind) return;
    OnElementRemoved(handle);
    auto& entry = t_entries[handle];
    entry.kind = *kind;
    entry.element = winrt::make_weak(element);
    t_dirty = true;
    if (*kind == Kind::Frame) {
        entry.layout = element.LayoutUpdated(winrt::auto_revoke, [](auto&&, auto&&) { Schedule(); });
        status::Count(status::kLayoutSubscriptions);
    }
    Schedule();
}

void OnElementRemoved(InstanceHandle handle) {
    auto found = t_entries.find(handle);
    if (found == t_entries.end()) return;
    if (found->second.layout) status::Count(status::kLayoutSubscriptionsReleased);
    t_entries.erase(found);
    t_dirty = true;
    std::erase_if(t_bars, [&](auto const& bar) { return bar.handle == handle; });
    Schedule();
}

void OnConfigApplied() {
    t_dirty = true;
    if (!Active()) {
        t_bars.clear();
        for (auto const& [handle, entry] : t_entries) {
            if (entry.kind == Kind::Frame)
                if (auto frame = entry.element.get()) monitor::SetCompactLevel(frame, 0);
        }
    }
    Schedule();
}

std::vector<ResolvedStyle> Styles(InstanceHandle handle) {
    std::vector<ResolvedStyle> result;
    if (!Active()) return result;
    auto entry = t_entries.find(handle);
    // The native task list already reserves shell chrome during measure. Writing
    // either Margin or MaxWidth on its outer frame can reserve the same space
    // again, even when XAML reports a full-screen layout slot. Keep the theme's
    // proven outer layout and adapt only the buttons and our own monitor island.
    if (entry == t_entries.end() || entry->second.kind != Kind::Button) return result;
    auto element = entry->second.element.get();
    if (!element) return result;
    auto config = config::Current();
    auto display = config::ForElement(*config, element);
    if (!(display ? display->adaptiveLayout : config->adaptiveLayout)) return result;
    for (auto const& bar : t_bars) {
        if (bar.root.get() != element.XamlRoot()) continue;
        auto frameEntry = t_entries.find(bar.handle);
        auto frame = frameEntry == t_entries.end() ? nullptr : frameEntry->second.element.get();
        if (!frame || !DescendsFrom(element, frame)) continue;
        auto const& plan = bar.plan;
        result.push_back({wux::FrameworkElement::MinWidthProperty(), winrt::box_value(plan.button),
            L"adaptive-button:" + std::to_wstring(plan.button)});
        break;
    }
    return result;
}

void OnThreadShutdown() {
    if (t_timer) t_timer.Stop();
    t_timer = nullptr;
    t_pending = false;
    t_dirty = false;
    for (auto const& [handle, entry] : t_entries)
        if (entry.layout) status::Count(status::kLayoutSubscriptionsReleased);
    t_entries.clear();
    t_bars.clear();
}
} // namespace desk::adaptive
