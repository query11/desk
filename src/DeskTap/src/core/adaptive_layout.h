#pragma once
#include <algorithm>
#include <cmath>

namespace desk::core {

struct LayoutInput {
    double width = 0, tray = 0, monitor = 0, compactMonitor = 0;
    double extra = 24, requestedButton = 48, icon = 28;
    int buttons = 0, previousLevel = 0;
};
struct LayoutPlan {
    // Screen-space planning budget only, not values to write to TaskbarFrame.
    double left = 16, right = 16, maxWidth = 0, button = 48;
    int monitorLevel = 0; // full, primary metric, hidden
    bool operator==(const LayoutPlan&) const = default;
};

inline LayoutPlan FitTaskbar(const LayoutInput& in) {
    LayoutPlan out;
    double width = std::max(0.0, in.width);
    double minimum = std::max(40.0, std::clamp(in.icon, 16.0, 48.0) + 8);
    double requested = std::clamp(in.requestedButton, minimum, 80.0);
    int buttons = std::max(1, in.buttons);
    out.right = std::min(width, std::max(0.0, in.tray) + 16);
    auto monitorWidth = [&](int level) {
        return std::max(0.0, level == 0 ? in.monitor : level == 1 ? in.compactMonitor : 0.0);
    };
    for (int level = 0; level <= 2; level++) {
        out.monitorLevel = level;
        out.left = std::min(std::max(0.0, width - out.right), monitorWidth(level) + 16);
        out.maxWidth = std::max(0.0, width - out.left - out.right);
        double available = out.maxWidth - std::max(0.0, in.extra);
        // Require a little spare room before bringing hidden information back.
        double recovery = level < in.previousLevel ? 24.0 : 0.0;
        if (available >= buttons * minimum + recovery || level == 2) {
            out.button = std::clamp(std::floor(available / buttons / 2) * 2, minimum, requested);
            break;
        }
    }
    // Keep the familiar screen centre whenever it fits between both side islands.
    // Unequal margins are only needed when the centre would otherwise overlap a side.
    double symmetric = std::max(out.left, out.right);
    double centredWidth = std::max(0.0, width - 2 * symmetric);
    if (buttons * out.button + std::max(0.0, in.extra) <= centredWidth) {
        out.left = out.right = symmetric;
        out.maxWidth = centredWidth;
    }
    return out;
}

} // namespace desk::core
