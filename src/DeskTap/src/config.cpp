#include "pch.h"
#include "config.h"
#include "log.h"
#include "paths.h"

namespace desk::config {
namespace {

using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

std::mutex g_mutex;
std::shared_ptr<const Config> g_current = std::make_shared<Config>();
std::function<void()> g_onChanged;
std::wstring g_path;
FILETIME g_lastWriteTime{};
ULONGLONG g_lastSize = 0;

std::optional<std::wstring> ReadUtf8File(const std::wstring& path) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE |
                                  FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                              nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return std::nullopt;
    }

    std::string bytes;
    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) && size.QuadPart > 0 &&
        size.QuadPart < 16 * 1024 * 1024) {
        bytes.resize(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        if (!ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()),
                      &read, nullptr)) {
            bytes.clear();
        } else {
            bytes.resize(read);
        }
    }
    CloseHandle(file);

    if (bytes.empty()) {
        return std::nullopt;
    }

    size_t offset = 0;
    if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF &&
        static_cast<unsigned char>(bytes[1]) == 0xBB &&
        static_cast<unsigned char>(bytes[2]) == 0xBF) {
        offset = 3;
    }

    int length = MultiByteToWideChar(CP_UTF8, 0, bytes.data() + offset,
                                     static_cast<int>(bytes.size() - offset),
                                     nullptr, 0);
    if (length <= 0) {
        return std::nullopt;
    }

    std::wstring text(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data() + offset,
                        static_cast<int>(bytes.size() - offset), text.data(),
                        length);
    return text;
}

std::shared_ptr<Config> Parse(const std::wstring& text) {
    JsonObject root{nullptr};
    if (!JsonObject::TryParse(text, root) || !root) {
        DLOG(L"Config is not valid JSON");
        return nullptr;
    }

    auto config = std::make_shared<Config>();
    config->serial =
        static_cast<uint64_t>(root.GetNamedNumber(L"serial", 0));
    config->enabled = root.GetNamedBoolean(L"enabled", false);
    config->clickThrough = root.GetNamedBoolean(L"clickThrough", false);

    if (root.HasKey(L"rules") &&
        root.GetNamedValue(L"rules").ValueType() == JsonValueType::Array) {
        config->rules = ParseRules(root.GetNamedArray(L"rules"));
    }

    return config;
}

// Returns true when a new configuration was published.
bool ReloadIfChanged() {
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(g_path.c_str(), GetFileExInfoStandard,
                              &attributes)) {
        return false;
    }

    ULONGLONG size =
        (static_cast<ULONGLONG>(attributes.nFileSizeHigh) << 32) |
        attributes.nFileSizeLow;
    if (CompareFileTime(&attributes.ftLastWriteTime, &g_lastWriteTime) == 0 &&
        size == g_lastSize) {
        return false;
    }

    auto text = ReadUtf8File(g_path);
    if (!text) {
        // Probably being replaced right now; try again on the next tick.
        return false;
    }

    std::shared_ptr<Config> parsed;
    try {
        parsed = Parse(*text);
    } catch (...) {
        DLOG(L"Failed to parse config: %08X", winrt::to_hresult().value);
    }

    g_lastWriteTime = attributes.ftLastWriteTime;
    g_lastSize = size;

    if (!parsed) {
        return false;
    }

    {
        std::lock_guard lock(g_mutex);
        if (g_current && g_current->serial == parsed->serial &&
            parsed->serial != 0) {
            return false;
        }
        g_current = parsed;
    }

    DLOG(L"Loaded config serial %llu: enabled=%d, %zu rules", parsed->serial,
         parsed->enabled, parsed->rules.size());
    return true;
}

DWORD WINAPI WatchThread(LPVOID) {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    std::wstring dir = paths::RuntimeDir();
    HANDLE change = FindFirstChangeNotificationW(
        dir.c_str(), FALSE,
        FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_FILE_NAME |
            FILE_NOTIFY_CHANGE_SIZE);
    if (change == INVALID_HANDLE_VALUE) {
        DLOG(L"FindFirstChangeNotification failed (%lu), polling instead",
             GetLastError());
    }

    for (;;) {
        if (change != INVALID_HANDLE_VALUE) {
            if (WaitForSingleObject(change, 2000) == WAIT_OBJECT_0) {
                // Let the writer finish replacing the file.
                Sleep(50);
                FindNextChangeNotification(change);
            }
        } else {
            Sleep(1000);
        }

        bool changed = false;
        try {
            changed = ReloadIfChanged();
        } catch (...) {
            DLOG(L"Config reload failed: %08X", winrt::to_hresult().value);
        }

        if (changed && g_onChanged) {
            try {
                g_onChanged();
            } catch (...) {
                DLOG(L"Config change handler failed: %08X",
                     winrt::to_hresult().value);
            }
        }
    }
}

}  // namespace

void Start(std::function<void()> onChanged) {
    g_onChanged = std::move(onChanged);
    g_path = paths::RuntimeDir() + L"\\" + paths::ProcessName() + L".json";
    DLOG(L"Config path: %s", g_path.c_str());

    try {
        ReloadIfChanged();
    } catch (...) {
        DLOG(L"Initial config load failed: %08X", winrt::to_hresult().value);
    }

    HANDLE thread = CreateThread(nullptr, 0, WatchThread, nullptr, 0, nullptr);
    if (thread) {
        CloseHandle(thread);
    }
}

std::shared_ptr<const Config> Current() {
    std::lock_guard lock(g_mutex);
    return g_current;
}

}  // namespace desk::config
