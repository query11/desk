// Decisions without Windows dependencies, unit-tested in tests/core_tests.cpp.
#pragma once
#include <optional>

namespace desk::core {

// Whether click-through is on, as far as one XAML thread is concerned.
//
// Only applying a configuration on the thread changes it. An element added
// in between follows what the thread applied last, not the newest
// configuration: otherwise, when "off" was published but not applied yet, an
// added element would turn the flag off first and the following "off" would
// look like no change and skip clearing the regions. Turning it off always
// clears what this thread clipped, however often it happens.
class ClickThroughGate {
public:
    enum class Action { Apply, Clear };

    // `configured`: the newest configuration, used until the thread applied one.
    bool EnabledForNewElement(bool configured) const {
        return m_applied.value_or(configured);
    }

    // For layout passes and window changes: nothing before the first application.
    bool Enabled() const { return m_applied.value_or(false); }

    Action OnConfigApplied(bool enabled) {
        m_applied = enabled;
        return enabled ? Action::Apply : Action::Clear;
    }

private:
    std::optional<bool> m_applied;
};

}  // namespace desk::core
