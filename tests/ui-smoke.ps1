# UI smoke test for the settings window.
#
# Starts a published Desk.exe *without* DeskTap.dll (so nothing is injected
# into the build machine's shell), then drives the window through UI
# Automation: every page at a wide and a narrow size, in light and dark mode,
# plus the toast and the confirmation dialogs. Fails when the window throws,
# a page doesn't open, or a dialog doesn't behave. Writes a screenshot of each
# step and prints the layout of every page, so a change to the window can be
# reviewed from the build log and the "ui-smoke" artifact.
#
# Windows PowerShell 5.1: UI Automation is always there. Saved with a BOM so
# the Chinese labels survive.

param(
    [Parameter(Mandatory = $true)][string]$Publish,
    [Parameter(Mandatory = $true)][string]$Themes,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
'@

$A = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
# Searches walk the raw tree: page titles and dialog texts sit inside control
# templates, which the default (control) view leaves out.
$Raw = [System.Windows.Automation.TreeWalker]::RawViewWalker
$failures = New-Object System.Collections.Generic.List[string]

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $failures.Add($message)
}

New-Item -ItemType Directory -Force $Out | Out-Null
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop } catch { Write-Host "Display resolution unchanged: $($_.Exception.Message)" }

# A copy of Desk without its engine.
$app = Join-Path $env:RUNNER_TEMP 'desk-ui-smoke'
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
Copy-Item -Recurse $Publish $app
Remove-Item -Force (Join-Path $app 'DeskTap.dll') -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $app 'themes'))) { Copy-Item -Recurse $Themes (Join-Path $app 'themes') }
$logs = Join-Path $env:LOCALAPPDATA 'Desk\logs'

function Wait-Until([scriptblock]$condition, [int]$seconds = 10) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

# Everything under $root, depth first, in reading order.
function Get-Elements($root, [int]$limit = 4000) {
    $list = New-Object System.Collections.Generic.List[object]
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    while ($stack.Count -gt 0 -and $list.Count -lt $limit) {
        $element = $stack.Pop()
        $list.Add($element)
        try {
            $child = $Raw.GetLastChild($element)
            while ($child -ne $null) { $stack.Push($child); $child = $Raw.GetPreviousSibling($child) }
        } catch { }
    }
    return ,$list
}

function Find-Element($root, [scriptblock]$match) {
    foreach ($element in (Get-Elements $root)) {
        try { if (& $match $element.Current) { return $element } } catch { }
    }
    return $null
}

function Find-Id($root, [string]$id) {
    Find-Element $root ({ param($c) $c.AutomationId -eq $id }.GetNewClosure())
}

function Find-Name($root, [string]$name, $type = $null) {
    Find-Element $root ({ param($c) $c.Name -eq $name -and ($type -eq $null -or $c.ControlType -eq $type) }.GetNewClosure())
}

# What UI Automation sees, once, when something expected is missing.
$script:treeShown = $false
function Write-Tree($root) {
    if ($script:treeShown) { return }
    $script:treeShown = $true
    $origin = $root.Current.BoundingRectangle
    Write-Host "----- raw tree of '$($root.Current.Name)' ($($root.Current.ClassName)):"
    foreach ($element in (Get-Elements $root 250)) {
        try {
            $c = $element.Current
            $box = $c.BoundingRectangle
            $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            Write-Host ("  {0} '{1}' #{2} [{3}] @{4},{5} {6}x{7}" -f $type, $c.Name, $c.AutomationId, $c.ClassName,
                [int]($box.Left - $origin.Left), [int]($box.Top - $origin.Top), [int]$box.Width, [int]$box.Height)
        } catch { }
    }
}

# Invoke or select through UI Automation; a real mouse click otherwise.
function Click($element, [string]$what) {
    if (-not $element) { Fail "$what not found"; return $false }
    [object]$pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    } elseif ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
    } else {
        $box = $element.Current.BoundingRectangle
        [Native]::SetCursorPos([int]($box.Left + $box.Width / 2), [int]($box.Top + $box.Height / 2)) | Out-Null
        [Native]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
        [Native]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
    }
    return $true
}

# The window as it is on screen; the bitmap starts at the window rectangle's corner.
function Get-Shot($window) {
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    $rect = New-Object Native+RECT
    [Native]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    # PW_RENDERFULLCONTENT: what DWM shows, also for hardware-rendered windows.
    [Native]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    return @($bitmap, $rect)
}

function Save-Shot($window, [string]$name) {
    $bitmap, $rect = Get-Shot $window
    $bitmap.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Average brightness (0 black .. 1 white) of a 5 x 5 patch around a point of the screen.
function Get-Brightness($bitmap, $rect, [double]$screenX, [double]$screenY) {
    $sum = 0.0
    $count = 0
    for ($dx = -2; $dx -le 2; $dx++) {
        for ($dy = -2; $dy -le 2; $dy++) {
            $x = [Math]::Min([Math]::Max([int]$screenX - $rect.Left + $dx, 0), $bitmap.Width - 1)
            $y = [Math]::Min([Math]::Max([int]$screenY - $rect.Top + $dy, 0), $bitmap.Height - 1)
            $color = $bitmap.GetPixel($x, $y)
            $sum += (0.2126 * $color.R + 0.7152 * $color.G + 0.0722 * $color.B) / 255
            $count++
        }
    }
    return $sum / $count
}

# Whether the window really shows the mode it should. Measured on the
# overview's taskbar status card, in its top padding: WPF draws it, so the
# capture shows it whatever the system backdrop behind the window does.
# The title bar is only printed, for comparison.
function Assert-Mode($window, [bool]$light, [string]$what) {
    $tile = Find-Id $window 'TaskbarTile'
    if (-not $tile) { Fail "$what`: no status card to check the colors on"; return }
    $box = $tile.Current.BoundingRectangle
    $origin = $window.Current.BoundingRectangle
    $bitmap, $rect = Get-Shot $window
    try {
        $card = Get-Brightness $bitmap $rect ($box.Left + $box.Width / 2) ($box.Top + 8)
        $titleBar = Get-Brightness $bitmap $rect ($origin.Left + $origin.Width / 2) ($origin.Top + 12)
    } finally { $bitmap.Dispose() }
    $expected = if ($light) { 'light' } else { 'dark' }
    Write-Host ("Colors of '{0}' (expected {1}): card {2:0.00}, title bar {3:0.00}" -f $what, $expected, $card, $titleBar)
    if ($light -and $card -lt 0.5) { Fail "$what`: the window should be light, but its cards are dark ($([Math]::Round($card, 2)))" }
    if (-not $light -and $card -gt 0.5) { Fail "$what`: the window should be dark, but its cards are light ($([Math]::Round($card, 2)))" }
}

# Every named control and text in the page, with its place relative to the
# window; fails when one reaches past the window's right edge.
function Write-Layout($window, [string]$title) {
    $origin = $window.Current.BoundingRectangle
    Write-Host "----- layout: $title (window $([int]$origin.Width)x$([int]$origin.Height))"
    $shown = 0
    $index = 0
    # foreach, not a pipeline: Get-Elements hands back the list as one object.
    foreach ($element in (Get-Elements $window)) {
        if ($index++ -eq 0) { continue }  # the window itself
        try {
            $c = $element.Current
            $box = $c.BoundingRectangle
            if ($box.IsEmpty -or $c.IsOffscreen) { continue }
            $id = ''
            if ($c.AutomationId -and $c.AutomationId -notmatch '^\d+$') { $id = " #$($c.AutomationId)" }
            $past = ($box.Right - $origin.Left) -gt ($origin.Width + 1)
            if (-not ($c.Name -or $id -or $past)) { continue }
            $type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            $name = $c.Name
            if ($name.Length -gt 36) { $name = $name.Substring(0, 36) + '…' }
            $flag = ''
            if ($past) { $flag = '  <-- past the right edge' }
            Write-Host ("  {0} '{1}'{2} @{3},{4} {5}x{6}{7}" -f $type, $name, $id, [int]($box.Left - $origin.Left),
                [int]($box.Top - $origin.Top), [int]$box.Width, [int]$box.Height, $flag)
            $shown++
            if ($past) { Fail "$title`: '$($c.Name)' goes past the right edge of the window" }
            # WPF UI can't show a symbol past U+FFFF: it comes out as a letter
            # and a control character ('ԧ' and U+000F) instead of the icon.
            if ($c.Name -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') {
                Fail "$title`: an icon shows as the text '$($c.Name)' at $([int]($box.Left - $origin.Left)),$([int]($box.Top - $origin.Top))"
            }
        } catch { }
    }
    # An empty walk would make the edge check pass without looking at anything.
    if ($shown -lt 10) { Fail "$title`: only $shown elements found for the layout check" }
}

$personalize = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$modeNames = @('AppsUseLightTheme', 'SystemUsesLightTheme')
$savedModes = @{}
if (Test-Path $personalize) {
    $current = Get-ItemProperty $personalize
    foreach ($name in $modeNames) { if ($current.PSObject.Properties[$name]) { $savedModes[$name] = $current.$name } }
} else {
    New-Item -Force $personalize | Out-Null
}

# The window follows the app mode; the taskbar and the Start menu follow the
# Windows mode. They are set opposite to each other, so a window that went by
# the Windows mode (or by the name of the Windows theme, as WPF UI does) would
# come out in the wrong colors. Values only: -Force on the existing key would
# delete its other values.
function Start-Desk([int]$light) {
    Set-ItemProperty $personalize AppsUseLightTheme $light -Type DWord
    Set-ItemProperty $personalize SystemUsesLightTheme (1 - $light) -Type DWord
    $process = Start-Process (Join-Path $app 'Desk.exe') -PassThru
    $window = Wait-Until {
        $condition = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
        $found = $A::RootElement.FindAll($Tree::Children, $condition)
        # "Desk", then "Desk · <page>" once a page is open.
        foreach ($w in $found) { if ($w.Current.Name -like 'Desk*' -and $w.Current.ClassName -notmatch 'Tray') { return $w } }
    } 30
    if (-not $window) { throw 'The settings window did not open' }
    $box = $window.Current.BoundingRectangle
    Write-Host "Window '$($window.Current.Name)' [$($window.Current.ClassName)] $([int]$box.Width)x$([int]$box.Height), light=$light"
    return @($process, $window)
}

function Set-Size($window, [int]$width, [int]$height) {
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    [Native]::MoveWindow($hwnd, 20, 20, $width, $height, $true) | Out-Null
    [Native]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 800
}

$pages = @('概览', '任务栏', '开始菜单', '设置', '外观方案', '显示器')
$navIds = @('NavHome', 'NavTaskbar', 'NavStartMenu', 'NavSettings', 'NavAppearance', 'NavDisplays')
$headings = @{
    '概览' = '桌面，刚刚好'
    '任务栏' = '让常用应用，恰到好处'
    '开始菜单' = '每一次打开，都清爽'
    '设置' = '按你的习惯运行'
    '外观方案' = '给桌面换一种气质'
    '显示器' = '每一块屏幕，都合适'
}

function Open-Page($window, [int]$index) {
    $item = Find-Id $window $navIds[$index]
    if (-not $item) { Write-Tree $window }
    if (-not (Click $item "navigation item $($navIds[$index])")) { return $false }
    Start-Sleep -Milliseconds 900
    if (-not (Assert-Title $window $pages[$index])) { return $false }
    # No header above the pages: the title bar names the page, also when
    # the pane is only icons.
    $name = $window.Current.Name
    if ($name -ne "Desk · $($pages[$index])") { Fail "the title bar says '$name' on page '$($pages[$index])'" }
    return $true
}

# Check the visible page heading.
function Assert-Title($window, [string]$expected) {
    $expected = $headings[$expected]
    $left = $window.Current.BoundingRectangle.Left + 40
    # Built here: GetNewClosure only captures the variables of the scope it runs in.
    $isTitle = { param($c) $c.Name -eq $expected -and $c.BoundingRectangle.Height -ge 30 -and
        $c.BoundingRectangle.Left -gt $left }.GetNewClosure()
    $title = Wait-Until { Find-Element $window $isTitle } 5
    if (-not $title) { Fail "page title '$expected' not shown"; Write-Tree $window; return $false }
    # A page opens at its top: the heading is at the top of the content area,
    # not scrolled up out of view by the position the previous page was left
    # at. (With the header gone, the heading is the first thing in there.)
    $origin = $window.Current.BoundingRectangle.Top
    $top = [int]($title.Current.BoundingRectangle.Top - $origin)
    $content = Find-Id $window 'PART_NavigationViewContentPresenter'
    $start = 60
    if ($content) { $start = [int]($content.Current.BoundingRectangle.Top - $origin) }
    if ($top -lt $start - 2) { Fail "page '$expected' opened scrolled (heading at $top, content area from $start)" }
    return $true
}

function Visit-Pages($window, [string]$prefix, [bool]$layout, [bool]$light) {
    for ($i = 0; $i -lt $pages.Count; $i++) {
        if (Open-Page $window $i) {
            Save-Shot $window "$prefix-$i-$($pages[$i])"
            if ($i -eq 0) { Assert-Mode $window $light $prefix }
            if ($layout) { Write-Layout $window "$prefix $($pages[$i])" }
        }
    }
}

# A text that is on screen now (a closed notice may still be in the tree).
function Find-Shown($root, [string]$name) {
    Find-Element $root ({ param($c) $c.Name -eq $name -and -not $c.IsOffscreen -and -not $c.BoundingRectangle.IsEmpty }.GetNewClosure())
}

# The conflict notice on the overview: closed once, it stays closed when the
# page is built again (on every visit).
function Test-ConflictNotice($window) {
    Open-Page $window 0 | Out-Null
    $title = Find-Shown $window '可能有冲突'
    if (-not $title) { Fail 'the conflict notice is not on the overview'; Write-Tree $window; return }
    $row = $title.Current.BoundingRectangle
    $middle = $row.Top + $row.Height / 2
    # The notice's own close button: on the line of its title.
    $close = Find-Element $window ({ param($c) $c.AutomationId -eq 'CloseButton' -and -not $c.IsOffscreen -and
        [Math]::Abs($c.BoundingRectangle.Top + $c.BoundingRectangle.Height / 2 - $middle) -lt 24 }.GetNewClosure())
    if (-not (Click $close 'the close button of the conflict notice')) { Write-Tree $window; return }
    if (-not (Wait-Until { -not (Find-Shown $window '可能有冲突') } 5)) { Fail 'the conflict notice did not close' }
    Open-Page $window 1 | Out-Null
    Open-Page $window 0 | Out-Null
    Start-Sleep -Milliseconds 500
    if (Find-Shown $window '可能有冲突') { Fail 'the closed conflict notice is back after visiting another page' }
    Save-Shot $window 'dark-conflict-closed'
}

# A window opened again comes back where it was closed, at its size and on
# its page, also after Desk restarted.
function Test-Reopened($window, [int]$x, [int]$y, [int]$width, [int]$height, [string]$page) {
    $rect = New-Object Native+RECT
    [Native]::GetWindowRect([IntPtr]$window.Current.NativeWindowHandle, [ref]$rect) | Out-Null
    $now = "$($rect.Left),$($rect.Top) $($rect.Right - $rect.Left)x$($rect.Bottom - $rect.Top)"
    Write-Host "Reopened at $now"
    if ([Math]::Abs($rect.Left - $x) -gt 2 -or [Math]::Abs($rect.Top - $y) -gt 2 -or
        [Math]::Abs($rect.Right - $rect.Left - $width) -gt 2 -or [Math]::Abs($rect.Bottom - $rect.Top - $height) -gt 2) {
        Fail "the window reopened at $now, not where it was closed ($x,$y ${width}x$height)"
    }
    $heading = $headings[$page]
    if (-not (Wait-Until { Find-Shown $window $heading } 5)) { Fail "the window did not reopen on the page it was closed on ($page)" }
}

# Clicks a button that opens a confirmation, checks the dialog, then answers it.
function Confirm-Dialog($window, [string]$button, [string]$title, [string]$answer, [string]$shot) {
    if (-not (Click (Find-Id $window $button) $button)) { return }
    $dialogTitle = Wait-Until { Find-Name $window $title } 5
    if (-not $dialogTitle) { Fail "dialog '$title' did not open"; Write-Tree $window; return }
    Start-Sleep -Milliseconds 500
    Save-Shot $window $shot
    $choice = Wait-Until { Find-Name $window $answer ([System.Windows.Automation.ControlType]::Button) } 5
    Click $choice "'$answer' in dialog '$title'" | Out-Null
}

function Stop-Desk($window, $process, [string]$prefix) {
    Open-Page $window 3 | Out-Null
    Confirm-Dialog $window 'ExitButton' '退出 Desk？' '退出并还原' "$prefix-dialog-exit"
    if (-not $process.WaitForExit(15000)) {
        Fail "$prefix`: Desk did not exit after confirming"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- light mode
$process, $window = Start-Desk 1
Set-Size $window 1040 1000
Visit-Pages $window 'light-wide' $true $true

# Feedback and confirmations on the overview.
Open-Page $window 0 | Out-Null
# Without its engine Desk can't run: the line under the title says so too,
# instead of "已启用" next to the label.
$stateText = (Find-Id $window 'StateText').Current.Name
$summaryText = (Find-Id $window 'SummaryText').Current.Name
Write-Host "Overview without the engine: '$stateText' · '$summaryText'"
if ($stateText -ne '无法运行' -or $summaryText -notmatch '没有启动') { Fail "overview without the engine says '$stateText' / '$summaryText'" }
$maintenance = Find-Id $window 'MaintenanceExpander'
[object]$expand = $null
if ($maintenance -and $maintenance.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
    $expand.Expand()
    Start-Sleep -Milliseconds 300
} else { Fail 'Maintenance expander not found' }
Click (Find-Id $window 'ReapplyButton') 'ReapplyButton' | Out-Null
if (-not (Wait-Until { Find-Name $window '已重新应用' } 5)) { Fail 'no toast after 重新应用' }
Start-Sleep -Milliseconds 400
Save-Shot $window 'light-toast'
Confirm-Dialog $window 'RestartExplorerButton' '重启资源管理器？' '取消' 'light-dialog-restart'
if (-not (Wait-Until { -not (Find-Name $window '重启资源管理器？') } 5)) { Fail 'the restart dialog did not close on 取消' }

# A status tile opens its page.
Click (Find-Id $window 'TaskbarTile') 'TaskbarTile' | Out-Null
Start-Sleep -Milliseconds 900
Assert-Title $window '任务栏' | Out-Null

# Narrow: the pane folds to icons and the cards keep their content in view.
Set-Size $window 760 900
Visit-Pages $window 'light-narrow' $true $true

Set-Size $window 1040 1000
Stop-Desk $window $process 'light'

# ----------------------------------------------------------------- dark mode
# With a program running that also changes the taskbar, as far as Desk can
# tell: a copy of ping named like Windhawk. Desk looks 3 seconds after it
# starts, and the overview warns about it.
$fake = Join-Path $env:RUNNER_TEMP 'windhawk.exe'
Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $fake -Force
$conflict = Start-Process $fake -ArgumentList '-n 900 127.0.0.1' -WindowStyle Hidden -PassThru
$process, $window = Start-Desk 0
# The light pass closed the window at 20,20, 1040 x 1000, on 设置 (by exiting there).
Test-Reopened $window 20 20 1040 1000 '设置'
Set-Size $window 1040 1000
Open-Page $window 0 | Out-Null
if (-not (Wait-Until { Find-Shown $window '可能有冲突' } 15)) { Fail 'no conflict notice while "Windhawk" runs' }
Visit-Pages $window 'dark-wide' $false $false
Test-ConflictNotice $window
Stop-Desk $window $process 'dark'
Stop-Process -Id $conflict.Id -Force -ErrorAction SilentlyContinue

# Back to the modes the machine had.
foreach ($name in $modeNames) {
    if ($savedModes.ContainsKey($name)) { Set-ItemProperty $personalize $name $savedModes[$name] -Type DWord }
    else { Remove-ItemProperty $personalize $name -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------- verdict
$errors = @(Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue |
    Select-String -Pattern 'ERROR' | Where-Object { $_.Line -match 'settings window|Unhandled' })
foreach ($e in $errors) { Fail "logged: $($e.Line)" }

Write-Host "Screenshots: $((Get-ChildItem $Out -Filter *.png).Count) in $Out"
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) problem(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host 'UI smoke test passed.' -ForegroundColor Green
