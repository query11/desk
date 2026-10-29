#pragma once

namespace desk::winutil {

// Runs `callback` synchronously on the thread which owns `window`.
bool RunOnWindowThread(HWND window, const std::function<void()>& callback);

// SetWindowSubclass, which must be called on the window's own thread.
bool SetWindowSubclassFromAnyThread(HWND window,
                                    SUBCLASSPROC proc,
                                    UINT_PTR id,
                                    DWORD_PTR data = 0);

bool IsTaskbarWindow(HWND window);

}  // namespace desk::winutil
