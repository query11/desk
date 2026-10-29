#pragma once

namespace desk::surface {

// Explorer never paints the taskbar windows themselves. After a display
// change, leftovers in their surface can show up as white boxes behind a
// see-through taskbar. Subclasses the taskbar windows of the current thread to
// clear them. Idempotent.
void EnsureForCurrentThread();

}  // namespace desk::surface
