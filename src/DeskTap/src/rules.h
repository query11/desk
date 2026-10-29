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

struct Config {
    uint64_t serial = 0;
    bool enabled = false;
    bool clickThrough = false;
    std::vector<Rule> rules;
};

// Parses `{"target": "...", "styles": ["..."]}` entries. Invalid entries are
// logged and skipped.
std::vector<Rule> ParseRules(winrt::Windows::Data::Json::JsonArray const& array);

// Expands short type names: `Grid` -> `Windows.UI.Xaml.Controls.Grid`.
std::wstring AdjustTypeName(std::wstring_view type);

std::wstring_view Trim(std::wstring_view text);

}  // namespace desk
