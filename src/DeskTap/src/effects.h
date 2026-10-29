// Minimal Direct2D effect descriptions for Windows.UI.Composition effect
// brushes, so that no Win2D dependency is needed.
//
// Adapted from TranslucentTB (https://github.com/TranslucentTB/TranslucentTB,
// GPL-3.0) and the Windows 11 Taskbar Styler mod for Windhawk
// (https://github.com/ramensoftware/windhawk-mods, GPL-3.0).
#pragma once

namespace desk::effects {

using AbiPropertyValue = ABI::Windows::Foundation::IPropertyValue;

#define DESK_EFFECT_INTEROP_METHODS                                            \
    HRESULT STDMETHODCALLTYPE GetEffectId(GUID* id) noexcept override;         \
    HRESULT STDMETHODCALLTYPE GetNamedPropertyMapping(                         \
        LPCWSTR name, UINT* index,                                             \
        awge::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping) noexcept override;    \
    HRESULT STDMETHODCALLTYPE GetPropertyCount(UINT* count) noexcept override; \
    HRESULT STDMETHODCALLTYPE GetProperty(                                     \
        UINT index, AbiPropertyValue** value) noexcept override;               \
    HRESULT STDMETHODCALLTYPE GetSource(                                       \
        UINT index, awge::IGraphicsEffectSource** source) noexcept override;   \
    HRESULT STDMETHODCALLTYPE GetSourceCount(UINT* count) noexcept override;   \
    winrt::hstring Name() { return m_name; }                                   \
    void Name(winrt::hstring name) { m_name = std::move(name); }

struct GaussianBlurEffect
    : winrt::implements<GaussianBlurEffect, wge::IGraphicsEffect,
                        wge::IGraphicsEffectSource,
                        awge::IGraphicsEffectD2D1Interop> {
    DESK_EFFECT_INTEROP_METHODS

    wge::IGraphicsEffectSource Source{nullptr};
    float BlurAmount = 3.0f;
    D2D1_GAUSSIANBLUR_OPTIMIZATION Optimization =
        D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED;
    D2D1_BORDER_MODE BorderMode = D2D1_BORDER_MODE_SOFT;

private:
    winrt::hstring m_name = L"GaussianBlurEffect";
};

struct FloodEffect : winrt::implements<FloodEffect, wge::IGraphicsEffect,
                                       wge::IGraphicsEffectSource,
                                       awge::IGraphicsEffectD2D1Interop> {
    DESK_EFFECT_INTEROP_METHODS

    // Straight (non-premultiplied) RGBA in the 0..1 range.
    wfn::float4 Color{0.0f, 0.0f, 0.0f, 1.0f};

private:
    winrt::hstring m_name = L"FloodEffect";
};

struct CompositeEffect
    : winrt::implements<CompositeEffect, wge::IGraphicsEffect,
                        wge::IGraphicsEffectSource,
                        awge::IGraphicsEffectD2D1Interop> {
    DESK_EFFECT_INTEROP_METHODS

    std::vector<wge::IGraphicsEffectSource> Sources;
    D2D1_COMPOSITE_MODE Mode = D2D1_COMPOSITE_MODE_SOURCE_OVER;

private:
    winrt::hstring m_name = L"CompositeEffect";
};

struct ColorMatrixEffect
    : winrt::implements<ColorMatrixEffect, wge::IGraphicsEffect,
                        wge::IGraphicsEffectSource,
                        awge::IGraphicsEffectD2D1Interop> {
    DESK_EFFECT_INTEROP_METHODS

    wge::IGraphicsEffectSource Source{nullptr};
    // D2D1_MATRIX_5X4_F, row major, identity by default.
    float Matrix[20] = {
        1, 0, 0, 0,  //
        0, 1, 0, 0,  //
        0, 0, 1, 0,  //
        0, 0, 0, 1,  //
        0, 0, 0, 0,  //
    };
    D2D1_COLORMATRIX_ALPHA_MODE AlphaMode =
        D2D1_COLORMATRIX_ALPHA_MODE_PREMULTIPLIED;
    bool ClampOutput = false;

private:
    winrt::hstring m_name = L"ColorMatrixEffect";
};

// Extends its source beyond its bounds; used to tile the noise texture.
struct BorderEffect : winrt::implements<BorderEffect, wge::IGraphicsEffect,
                                        wge::IGraphicsEffectSource,
                                        awge::IGraphicsEffectD2D1Interop> {
    DESK_EFFECT_INTEROP_METHODS

    wge::IGraphicsEffectSource Source{nullptr};
    D2D1_BORDER_EDGE_MODE ExtendX = D2D1_BORDER_EDGE_MODE_WRAP;
    D2D1_BORDER_EDGE_MODE ExtendY = D2D1_BORDER_EDGE_MODE_WRAP;

private:
    winrt::hstring m_name = L"BorderEffect";
};

#undef DESK_EFFECT_INTEROP_METHODS

}  // namespace desk::effects
