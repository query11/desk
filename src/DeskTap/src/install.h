#pragma once

namespace desk::install {

// Whether DeskTap should start its engine in this process.
bool IsTargetProcess();

// Started from DllMain in target processes: loads the configuration and
// initializes XAML diagnostics with DeskTap as the TAP.
DWORD WINAPI InstallThread(LPVOID);

}  // namespace desk::install
