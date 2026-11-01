#pragma once

namespace desk {

struct BlurParams {
    // What gets blurred. In-app backdrop is everything drawn behind the
    // element by the same process; host backdrop is what's behind the window.
    // An app container (the Start menu) only sees its own content in-app, so
    // Auto picks the host backdrop there.
    enum class Source { Auto, App, Host };

    Source source = Source::Auto;
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

// Lets the engine change the numbers of a brush it set without replacing it.
struct __declspec(uuid("6c4c3a5e-2f0b-4f7a-9d63-0f7c1b2e8a41")) IDeskBlurBrush
    : ::IUnknown {
    virtual HRESULT STDMETHODCALLTYPE UpdateParams(const BlurParams* params) noexcept = 0;
};

// A XAML brush which blurs whatever is behind the element and tints it.
// Based on XamlBlurBrush from TranslucentTB (GPL-3.0) and its extended version
// in the Windows 11 Taskbar Styler mod for Windhawk (GPL-3.0).
//
// Changing only the blur amount or the tint (color and opacity) updates the
// running effect's properties. Other changes need another effect graph; the
// graphs (effect factories) and the noise image are cached per UI thread and
// compositor, so moving a slider back and forth doesn't build them again.
class DeskBlurBrush
    : public wuxm::XamlCompositionBrushBaseT<DeskBlurBrush, IDeskBlurBrush> {
public:
    DeskBlurBrush(wux::UIElement const& element, BlurParams params);
    ~DeskBlurBrush();

    void OnConnected();
    void OnDisconnected();

    HRESULT STDMETHODCALLTYPE UpdateParams(const BlurParams* params) noexcept override;

    // Drops the calling thread's cached factories and noise images (the
    // thread's dispatcher is shutting down).
    static void ReleaseThreadCaches();

private:
    wuc::CompositionBrush CreateEffectBrush();
    wuc::CompositionBrush CreateFallbackBrush();
    bool ShouldUseFallback() const;
    void Refresh();

    wuc::Compositor m_compositor{nullptr};
    BlurParams m_params;
    // Set while the current composition brush is the solid fallback.
    bool m_usingFallback = false;
    // Whether the current effect brush accepts blur/tint property updates.
    bool m_animatable = false;
    winrt::Windows::System::DispatcherQueue m_dispatcher{nullptr};
    winrt::Windows::UI::ViewManagement::UISettings m_uiSettings{nullptr};
    winrt::event_token m_advancedEffectsToken{};
    winrt::event_token m_energySaverToken{};
};

}  // namespace desk
