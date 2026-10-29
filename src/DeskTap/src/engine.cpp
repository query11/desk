// The styling engine: matches every element reported by XAML diagnostics
// against the rules, applies the resolved values, remembers the original
// local values and restores them when a rule stops applying.
//
// The approach and many details (original value tracking, re-applying after
// external writes, the delayed BackgroundFill write) follow the Windows 11
// Taskbar Styler mod for Windhawk (https://github.com/ramensoftware/windhawk-mods,
// GPL-3.0).
#include "pch.h"
#include "engine.h"
#include "clickthrough.h"
#include "config.h"
#include "log.h"
#include "paths.h"
#include "surface.h"
#include "values.h"

namespace desk::engine {
namespace {

using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::System::DispatcherQueuePriority;

struct AppliedProperty {
    wux::DependencyProperty property{nullptr};
    std::wstring signature;
    wf::IInspectable original{nullptr};
    wf::IInspectable applied{nullptr};
    int64_t callbackToken = 0;
    ULONGLONG reapplyWindowStart = 0;
    uint32_t reapplyCount = 0;
};

struct TrackedElement {
    winrt::weak_ref<wux::FrameworkElement> element;
    std::wstring diagnosticsType;
    std::vector<AppliedProperty> properties;
    uint64_t serial = UINT64_MAX;
};

using ConditionValues =
    std::vector<std::pair<wux::DependencyProperty, wf::IInspectable>>;

// Resolved values for one configuration. Held by shared_ptr so that a config
// change in the middle of applying can't free values still in use.
struct Cache {
    uint64_t serial = 0;
    std::unordered_map<std::wstring, std::vector<ResolvedStyle>> styles;
    std::unordered_map<std::wstring, ConditionValues> conditions;
};

struct ThreadState {
    std::unordered_map<InstanceHandle, TrackedElement> elements;
    std::shared_ptr<Cache> cache;
    int modifying = 0;
    std::vector<InstanceHandle> pendingRemovals;
    size_t addsSincePrune = 0;
};

// Each XAML UI thread has its own elements; all access happens on that thread.
thread_local ThreadState* t_state = nullptr;

struct UiThread {
    DWORD id = 0;
    DispatcherQueue queue{nullptr};
    winrt::Windows::UI::Core::CoreDispatcher core{nullptr};
};

std::mutex g_threadsMutex;
std::vector<UiThread> g_threads;

struct ModifyingScope {
    explicit ModifyingScope(ThreadState& state) : state(state) {
        state.modifying++;
    }
    ~ModifyingScope() { state.modifying--; }
    ThreadState& state;
};

ThreadState& StateFor(wux::FrameworkElement const& element) {
    if (!t_state) {
        t_state = new ThreadState();

        UiThread thread;
        thread.id = GetCurrentThreadId();
        thread.queue = DispatcherQueue::GetForCurrentThread();
        if (!thread.queue) {
            try {
                thread.core = element.Dispatcher();
            } catch (...) {
            }
        }
        DLOG(L"New XAML thread (dispatcher queue: %d)", thread.queue != nullptr);

        std::lock_guard lock(g_threadsMutex);
        g_threads.push_back(std::move(thread));
    }
    return *t_state;
}

std::shared_ptr<Cache> CacheFor(ThreadState& state, const Config& config) {
    if (!state.cache || state.cache->serial != config.serial) {
        state.cache = std::make_shared<Cache>();
        state.cache->serial = config.serial;
    }
    return state.cache;
}

const std::vector<ResolvedStyle>& ResolvedFor(Cache& cache,
                                              const Rule& rule,
                                              std::wstring_view diagnosticsType) {
    std::wstring key = std::to_wstring(rule.id);
    key += L'|';
    key += rule.element.type;
    key += L'|';
    key += diagnosticsType;

    auto it = cache.styles.find(key);
    if (it == cache.styles.end()) {
        it = cache.styles
                 .emplace(std::move(key),
                          ResolveStyles(rule.element.type, diagnosticsType,
                                        rule.styles))
                 .first;
    }
    return it->second;
}

const ConditionValues& ConditionsFor(Cache& cache,
                                     const ElementMatcher& matcher) {
    std::wstring key = matcher.type;
    for (const auto& [property, value] : matcher.propertyConditions) {
        key += L'|';
        key += property;
        key += L'=';
        key += value;
    }

    auto it = cache.conditions.find(key);
    if (it == cache.conditions.end()) {
        it = cache.conditions
                 .emplace(std::move(key),
                          ResolveConditions(matcher.type,
                                            matcher.propertyConditions))
                 .first;
    }
    return it->second;
}

bool TestMatcher(Cache& cache,
                 wux::FrameworkElement const& element,
                 const ElementMatcher& matcher,
                 std::wstring_view diagnosticsType) {
    if (!matcher.type.empty()) {
        auto className = winrt::get_class_name(element);
        if (matcher.type != std::wstring_view(className) &&
            (diagnosticsType.empty() || matcher.type != diagnosticsType)) {
            return false;
        }
    }

    if (!matcher.name.empty() &&
        matcher.name != std::wstring_view(element.Name())) {
        return false;
    }

    if (matcher.oneBasedIndex > 0) {
        auto parent = wuxm::VisualTreeHelper::GetParent(element);
        if (!parent) {
            return false;
        }
        int index = matcher.oneBasedIndex - 1;
        if (index >= wuxm::VisualTreeHelper::GetChildrenCount(parent) ||
            wuxm::VisualTreeHelper::GetChild(parent, index) != element) {
            return false;
        }
    }

    if (!matcher.propertyConditions.empty()) {
        const auto& conditions = ConditionsFor(cache, matcher);
        if (conditions.size() != matcher.propertyConditions.size()) {
            return false;
        }
        for (const auto& [property, expected] : conditions) {
            auto value = ReadLocalValueSafe(element, property);
            if (!value || value == wux::DependencyProperty::UnsetValue() ||
                !SameLocalValue(value, expected)) {
                return false;
            }
        }
    }

    return true;
}

bool MatchParents(Cache& cache,
                  wux::FrameworkElement const& element,
                  const std::vector<ElementMatcher>& parents,
                  size_t index) {
    if (index >= parents.size()) {
        return true;
    }

    const auto& matcher = parents[index];
    if (matcher.kind == ElementMatcher::Kind::Wildcard) {
        // Validated at parse time: a wildcard is always followed by an
        // element matcher.
        const auto& next = parents[index + 1];
        wux::FrameworkElement current = element;
        for (;;) {
            auto parent = wuxm::VisualTreeHelper::GetParent(current)
                              .try_as<wux::FrameworkElement>();
            if (!parent) {
                return false;
            }
            current = parent;
            if (TestMatcher(cache, current, next, {}) &&
                MatchParents(cache, current, parents, index + 2)) {
                return true;
            }
        }
    }

    auto parent =
        wuxm::VisualTreeHelper::GetParent(element).try_as<wux::FrameworkElement>();
    if (!parent || !TestMatcher(cache, parent, matcher, {})) {
        return false;
    }
    return MatchParents(cache, parent, parents, index + 1);
}

using Desired = std::vector<const ResolvedStyle*>;

Desired ComputeDesired(Cache& cache,
                       wux::FrameworkElement const& element,
                       std::wstring_view diagnosticsType,
                       const Config& config) {
    Desired desired;
    for (const auto& rule : config.rules) {
        bool matched = false;
        try {
            matched =
                TestMatcher(cache, element, rule.element, diagnosticsType) &&
                MatchParents(cache, element, rule.parents, 0);
        } catch (...) {
            matched = false;
        }
        if (!matched) {
            continue;
        }

        // Later rules win for the same property.
        for (const auto& style : ResolvedFor(cache, rule, diagnosticsType)) {
            auto it = std::find_if(desired.begin(), desired.end(),
                                   [&](const ResolvedStyle* existing) {
                                       return existing->property ==
                                              style.property;
                                   });
            if (it != desired.end()) {
                *it = &style;
            } else {
                desired.push_back(&style);
            }
        }
    }
    return desired;
}

void OnPropertyChanged(InstanceHandle handle,
                       wux::DependencyObject const& sender,
                       wux::DependencyProperty const& property) noexcept {
    try {
        if (!t_state || t_state->modifying) {
            return;
        }
        auto& state = *t_state;

        auto it = state.elements.find(handle);
        if (it == state.elements.end()) {
            return;
        }
        auto& properties = it->second.properties;
        auto applied = std::find_if(
            properties.begin(), properties.end(),
            [&](const AppliedProperty& p) { return p.property == property; });
        if (applied == properties.end()) {
            return;
        }

        // Animations and visual states only change the effective value; the
        // local value is still ours then.
        auto local = ReadLocalValueSafe(sender, property);
        if (SameLocalValue(local, applied->applied)) {
            return;
        }

        ULONGLONG now = GetTickCount64();
        if (now - applied->reapplyWindowStart > 1000) {
            applied->reapplyWindowStart = now;
            applied->reapplyCount = 0;
        }
        if (++applied->reapplyCount > 20) {
            if (applied->reapplyCount == 21) {
                DLOG(L"Something keeps overwriting a %s property, giving up",
                     winrt::get_class_name(sender).c_str());
            }
            return;
        }

        // Remember the new value as the one to restore later.
        applied->original = local;

        ModifyingScope scope(state);
        SetOrClearValue(sender, property, applied->applied);
    } catch (...) {
        DLOG(L"Property change handler failed: %08X", winrt::to_hresult().value);
    }
}

void Restore(wux::FrameworkElement const& element, AppliedProperty& applied) {
    try {
        element.UnregisterPropertyChangedCallback(applied.property,
                                                  applied.callbackToken);
    } catch (...) {
    }

    try {
        auto local = ReadLocalValueSafe(element, applied.property);
        if (!SameLocalValue(local, applied.applied)) {
            // Somebody else wrote the property since; keep their value.
            return;
        }
        SetOrClearValue(element, applied.property, applied.original);
    } catch (...) {
        DLOG(L"Restore failed: %08X", winrt::to_hresult().value);
    }
}

// Setting the taskbar's BackgroundFill before TaskbarBackground has applied
// its template can crash or be overridden, see
// https://github.com/ramensoftware/windows-11-taskbar-styling-guide/issues/4
bool NeedsDelayedSet(wux::FrameworkElement const& element,
                     wux::DependencyProperty const& property,
                     wf::IInspectable const& value) {
    return value != wux::DependencyProperty::UnsetValue() &&
           property == wux::Shapes::Shape::FillProperty() &&
           element.Name() == L"BackgroundFill" &&
           winrt::get_class_name(element) == L"Windows.UI.Xaml.Shapes.Rectangle";
}

void SetDelayed(InstanceHandle handle,
                wux::FrameworkElement const& element,
                wux::DependencyProperty const& property,
                MaterializedValue value) {
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        SetOrClearValue(element, property, value.value);
        return;
    }

    auto weak = winrt::make_weak(element);
    queue.TryEnqueue(DispatcherQueuePriority::High, [weak, property, value,
                                                      handle] {
        try {
            auto strong = weak.get();
            if (!strong || !t_state) {
                return;
            }
            auto it = t_state->elements.find(handle);
            if (it == t_state->elements.end()) {
                return;
            }
            const auto& properties = it->second.properties;
            bool stillApplied = std::any_of(
                properties.begin(), properties.end(),
                [&](const AppliedProperty& p) {
                    return p.property == property && p.applied == value.value;
                });
            if (!stillApplied) {
                return;
            }
            ModifyingScope scope(*t_state);
            SetOrClearValue(strong, property, value.value);
        } catch (...) {
            DLOG(L"Delayed set failed: %08X", winrt::to_hresult().value);
        }
    });
}

void Apply(ThreadState& state,
           InstanceHandle handle,
           TrackedElement& tracked,
           wux::FrameworkElement const& element,
           const Desired& desired,
           bool initial) {
    ModifyingScope scope(state);

    for (auto it = tracked.properties.begin(); it != tracked.properties.end();) {
        bool wanted = std::any_of(desired.begin(), desired.end(),
                                  [&](const ResolvedStyle* style) {
                                      return style->property == it->property;
                                  });
        if (wanted) {
            ++it;
            continue;
        }
        Restore(element, *it);
        it = tracked.properties.erase(it);
    }

    for (const ResolvedStyle* style : desired) {
        auto existing = std::find_if(
            tracked.properties.begin(), tracked.properties.end(),
            [&](const AppliedProperty& p) { return p.property == style->property; });
        if (existing != tracked.properties.end() &&
            existing->signature == style->signature) {
            continue;
        }

        MaterializedValue value;
        try {
            value = MaterializeValue(element, style->value);
        } catch (...) {
            DLOG(L"Can't create value for '%s': %08X", style->signature.c_str(),
                 winrt::to_hresult().value);
            continue;
        }

        if (existing != tracked.properties.end()) {
            auto local = ReadLocalValueSafe(element, style->property);
            if (!SameLocalValue(local, existing->applied)) {
                existing->original = local;
            }
            SetOrClearValue(element, style->property, value.value);
            existing->applied = value.value;
            existing->signature = style->signature;
            continue;
        }

        AppliedProperty applied;
        applied.property = style->property;
        applied.signature = style->signature;
        applied.original = ReadLocalValueSafe(element, style->property);
        applied.applied = value.value;

        if (initial && NeedsDelayedSet(element, style->property, value.value)) {
            SetDelayed(handle, element, style->property, value);
        } else {
            SetOrClearValue(element, style->property, value.value);
        }

        try {
            applied.callbackToken = element.RegisterPropertyChangedCallback(
                style->property,
                [handle](wux::DependencyObject const& sender,
                         wux::DependencyProperty const& property) {
                    OnPropertyChanged(handle, sender, property);
                });
        } catch (...) {
        }

        tracked.properties.push_back(std::move(applied));
    }
}

void Evaluate(ThreadState& state,
              InstanceHandle handle,
              TrackedElement& tracked,
              wux::FrameworkElement const& element,
              const Config& config,
              bool initial) {
    tracked.serial = config.serial;

    // Keeps the resolved values alive while they are being applied.
    auto cache = CacheFor(state, config);

    Desired desired;
    if (config.enabled) {
        desired = ComputeDesired(*cache, element, tracked.diagnosticsType, config);
    }
    if (desired.empty() && tracked.properties.empty()) {
        return;
    }
    Apply(state, handle, tracked, element, desired, initial);
}

void RemoveTracked(ThreadState& state, InstanceHandle handle) {
    auto it = state.elements.find(handle);
    if (it == state.elements.end()) {
        return;
    }
    if (auto element = it->second.element.get()) {
        ModifyingScope scope(state);
        for (auto& applied : it->second.properties) {
            Restore(element, applied);
        }
    }
    state.elements.erase(it);
}

void FlushPendingRemovals(ThreadState& state) {
    if (state.modifying || state.pendingRemovals.empty()) {
        return;
    }
    auto pending = std::move(state.pendingRemovals);
    state.pendingRemovals.clear();
    for (auto handle : pending) {
        RemoveTracked(state, handle);
    }
}

void Prune(ThreadState& state) {
    for (auto it = state.elements.begin(); it != state.elements.end();) {
        if (!it->second.element.get()) {
            it = state.elements.erase(it);
        } else {
            ++it;
        }
    }
}

void ReapplyAll() {
    if (!t_state) {
        return;
    }
    auto& state = *t_state;
    auto config = config::Current();

    std::vector<InstanceHandle> handles;
    handles.reserve(state.elements.size());
    for (const auto& [handle, tracked] : state.elements) {
        if (tracked.serial != config->serial) {
            handles.push_back(handle);
        }
    }

    for (auto handle : handles) {
        auto it = state.elements.find(handle);
        if (it == state.elements.end()) {
            continue;
        }
        auto element = it->second.element.get();
        if (!element) {
            state.elements.erase(it);
            continue;
        }
        try {
            Evaluate(state, handle, it->second, element, *config, false);
        } catch (...) {
            DLOG(L"Re-applying styles failed: %08X", winrt::to_hresult().value);
        }
    }

    FlushPendingRemovals(state);

    if (paths::IsExplorer()) {
        clickthrough::OnConfigApplied(config->enabled && config->clickThrough);
    }

    DLOG(L"Applied config %llu to %zu elements on this thread", config->serial,
         handles.size());
}

}  // namespace

void OnElementAdded(InstanceHandle handle,
                    wux::FrameworkElement const& element,
                    std::wstring_view diagnosticsType) {
    auto& state = StateFor(element);
    auto config = config::Current();

    auto [it, inserted] = state.elements.try_emplace(handle);
    if (!inserted) {
        // Reported again (e.g. re-parented): start over.
        if (auto previous = it->second.element.get()) {
            ModifyingScope scope(state);
            for (auto& applied : it->second.properties) {
                Restore(previous, applied);
            }
        }
        it->second = TrackedElement{};
    }

    auto& tracked = it->second;
    tracked.element = winrt::make_weak(element);
    tracked.diagnosticsType = diagnosticsType;
    Evaluate(state, handle, tracked, element, *config, true);

    if (paths::IsExplorer()) {
        if (winrt::get_class_name(element) == L"Taskbar.TaskbarFrame") {
            surface::EnsureForCurrentThread();
        }
        clickthrough::OnElementAdded(element,
                                     config->enabled && config->clickThrough);
    }

    FlushPendingRemovals(state);

    if (++state.addsSincePrune >= 512) {
        state.addsSincePrune = 0;
        Prune(state);
    }
}

void OnElementRemoved(InstanceHandle handle) {
    if (!t_state) {
        return;
    }
    auto& state = *t_state;
    if (state.modifying) {
        state.pendingRemovals.push_back(handle);
        return;
    }
    RemoveTracked(state, handle);
}

void OnConfigChanged() {
    RunOnUiThreads([] { ReapplyAll(); });
}

void RunOnUiThreads(std::function<void()> callback) {
    std::vector<UiThread> threads;
    {
        std::lock_guard lock(g_threadsMutex);
        threads = g_threads;
    }

    auto run = [callback] {
        try {
            callback();
        } catch (...) {
            DLOG(L"UI thread callback failed: %08X", winrt::to_hresult().value);
        }
    };

    for (auto& thread : threads) {
        try {
            if (thread.queue) {
                thread.queue.TryEnqueue(run);
            } else if (thread.core) {
                thread.core.RunAsync(
                    winrt::Windows::UI::Core::CoreDispatcherPriority::Normal, run);
            }
        } catch (...) {
        }
    }
}

}  // namespace desk::engine
