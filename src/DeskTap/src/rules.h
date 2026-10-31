#pragma once

namespace desk {

// One `Property=value` (or `Property:=<xaml>`) entry of a rule.
struct StyleSpec {
    std::wstring property;
    std::wstring value;
    bool isXaml = false;
};

// Matches a single element in a target path such as
// `Taskbar.TaskbarFrame > Grid#RootGrid`.
struct ElementMatcher {
    enum class Kind { Element, Wildcard };

    Kind kind = Kind::Element;
    std::wstring type;  // Full runtime class name.
    std::wstring name;  // x:Name, optional.
    int oneBasedIndex = 0;  // `[2]`: the element must be the 2nd child.
    std::vector<std::pair<std::wstring, std::wstring>> propertyConditions;
};

struct Rule {
    size_t id = 0;  // Index in Config::rules, used as a cache key.
    std::wstring targetText;
    ElementMatcher element;
    // Parent matchers, the immediate parent first.
    std::vector<ElementMatcher> parents;
    std::vector<StyleSpec> styles;
};

// The island with live network, CPU and memory figures that Desk adds to the
// left of the taskbar. Its look comes from the rules (`Border#DeskMonitorIsland`).
struct MonitorConfig {
    bool enabled = false;
    bool network = true;
    bool cpu = true;
    bool memory = true;
    std::wstring cpuLabel = L"CPU";
    std::wstring memoryLabel = L"RAM";
    winrt::Windows::UI::Color text{0xFF, 0xFF, 0xFF, 0xFF};
    winrt::Windows::UI::Color secondaryText{0xB3, 0xFF, 0xFF, 0xFF};
    winrt::Windows::UI::Color warning{0xFF, 0xFF, 0x99, 0xA4};
};

struct Config {
    // Given by Desk.exe, reported back once applied.
    uint64_t serial = 0;
    // The engine's own counter of what it applies; changes with the serial
    // and when the host is lost (the same serial, turned off).
    uint64_t revision = 0;
    bool enabled = false;
    // The Desk.exe that owns this configuration; empty for none (older Desk
    // versions, the emergency restore).
    std::wstring hostSession;
    DWORD hostPid = 0;
    uint64_t hostStart = 0;  // Process creation time (FILETIME).
    // Changed by Desk.exe to ask for a retry of a failed initialization.
    uint64_t recover = 0;
    bool clickThrough = false;
    std::vector<Rule> rules;
    MonitorConfig monitor;
    // Light or dark for the whole window content, whatever the Windows mode
    // (the Start menu's color scheme). Unset leaves the system's choice.
    std::optional<wux::ElementTheme> requestedTheme;
};

// Parses `{"target": "...", "styles": ["..."]}` entries. Invalid entries are
// logged and skipped.
std::vector<Rule> ParseRules(winrt::Windows::Data::Json::JsonArray const& array);

// Expands short type names: `Grid` -> `Windows.UI.Xaml.Controls.Grid`.
std::wstring AdjustTypeName(std::wstring_view type);

std::wstring_view Trim(std::wstring_view text);

}  // namespace desk
