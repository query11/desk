#pragma once
#include "rules.h"

namespace desk::monitor {

// An island on the left of the taskbar with the live network speed and the
// CPU and memory usage. The engine creates the elements; the theme styles
// `Border#DeskMonitorIsland` like the other islands. Explorer only, UI thread
// only.

// A Taskbar.TaskbarFrame was reported; the island goes into its parent.
void OnTaskbarFrameAdded(wux::FrameworkElement const& frame);

// The configuration changed: adds, updates or removes this thread's islands.
void OnConfigApplied(const Config& config);

}  // namespace desk::monitor
