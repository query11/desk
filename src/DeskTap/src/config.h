#pragma once
#include "rules.h"

namespace desk::config {

// Remembers where <runtime>\<process>.json is and loads it once. Reloading
// is driven by the control thread (control.cpp).
void Init();

// Re-reads the file if it changed. Returns true when a new configuration was
// parsed.
bool ReloadIfChanged();

// What Desk.exe wrote last; never null.
std::shared_ptr<const Config> Parsed();

// Decides what the engine applies: the parsed configuration, or the same
// turned off when its owner is gone. Returns true when that changed, with a
// new revision. Control thread only.
bool UpdateEffective(bool hostLost);

// The configuration to apply; never null.
std::shared_ptr<const Config> Current();

// The heartbeat Desk.exe writes to <runtime>\host.json.
struct Heartbeat {
    std::wstring session;
    uint64_t seq = 0;
    bool exiting = false;
};
std::optional<Heartbeat> ReadHeartbeat();

}  // namespace desk::config
