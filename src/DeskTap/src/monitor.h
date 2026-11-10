#pragma once
#include "rules.h"

namespace desk::monitor {

// An island on the left of the taskbar with the live network speed and the
// CPU and memory usage. The engine creates the elements; the theme styles
// `Border#DeskMonitorIsland` like the other islands. Explorer only, UI thread
// only.

// A Taskbar.TaskbarFrame was reported; the island goes into its parent.
void OnTaskbarFrameAdded(wux::FrameworkElement const& frame);

// The configuration changed: adds, updates or removes this thread's islands,
// and starts or stops sampling what they show.
void OnConfigApplied(const Config& config);

// Widths in logical pixels, independent of the current compact state.
std::pair<double, double> LayoutWidths(wux::FrameworkElement const& frame);
void SetCompactLevel(wux::FrameworkElement const& frame, int level);

// The thread's dispatcher is shutting down: removes its islands and stops
// its sampling while XAML is still usable.
void OnThreadShutdown();

}  // namespace desk::monitor
