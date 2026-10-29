#pragma once
#include "rules.h"

namespace desk::config {

// Loads <runtime>\<process>.json and starts watching it for changes.
void Start(std::function<void()> onChanged);

// The current configuration; never null.
std::shared_ptr<const Config> Current();

}  // namespace desk::config
