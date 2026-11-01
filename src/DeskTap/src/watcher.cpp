// Based on VisualTreeWatcher from TranslucentTB's ExplorerTAP
// (https://github.com/TranslucentTB/TranslucentTB, GPL-3.0), with the
// diagnostics reference release and the composition diagnostics workaround
// found by the Windows 11 Taskbar Styler mod for Windhawk (GPL-3.0).
#include "pch.h"
#include "watcher.h"
#include "clickthrough.h"
#include "engine.h"
#include "install.h"
#include "log.h"
#include "paths.h"
#include "regpatch.h"

namespace desk {
namespace {

using winrt::Windows::System::DispatcherQueue;
using winrt::Windows::System::DispatcherQueuePriority;

// Implemented by XamlDiagnostics but not declared in xamlom.h.
// UnregisterInstance drops the runtime object cached for a handle, which is
// the only reference the diagnostics keep to an element once reported.
struct __declspec(uuid("735941a2-3ee3-495a-8da9-972627003075"))
    IXamlDiagnosticsTestHooks : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE UnregisterInstance(InstanceHandle handle) = 0;
    virtual HRESULT STDMETHODCALLTYPE TryGetDispatcherQueueForObject(
        InstanceHandle handle,
        void** dispatcherQueue) = 0;
};

// The BSTRs in VisualElement are owned by the callee.
struct OwnedBstr {
    explicit OwnedBstr(BSTR value) : value(value) {}
    ~OwnedBstr() { SysFreeString(value); }
    OwnedBstr(const OwnedBstr&) = delete;
    OwnedBstr& operator=(const OwnedBstr&) = delete;
    std::wstring_view view() const {
        return value ? std::wstring_view(value, SysStringLen(value))
                     : std::wstring_view();
    }
    BSTR value;
};

thread_local std::vector<InstanceHandle> t_pendingReleases;
thread_local bool t_releaseScheduled = false;

}  // namespace

VisualTreeWatcher::VisualTreeWatcher(winrt::com_ptr<IUnknown> site)
    : m_diagnostics(site.as<IXamlDiagnostics>()) {
    winrt::com_ptr<IXamlDiagnosticsTestHooks> hooks;
    if (SUCCEEDED(m_diagnostics->QueryInterface(__uuidof(IXamlDiagnosticsTestHooks),
                                                hooks.put_void()))) {
        m_testHooks = hooks.as<IUnknown>();
    } else {
        DLOG(L"IXamlDiagnosticsTestHooks unavailable; removed elements stay referenced");
    }
    Advise();
}

void VisualTreeWatcher::Advise() {
    if (m_advising.exchange(true)) {
        return;
    }

    // Advising from the current thread can hang in Advising::RunOnUIThread, so
    // it happens on a new thread. Windows.UI.Xaml reads DisableCompositionDiag
    // during this call; reporting it as set avoids composition diagnostics,
    // which can corrupt the heap when several UI threads add visuals at once.
    AddRef();
    HANDLE thread = CreateThread(
        nullptr, 0,
        [](LPVOID parameter) -> DWORD {
            auto* self = static_cast<VisualTreeWatcher*>(parameter);
            HRESULT hr;
            {
                regpatch::ReportCompositionDiagDisabledScope scope;
                hr = self->m_diagnostics.as<IVisualTreeService3>()
                         ->AdviseVisualTreeChange(self);
            }
            DLOG(L"AdviseVisualTreeChange: %08X", static_cast<uint32_t>(hr));
            self->m_advising = false;
            install::OnAdviseResult(hr);
            self->Release();
            return 0;
        },
        this, 0, nullptr);
    if (thread) {
        CloseHandle(thread);
    } else {
        HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
        m_advising = false;
        install::OnAdviseResult(hr);
        Release();
    }
}

void VisualTreeWatcher::Unadvise() {
    HRESULT hr = m_diagnostics.as<IVisualTreeService3>()->UnadviseVisualTreeChange(this);
    DLOG(L"UnadviseVisualTreeChange: %08X", static_cast<uint32_t>(hr));
}

HRESULT VisualTreeWatcher::OnVisualTreeChange(ParentChildRelation,
                                              VisualElement element,
                                              VisualMutationType mutationType) {
    OwnedBstr fileName(element.SrcInfo.FileName);
    OwnedBstr hash(element.SrcInfo.Hash);
    OwnedBstr name(element.Name);
    OwnedBstr type(element.Type);

    try {
        if (mutationType == Add) {
            wf::IInspectable inspectable;
            HRESULT hr = m_diagnostics->GetIInspectableFromHandle(
                element.Handle,
                reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable)));
            if (SUCCEEDED(hr) && inspectable) {
                if (auto frameworkElement =
                        inspectable.try_as<wux::FrameworkElement>()) {
                    engine::OnElementAdded(element.Handle, frameworkElement,
                                           type.view());
                } else if (paths::IsExplorer()) {
                    clickthrough::OnIslandRoot(inspectable);
                }
            }
        } else if (mutationType == Remove) {
            engine::OnElementRemoved(element.Handle);
            QueueRelease(element.Handle);
        }
    } catch (...) {
        DLOG(L"OnVisualTreeChange failed for %.*s: %08X",
             static_cast<int>(type.view().size()), type.view().data(),
             winrt::to_hresult().value);
    }

    // Returning an error stops (some) further notifications.
    return S_OK;
}

HRESULT VisualTreeWatcher::OnElementStateChanged(InstanceHandle,
                                                 VisualElementState,
                                                 LPCWSTR) noexcept {
    return S_OK;
}

void VisualTreeWatcher::QueueRelease(InstanceHandle handle) {
    if (!m_testHooks) {
        return;
    }

    t_pendingReleases.push_back(handle);
    if (t_releaseScheduled) {
        return;
    }

    // The removal is reported from inside the walk over the removed subtree,
    // so the release waits until that is done.
    auto queue = DispatcherQueue::GetForCurrentThread();
    if (!queue) {
        t_pendingReleases.clear();
        return;
    }

    t_releaseScheduled = true;
    auto hooks = m_testHooks.as<IXamlDiagnosticsTestHooks>();
    queue.TryEnqueue(DispatcherQueuePriority::Low, [hooks] {
        t_releaseScheduled = false;
        auto pending = std::move(t_pendingReleases);
        t_pendingReleases.clear();
        for (auto handle : pending) {
            hooks->UnregisterInstance(handle);
        }
    });
}

}  // namespace desk
