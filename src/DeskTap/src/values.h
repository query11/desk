#pragma once
#include "blur.h"
#include "rules.h"

namespace desk {

// What a style resolves to: a ready value, or a blur brush description that
// is turned into one brush instance per element.
using StyleValue = std::variant<wf::IInspectable, BlurParams>;

struct ResolvedStyle {
    wux::DependencyProperty property{nullptr};
    StyleValue value;
    // The style text it came from, used to skip rewriting unchanged values.
    std::wstring signature;
};

// Resolves the styles of a rule for an element type by letting the XAML
// parser build a <Style> with matching setters. `fallbackType` is tried when
// `type` can't be used as a XAML TargetType. Styles which fail are logged and
// skipped.
std::vector<ResolvedStyle> ResolveStyles(std::wstring_view type,
                                         std::wstring_view fallbackType,
                                         const std::vector<StyleSpec>& styles);

// Resolves the `[Property=Value]` conditions of a matcher.
std::vector<std::pair<wux::DependencyProperty, wf::IInspectable>>
ResolveConditions(
    std::wstring_view type,
    const std::vector<std::pair<std::wstring, std::wstring>>& conditions);

struct MaterializedValue {
    wf::IInspectable value{nullptr};
    // Must stay alive until the value was set (owner of a cloned collection).
    wf::IInspectable keepAlive{nullptr};
};

// Creates the value to set for an element.
MaterializedValue MaterializeValue(wux::FrameworkElement const& element,
                                   const StyleValue& value);

// ReadLocalValue, with a workaround for template bindings, which can't be
// written back.
wf::IInspectable ReadLocalValueSafe(wux::DependencyObject const& element,
                                    wux::DependencyProperty const& property);

// Sets the value, or clears it for DependencyProperty::UnsetValue().
void SetOrClearValue(wux::DependencyObject const& element,
                     wux::DependencyProperty const& property,
                     wf::IInspectable value);

// Whether two values read from a property are the same local value. Value
// types are compared by value, reference types by identity.
bool SameLocalValue(wf::IInspectable const& a, wf::IInspectable const& b);

}  // namespace desk
