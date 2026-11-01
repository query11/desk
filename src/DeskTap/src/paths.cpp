#include "pch.h"
#include "paths.h"

namespace desk::paths {
namespace {

std::wstring ParentDir(const std::wstring& path) {
    auto pos = path.find_last_of(L"\\/");
    return pos == std::wstring::npos ? std::wstring() : path.substr(0, pos);
}

std::wstring GetModuleFileNameString(HMODULE module) {
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        DWORD length = GetModuleFileNameW(module, buffer.data(),
                                          static_cast<DWORD>(buffer.size()));
        if (length == 0) {
            return {};
        }
        if (length < buffer.size()) {
            buffer.resize(length);
            return buffer;
        }
        buffer.resize(buffer.size() * 2);
    }
}

}  // namespace

HMODULE Module() {
    static HMODULE module = [] {
        HMODULE result = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&Module), &result);
        return result;
    }();
    return module;
}

std::wstring ModulePath() {
    return GetModuleFileNameString(Module());
}

std::wstring BaseDir() {
    return ParentDir(ParentDir(ModulePath()));
}

std::wstring RuntimeDir() {
    return BaseDir() + L"\\runtime";
}

std::wstring LogsDir() {
    return BaseDir() + L"\\logs";
}

std::wstring StateDir() {
    return BaseDir() + L"\\state";
}

std::wstring ProcessName() {
    static std::wstring name = [] {
        auto path = GetModuleFileNameString(nullptr);
        auto pos = path.find_last_of(L"\\/");
        std::wstring fileName =
            pos == std::wstring::npos ? path : path.substr(pos + 1);
        // Called from DllMain, so no user32 (CharLower) here.
        for (auto& c : fileName) {
            c = static_cast<wchar_t>(towlower(c));
        }
        return fileName;
    }();
    return name;
}

bool IsExplorer() {
    return ProcessName() == L"explorer.exe";
}

bool IsStartMenu() {
    return ProcessName() == L"startmenuexperiencehost.exe";
}

}  // namespace desk::paths
