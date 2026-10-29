// Based on XamlBlurBrush from TranslucentTB (GPL-3.0) and the extended version
// in the Windows 11 Taskbar Styler mod for Windhawk (GPL-3.0).
#include "pch.h"
#include "blur.h"
#include "effects.h"
#include "log.h"

namespace desk {

namespace {

using winrt::Windows::System::Power::PowerManager;

bool IsEnergySaverOn() {
    // On Windows 11 24H2+ the registry value is the only signal that reliably
    // reflects "Always use energy saver". 1 = on, 2 = off.
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"SYSTEM\\CurrentControlSet\\Control\\Power", 0,
                      KEY_QUERY_VALUE, &key) == ERROR_SUCCESS) {
        DWORD value = 0;
        DWORD type = 0;
        DWORD size = sizeof(value);
        bool on = RegQueryValueExW(key, L"EnergySaverState", nullptr, &type,
                                   reinterpret_cast<LPBYTE>(&value),
                                   &size) == ERROR_SUCCESS &&
                  type == REG_DWORD && value == 1;
        RegCloseKey(key);
        if (on) {
            return true;
        }
    }

    SYSTEM_POWER_STATUS status{};
    return GetSystemPowerStatus(&status) && status.SystemStatusFlag != 0;
}

// A tileable gray noise bitmap (BMP) in memory, from the Windows 11 Taskbar
// Styler mod for Windhawk. The opacity is applied by the effect graph.
winrt::Windows::Storage::Streams::IRandomAccessStream CreateNoiseStream(
    float density) {
    using winrt::Windows::Storage::Streams::DataWriter;
    using winrt::Windows::Storage::Streams::InMemoryRandomAccessStream;

    // Every brush on a thread shares one generated bitmap per density; each
    // caller gets its own clone so that they don't share a seek position.
    thread_local float cachedDensity = std::numeric_limits<float>::quiet_NaN();
    thread_local InMemoryRandomAccessStream cachedStream{nullptr};
    if (cachedStream && density == cachedDensity) {
        return cachedStream.CloneStream();
    }

    constexpr LONG kSize = 256;  // Large enough to hide the tiling.
    constexpr DWORD kDataSize = static_cast<DWORD>(kSize) * kSize * 4;

    constexpr DWORD kHeadersSize =
        sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);

    BITMAPFILEHEADER fileHeader{};
    fileHeader.bfType = 0x4D42;  // "BM"
    fileHeader.bfSize = kHeadersSize + kDataSize;
    fileHeader.bfOffBits = kHeadersSize;

    BITMAPINFOHEADER infoHeader{};
    infoHeader.biSize = static_cast<DWORD>(sizeof(BITMAPINFOHEADER));
    infoHeader.biWidth = kSize;
    infoHeader.biHeight = kSize;
    infoHeader.biPlanes = 1;
    infoHeader.biBitCount = 32;
    infoHeader.biSizeImage = kDataSize;

    float exponent = 1.0f / std::clamp(density, 0.001f, 1.0f);
    uint8_t curve[256];
    for (int i = 0; i < 256; i++) {
        curve[i] = static_cast<uint8_t>(std::pow(i / 255.0f, exponent) * 255.0f);
    }

    std::vector<uint8_t> pixels(kDataSize);
    std::mt19937 random(0);
    std::uniform_int_distribution<int> distribution(0, 255);
    for (size_t i = 0; i < pixels.size(); i += 4) {
        uint8_t gray = curve[distribution(random)];
        pixels[i] = gray;
        pixels[i + 1] = gray;
        pixels[i + 2] = gray;
        pixels[i + 3] = 255;
    }

    InMemoryRandomAccessStream stream;
    DataWriter writer(stream);
    writer.WriteBytes(winrt::array_view<const uint8_t>(
        reinterpret_cast<const uint8_t*>(&fileHeader), sizeof(fileHeader)));
    writer.WriteBytes(winrt::array_view<const uint8_t>(
        reinterpret_cast<const uint8_t*>(&infoHeader), sizeof(infoHeader)));
    writer.WriteBytes(pixels);
    // Completes synchronously for an in-memory stream.
    writer.StoreAsync().get();
    writer.DetachStream();

    cachedStream = std::move(stream);
    cachedDensity = density;
    return cachedStream.CloneStream();
}

}  // namespace

DeskBlurBrush::DeskBlurBrush(wux::UIElement const& element, BlurParams params)
    : m_compositor(wuxh::ElementCompositionPreview::GetElementVisual(element)
                       .Compositor()),
      m_params(std::move(params)) {
    m_dispatcher =
        winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
    if (!m_dispatcher) {
        return;
    }

    try {
        auto weak = get_weak();
        auto dispatcher = m_dispatcher;
        auto refresh = [weak, dispatcher](auto&&, auto&&) {
            try {
                dispatcher.TryEnqueue([weak] {
                    try {
                        if (auto self = weak.get()) {
                            self->Refresh();
                        }
                    } catch (...) {
                    }
                });
            } catch (...) {
            }
        };

        m_uiSettings = winrt::Windows::UI::ViewManagement::UISettings();
        m_advancedEffectsToken =
            m_uiSettings.AdvancedEffectsEnabledChanged(refresh);
        m_energySaverToken = PowerManager::EnergySaverStatusChanged(refresh);
    } catch (...) {
        DLOG(L"Blur brush: failed to watch effect settings: %08X",
             winrt::to_hresult().value);
    }
}

DeskBlurBrush::~DeskBlurBrush() {
    try {
        if (m_uiSettings && m_advancedEffectsToken.value) {
            m_uiSettings.AdvancedEffectsEnabledChanged(m_advancedEffectsToken);
        }
    } catch (...) {
    }
    try {
        if (m_energySaverToken.value) {
            PowerManager::EnergySaverStatusChanged(m_energySaverToken);
        }
    } catch (...) {
    }
}

void DeskBlurBrush::OnConnected() {
    if (CompositionBrush()) {
        return;
    }

    try {
        CompositionBrush(ShouldUseFallback() ? CreateFallbackBrush()
                                             : CreateEffectBrush());
    } catch (...) {
        DLOG(L"Blur brush: falling back to a solid color: %08X",
             winrt::to_hresult().value);
        try {
            CompositionBrush(CreateFallbackBrush());
        } catch (...) {
        }
    }
}

void DeskBlurBrush::OnDisconnected() {
    if (auto brush = CompositionBrush()) {
        brush.Close();
        CompositionBrush(nullptr);
    }
}

void DeskBlurBrush::Refresh() {
    if (auto brush = CompositionBrush()) {
        brush.Close();
        CompositionBrush(nullptr);
        OnConnected();
    }
}

bool DeskBlurBrush::ShouldUseFallback() const {
    bool effectsOff = false;
    if (m_uiSettings) {
        try {
            effectsOff = !m_uiSettings.AdvancedEffectsEnabled();
        } catch (...) {
        }
    }
    return effectsOff || IsEnergySaverOn();
}

wuc::CompositionBrush DeskBlurBrush::CreateFallbackBrush() {
    auto color = m_params.fallback.value_or(m_params.tint);
    if (!m_params.fallback) {
        color.A = std::max<uint8_t>(color.A, 0xE6);
    }
    return m_compositor.CreateColorBrush(color);
}

wuc::CompositionBrush DeskBlurBrush::CreateEffectBrush() {
    // Rec. 709 luma coefficients.
    constexpr float kLumaR = 0.2126f;
    constexpr float kLumaG = 0.7152f;
    constexpr float kLumaB = 0.0722f;

    auto blur = winrt::make_self<effects::GaussianBlurEffect>();
    blur->Source = wuc::CompositionEffectSourceParameter(L"backdrop");
    blur->BlurAmount = m_params.blurAmount;
    blur->Name(L"Blur");

    wge::IGraphicsEffectSource top = *blur;

    if (std::abs(m_params.saturation - 1.0f) > 0.001f) {
        float s = std::max(m_params.saturation, 0.0f);
        float inv = 1.0f - s;

        auto saturation = winrt::make_self<effects::ColorMatrixEffect>();
        saturation->Source = top;
        auto& m = saturation->Matrix;
        m[0] = inv * kLumaR + s;
        m[1] = inv * kLumaR;
        m[2] = inv * kLumaR;
        m[3] = 0.0f;
        m[4] = inv * kLumaG;
        m[5] = inv * kLumaG + s;
        m[6] = inv * kLumaG;
        m[7] = 0.0f;
        m[8] = inv * kLumaB;
        m[9] = inv * kLumaB;
        m[10] = inv * kLumaB + s;
        m[11] = 0.0f;
        m[12] = 0.0f;
        m[13] = 0.0f;
        m[14] = 0.0f;
        m[15] = 1.0f;
        saturation->Name(L"Saturation");
        top = *saturation;
    }

    if (m_params.luminosityOpacity && *m_params.luminosityOpacity > 0.0f) {
        // Pulls the luminance of the backdrop towards the tint's luminance.
        float op = std::clamp(*m_params.luminosityOpacity, 0.0f, 1.0f);
        const auto& tint = m_params.tint;
        float tintLuma = (tint.R / 255.0f) * kLumaR +
                         (tint.G / 255.0f) * kLumaG +
                         (tint.B / 255.0f) * kLumaB;

        auto luminosity = winrt::make_self<effects::ColorMatrixEffect>();
        luminosity->Source = top;
        auto& m = luminosity->Matrix;
        m[0] = 1.0f - kLumaR * op;
        m[1] = -kLumaR * op;
        m[2] = -kLumaR * op;
        m[3] = 0.0f;
        m[4] = -kLumaG * op;
        m[5] = 1.0f - kLumaG * op;
        m[6] = -kLumaG * op;
        m[7] = 0.0f;
        m[8] = -kLumaB * op;
        m[9] = -kLumaB * op;
        m[10] = 1.0f - kLumaB * op;
        m[11] = 0.0f;
        m[12] = 0.0f;
        m[13] = 0.0f;
        m[14] = 0.0f;
        m[15] = 1.0f;
        m[16] = tintLuma * op;
        m[17] = tintLuma * op;
        m[18] = tintLuma * op;
        m[19] = 0.0f;
        luminosity->Name(L"Luminosity");
        top = *luminosity;
    }

    // Grain, tiled across the element. It breaks up the banding a strong blur
    // produces, and is what makes acrylic look like a material.
    wuc::CompositionSurfaceBrush noiseBrush{nullptr};
    if (m_params.noiseOpacity > 0.0f) {
        try {
            auto surface = wuxm::LoadedImageSurface::StartLoadFromStream(
                CreateNoiseStream(m_params.noiseDensity));
            noiseBrush = m_compositor.CreateSurfaceBrush(surface);
            noiseBrush.Stretch(wuc::CompositionStretch::None);
        } catch (...) {
            DLOG(L"Blur brush: no noise texture: %08X",
                 winrt::to_hresult().value);
            noiseBrush = nullptr;
        }
    }
    if (noiseBrush) {
        auto tiled = winrt::make_self<effects::BorderEffect>();
        tiled->Source = wuc::CompositionEffectSourceParameter(L"noise");

        // The texture is opaque; scale all premultiplied channels to fade it.
        float opacity = std::clamp(m_params.noiseOpacity, 0.0f, 1.0f);
        auto faded = winrt::make_self<effects::ColorMatrixEffect>();
        faded->Source = *tiled;
        faded->Matrix[0] = opacity;
        faded->Matrix[5] = opacity;
        faded->Matrix[10] = opacity;
        faded->Matrix[15] = opacity;
        faded->Name(L"NoiseOpacity");

        auto grain = winrt::make_self<effects::CompositeEffect>();
        grain->Mode = D2D1_COMPOSITE_MODE_SOURCE_OVER;
        grain->Sources.push_back(top);
        grain->Sources.push_back(*faded);
        top = *grain;
    }

    auto flood = winrt::make_self<effects::FloodEffect>();
    const auto& tint = m_params.tint;
    flood->Color = {tint.R / 255.0f, tint.G / 255.0f, tint.B / 255.0f,
                    tint.A / 255.0f};
    flood->Name(L"Tint");

    auto composite = winrt::make_self<effects::CompositeEffect>();
    composite->Mode = D2D1_COMPOSITE_MODE_SOURCE_OVER;
    composite->Sources.push_back(top);
    composite->Sources.push_back(*flood);

    auto factory = m_compositor.CreateEffectFactory(*composite);
    auto brush = factory.CreateBrush();
    brush.SetSourceParameter(L"backdrop", m_compositor.CreateBackdropBrush());
    if (noiseBrush) {
        brush.SetSourceParameter(L"noise", noiseBrush);
    }
    return brush;
}

}  // namespace desk
