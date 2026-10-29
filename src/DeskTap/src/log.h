#pragma once
#include <string>

namespace desk::log {

// Opens the log file. Safe to call once; later calls are ignored.
void Init(const std::wstring& path);

// printf-style logging, also mirrored to OutputDebugString.
void Write(const wchar_t* format, ...);

}  // namespace desk::log

#define DLOG(...) ::desk::log::Write(__VA_ARGS__)
