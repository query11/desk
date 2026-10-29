// The TAP (tool access provider) object XAML diagnostics creates in the
// target process. Based on TAPSite and SimpleFactory from TranslucentTB's
// ExplorerTAP (https://github.com/TranslucentTB/TranslucentTB, GPL-3.0).
#include "pch.h"
#include "log.h"
#include "version.h"
#include "watcher.h"

namespace desk {
namespace {

winrt::com_ptr<VisualTreeWatcher> g_watcher;

class DeskTapSite
    : public winrt::implements<DeskTapSite, IObjectWithSite, winrt::non_agile> {
public:
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override try {
        // Only one watcher at a time.
        if (g_watcher) {
            g_watcher->Unadvise();
            g_watcher = nullptr;
        }

        m_site.copy_from(site);
        if (m_site) {
            g_watcher = winrt::make_self<VisualTreeWatcher>(m_site);
        }
        return S_OK;
    } catch (...) {
        HRESULT hr = winrt::to_hresult();
        DLOG(L"SetSite failed: %08X", static_cast<uint32_t>(hr));
        return hr;
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) noexcept override {
        return m_site.as(riid, site);
    }

private:
    winrt::com_ptr<IUnknown> m_site;
};

template <typename T>
class SimpleFactory
    : public winrt::implements<SimpleFactory<T>, IClassFactory, winrt::non_agile> {
public:
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,
                                             REFIID riid,
                                             void** object) override try {
        if (outer) {
            return CLASS_E_NOAGGREGATION;
        }
        *object = nullptr;
        return winrt::make<T>().as(riid, object);
    } catch (...) {
        return winrt::to_hresult();
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
};

}  // namespace
}  // namespace desk

_Use_decl_annotations_ STDAPI DllGetClassObject(REFCLSID rclsid,
                                                REFIID riid,
                                                LPVOID* ppv) try {
    if (rclsid == CLSID_DeskTap) {
        *ppv = nullptr;
        return winrt::make<desk::SimpleFactory<desk::DeskTapSite>>().as(riid, ppv);
    }
    return CLASS_E_CLASSNOTAVAILABLE;
} catch (...) {
    return winrt::to_hresult();
}

// The DLL pins itself in target processes; elsewhere it can be unloaded.
_Use_decl_annotations_ STDAPI DllCanUnloadNow() {
    return winrt::get_module_lock() ? S_FALSE : S_OK;
}
