// Production JSON parser, isolated from Explorer and the user's runtime directory.
#include "pch.h"
#include "config.h"
#include <filesystem>
#include <fstream>
#include <cstdio>

static std::wstring directory;
namespace desk::paths {
std::wstring RuntimeDir() { return directory; }
std::wstring ProcessName() { return L"config-test"; }
}
namespace desk::log { void Write(const wchar_t*, ...) {} }
namespace desk::clickthrough { HWND WindowForRoot(wux::XamlRoot const&) { return nullptr; } }

int main() try {
    winrt::init_apartment();
    wchar_t temp[MAX_PATH]; GetTempPathW(MAX_PATH, temp);
    directory = std::wstring(temp) + L"Desk-config-tests-" + std::to_wstring(GetCurrentProcessId());
    std::filesystem::create_directories(directory);
    struct Cleanup { ~Cleanup() { std::error_code e; std::filesystem::remove_all(directory, e); } } cleanup;
    auto check = [](bool good) { if (!good) throw std::runtime_error("Config assertion failed"); };
    {
        std::ofstream file(std::filesystem::path(directory) / L"config-test.json");
        file << R"({"serial":101,"enabled":true,"rules":[{"target":"Grid","styles":["Opacity=1"]}],"displays":[
          {"device":"DISPLAY1","rules":[{"target":"Grid","styles":["Opacity=0.2"]}],"monitor":{"enabled":true,"cpu":true},"layout":{"adaptive":true,"iconSize":32,"buttonWidth":54}},
          {"device":"DISPLAY2","rules":[{"target":"Grid","styles":["Opacity=0.8"]}],"monitor":{"enabled":false,"cpu":false},"layout":{"adaptive":false,"iconSize":26,"buttonWidth":44}}
        ]})";
    }
    desk::config::Init(); check(desk::config::ReloadIfChanged());
    auto parsed = desk::config::Parsed();
    check(parsed->displays.size() == 2);
    auto const& a = parsed->displays[0]; auto const& b = parsed->displays[1];
    check(a.iconSize == 32 && b.iconSize == 26 && a.adaptiveLayout && !b.adaptiveLayout);
    check(a.monitor.enabled && !b.monitor.enabled);
    check(parsed->rules[0].id != a.rules[0].id && a.rules[0].id != b.rules[0].id);
    check(a.rules[0].styles[0].value == L"0.2" && b.rules[0].styles[0].value == L"0.8");
    check(desk::config::UpdateEffective(false) && desk::config::Current()->enabled);
    check(desk::config::UpdateEffective(true) && !desk::config::Current()->enabled);
    check(desk::config::ForElement(*parsed, nullptr) == nullptr);
    check(!desk::config::ReloadIfChanged());
    std::puts("PASS per-display parse, independent rules, unique cache ids, host-loss disable, unknown-root fallback");
    return 0;
} catch (...) { std::puts("FAIL native display config"); return 1; }
