// Based on XamlBlurBrush from TranslucentTB (GPL-3.0) and the extended version
// in the Windows 11 Taskbar Styler mod for Windhawk (GPL-3.0).
#include "pch.h"
#include "blur.h"
#include "effects.h"
#include "log.h"
#include "paths.h"
#include "status.h"

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

// Rec. 709 luma coefficients.
constexpr float kLumaR = 0.2126f;
constexpr float kLumaG = 0.7152f;
constexpr float kLumaB = 0.0722f;

// Effect properties that each brush sets for itself (the rest is baked into
// the effect graph). A slider for blur or tint only changes these.
const wchar_t* const kBlurAmount = L"Blur.BlurAmount";
const wchar_t* const kTintColor = L"Tint.Color";

bool UsesHostBackdrop(const BlurParams& params) {
    return params.source == BlurParams::Source::Host ||
           (params.source == BlurParams::Source::Auto && paths::IsStartMenu());
}

float TintLuma(const winrt::Windows::UI::Color& tint) {
    return (tint.R / 255.0f) * kLumaR + (tint.G / 255.0f) * kLumaG +
           (tint.B / 255.0f) * kLumaB;
}

wfn::float4 TintVector(const winrt::Windows::UI::Color& tint) {
    return {tint.R / 255.0f, tint.G / 255.0f, tint.B / 255.0f, tint.A / 255.0f};
}

// What shapes the effect graph. Blur amount and tint color are not part of
// it: they are set per brush.
struct GraphKey {
    bool host = false;
    float saturation = 1.0f;     // 1 = no saturation step.
    float luminosity = 0.0f;     // 0 = no luminosity step.
    float tintLuma = 0.0f;       // Only with a luminosity step.
    float noiseOpacity = 0.0f;   // 0 = no grain.
    float noiseDensity = 1.0f;   // Only with grain.

    bool operator==(const GraphKey&) const = default;
};

GraphKey KeyFor(const BlurParams& params) {
    GraphKey key;
    key.host = UsesHostBackdrop(params);
    if (std::abs(params.saturation - 1.0f) > 0.001f) {
        key.saturation = std::max(params.saturation, 0.0f);
    }
    if (params.luminosityOpacity && *params.luminosityOpacity > 0.0f) {
        key.luminosity = std::clamp(*params.luminosityOpacity, 0.0f, 1.0f);
        key.tintLuma = TintLuma(params.tint);
    }
    if (params.noiseOpacity > 0.0f) {
        key.noiseOpacity = std::clamp(params.noiseOpacity, 0.0f, 1.0f);
        key.noiseDensity = params.noiseDensity;
    }
    return key;
}

wge::IGraphicsEffect BuildGraph(const GraphKey& key, const BlurParams& params) {
    auto blur = winrt::make_self<effects::GaussianBlurEffect>();
    blur->Source = wuc::CompositionEffectSourceParameter(L"backdrop");
    blur->BlurAmount = params.blurAmount;
    // The host backdrop ends at the window's edges; a soft border would fade
    // the blur out there and show the unblurred content behind.
    if (key.host) {
        blur->BorderMode = D2D1_BORDER_MODE_HARD;
    }
    blur->Name(L"Blur");

    wge::IGraphicsEffectSource top = *blur;

    if (key.saturation != 1.0f) {
        float s = key.saturation;
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

    if (key.luminosity > 0.0f) {
        // Pulls the luminance of the backdrop towards the tint's luminance.
        float op = key.luminosity;
        float tintLuma = key.tintLuma;

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
    if (key.noiseOpacity > 0.0f) {
        auto tiled = winrt::make_self<effects::BorderEffect>();
        tiled->Source = wuc::CompositionEffectSourceParameter(L"noise");

        // The texture is opaque; scale all channels to fade it.
        float opacity = key.noiseOpacity;
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
    flood->Color = TintVector(params.tint);
    flood->Name(L"Tint");

    auto composite = winrt::make_self<effects::CompositeEffect>();
    composite->Mode = D2D1_COMPOSITE_MODE_SOURCE_OVER;
    composite->Sources.push_back(top);
    composite->Sources.push_back(*flood);
    return *composite;
}

// Per UI thread: effect graphs and noise images, which only work with the
// compositor they were made for. Bounded; released on the thread when its
// dispatcher shuts down (ReleaseThreadCaches).
struct FactoryEntry {
    GraphKey key;
    wuc::Compositor compositor{nullptr};
    wuc::CompositionEffectFactory factory{nullptr};
    // Without animatable properties the blur and tint are baked in too.
    bool animatable = false;
    float blurAmount = 0.0f;
    winrt::Windows::UI::Color tint{};
};

struct NoiseEntry {
    float density = 1.0f;
    wuc::Compositor compositor{nullptr};
    wuxm::LoadedImageSurface surface{nullptr};
};

struct ThreadCaches {
    std::vector<FactoryEntry> factories;  // Most recently used first.
    std::vector<NoiseEntry> noise;
};

constexpr size_t kMaxFactories = 16;
constexpr size_t kMaxNoiseImages = 4;

thread_local ThreadCaches* t_caches = nullptr;

ThreadCaches& Caches() {
    if (!t_caches) {
        t_caches = new ThreadCaches();
    }
    return *t_caches;
}

bool Usable(wuc::CompositionEffectFactory const& factory) {
    try {
        return factory.LoadStatus() == wuc::CompositionEffectFactoryLoadStatus::Success;
    } catch (...) {
        return false;
    }
}

const FactoryEntry& FactoryFor(wuc::Compositor const& compositor,
                               const BlurParams& params) {
    auto& caches = Caches();
    auto key = KeyFor(params);
    auto& factories = caches.factories;
    for (auto it = factories.begin(); it != factories.end(); ++it) {
        bool matches = it->key == key && it->compositor == compositor &&
                       (it->animatable || (it->blurAmount == params.blurAmount &&
                                           it->tint == params.tint));
        if (!matches) {
            continue;
        }
        if (!Usable(it->factory)) {
            factories.erase(it);
            break;
        }
        status::Count(status::kEffectFactoryHits);
        std::rotate(factories.begin(), it, it + 1);
        return factories.front();
    }

    FactoryEntry entry;
    entry.key = key;
    entry.compositor = compositor;
    entry.blurAmount = params.blurAmount;
    entry.tint = params.tint;
    auto graph = BuildGraph(key, params);
    try {
        entry.factory = compositor.CreateEffectFactory(
            graph, winrt::single_threaded_vector<winrt::hstring>({kBlurAmount, kTintColor}));
        entry.animatable = true;
    } catch (...) {
        static thread_local bool logged = false;
        if (!logged) {
            logged = true;
            DLOG(L"Blur brush: effect properties can't be animated (%08X); "
                 L"changes rebuild the effect",
                 winrt::to_hresult().value);
        }
        entry.factory = compositor.CreateEffectFactory(graph);
        entry.animatable = false;
    }
    status::Count(status::kEffectFactories);
    factories.insert(factories.begin(), std::move(entry));
    if (factories.size() > kMaxFactories) {
        factories.pop_back();
    }
    return factories.front();
}

wuxm::LoadedImageSurface NoiseFor(wuc::Compositor const& compositor, float density) {
    auto& noise = Caches().noise;
    for (auto it = noise.begin(); it != noise.end(); ++it) {
        if (it->density == density && it->compositor == compositor) {
            status::Count(status::kNoiseSurfaceHits);
            std::rotate(noise.begin(), it, it + 1);
            return noise.front().surface;
        }
    }
    NoiseEntry entry;
    entry.density = density;
    entry.compositor = compositor;
    entry.surface = wuxm::LoadedImageSurface::StartLoadFromStream(CreateNoiseStream(density));
    status::Count(status::kNoiseSurfaces);
    noise.insert(noise.begin(), std::move(entry));
    if (noise.size() > kMaxNoiseImages) {
        noise.pop_back();
    }
    return noise.front().surface;
}

winrt::Windows::UI::Color SolidFallbackColor(const BlurParams& params) {
    auto color = params.fallback.value_or(params.tint);
    if (!params.fallback) {
        color.A = std::max<uint8_t>(color.A, 0xE6);
    }
    return color;
}

void SetAnimatedValues(wuc::CompositionEffectBrush const& brush, const BlurParams& params) {
    auto properties = brush.Properties();
    properties.InsertScalar(kBlurAmount, params.blurAmount);
    properties.InsertVector4(kTintColor, TintVector(params.tint));
}

}  // namespace

DeskBlurBrush::DeskBlurBrush(wux::UIElement const& element, BlurParams params)
    : m_compositor(wuxh::ElementCompositionPreview::GetElementVisual(element)
                       .Compositor()),
      m_params(std::move(params)) {
    status::Count(status::kBlurBrushes);
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
    status::Count(status::kBlurBrushesReleased);
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
        if (ShouldUseFallback()) {
            CompositionBrush(CreateFallbackBrush());
        } else {
            CompositionBrush(CreateEffectBrush());
        }
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

HRESULT DeskBlurBrush::UpdateParams(const BlurParams* params) noexcept try {
    if (!params) {
        return E_INVALIDARG;
    }
    auto previous = KeyFor(m_params);
    m_params = *params;

    auto brush = CompositionBrush();
    if (!brush) {
        return S_OK;  // Used when it connects.
    }
    if (m_usingFallback) {
        if (auto color = brush.try_as<wuc::CompositionColorBrush>();
            color && ShouldUseFallback()) {
            color.Color(SolidFallbackColor(m_params));
            status::Count(status::kBlurInPlaceUpdates);
            return S_OK;
        }
    } else if (m_animatable && previous == KeyFor(m_params)) {
        if (auto effect = brush.try_as<wuc::CompositionEffectBrush>()) {
            try {
                SetAnimatedValues(effect, m_params);
                status::Count(status::kBlurInPlaceUpdates);
                return S_OK;
            } catch (...) {
                m_animatable = false;
            }
        }
    }
    Refresh();
    return S_OK;
} catch (...) {
    return winrt::to_hresult();
}

void DeskBlurBrush::ReleaseThreadCaches() {
    delete t_caches;
    t_caches = nullptr;
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
    m_usingFallback = true;
    m_animatable = false;
    return m_compositor.CreateColorBrush(SolidFallbackColor(m_params));
}

wuc::CompositionBrush DeskBlurBrush::CreateEffectBrush() {
    const auto& entry = FactoryFor(m_compositor, m_params);
    auto brush = entry.factory.CreateBrush();
    if (entry.animatable) {
        // The graph was built with whoever needed it first; these are ours.
        SetAnimatedValues(brush, m_params);
    }
    brush.SetSourceParameter(L"backdrop", entry.key.host
                                              ? m_compositor.CreateHostBackdropBrush()
                                              : m_compositor.CreateBackdropBrush());
    if (entry.key.noiseOpacity > 0.0f) {
        try {
            auto noise = m_compositor.CreateSurfaceBrush(
                NoiseFor(m_compositor, entry.key.noiseDensity));
            noise.Stretch(wuc::CompositionStretch::None);
            brush.SetSourceParameter(L"noise", noise);
        } catch (...) {
            DLOG(L"Blur brush: no noise texture: %08X", winrt::to_hresult().value);
        }
    }
    m_usingFallback = false;
    m_animatable = entry.animatable;
    return brush;
}

}  // namespace desk
