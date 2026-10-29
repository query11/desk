// Value resolution and property writes. The <Style>/<Setter> technique, the
// workarounds in ReadLocalValueSafe/SetOrClearValue and SameLocalValue follow
// the Windows 11 Taskbar Styler mod for Windhawk
// (https://github.com/ramensoftware/windhawk-mods, GPL-3.0).
#include "pch.h"
#include "values.h"
#include "log.h"

namespace desk {
namespace {

using winrt::Windows::UI::Color;
using wux::Markup::XamlReader;

constexpr std::wstring_view kXamlNamespaces =
    LR"(xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" )"
    LR"(xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" )"
    LR"(xmlns:muxc="using:Microsoft.UI.Xaml.Controls")";

std::wstring EscapeXmlAttribute(std::wstring_view text) {
    std::wstring result;
    result.reserve(text.size());
    for (wchar_t c : text) {
        switch (c) {
            case L'&':
                result += L"&amp;";
                break;
            case L'"':
                result += L"&quot;";
                break;
            case L'\'':
                result += L"&apos;";
                break;
            case L'<':
                result += L"&lt;";
                break;
            case L'>':
                result += L"&gt;";
                break;
            default:
                result += c;
        }
    }
    return result;
}

std::wstring HResultMessage(winrt::hresult_error const& error) {
    wchar_t code[16];
    swprintf_s(code, L"%08X", static_cast<uint32_t>(error.code().value));
    return std::wstring(code) + L" " + std::wstring(error.message());
}

wux::Style LoadStyle(std::wstring_view type, std::wstring_view setters) {
    std::wstring xaml = L"<ResourceDictionary ";
    xaml += kXamlNamespaces;

    auto dot = type.rfind(L'.');
    if (dot != std::wstring_view::npos) {
        xaml += L" xmlns:t=\"using:";
        xaml += EscapeXmlAttribute(type.substr(0, dot));
        xaml += L"\"><Style TargetType=\"t:";
        xaml += EscapeXmlAttribute(type.substr(dot + 1));
        xaml += L"\">";
    } else {
        xaml += L"><Style TargetType=\"";
        xaml += EscapeXmlAttribute(type);
        xaml += L"\">";
    }
    xaml += setters;
    xaml += L"</Style></ResourceDictionary>";

    auto dictionary = XamlReader::Load(xaml).as<wux::ResourceDictionary>();
    return dictionary.First().Current().Value().as<wux::Style>();
}

std::optional<uint8_t> HexByte(std::wstring_view text) {
    unsigned value = 0;
    for (wchar_t c : text) {
        value <<= 4;
        if (c >= L'0' && c <= L'9') {
            value |= c - L'0';
        } else if (c >= L'a' && c <= L'f') {
            value |= c - L'a' + 10;
        } else if (c >= L'A' && c <= L'F') {
            value |= c - L'A' + 10;
        } else {
            return std::nullopt;
        }
    }
    if (text.size() == 1) {
        value |= value << 4;
    }
    return static_cast<uint8_t>(value);
}

std::optional<Color> ParseColor(std::wstring_view text) {
    text = Trim(text);
    if (!text.empty() && text.front() == L'#') {
        auto hex = text.substr(1);
        std::optional<uint8_t> a = 0xFF, r, g, b;
        switch (hex.size()) {
            case 3:
                r = HexByte(hex.substr(0, 1));
                g = HexByte(hex.substr(1, 1));
                b = HexByte(hex.substr(2, 1));
                break;
            case 4:
                a = HexByte(hex.substr(0, 1));
                r = HexByte(hex.substr(1, 1));
                g = HexByte(hex.substr(2, 1));
                b = HexByte(hex.substr(3, 1));
                break;
            case 6:
                r = HexByte(hex.substr(0, 2));
                g = HexByte(hex.substr(2, 2));
                b = HexByte(hex.substr(4, 2));
                break;
            case 8:
                a = HexByte(hex.substr(0, 2));
                r = HexByte(hex.substr(2, 2));
                g = HexByte(hex.substr(4, 2));
                b = HexByte(hex.substr(6, 2));
                break;
        }
        if (a && r && g && b) {
            return Color{*a, *r, *g, *b};
        }
        return std::nullopt;
    }

    // Named colors and {ThemeResource ...}: let XAML convert them.
    try {
        std::wstring xaml = L"<SolidColorBrush ";
        xaml += kXamlNamespaces;
        xaml += L" Color=\"";
        xaml += EscapeXmlAttribute(text);
        xaml += L"\"/>";
        return XamlReader::Load(xaml).as<wuxm::SolidColorBrush>().Color();
    } catch (...) {
        return std::nullopt;
    }
}

std::optional<float> ParseFloat(std::wstring_view text) {
    std::wstring copy(Trim(text));
    if (copy.empty()) {
        return std::nullopt;
    }
    wchar_t* end = nullptr;
    float value = std::wcstof(copy.c_str(), &end);
    if (end == copy.c_str()) {
        return std::nullopt;
    }
    return value;
}

// Parses `<DeskBlur BlurAmount="20" TintColor="#80202020" .../>`. The
// `WindhawkBlur` tag is accepted too, for themes written for Windhawk.
std::optional<BlurParams> ParseBlur(std::wstring_view xaml) {
    xaml = Trim(xaml);
    std::wstring_view tag;
    for (std::wstring_view candidate : {L"<DeskBlur", L"<WindhawkBlur"}) {
        if (xaml.starts_with(candidate)) {
            tag = candidate;
        }
    }
    if (tag.empty()) {
        return std::nullopt;
    }

    std::vector<std::pair<std::wstring, std::wstring>> attributes;
    size_t i = tag.size();
    while (i < xaml.size()) {
        while (i < xaml.size() && iswspace(xaml[i])) {
            i++;
        }
        if (i >= xaml.size() || xaml[i] == L'/' || xaml[i] == L'>') {
            break;
        }
        size_t nameStart = i;
        while (i < xaml.size() && xaml[i] != L'=' && !iswspace(xaml[i])) {
            i++;
        }
        std::wstring name(xaml.substr(nameStart, i - nameStart));
        while (i < xaml.size() && (iswspace(xaml[i]) || xaml[i] == L'=')) {
            i++;
        }
        if (i >= xaml.size() || (xaml[i] != L'"' && xaml[i] != L'\'')) {
            break;
        }
        wchar_t quote = xaml[i++];
        size_t valueStart = i;
        while (i < xaml.size() && xaml[i] != quote) {
            i++;
        }
        attributes.emplace_back(std::move(name),
                                std::wstring(xaml.substr(valueStart, i - valueStart)));
        i++;
    }

    BlurParams params;
    std::optional<float> tintOpacity;
    for (const auto& [name, value] : attributes) {
        if (name == L"BlurAmount") {
            params.blurAmount = ParseFloat(value).value_or(params.blurAmount);
        } else if (name == L"TintColor") {
            if (auto color = ParseColor(value)) {
                params.tint = *color;
            } else {
                DLOG(L"Invalid blur TintColor '%s'", value.c_str());
            }
        } else if (name == L"TintOpacity") {
            tintOpacity = ParseFloat(value);
        } else if (name == L"TintSaturation") {
            params.saturation = ParseFloat(value).value_or(1.0f);
        } else if (name == L"TintLuminosityOpacity") {
            params.luminosityOpacity = ParseFloat(value);
        } else if (name == L"FallbackColor") {
            params.fallback = ParseColor(value);
        }
    }

    if (tintOpacity) {
        params.tint.A = static_cast<uint8_t>(
            std::lround(std::clamp(*tintOpacity, 0.0f, 1.0f) * 255.0f));
    }

    return params;
}

std::wstring SetterXaml(const StyleSpec& style, bool placeholder) {
    std::wstring xaml = L"<Setter Property=\"";
    xaml += EscapeXmlAttribute(style.property);
    xaml += L"\"";
    if (placeholder) {
        xaml += L" Value=\"{x:Null}\"/>";
    } else if (!style.isXaml) {
        xaml += L" Value=\"";
        xaml += EscapeXmlAttribute(style.value);
        xaml += L"\"/>";
    } else {
        xaml += L"><Setter.Value>";
        xaml += style.value;
        xaml += L"</Setter.Value></Setter>";
    }
    return xaml;
}

struct PreparedStyle {
    const StyleSpec* spec;
    std::optional<BlurParams> blur;
    bool clear = false;
};

std::optional<wux::Style> TryLoadStyle(std::wstring_view type,
                                       std::wstring_view fallbackType,
                                       const std::wstring& setters,
                                       std::wstring* error) {
    try {
        return LoadStyle(type, setters);
    } catch (winrt::hresult_error const& e) {
        *error = HResultMessage(e);
    }
    if (!fallbackType.empty() && fallbackType != type) {
        try {
            return LoadStyle(fallbackType, setters);
        } catch (winrt::hresult_error const& e) {
            *error = HResultMessage(e);
        }
    }
    // Some internal types can't be used as a TargetType at all. Properties
    // every element has (Margin, Width, Visibility, ...) still resolve here.
    constexpr std::wstring_view kFrameworkElement = L"Windows.UI.Xaml.FrameworkElement";
    if (type != kFrameworkElement && fallbackType != kFrameworkElement) {
        try {
            return LoadStyle(kFrameworkElement, setters);
        } catch (winrt::hresult_error const&) {
            // Keep the more specific error.
        }
    }
    return std::nullopt;
}

}  // namespace

std::vector<ResolvedStyle> ResolveStyles(std::wstring_view type,
                                         std::wstring_view fallbackType,
                                         const std::vector<StyleSpec>& styles) {
    std::vector<PreparedStyle> prepared;
    prepared.reserve(styles.size());
    for (const auto& style : styles) {
        PreparedStyle item{&style};
        if (style.isXaml) {
            item.blur = ParseBlur(style.value);
            item.clear = !item.blur && Trim(style.value).empty();
        }
        prepared.push_back(std::move(item));
    }

    auto setterFor = [](const PreparedStyle& item) {
        return SetterXaml(*item.spec, item.blur.has_value() || item.clear);
    };

    auto toResolved = [](const PreparedStyle& item, wux::Setter const& setter) {
        ResolvedStyle resolved;
        resolved.property = setter.Property();
        resolved.signature = item.spec->property +
                             (item.spec->isXaml ? L":=" : L"=") +
                             item.spec->value;
        if (item.blur) {
            resolved.value = *item.blur;
        } else if (item.clear) {
            resolved.value = wux::DependencyProperty::UnsetValue();
        } else {
            resolved.value = setter.Value();
        }
        return resolved;
    };

    std::vector<ResolvedStyle> result;

    // Fast path: all setters in one parse.
    std::wstring allSetters;
    for (const auto& item : prepared) {
        allSetters += setterFor(item);
    }

    std::wstring error;
    if (auto style = TryLoadStyle(type, fallbackType, allSetters, &error)) {
        auto setters = style->Setters();
        for (uint32_t i = 0; i < setters.Size() && i < prepared.size(); i++) {
            if (auto setter = setters.GetAt(i).try_as<wux::Setter>()) {
                result.push_back(toResolved(prepared[i], setter));
            }
        }
        return result;
    }

    // Something is wrong; resolve one by one so that a single bad style
    // doesn't disable the whole rule.
    for (const auto& item : prepared) {
        std::wstring itemError;
        auto style = TryLoadStyle(type, fallbackType, setterFor(item), &itemError);
        if (!style || style->Setters().Size() == 0) {
            DLOG(L"Can't apply '%s' to %s: %s", item.spec->property.c_str(),
                 std::wstring(type).c_str(), itemError.c_str());
            continue;
        }
        if (auto setter = style->Setters().GetAt(0).try_as<wux::Setter>()) {
            result.push_back(toResolved(item, setter));
        }
    }

    return result;
}

std::vector<std::pair<wux::DependencyProperty, wf::IInspectable>>
ResolveConditions(
    std::wstring_view type,
    const std::vector<std::pair<std::wstring, std::wstring>>& conditions) {
    std::vector<std::pair<wux::DependencyProperty, wf::IInspectable>> result;
    std::wstring setters;
    for (const auto& [property, value] : conditions) {
        setters += SetterXaml(StyleSpec{property, value, false}, false);
    }

    std::wstring error;
    auto style = TryLoadStyle(type, L"Windows.UI.Xaml.FrameworkElement",
                              setters, &error);
    if (!style) {
        DLOG(L"Can't resolve conditions for %s: %s", std::wstring(type).c_str(),
             error.c_str());
        return result;
    }

    for (auto const& base : style->Setters()) {
        if (auto setter = base.try_as<wux::Setter>()) {
            result.emplace_back(setter.Property(), setter.Value());
        }
    }
    return result;
}

MaterializedValue MaterializeValue(wux::FrameworkElement const& element,
                                   const StyleValue& value) {
    if (auto* blur = std::get_if<BlurParams>(&value)) {
        return {winrt::make<DeskBlurBrush>(element, *blur)};
    }

    auto inspectable = std::get<wf::IInspectable>(value);
    if (!inspectable) {
        return {inspectable};
    }

    // Grid column/row definitions are DependencyObjects that layout writes
    // back into, so each element needs its own copy (e.g. one taskbar per
    // monitor would otherwise share the same collection). The scratch Grid
    // owns the copy until SetValue hands it to the target.
    if (auto columns = inspectable.try_as<wuxc::ColumnDefinitionCollection>()) {
        wuxc::Grid owner;
        auto copy = owner.ColumnDefinitions();
        for (auto const& column : columns) {
            wuxc::ColumnDefinition clone;
            clone.Width(column.Width());
            clone.MinWidth(column.MinWidth());
            clone.MaxWidth(column.MaxWidth());
            copy.Append(clone);
        }
        return {copy, owner};
    }
    if (auto rows = inspectable.try_as<wuxc::RowDefinitionCollection>()) {
        wuxc::Grid owner;
        auto copy = owner.RowDefinitions();
        for (auto const& row : rows) {
            wuxc::RowDefinition clone;
            clone.Height(row.Height());
            clone.MinHeight(row.MinHeight());
            clone.MaxHeight(row.MaxHeight());
            copy.Append(clone);
        }
        return {copy, owner};
    }

    return {inspectable};
}

wf::IInspectable ReadLocalValueSafe(wux::DependencyObject const& element,
                                    wux::DependencyProperty const& property) {
    auto value = element.ReadLocalValue(property);
    if (value) {
        auto className = winrt::get_class_name(value);
        if (className == L"Windows.UI.Xaml.Data.BindingExpressionBase" ||
            className == L"Windows.UI.Xaml.Data.BindingExpression") {
            // e.g. CornerRadius="{TemplateBinding CornerRadius}" can't be
            // written back, so remember the animation base value instead.
            value = element.GetAnimationBaseValue(property);
        }
    }
    return value;
}

void SetOrClearValue(wux::DependencyObject const& element,
                     wux::DependencyProperty const& property,
                     wf::IInspectable value) {
    try {
        if (value == wux::DependencyProperty::UnsetValue()) {
            element.ClearValue(property);
            return;
        }

        // Setter values hold font weights as plain integers, which SetValue
        // rejects for FontWeight properties.
        if (value && (property == wuxc::TextBlock::FontWeightProperty() ||
                      property == wuxc::Control::FontWeightProperty() ||
                      property == wuxc::ContentPresenter::FontWeightProperty() ||
                      property == wuxc::FontIcon::FontWeightProperty())) {
            if (auto weight = value.try_as<int32_t>()) {
                if (*weight >= 0 && *weight <= 0xFFFF) {
                    value = winrt::box_value(winrt::Windows::UI::Text::FontWeight{
                        static_cast<uint16_t>(*weight)});
                }
            }
        }

        element.SetValue(property, value);
    } catch (winrt::hresult_error const& e) {
        DLOG(L"SetValue failed: %s", HResultMessage(e).c_str());
    }
}

namespace {

using Unboxed = std::variant<std::wstring, bool, char16_t, uint8_t, int16_t,
                             uint16_t, int32_t, uint32_t, int64_t, uint64_t,
                             float, double>;

std::optional<Unboxed> TryUnbox(wf::IInspectable const& value) {
    using wf::PropertyType;
    auto pv = value.try_as<wf::IPropertyValue>();
    if (!pv) {
        return std::nullopt;
    }
    switch (pv.Type()) {
        case PropertyType::String:
            return Unboxed{std::wstring(pv.GetString())};
        case PropertyType::Boolean:
            return Unboxed{pv.GetBoolean()};
        case PropertyType::Char16:
            return Unboxed{pv.GetChar16()};
        case PropertyType::UInt8:
            return Unboxed{pv.GetUInt8()};
        case PropertyType::Int16:
            return Unboxed{pv.GetInt16()};
        case PropertyType::UInt16:
            return Unboxed{pv.GetUInt16()};
        case PropertyType::Int32:
            return Unboxed{pv.GetInt32()};
        case PropertyType::UInt32:
            return Unboxed{pv.GetUInt32()};
        case PropertyType::Int64:
            return Unboxed{pv.GetInt64()};
        case PropertyType::UInt64:
            return Unboxed{pv.GetUInt64()};
        case PropertyType::Single:
            return Unboxed{pv.GetSingle()};
        case PropertyType::Double:
            return Unboxed{pv.GetDouble()};
        case PropertyType::OtherType:
            // Usually an enum.
            if (auto number = value.try_as<int32_t>()) {
                return Unboxed{*number};
            }
            return std::nullopt;
        default:
            return std::nullopt;
    }
}

template <typename T, typename... Ts>
std::optional<bool> SameBoxedStruct(wf::IInspectable const& a,
                                    wf::IInspectable const& b) {
    if (auto ra = a.try_as<wf::IReference<T>>()) {
        auto rb = b.try_as<wf::IReference<T>>();
        return rb && ra.Value() == rb.Value();
    }
    if constexpr (sizeof...(Ts) > 0) {
        return SameBoxedStruct<Ts...>(a, b);
    } else {
        return std::nullopt;
    }
}

}  // namespace

bool SameLocalValue(wf::IInspectable const& a, wf::IInspectable const& b) {
    if (a == b) {
        return true;
    }
    if (!a || !b) {
        return false;
    }

    auto ua = TryUnbox(a);
    auto ub = TryUnbox(b);
    if (ua || ub) {
        return ua && ub && *ua == *ub;
    }

    if (auto same = SameBoxedStruct<wux::Thickness, wux::CornerRadius,
                                    wux::GridLength, wf::Point, wf::Size,
                                    wf::Rect, Color,
                                    winrt::Windows::UI::Text::FontWeight>(a, b)) {
        return *same;
    }

    // Some other boxed value type: assume unchanged rather than adopting a
    // value which might be our own.
    auto isBoxed = [](wf::IInspectable const& v) {
        return v.try_as<wf::IPropertyValue>() != nullptr ||
               std::wstring_view(winrt::get_class_name(v))
                   .starts_with(L"Windows.Foundation.IReference`1<");
    };
    return isBoxed(a) && isBoxed(b);
}

}  // namespace desk
