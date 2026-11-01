#pragma once

namespace desk::control {

// Starts the engine's control thread (once per process). It is the only place
// that decides:
// - which configuration the UI threads apply (reloads <runtime>\<process>.json;
//   turns styling off when the owning Desk.exe is gone, see core/host_watch.h);
// - when XAML diagnostics are (re)initialized (core/init_policy.h);
// - when the status report is written (status.h).
// Doing all of it on one thread keeps the decisions in order: a verdict about
// an old owner can't undo a configuration a new one already wrote.
void Start();

// Makes the control thread run soon (status changed, initialization ended).
void Wake();

}  // namespace desk::control
