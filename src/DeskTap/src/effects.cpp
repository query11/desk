// Adapted from TranslucentTB (https://github.com/TranslucentTB/TranslucentTB,
// GPL-3.0) and the Windows 11 Taskbar Styler mod for Windhawk
// (https://github.com/ramensoftware/windhawk-mods, GPL-3.0).
#include "pch.h"
#include "effects.h"

namespace desk::effects {
namespace {

HRESULT Box(wf::IInspectable const& value, AbiPropertyValue** result) {
    *result = value.as<AbiPropertyValue>().detach();
    return S_OK;
}

HRESULT CopySource(wge::IGraphicsEffectSource const& source,
                   awge::IGraphicsEffectSource** result) {
    if (!source) {
        return E_BOUNDS;
    }
    winrt::copy_to_abi(source, *reinterpret_cast<void**>(result));
    return S_OK;
}

HRESULT Direct(UINT propertyIndex,
               UINT* index,
               awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) {
    *index = propertyIndex;
    *mapping = awge::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
    return S_OK;
}

}  // namespace

////////////////////////////////////////////////////////////////////////////////
// GaussianBlurEffect

HRESULT GaussianBlurEffect::GetEffectId(GUID* id) noexcept {
    if (!id) {
        return E_INVALIDARG;
    }
    *id = CLSID_D2D1GaussianBlur;
    return S_OK;
}

HRESULT GaussianBlurEffect::GetNamedPropertyMapping(
    LPCWSTR name,
    UINT* index,
    awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept {
    if (!name || !index || !mapping) {
        return E_INVALIDARG;
    }
    std::wstring_view view(name);
    if (view == L"BlurAmount") {
        return Direct(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, index, mapping);
    }
    if (view == L"Optimization") {
        return Direct(D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION, index, mapping);
    }
    if (view == L"BorderMode") {
        return Direct(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, index, mapping);
    }
    return E_INVALIDARG;
}

HRESULT GaussianBlurEffect::GetPropertyCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 3;
    return S_OK;
}

HRESULT GaussianBlurEffect::GetProperty(UINT index,
                                        AbiPropertyValue** value) noexcept try {
    if (!value) {
        return E_INVALIDARG;
    }
    switch (index) {
        case D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION:
            return Box(wf::PropertyValue::CreateSingle(BlurAmount), value);
        case D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION:
            return Box(wf::PropertyValue::CreateUInt32(
                           static_cast<uint32_t>(Optimization)),
                       value);
        case D2D1_GAUSSIANBLUR_PROP_BORDER_MODE:
            return Box(wf::PropertyValue::CreateUInt32(
                           static_cast<uint32_t>(BorderMode)),
                       value);
    }
    return E_BOUNDS;
} catch (...) {
    return winrt::to_hresult();
}

HRESULT GaussianBlurEffect::GetSource(
    UINT index,
    awge::IGraphicsEffectSource** source) noexcept {
    if (!source) {
        return E_INVALIDARG;
    }
    return index == 0 ? CopySource(Source, source) : E_BOUNDS;
}

HRESULT GaussianBlurEffect::GetSourceCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 1;
    return S_OK;
}

////////////////////////////////////////////////////////////////////////////////
// FloodEffect

HRESULT FloodEffect::GetEffectId(GUID* id) noexcept {
    if (!id) {
        return E_INVALIDARG;
    }
    *id = CLSID_D2D1Flood;
    return S_OK;
}

HRESULT FloodEffect::GetNamedPropertyMapping(
    LPCWSTR name,
    UINT* index,
    awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept {
    if (!name || !index || !mapping) {
        return E_INVALIDARG;
    }
    if (std::wstring_view(name) == L"Color") {
        return Direct(D2D1_FLOOD_PROP_COLOR, index, mapping);
    }
    return E_INVALIDARG;
}

HRESULT FloodEffect::GetPropertyCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 1;
    return S_OK;
}

HRESULT FloodEffect::GetProperty(UINT index,
                                 AbiPropertyValue** value) noexcept try {
    if (!value) {
        return E_INVALIDARG;
    }
    if (index == D2D1_FLOOD_PROP_COLOR) {
        float color[] = {Color.x, Color.y, Color.z, Color.w};
        return Box(wf::PropertyValue::CreateSingleArray(color), value);
    }
    return E_BOUNDS;
} catch (...) {
    return winrt::to_hresult();
}

HRESULT FloodEffect::GetSource(UINT,
                               awge::IGraphicsEffectSource** source) noexcept {
    return source ? E_BOUNDS : E_INVALIDARG;
}

HRESULT FloodEffect::GetSourceCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 0;
    return S_OK;
}

////////////////////////////////////////////////////////////////////////////////
// CompositeEffect

HRESULT CompositeEffect::GetEffectId(GUID* id) noexcept {
    if (!id) {
        return E_INVALIDARG;
    }
    *id = CLSID_D2D1Composite;
    return S_OK;
}

HRESULT CompositeEffect::GetNamedPropertyMapping(
    LPCWSTR name,
    UINT* index,
    awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept {
    if (!name || !index || !mapping) {
        return E_INVALIDARG;
    }
    if (std::wstring_view(name) == L"Mode") {
        return Direct(D2D1_COMPOSITE_PROP_MODE, index, mapping);
    }
    return E_INVALIDARG;
}

HRESULT CompositeEffect::GetPropertyCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 1;
    return S_OK;
}

HRESULT CompositeEffect::GetProperty(UINT index,
                                     AbiPropertyValue** value) noexcept try {
    if (!value) {
        return E_INVALIDARG;
    }
    if (index == D2D1_COMPOSITE_PROP_MODE) {
        return Box(
            wf::PropertyValue::CreateUInt32(static_cast<uint32_t>(Mode)),
            value);
    }
    return E_BOUNDS;
} catch (...) {
    return winrt::to_hresult();
}

HRESULT CompositeEffect::GetSource(
    UINT index,
    awge::IGraphicsEffectSource** source) noexcept {
    if (!source) {
        return E_INVALIDARG;
    }
    return index < Sources.size() ? CopySource(Sources[index], source)
                                  : E_BOUNDS;
}

HRESULT CompositeEffect::GetSourceCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = static_cast<UINT>(Sources.size());
    return S_OK;
}

////////////////////////////////////////////////////////////////////////////////
// ColorMatrixEffect

HRESULT ColorMatrixEffect::GetEffectId(GUID* id) noexcept {
    if (!id) {
        return E_INVALIDARG;
    }
    *id = CLSID_D2D1ColorMatrix;
    return S_OK;
}

HRESULT ColorMatrixEffect::GetNamedPropertyMapping(
    LPCWSTR name,
    UINT* index,
    awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept {
    if (!name || !index || !mapping) {
        return E_INVALIDARG;
    }
    std::wstring_view view(name);
    if (view == L"ColorMatrix") {
        return Direct(D2D1_COLORMATRIX_PROP_COLOR_MATRIX, index, mapping);
    }
    if (view == L"AlphaMode") {
        return Direct(D2D1_COLORMATRIX_PROP_ALPHA_MODE, index, mapping);
    }
    if (view == L"ClampOutput") {
        return Direct(D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT, index, mapping);
    }
    return E_INVALIDARG;
}

HRESULT ColorMatrixEffect::GetPropertyCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 3;
    return S_OK;
}

HRESULT ColorMatrixEffect::GetProperty(UINT index,
                                       AbiPropertyValue** value) noexcept try {
    if (!value) {
        return E_INVALIDARG;
    }
    switch (index) {
        case D2D1_COLORMATRIX_PROP_COLOR_MATRIX:
            return Box(wf::PropertyValue::CreateSingleArray(
                           winrt::array_view<const float>(Matrix, Matrix + 20)),
                       value);
        case D2D1_COLORMATRIX_PROP_ALPHA_MODE:
            return Box(wf::PropertyValue::CreateUInt32(
                           static_cast<uint32_t>(AlphaMode)),
                       value);
        case D2D1_COLORMATRIX_PROP_CLAMP_OUTPUT:
            return Box(wf::PropertyValue::CreateBoolean(ClampOutput), value);
    }
    return E_BOUNDS;
} catch (...) {
    return winrt::to_hresult();
}

HRESULT ColorMatrixEffect::GetSource(
    UINT index,
    awge::IGraphicsEffectSource** source) noexcept {
    if (!source) {
        return E_INVALIDARG;
    }
    return index == 0 ? CopySource(Source, source) : E_BOUNDS;
}

HRESULT ColorMatrixEffect::GetSourceCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 1;
    return S_OK;
}

////////////////////////////////////////////////////////////////////////////////
// BorderEffect

HRESULT BorderEffect::GetEffectId(GUID* id) noexcept {
    if (!id) {
        return E_INVALIDARG;
    }
    *id = CLSID_D2D1Border;
    return S_OK;
}

HRESULT BorderEffect::GetNamedPropertyMapping(
    LPCWSTR name,
    UINT* index,
    awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept {
    if (!name || !index || !mapping) {
        return E_INVALIDARG;
    }
    std::wstring_view view(name);
    if (view == L"ExtendX") {
        return Direct(D2D1_BORDER_PROP_EDGE_MODE_X, index, mapping);
    }
    if (view == L"ExtendY") {
        return Direct(D2D1_BORDER_PROP_EDGE_MODE_Y, index, mapping);
    }
    return E_INVALIDARG;
}

HRESULT BorderEffect::GetPropertyCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 2;
    return S_OK;
}

HRESULT BorderEffect::GetProperty(UINT index,
                                  AbiPropertyValue** value) noexcept try {
    if (!value) {
        return E_INVALIDARG;
    }
    switch (index) {
        case D2D1_BORDER_PROP_EDGE_MODE_X:
            return Box(wf::PropertyValue::CreateUInt32(
                           static_cast<uint32_t>(ExtendX)),
                       value);
        case D2D1_BORDER_PROP_EDGE_MODE_Y:
            return Box(wf::PropertyValue::CreateUInt32(
                           static_cast<uint32_t>(ExtendY)),
                       value);
    }
    return E_BOUNDS;
} catch (...) {
    return winrt::to_hresult();
}

HRESULT BorderEffect::GetSource(UINT index,
                                awge::IGraphicsEffectSource** source) noexcept {
    if (!source) {
        return E_INVALIDARG;
    }
    return index == 0 ? CopySource(Source, source) : E_BOUNDS;
}

HRESULT BorderEffect::GetSourceCount(UINT* count) noexcept {
    if (!count) {
        return E_INVALIDARG;
    }
    *count = 1;
    return S_OK;
}

}  // namespace desk::effects
