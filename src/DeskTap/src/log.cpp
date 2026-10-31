#include "pch.h"
#include "log.h"

#include <cstdarg>

namespace desk::log {
namespace {

// The same policy as Desk.exe's log: rotate before a file grows past the
// limit (also at start-up, instead of deleting the previous run's log), keep
// a few old files, and write a repeated message once.
constexpr LONGLONG kMaxLogSize = 1024 * 1024;
constexpr int kHistory = 3;  // tap-<process>.1.log ... .3.log
constexpr ULONGLONG kRepeatWindowMs = 10'000;

std::mutex g_mutex;
HANDLE g_file = INVALID_HANDLE_VALUE;
bool g_initialized = false;
std::wstring g_path;
LONGLONG g_size = 0;
LONGLONG g_rotateRetryAt = 0;
uint64_t g_dropped = 0;

std::wstring g_lastMessage;
ULONGLONG g_lastMessageAt = 0;
uint32_t g_repeats = 0;

std::wstring HistoryPath(int index) {
    if (index == 0) {
        return g_path;
    }
    // tap-explorer.exe.log -> tap-explorer.exe.1.log
    auto dot = g_path.rfind(L'.');
    return g_path.substr(0, dot) + L"." + std::to_wstring(index) + g_path.substr(dot);
}

void Open() {
    // Shared, including delete: Desk.exe's diagnostics export reads it, and
    // another process with the engine may rotate it.
    g_file = CreateFileW(g_path.c_str(), FILE_APPEND_DATA,
                         FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                         nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    LARGE_INTEGER size{};
    g_size = g_file != INVALID_HANDLE_VALUE && GetFileSizeEx(g_file, &size)
                 ? size.QuadPart
                 : 0;
}

// Caller holds the mutex.
void Rotate() {
    if (g_file != INVALID_HANDLE_VALUE) {
        CloseHandle(g_file);
        g_file = INVALID_HANDLE_VALUE;
    }
    bool ok = true;
    DeleteFileW(HistoryPath(kHistory).c_str());
    for (int i = kHistory - 1; i >= 0; i--) {
        auto from = HistoryPath(i);
        if (GetFileAttributesW(from.c_str()) == INVALID_FILE_ATTRIBUTES) {
            continue;
        }
        if (!MoveFileExW(from.c_str(), HistoryPath(i + 1).c_str(),
                         MOVEFILE_REPLACE_EXISTING)) {
            ok = false;
        }
    }
    Open();
    // When renaming failed, keep writing and try again a bit later.
    g_rotateRetryAt = ok ? 0 : g_size + kMaxLogSize / 4;
}

// Caller holds the mutex.
void WriteLine(const wchar_t* line) {
    if (g_file == INVALID_HANDLE_VALUE) {
        Open();
        if (g_file == INVALID_HANDLE_VALUE) {
            g_dropped++;
            return;
        }
    }

    int length = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0,
                                     nullptr, nullptr);
    if (length <= 1) {
        return;
    }
    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), length, nullptr,
                        nullptr);
    LONGLONG bytes = length - 1;

    if (g_size > 0 && g_size + bytes > kMaxLogSize && g_size >= g_rotateRetryAt) {
        Rotate();
        if (g_file == INVALID_HANDLE_VALUE) {
            g_dropped++;
            return;
        }
    }
    if (g_size + bytes > kMaxLogSize * 2) {
        // Rotating keeps failing: protect the disk.
        g_dropped++;
        return;
    }

    DWORD written = 0;
    if (WriteFile(g_file, utf8.data(), static_cast<DWORD>(bytes), &written, nullptr)) {
        g_size += written;
    } else {
        g_dropped++;
    }
}

void FormatLine(wchar_t (&line)[2200], const wchar_t* message) {
    SYSTEMTIME time;
    GetLocalTime(&time);
    swprintf_s(line, L"[%02u:%02u:%02u.%03u %5lu] %s\r\n", time.wHour,
               time.wMinute, time.wSecond, time.wMilliseconds,
               GetCurrentThreadId(), message);
}

}  // namespace

void Init(const std::wstring& path) {
    std::lock_guard lock(g_mutex);
    if (g_initialized) {
        return;
    }
    g_initialized = true;
    g_path = path;
    // A log left too large by the previous run is moved to the history on
    // the first write, not deleted.
    Open();
}

void Write(const wchar_t* format, ...) {
    wchar_t message[2048];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, format, args);
    va_end(args);

    wchar_t line[2200];
    FormatLine(line, message);
    OutputDebugStringW(L"[DeskTap] ");
    OutputDebugStringW(line);

    std::lock_guard lock(g_mutex);
    if (!g_initialized) {
        return;
    }

    ULONGLONG now = GetTickCount64();
    if (g_lastMessage == message && now - g_lastMessageAt < kRepeatWindowMs) {
        g_repeats++;
        return;
    }
    if (g_repeats > 0) {
        wchar_t note[64];
        swprintf_s(note, L"(last message repeated %u more times)", g_repeats);
        wchar_t repeated[2200];
        FormatLine(repeated, note);
        WriteLine(repeated);
        g_repeats = 0;
    }
    g_lastMessage = message;
    g_lastMessageAt = now;

    if (g_dropped > 0 && g_file != INVALID_HANDLE_VALUE) {
        wchar_t note[64];
        swprintf_s(note, L"(%llu log lines could not be written)",
                   static_cast<unsigned long long>(g_dropped));
        wchar_t dropped[2200];
        FormatLine(dropped, note);
        g_dropped = 0;
        WriteLine(dropped);
    }
    WriteLine(line);
}

}  // namespace desk::log
