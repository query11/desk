#pragma once

namespace desk::clickthrough {

// Clips the taskbar window to the task list and the tray, so the empty parts
// become click-through. Explorer only, UI thread only.

// A DesktopWindowXamlSource (island root) was reported.
void OnIslandRoot(wf::IInspectable const& inspectable);

// An element was added. Only TaskbarFrame and SystemTrayFrame matter.
void OnElementAdded(wux::FrameworkElement const& element, bool enabled);

// Called after a configuration change was applied on this thread.
void OnConfigApplied(bool enabled);

}  // namespace desk::clickthrough
