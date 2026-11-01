#pragma once

namespace desk::clickthrough {

// Clips the taskbar window to the task list and the tray, so the empty parts
// become click-through. Explorer only, UI thread only.

// A DesktopWindowXamlSource (island root) was reported.
void OnIslandRoot(wf::IInspectable const& inspectable);

// An element was added. Only TaskbarFrame and SystemTrayFrame matter.
// `configured`: click-through in the newest configuration; it only counts
// until this thread applied a configuration (see core/clickthrough_gate.h).
void OnElementAdded(wux::FrameworkElement const& element, bool configured);

// Called after a configuration was applied on this thread. Turning it off
// removes every region this thread set, also when it was off already.
void OnConfigApplied(bool enabled);

// The thread's dispatcher is shutting down: removes its regions and drops its
// subscriptions while XAML is still usable.
void OnThreadShutdown();

}  // namespace desk::clickthrough
