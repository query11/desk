#pragma once

namespace desk::engine {

// Called on the element's UI thread when XAML diagnostics reports it.
void OnElementAdded(InstanceHandle handle,
                    wux::FrameworkElement const& element,
                    std::wstring_view diagnosticsType);

// Called on the UI thread when an element leaves the tree. Only the handle is
// valid at this point.
void OnElementRemoved(InstanceHandle handle);

// Called from any thread after the configuration changed.
void OnConfigChanged();

// Runs `callback` on every XAML UI thread seen so far.
void RunOnUiThreads(std::function<void()> callback);

}  // namespace desk::engine
