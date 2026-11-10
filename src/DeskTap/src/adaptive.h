#pragma once
#include "values.h"

namespace desk::adaptive {
void OnElementAdded(InstanceHandle handle, wux::FrameworkElement const& element);
void OnElementRemoved(InstanceHandle handle);
void OnConfigApplied();
void OnThreadShutdown();
std::vector<ResolvedStyle> Styles(InstanceHandle handle);
}
