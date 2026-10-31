#pragma once

namespace desk::install {

// Whether DeskTap should start its engine in this process.
bool IsTargetProcess();

// Started from DllMain in target processes: claims the process for this
// engine, then hands over to the control thread, which initializes XAML
// diagnostics once styling is on (and again after failures).
DWORD WINAPI InstallThread(LPVOID);

struct InitResult {
    bool ok = false;
    const wchar_t* stage = nullptr;
    HRESULT hr = S_OK;
};

// Initializes XAML diagnostics with DeskTap as the TAP and waits until the
// visual tree watcher is advised. Called by one worker at a time. Steps that
// succeeded before are not repeated: diagnostics are only initialized again
// when no TAP site ever arrived, and a failed advise is retried on the
// existing watcher.
InitResult Initialize();

// From the TAP site and the watcher.
void OnSiteSet();
void OnAdviseResult(HRESULT hr);

}  // namespace desk::install
