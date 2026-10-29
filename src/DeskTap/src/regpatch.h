#pragma once

namespace desk::regpatch {

// Hooks RegOpenKeyExW/RegQueryValueExW so that, inside a
// ReportCompositionDiagDisabledScope, Windows.UI.Xaml reads
// HKLM\Software\Microsoft\XAML\Debug\DisableCompositionDiag as 1.
//
// XAML composition diagnostics rebuild a process-wide visual tree walker
// without locking whenever a DirectComposition visual is added, so in a
// process with several UI threads (Explorer) they can corrupt the heap. The
// value is read once, during AdviseVisualTreeChange. Found by the Windows 11
// Taskbar Styler mod for Windhawk (GPL-3.0).
bool Install();

class ReportCompositionDiagDisabledScope {
public:
    ReportCompositionDiagDisabledScope();
    ~ReportCompositionDiagDisabledScope();
    ReportCompositionDiagDisabledScope(const ReportCompositionDiagDisabledScope&) = delete;
    ReportCompositionDiagDisabledScope& operator=(const ReportCompositionDiagDisabledScope&) = delete;
};

}  // namespace desk::regpatch
