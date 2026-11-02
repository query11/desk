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

function Save-Shot($window, [string]$name) {
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
    $bitmap.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
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
        } catch { }
    }
    # An empty walk would make the edge check pass without looking at anything.
    if ($shown -lt 10) { Fail "$title`: only $shown elements found for the layout check" }
}

function Start-Desk([int]$light) {
    New-Item -Force 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' | Out-Null
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' AppsUseLightTheme $light -Type DWord
    $process = Start-Process (Join-Path $app 'Desk.exe') -PassThru
    $window = Wait-Until {
        $condition = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $process.Id)
        $found = $A::RootElement.FindAll($Tree::Children, $condition)
        foreach ($w in $found) { if ($w.Current.Name -eq 'Desk' -and $w.Current.ClassName -notmatch 'Tray') { return $w } }
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

$pages = @('概览', '任务栏', '开始菜单', '设置')
$navIds = @('NavHome', 'NavTaskbar', 'NavStartMenu', 'NavSettings')

function Open-Page($window, [int]$index) {
    $item = Find-Id $window $navIds[$index]
    if (-not $item) { Write-Tree $window }
    if (-not (Click $item "navigation item $($navIds[$index])")) { return $false }
    Start-Sleep -Milliseconds 900
    return Assert-Title $window $pages[$index]
}

# The pinned page title shows the page that is open.
function Assert-Title($window, [string]$expected) {
    $left = $window.Current.BoundingRectangle.Left + 40
    # Built here: GetNewClosure only captures the variables of the scope it runs in.
    $isTitle = { param($c) $c.Name -eq $expected -and $c.BoundingRectangle.Height -ge 30 -and
        $c.BoundingRectangle.Left -gt $left }.GetNewClosure()
    $title = Wait-Until { Find-Element $window $isTitle } 5
    if (-not $title) { Fail "page title '$expected' not shown"; Write-Tree $window; return $false }
    return $true
}

function Visit-Pages($window, [string]$prefix, [bool]$layout) {
    for ($i = 0; $i -lt $pages.Count; $i++) {
        if (Open-Page $window $i) {
            Save-Shot $window "$prefix-$i-$($pages[$i])"
            if ($layout) { Write-Layout $window "$prefix $($pages[$i])" }
        }
    }
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
Visit-Pages $window 'light-wide' $true

# Feedback and confirmations on the overview.
Open-Page $window 0 | Out-Null
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
Visit-Pages $window 'light-narrow' $true

Set-Size $window 1040 1000
Stop-Desk $window $process 'light'

# ----------------------------------------------------------------- dark mode
$process, $window = Start-Desk 0
Set-Size $window 1040 1000
Visit-Pages $window 'dark-wide' $false
Stop-Desk $window $process 'dark'

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
