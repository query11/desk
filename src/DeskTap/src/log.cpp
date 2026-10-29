#include "pch.h"
#include "log.h"

#include <cstdarg>

namespace desk::log {
namespace {

std::mutex g_mutex;
HANDLE g_file = INVALID_HANDLE_VALUE;
bool g_initialized = false;

constexpr LONGLONG kMaxLogSize = 2 * 1024 * 1024;

}  // namespace

void Init(const std::wstring& path) {
    std::lock_guard lock(g_mutex);
    if (g_initialized) {
        return;
    }
    g_initialized = true;

    // Start over when the previous log grew too large.
    DWORD disposition = OPEN_ALWAYS;
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) {
        LARGE_INTEGER size;
        size.HighPart = static_cast<LONG>(attributes.nFileSizeHigh);
        size.LowPart = attributes.nFileSizeLow;
        if (size.QuadPart > kMaxLogSize) {
            disposition = CREATE_ALWAYS;
        }
    }

    g_file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                         FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                         nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr);
}

void Write(const wchar_t* format, ...) {
    wchar_t message[2048];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, format, args);
    va_end(args);

    SYSTEMTIME time;
    GetLocalTime(&time);

    wchar_t line[2200];
    swprintf_s(line, L"[%02u:%02u:%02u.%03u %5lu] %s\r\n", time.wHour,
               time.wMinute, time.wSecond, time.wMilliseconds,
               GetCurrentThreadId(), message);

    OutputDebugStringW(L"[DeskTap] ");
    OutputDebugStringW(line);

    std::lock_guard lock(g_mutex);
    if (g_file == INVALID_HANDLE_VALUE) {
        return;
    }

    int length = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0,
                                     nullptr, nullptr);
    if (length <= 1) {
        return;
    }

    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), length, nullptr,
                        nullptr);
    DWORD written = 0;
    WriteFile(g_file, utf8.data(), static_cast<DWORD>(length - 1), &written,
              nullptr);
}

}  // namespace desk::log
