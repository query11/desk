#pragma once

namespace desk {

struct BlurParams {
    float blurAmount = 30.0f;
    winrt::Windows::UI::Color tint{0x80, 0x20, 0x20, 0x20};
    float saturation = 1.0f;
    // Pulls the backdrop's brightness towards the tint's, like acrylic does.
    std::optional<float> luminosityOpacity;
    // Grain on top of the blur, like acrylic's noise texture. Density shapes
    // the brightness of the grains (lower means sparser bright grains).
    float noiseOpacity = 0.0f;
    float noiseDensity = 1.0f;
    // Used instead of the blur when transparency effects are off or energy
    // saver is on. Defaults to the tint made mostly opaque.
    std::optional<winrt::Windows::UI::Color> fallback;
};

// A XAML brush which blurs whatever is behind the element and tints it.
// Based on XamlBlurBrush from TranslucentTB (GPL-3.0) and its extended version
// in the Windows 11 Taskbar Styler mod for Windhawk (GPL-3.0).
class DeskBlurBrush : public wuxm::XamlCompositionBrushBaseT<DeskBlurBrush> {
public:
    DeskBlurBrush(wux::UIElement const& element, BlurParams params);
    ~DeskBlurBrush();

    void OnConnected();
    void OnDisconnected();

private:
    wuc::CompositionBrush CreateEffectBrush();
    wuc::CompositionBrush CreateFallbackBrush();
    bool ShouldUseFallback() const;
    void Refresh();

    wuc::Compositor m_compositor{nullptr};
    BlurParams m_params;
    winrt::Windows::System::DispatcherQueue m_dispatcher{nullptr};
    winrt::Windows::UI::ViewManagement::UISettings m_uiSettings{nullptr};
    winrt::event_token m_advancedEffectsToken{};
    winrt::event_token m_energySaverToken{};
};

}  // namespace desk
