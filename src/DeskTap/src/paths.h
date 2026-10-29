#pragma once
#include <string>

namespace desk::paths {

// The module handle of DeskTap.dll.
HMODULE Module();

// Full path of DeskTap.dll.
std::wstring ModulePath();

// The Desk data directory. The DLL lives in <base>\bin, so this is the parent
// of the DLL's directory.
std::wstring BaseDir();

// <base>\runtime: the resolved rules written by Desk.exe.
std::wstring RuntimeDir();

// <base>\logs.
std::wstring LogsDir();

// The lowercase file name of the current process, e.g. "explorer.exe".
std::wstring ProcessName();

bool IsExplorer();
bool IsStartMenu();

}  // namespace desk::paths
