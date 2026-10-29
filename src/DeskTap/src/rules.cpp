// Target/style syntax follows the Windows 11 Taskbar Styler mod for Windhawk
// (GPL-3.0, https://github.com/ramensoftware/windhawk-mods) so that themes
// written for it can be reused. Supported: `Type`, `#Name`, `[N]`,
// `[Property=Value]`, `*` and ` > `. Visual-state rules (`@State`) are not
// supported yet and are skipped.
#include "pch.h"
#include "rules.h"
#include "log.h"

namespace desk {
namespace {

using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

// Splits on `separator` while ignoring separators inside `[...]`.
std::vector<std::wstring_view> SplitOutsideBrackets(std::wstring_view text,
                                                    wchar_t separator) {
    std::vector<std::wstring_view> result;
    size_t begin = 0;
    int depth = 0;
    for (size_t i = 0; i < text.size(); i++) {
        wchar_t c = text[i];
        if (c == L'[') {
            depth++;
        } else if (c == L']') {
            depth = std::max(0, depth - 1);
        } else if (c == separator && depth == 0) {
            result.push_back(text.substr(begin, i - begin));
            begin = i + 1;
        }
    }
    result.push_back(text.substr(begin));
    return result;
}

std::optional<ElementMatcher> ParseMatcher(std::wstring_view text,
                                           std::wstring* error) {
    text = Trim(text);
    ElementMatcher matcher;
    if (text == L"*") {
        matcher.kind = ElementMatcher::Kind::Wildcard;
        return matcher;
    }

    size_t i = text.find_first_of(L"#@[");
    matcher.type = AdjustTypeName(Trim(text.substr(0, i)));
    if (Trim(text.substr(0, i)).empty()) {
        *error = L"empty type";
        return std::nullopt;
    }

    while (i != std::wstring_view::npos && i < text.size()) {
        wchar_t c = text[i];
        if (c == L'[') {
            size_t close = text.find(L']', i + 1);
            if (close == std::wstring_view::npos) {
                *error = L"missing ']'";
                return std::nullopt;
            }
            auto content = Trim(text.substr(i + 1, close - i - 1));
            if (content.empty()) {
                *error = L"empty [...]";
                return std::nullopt;
            }
            if (content.find_first_not_of(L"0123456789") ==
                std::wstring_view::npos) {
                matcher.oneBasedIndex = _wtoi(std::wstring(content).c_str());
            } else {
                auto eq = content.find(L'=');
                if (eq == std::wstring_view::npos) {
                    *error = L"missing '=' in [...]";
                    return std::nullopt;
                }
                auto key = Trim(content.substr(0, eq));
                auto value = Trim(content.substr(eq + 1));
                if (key.empty()) {
                    *error = L"empty property name in [...]";
                    return std::nullopt;
                }
                matcher.propertyConditions.emplace_back(std::wstring(key),
                                                        std::wstring(value));
            }
            i = close + 1;
            if (i < text.size() && text[i] != L'#' && text[i] != L'@' &&
                text[i] != L'[') {
                i = text.find_first_of(L"#@[", i);
            }
            continue;
        }

        size_t next = text.find_first_of(L"#@[", i + 1);
        auto part = Trim(text.substr(
            i + 1, next == std::wstring_view::npos ? std::wstring_view::npos
                                                   : next - i - 1));
        if (c == L'#') {
            if (part.empty()) {
                *error = L"empty name";
                return std::nullopt;
            }
            matcher.name = part;
        }
        // `@VisualStateGroup` only matters for `Property@State=...` styles,
        // which aren't supported yet, so it's ignored here.
        i = next;
    }

    return matcher;
}

std::optional<StyleSpec> ParseStyle(std::wstring_view text,
                                    std::wstring* error) {
    auto eq = text.find(L'=');
    if (eq == std::wstring_view::npos) {
        *error = L"'=' is missing";
        return std::nullopt;
    }

    auto name = text.substr(0, eq);
    auto value = text.substr(eq + 1);

    if (!value.empty() && value.front() == L'>') {
        *error = L"capture rules (=>) are not supported";
        return std::nullopt;
    }

    StyleSpec style;
    if (!name.empty() && name.back() == L':') {
        style.isXaml = true;
        name = name.substr(0, name.size() - 1);
    }

    if (name.find(L'@') != std::wstring_view::npos) {
        *error = L"visual state styles (@State) are not supported yet";
        return std::nullopt;
    }

    style.property = Trim(name);
    style.value = Trim(value);
    if (style.property.empty()) {
        *error = L"empty property name";
        return std::nullopt;
    }

    return style;
}

}  // namespace

std::wstring_view Trim(std::wstring_view text) {
    constexpr std::wstring_view kWhitespace = L" \t\r\n";
    auto begin = text.find_first_not_of(kWhitespace);
    if (begin == std::wstring_view::npos) {
        return {};
    }
    auto end = text.find_last_not_of(kWhitespace);
    return text.substr(begin, end - begin + 1);
}

std::wstring AdjustTypeName(std::wstring_view type) {
    if (type.find_first_of(L".:") == std::wstring_view::npos) {
        static constexpr std::wstring_view kShapes[] = {
            L"Rectangle", L"Ellipse", L"Line", L"Path", L"Polygon", L"Polyline",
        };
        for (auto shape : kShapes) {
            if (type == shape) {
                return L"Windows.UI.Xaml.Shapes." + std::wstring(type);
            }
        }
        return L"Windows.UI.Xaml.Controls." + std::wstring(type);
    }

    static constexpr std::pair<std::wstring_view, std::wstring_view>
        kPrefixes[] = {
            {L"taskbar:", L"Taskbar."},
            {L"systemtray:", L"SystemTray."},
            {L"udk:", L"WindowsUdk.UI.Shell."},
            {L"muxc:", L"Microsoft.UI.Xaml.Controls."},
        };
    for (auto [from, to] : kPrefixes) {
        if (type.starts_with(from)) {
            return std::wstring(to) + std::wstring(type.substr(from.size()));
        }
    }

    return std::wstring(type);
}

std::vector<Rule> ParseRules(JsonArray const& array) {
    std::vector<Rule> rules;

    for (uint32_t index = 0; index < array.Size(); index++) {
        auto value = array.GetAt(index);
        if (value.ValueType() != JsonValueType::Object) {
            continue;
        }

        auto object = value.GetObject();
        std::wstring target(object.GetNamedString(L"target", L""));
        if (Trim(target).empty()) {
            continue;
        }

        std::vector<StyleSpec> styles;
        if (object.HasKey(L"styles") &&
            object.GetNamedValue(L"styles").ValueType() ==
                JsonValueType::Array) {
            auto styleArray = object.GetNamedArray(L"styles");
            for (uint32_t s = 0; s < styleArray.Size(); s++) {
                auto styleValue = styleArray.GetAt(s);
                if (styleValue.ValueType() != JsonValueType::String) {
                    continue;
                }
                std::wstring text(styleValue.GetString());
                std::wstring error;
                if (auto style = ParseStyle(text, &error)) {
                    styles.push_back(std::move(*style));
                } else {
                    DLOG(L"Skipping style '%s' of '%s': %s", text.c_str(),
                         target.c_str(), error.c_str());
                }
            }
        }

        if (styles.empty()) {
            continue;
        }

        for (auto singleTarget : SplitOutsideBrackets(target, L',')) {
            auto parts = SplitOutsideBrackets(singleTarget, L'>');
            Rule rule;
            rule.targetText = Trim(singleTarget);
            rule.styles = styles;

            bool valid = true;
            std::wstring error;
            std::vector<ElementMatcher> matchers;
            for (auto part : parts) {
                auto matcher = ParseMatcher(part, &error);
                if (!matcher) {
                    valid = false;
                    break;
                }
                matchers.push_back(std::move(*matcher));
            }

            if (valid && matchers.empty()) {
                valid = false;
                error = L"empty target";
            }

            if (valid) {
                // The last part is the styled element; it can't be `*`, and
                // `*` can't be the first part or follow another `*`.
                if (matchers.back().kind == ElementMatcher::Kind::Wildcard ||
                    matchers.front().kind == ElementMatcher::Kind::Wildcard) {
                    valid = false;
                    error = L"'*' must be between two elements";
                }
                for (size_t i = 1; valid && i < matchers.size(); i++) {
                    if (matchers[i].kind == ElementMatcher::Kind::Wildcard &&
                        matchers[i - 1].kind ==
                            ElementMatcher::Kind::Wildcard) {
                        valid = false;
                        error = L"'*' can't follow '*'";
                    }
                }
            }

            if (!valid) {
                DLOG(L"Skipping target '%s': %s",
                     std::wstring(singleTarget).c_str(), error.c_str());
                continue;
            }

            rule.element = std::move(matchers.back());
            matchers.pop_back();
            rule.parents.assign(std::make_move_iterator(matchers.rbegin()),
                                std::make_move_iterator(matchers.rend()));
            rule.id = rules.size();
            rules.push_back(std::move(rule));
        }
    }

    return rules;
}

}  // namespace desk
