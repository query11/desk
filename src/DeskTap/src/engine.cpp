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
#include "blur.h"
#include "clickthrough.h"
#include "config.h"
#include "control.h"
#include "log.h"
#include "monitor.h"
#include "paths.h"
#include "status.h"
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
    // Set when something kept overwriting the value; we stop competing.
    bool abandoned = false;
};

struct TrackedElement {
    winrt::weak_ref<wux::FrameworkElement> element;
    std::wstring diagnosticsType;
    std::vector<AppliedProperty> properties;
    uint64_t revision = UINT64_MAX;
};

using ConditionValues =
    std::vector<std::pair<wux::DependencyProperty, wf::IInspectable>>;
using StyleList = std::shared_ptr<const std::vector<ResolvedStyle>>;

// Resolved styles that outlive a configuration, by type and style text. A
// slider changes one constant; the rules that don't use it resolve to the
// same values, so they aren't parsed again.
using StyleTextCache = std::unordered_map<std::wstring, StyleList>;
constexpr size_t kMaxStyleTexts = 256;

// Resolved values for one configuration. Held by shared_ptr so that a config
// change in the middle of applying can't free values still in use.
struct Cache {
    uint64_t revision = 0;
    std::unordered_map<std::wstring, StyleList> styles;
    std::unordered_map<std::wstring, ConditionValues> conditions;
    StyleTextCache* byText = nullptr;  // The thread's, outlives this.
};

struct ThreadState {
    std::unordered_map<InstanceHandle, TrackedElement> elements;
    std::shared_ptr<Cache> cache;
    StyleTextCache stylesByText;
    int modifying = 0;
    std::vector<InstanceHandle> pendingRemovals;
    size_t addsSincePrune = 0;

    // The window content whose RequestedTheme we set (Start menu scheme).
    winrt::weak_ref<wux::FrameworkElement> themedRoot;
    wux::ElementTheme originalTheme = wux::ElementTheme::Default;
    std::optional<wux::ElementTheme> wantedTheme;
    int64_t themeCallbackToken = 0;
    uint64_t themeRevision = UINT64_MAX;

    // What this thread reports to Desk.exe (status.h): the revision it fully
    // applied, and styles that failed for it.
    uint64_t appliedRevision = 0;
    uint64_t appliedSerial = 0;
    uint32_t errors = 0;
    bool reportScheduled = false;
};

// Each XAML UI thread has its own elements; all access happens on that
// thread. Created when the thread reports its first element, deleted on the
// thread when its dispatcher shuts down (OnThreadShutdown), so the XAML
// objects it holds are released where they belong, while XAML still works.
// Callbacks that arrive later find no state and do nothing.
thread_local ThreadState* t_state = nullptr;

struct UiThread {
    DWORD id = 0;
    DispatcherQueue queue{nullptr};
    winrt::Windows::UI::Core::CoreDispatcher core{nullptr};
};

// Threads to reach for configuration changes. A thread leaves when its
// dispatcher shuts down or refuses work.
std::mutex g_threadsMutex;
std::vector<UiThread> g_threads;

void ForgetThread(DWORD id) {
    {
        std::lock_guard lock(g_threadsMutex);
        g_threads.erase(std::remove_if(g_threads.begin(), g_threads.end(),
                                       [id](const UiThread& t) { return t.id == id; }),
                        g_threads.end());
    }
    status::ForgetThread(id);
    control::Wake();
}

void OnThreadShutdown();

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
        status::Count(status::kUiThreadsSeen);

        UiThread thread;
        thread.id = GetCurrentThreadId();
        thread.queue = DispatcherQueue::GetForCurrentThread();
        if (thread.queue) {
            try {
                thread.queue.ShutdownStarting([](auto&&, auto&&) {
                    try {
                        OnThreadShutdown();
                    } catch (...) {
                    }
                });
            } catch (...) {
                DLOG(L"Can't watch the dispatcher's shutdown: %08X",
                     winrt::to_hresult().value);
            }
        } else {
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
    if (!state.cache || state.cache->revision != config.revision) {
        state.cache = std::make_shared<Cache>();
        state.cache->revision = config.revision;
        state.cache->byText = &state.stylesByText;
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
    if (it != cache.styles.end()) {
        return *it->second;
    }

    // Markup extensions ({ThemeResource ...}) can resolve differently later,
    // so only plain values are kept across configurations.
    bool plain = true;
    std::wstring text = rule.element.type;
    text += L'\x1f';
    text += diagnosticsType;
    for (const auto& style : rule.styles) {
        plain &= style.value.find(L'{') == std::wstring::npos;
        text += L'\x1f';
        text += style.property;
        text += style.isXaml ? L":=" : L"=";
        text += style.value;
    }

    StyleList list;
    if (plain && cache.byText) {
        if (auto found = cache.byText->find(text); found != cache.byText->end()) {
            list = found->second;
            status::Count(status::kStyleCacheHits);
        }
    }
    if (!list) {
        list = std::make_shared<const std::vector<ResolvedStyle>>(
            ResolveStyles(rule.element.type, diagnosticsType, rule.styles));
        status::Count(status::kStyleResolves);
        if (plain && cache.byText) {
            if (cache.byText->size() >= kMaxStyleTexts) {
                cache.byText->clear();
            }
            cache.byText->emplace(std::move(text), list);
        }
    }
    return *cache.styles.emplace(std::move(key), std::move(list)).first->second;
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
        if (applied == properties.end() || applied->abandoned) {
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
            applied->abandoned = true;
            DLOG(L"Something keeps overwriting a %s property, giving up",
                 winrt::get_class_name(sender).c_str());
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
        if (applied.callbackToken) {
            element.UnregisterPropertyChangedCallback(applied.property,
                                                      applied.callbackToken);
            status::Count(status::kPropertyCallbacksReleased);
        }
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

// Logs the brush a rule replaces, once per element name and brush type. The
// details of an AcrylicBrush show how the system draws a surface natively.
void LogReplacedBrush(wux::FrameworkElement const& element,
                      wf::IInspectable const& original) {
    auto brush = original.try_as<wuxm::Brush>();
    if (!brush) {
        return;
    }
    static thread_local std::unordered_set<std::wstring> logged;
    std::wstring name(element.Name());
    std::wstring type(winrt::get_class_name(brush));
    if (!logged.insert(name + L"|" + type).second) {
        return;
    }
    if (auto acrylic = brush.try_as<wuxm::AcrylicBrush>()) {
        auto tint = acrylic.TintColor();
        auto luminosity = acrylic.TintLuminosityOpacity();
        DLOG(L"%s#%s had AcrylicBrush: source=%s tint=#%02X%02X%02X%02X "
             L"tintOpacity=%.2f luminosity=%s",
             winrt::get_class_name(element).c_str(), name.c_str(),
             acrylic.BackgroundSource() == wuxm::AcrylicBackgroundSource::HostBackdrop
                 ? L"HostBackdrop"
                 : L"Backdrop",
             tint.A, tint.R, tint.G, tint.B, acrylic.TintOpacity(),
             luminosity ? std::to_wstring(luminosity.Value()).c_str() : L"auto");
    } else {
        DLOG(L"%s#%s had %s", winrt::get_class_name(element).c_str(),
             name.c_str(), type.c_str());
    }
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

        // The material's numbers changed (a slider): the brush that is set
        // takes them, instead of a new brush, a new effect and a property
        // write for every element.
        if (existing != tracked.properties.end()) {
            if (auto* blur = std::get_if<BlurParams>(&style->value)) {
                if (auto brush = existing->applied.try_as<IDeskBlurBrush>()) {
                    auto local = ReadLocalValueSafe(element, style->property);
                    if (SameLocalValue(local, existing->applied) &&
                        SUCCEEDED(brush->UpdateParams(blur))) {
                        existing->signature = style->signature;
                        continue;
                    }
                }
            }
        }

        MaterializedValue value;
        try {
            value = MaterializeValue(element, style->value);
        } catch (...) {
            DLOG(L"Can't create value for '%s': %08X", style->signature.c_str(),
                 winrt::to_hresult().value);
            state.errors++;
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
        LogReplacedBrush(element, applied.original);

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
            status::Count(status::kPropertyCallbacks);
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
    tracked.revision = config.revision;

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
            // The callbacks went with the element.
            status::Count(status::kPropertyCallbacksReleased,
                          static_cast<int64_t>(it->second.properties.size()));
            it = state.elements.erase(it);
        } else {
            ++it;
        }
    }
}

// The Start menu's color scheme: the window content gets the requested
// theme, so text and controls switch along with the material. The Windows
// mode isn't changed. The per-element rules cover parts with their own theme.
void ApplyRequestedTheme(const Config& config) {
    if (!t_state || !paths::IsStartMenu()) {
        return;
    }
    auto& state = *t_state;
    state.themeRevision = config.revision;
    state.wantedTheme = config.enabled ? config.requestedTheme : std::nullopt;

    wux::FrameworkElement root{nullptr};
    try {
        if (auto window = wux::Window::Current()) {
            root = window.Content().try_as<wux::FrameworkElement>();
        }
    } catch (...) {
    }

    auto previous = state.themedRoot.get();
    if (previous && (previous != root || !state.wantedTheme)) {
        try {
            previous.UnregisterPropertyChangedCallback(
                wux::FrameworkElement::RequestedThemeProperty(),
                state.themeCallbackToken);
            previous.RequestedTheme(state.originalTheme);
        } catch (...) {
        }
        state.themedRoot = nullptr;
        DLOG(L"Window content theme restored");
    }
    if (!state.wantedTheme || !root) {
        return;
    }

    if (state.themedRoot.get() != root) {
        state.originalTheme = root.RequestedTheme();
        state.themedRoot = winrt::make_weak(root);
        // The shell sets the theme again when the Windows mode changes.
        state.themeCallbackToken = root.RegisterPropertyChangedCallback(
            wux::FrameworkElement::RequestedThemeProperty(),
            [](wux::DependencyObject const& sender, wux::DependencyProperty const&) {
                try {
                    if (!t_state || !t_state->wantedTheme) {
                        return;
                    }
                    auto element = sender.as<wux::FrameworkElement>();
                    if (element.RequestedTheme() != *t_state->wantedTheme) {
                        t_state->originalTheme = element.RequestedTheme();
                        element.RequestedTheme(*t_state->wantedTheme);
                    }
                } catch (...) {
                }
            });
    }
    if (root.RequestedTheme() != *state.wantedTheme) {
        root.RequestedTheme(*state.wantedTheme);
    }
    DLOG(L"Window content (%s) theme: %s",
         winrt::get_class_name(root).c_str(),
         *state.wantedTheme == wux::ElementTheme::Dark ? L"dark" : L"light");
}

// Tells Desk.exe how far this thread got (see core/apply_status.h).
void Report(ThreadState& state) {
    uint32_t matched = 0;
    for (const auto& [handle, tracked] : state.elements) {
        if (!tracked.properties.empty() && tracked.element.get()) {
            matched++;
        }
    }
    status::ReportThread(GetCurrentThreadId(),
                         {state.appliedRevision, state.appliedSerial, matched,
                          state.errors});
    control::Wake();
}

// Reports once the current burst of added elements is through.
void ScheduleReport(ThreadState& state) {
    if (state.reportScheduled) {
        return;
    }
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        Report(state);
        return;
    }
    state.reportScheduled = queue.TryEnqueue(DispatcherQueuePriority::Low, [] {
        try {
            if (t_state) {
                t_state->reportScheduled = false;
                Report(*t_state);
            }
        } catch (...) {
        }
    });
    if (!state.reportScheduled) {
        status::Count(status::kDispatcherPostFailures);
    }
}

// The thread's dispatcher is shutting down: everything this thread holds is
// released here, on the thread, while XAML still works. The elements are going
// away with the tree, so they aren't restored.
void OnThreadShutdown() {
    DWORD id = GetCurrentThreadId();
    if (paths::IsExplorer()) {
        monitor::OnThreadShutdown();
        clickthrough::OnThreadShutdown();
    }
    DeskBlurBrush::ReleaseThreadCaches();
    if (t_state) {
        size_t callbacks = 0;
        for (auto& [handle, tracked] : t_state->elements) {
            callbacks += tracked.properties.size();
        }
        status::Count(status::kPropertyCallbacksReleased, static_cast<int64_t>(callbacks));
        delete t_state;
        t_state = nullptr;
        status::Count(status::kUiThreadsReleased);
    }
    ForgetThread(id);
    DLOG(L"XAML thread shutting down; its state was released");
}

void ReapplyAll() {
    if (!t_state) {
        return;
    }
    auto& state = *t_state;
    auto config = config::Current();
    if (state.appliedRevision != config->revision) {
        state.errors = 0;
    }

    std::vector<InstanceHandle> handles;
    handles.reserve(state.elements.size());
    for (const auto& [handle, tracked] : state.elements) {
        if (tracked.revision != config->revision) {
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
            state.errors++;
            DLOG(L"Re-applying styles failed: %08X", winrt::to_hresult().value);
        }
    }

    FlushPendingRemovals(state);

    if (paths::IsExplorer()) {
        clickthrough::OnConfigApplied(config->enabled && config->clickThrough);
        monitor::OnConfigApplied(*config);
    }
    ApplyRequestedTheme(*config);

    state.appliedRevision = config->revision;
    state.appliedSerial = config->serial;
    Report(state);
    DLOG(L"Applied config %llu (revision %llu, %s) to %zu elements on this thread",
         config->serial, config->revision, config->enabled ? L"on" : L"off",
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

    if (state.appliedRevision == 0) {
        // A new thread starts out with the configuration it first sees; a
        // later change reaches it through ReapplyAll.
        state.appliedRevision = config->revision;
        state.appliedSerial = config->serial;
    }

    auto& tracked = it->second;
    tracked.element = winrt::make_weak(element);
    tracked.diagnosticsType = diagnosticsType;
    try {
        Evaluate(state, handle, tracked, element, *config, true);
    } catch (...) {
        state.errors++;
        DLOG(L"Applying styles failed: %08X", winrt::to_hresult().value);
    }

    if (paths::IsExplorer()) {
        if (winrt::get_class_name(element) == L"Taskbar.TaskbarFrame") {
            surface::EnsureForCurrentThread();
            monitor::OnTaskbarFrameAdded(element);
        }
        clickthrough::OnElementAdded(element,
                                     config->enabled && config->clickThrough);
    }

    if (paths::IsStartMenu() && state.themeRevision != config->revision) {
        state.themeRevision = config->revision;
        if (auto queue = DispatcherQueue::GetForCurrentThread()) {
            queue.TryEnqueue(DispatcherQueuePriority::Low, [] {
                try {
                    ApplyRequestedTheme(*config::Current());
                } catch (...) {
                }
            });
        }
    }

    FlushPendingRemovals(state);
    ScheduleReport(state);

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
        bool queued = false;
        try {
            if (thread.queue) {
                queued = thread.queue.TryEnqueue(run);
            } else if (thread.core) {
                thread.core.RunAsync(
                    winrt::Windows::UI::Core::CoreDispatcherPriority::Normal, run);
                queued = true;
            }
        } catch (...) {
        }
        if (!queued) {
            // The dispatcher is gone or shutting down: stop trying.
            status::Count(status::kDispatcherPostFailures);
            DLOG(L"XAML thread %lu doesn't take work any more; forgetting it", thread.id);
            ForgetThread(thread.id);
        }
    }
}

}  // namespace desk::engine
