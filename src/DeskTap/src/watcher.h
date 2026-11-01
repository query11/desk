#pragma once

namespace desk {

// Receives element add/remove notifications from XAML diagnostics on the UI
// threads and forwards them to the engine.
// Based on VisualTreeWatcher from TranslucentTB's ExplorerTAP (GPL-3.0).
class VisualTreeWatcher
    : public winrt::implements<VisualTreeWatcher,
                               IVisualTreeServiceCallback2,
                               winrt::non_agile> {
public:
    explicit VisualTreeWatcher(winrt::com_ptr<IUnknown> site);

    VisualTreeWatcher(const VisualTreeWatcher&) = delete;
    VisualTreeWatcher& operator=(const VisualTreeWatcher&) = delete;

    void Unadvise();

    // Starts AdviseVisualTreeChange on a new thread unless one is running;
    // the result goes to install::OnAdviseResult.
    void Advise();

private:
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation,
        VisualElement element,
        VisualMutationType mutationType) override;
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle element,
        VisualElementState elementState,
        LPCWSTR context) noexcept override;

    void QueueRelease(InstanceHandle handle);

    winrt::com_ptr<IXamlDiagnostics> m_diagnostics;
    winrt::com_ptr<IUnknown> m_testHooks;
    std::atomic<bool> m_advising{false};
};

// Advises the current watcher again after a failure. False when there is no
// watcher (no TAP site yet).
bool RetryAdvise();

}  // namespace desk
