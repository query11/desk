// Real Windows Composition regression test. No Explorer injection or settings
// changes: builds effect factories in this process using production interop.
#include "pch.h"
#include "effects.h"
#include <DispatcherQueue.h>
#include <cstdio>

namespace {

wge::IGraphicsEffect Graph(bool noise) {
    auto blur = winrt::make_self<desk::effects::GaussianBlurEffect>();
    blur->Name(L"Blur");
    blur->Source = wuc::CompositionEffectSourceParameter(L"backdrop");
    wge::IGraphicsEffectSource top = *blur;

    // Repeated unnamed nodes must be legal, including color matrices.
    for (int i = 0; i < 2; ++i) {
        auto matrix = winrt::make_self<desk::effects::ColorMatrixEffect>();
        matrix->Source = top;
        top = *matrix;
    }
    if (noise) {
        auto tiled = winrt::make_self<desk::effects::BorderEffect>();
        tiled->Source = wuc::CompositionEffectSourceParameter(L"noise");
        auto faded = winrt::make_self<desk::effects::ColorMatrixEffect>();
        faded->Source = *tiled;
        faded->Matrix[0] = faded->Matrix[5] = faded->Matrix[10] = faded->Matrix[15] = 0.025f;
        auto grain = winrt::make_self<desk::effects::CompositeEffect>();
        grain->Sources = {top, *faded};
        top = *grain;
    }
    auto tint = winrt::make_self<desk::effects::FloodEffect>();
    tint->Name(L"Tint");
    auto composite = winrt::make_self<desk::effects::CompositeEffect>();
    composite->Sources = {top, *tint};
    return *composite;
}

}  // namespace

int main() try {
    winrt::init_apartment(winrt::apartment_type::single_threaded);
    winrt::Windows::System::DispatcherQueueController queue{nullptr};
    DispatcherQueueOptions options{sizeof(options), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA};
    winrt::check_hresult(CreateDispatcherQueueController(options,
        reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(queue))));
    wuc::Compositor compositor;
    int failures = 0;
    for (bool noise : {false, true}) {
        for (bool animated : {false, true}) {
            const char* stage = "factory";
            try {
                auto graph = Graph(noise);
                auto factory = animated ? compositor.CreateEffectFactory(graph,
                    winrt::single_threaded_vector<winrt::hstring>({L"Blur.BlurAmount", L"Tint.Color"}))
                    : compositor.CreateEffectFactory(graph);
                // Compilation may still be pending here. CreateBrush validates
                // the factory synchronously; the compositor finishes on commit.
                stage = "brush";
                auto brush = factory.CreateBrush();
                stage = "source binding";
                brush.SetSourceParameter(L"backdrop", compositor.CreateColorBrush({255, 40, 60, 80}));
                if (noise) {
                    brush.SetSourceParameter(L"noise", compositor.CreateColorBrush({255, 128, 128, 128}));
                }
                if (animated) {
                    stage = "blur update";
                    brush.Properties().InsertScalar(L"Blur.BlurAmount", 20.0f);
                    stage = "tint update";
                    brush.Properties().InsertColor(L"Tint.Color", {128, 243, 243, 243});
                }
                std::printf("PASS noise=%d animated=%d\n", noise, animated);
            } catch (winrt::hresult_error const& e) {
                std::printf("FAIL noise=%d animated=%d stage=%s HRESULT=%08X\n", noise, animated, stage, unsigned(e.code().value));
                ++failures;
            }
        }
    }
    return failures ? 1 : 0;
} catch (winrt::hresult_error const& e) {
    std::printf("FAIL Composition initialization HRESULT=%08X\n", unsigned(e.code().value));
    return 1;
}
