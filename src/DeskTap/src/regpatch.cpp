// Ported from the Windows 11 Taskbar Styler mod for Windhawk
// (https://github.com/ramensoftware/windhawk-mods, GPL-3.0).
#include "pch.h"
#include "regpatch.h"
#include "log.h"

#include <MinHook.h>

namespace desk::regpatch {
namespace {

using RegOpenKeyExW_t = decltype(&RegOpenKeyExW);
using RegQueryValueExW_t = decltype(&RegQueryValueExW);

RegOpenKeyExW_t g_originalOpen = nullptr;
RegQueryValueExW_t g_originalQuery = nullptr;

thread_local bool t_reportDisabled = false;

constexpr wchar_t kXamlDebugKey[] = L"Software\\Microsoft\\XAML\\Debug";
constexpr wchar_t kValueName[] = L"DisableCompositionDiag";

LSTATUS APIENTRY RegOpenKeyExW_Hook(HKEY key,
                                    LPCWSTR subKey,
                                    DWORD options,
                                    REGSAM desired,
                                    PHKEY result) {
    LSTATUS status = g_originalOpen(key, subKey, options, desired, result);
    if (status == ERROR_SUCCESS || !t_reportDisabled ||
        key != HKEY_LOCAL_MACHINE || !subKey ||
        _wcsicmp(subKey, kXamlDebugKey) != 0) {
        return status;
    }

    // The key usually doesn't exist, and the value is only queried when the
    // key could be opened, so hand out a key which exists.
    return g_originalOpen(HKEY_LOCAL_MACHINE, L"Software\\Microsoft", options,
                          desired, result);
}

LSTATUS APIENTRY RegQueryValueExW_Hook(HKEY key,
                                       LPCWSTR valueName,
                                       LPDWORD reserved,
                                       LPDWORD type,
                                       LPBYTE data,
                                       LPDWORD dataSize) {
    if (!t_reportDisabled || !valueName ||
        _wcsicmp(valueName, kValueName) != 0) {
        return g_originalQuery(key, valueName, reserved, type, data, dataSize);
    }

    if (type) {
        *type = REG_DWORD;
    }
    if (data && (!dataSize || *dataSize < sizeof(DWORD))) {
        if (dataSize) {
            *dataSize = sizeof(DWORD);
        }
        return ERROR_MORE_DATA;
    }
    if (data) {
        *reinterpret_cast<DWORD*>(data) = 1;
    }
    if (dataSize) {
        *dataSize = sizeof(DWORD);
    }
    return ERROR_SUCCESS;
}

}  // namespace

bool Install() {
    MH_STATUS status = MH_Initialize();
    if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
        DLOG(L"MH_Initialize failed: %d", status);
        return false;
    }

    HMODULE kernelBase = GetModuleHandleW(L"kernelbase.dll");
    if (!kernelBase) {
        return false;
    }
    auto open = reinterpret_cast<LPVOID>(GetProcAddress(kernelBase, "RegOpenKeyExW"));
    auto query =
        reinterpret_cast<LPVOID>(GetProcAddress(kernelBase, "RegQueryValueExW"));
    if (!open || !query) {
        DLOG(L"Registry functions not found in kernelbase");
        return false;
    }

    if (MH_CreateHook(open, reinterpret_cast<LPVOID>(&RegOpenKeyExW_Hook),
                      reinterpret_cast<LPVOID*>(&g_originalOpen)) != MH_OK ||
        MH_CreateHook(query, reinterpret_cast<LPVOID>(&RegQueryValueExW_Hook),
                      reinterpret_cast<LPVOID*>(&g_originalQuery)) != MH_OK) {
        DLOG(L"MH_CreateHook failed");
        return false;
    }

    status = MH_EnableHook(MH_ALL_HOOKS);
    if (status != MH_OK) {
        DLOG(L"MH_EnableHook failed: %d", status);
        return false;
    }

    DLOG(L"Registry hooks installed");
    return true;
}

ReportCompositionDiagDisabledScope::ReportCompositionDiagDisabledScope() {
    t_reportDisabled = true;
}

ReportCompositionDiagDisabledScope::~ReportCompositionDiagDisabledScope() {
    t_reportDisabled = false;
}

}  // namespace desk::regpatch
