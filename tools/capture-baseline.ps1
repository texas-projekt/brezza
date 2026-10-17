<#
.SYNOPSIS
Captures the native UI visual baseline in one pass.

.DESCRIPTION
Drives the Debug shell through every baseline scenario that can be reached from outside the
application and captures each state with tools/capture-window.ps1 -Scenario. The driver only
prepares the shell's own data files and sends the window messages a user's input produces —
commands by control id and a wheel notch over the tab strip — so no application code is
involved and the pass stays reproducible. (Tabs are reached with the wheel rather than a
left-click: a click sent from outside the process right after an item-rectangle query crashes
the tab control, which the wheel does not.)

States that need a loaded page are reached through the shell's own startup behavior: a seeded
session.xml plus the RestoreSession setting makes the shell open that page on launch, which is
how the favicon, loading, and failure-notice scenarios get their page without typing an address.

Three scenarios are not part of the automated pass and keep their recipes in
docs/UI_VISUAL_BASELINE.md: Dpi150 (needs the display at 150% scaling), LongNotice (its long
detail is injected by the smoke driver inside the application), and the cookies panel (its rows
are the machine's real cookies, which is not a reproducible baseline).

Run it through the single entry point:

    pwsh tools/test.ps1 -Stage Visual

.EXAMPLE
pwsh tools/capture-baseline.ps1 -Only TabsCrowded, TabFavicon
#>
param(
    # Capture only these scenarios instead of the whole baseline.
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toolsRoot = $PSScriptRoot
$exe = Join-Path $projectRoot 'src\Brezza.App\bin\Debug\net48\Brezza.exe'
if (-not (Test-Path $exe)) {
    throw 'Build Brezza.slnx in Debug first: ' + $exe
}

# The capture tool resolves the one Brezza process by name, so a second one makes every capture
# ambiguous.
if (@(Get-Process -Name 'Brezza' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'A Brezza process is already running. Close it first: the baseline capture needs the only one.'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BaselineDrive
{
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hWnd, int controlId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    private const uint WM_COMMAND = 0x0111;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint TCM_GETITEMCOUNT = 0x1304;
    private const uint TCM_GETCURSEL = 0x130B;
    private const int WHEEL_DELTA = 120;

    private static IntPtr Strip(IntPtr window) => GetDlgItem(window, 1001);

    /// <summary>A browser command by id, exactly as a button or menu entry raises it.</summary>
    public static void Command(IntPtr window, int controlId) => PostMessage(window, WM_COMMAND, new IntPtr(controlId), IntPtr.Zero);

    public static int TabCount(IntPtr window) => (int)SendMessage(Strip(window), TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

    public static int SelectedTab(IntPtr window) => (int)SendMessage(Strip(window), TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero);

    /// <summary>True while the failure notice is showing (its static control is visible).</summary>
    public static bool NoticeVisible(IntPtr window)
    {
        var notice = GetDlgItem(window, 1020);   // IdNotice
        return notice != IntPtr.Zero && IsWindowVisible(notice);
    }

    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Wheel notches over the strip; a positive count moves the selection towards the first tab.
    /// The wheel is how this driver reaches a tab: a left-click is what a user sends, but a click
    /// sent from outside the process right after an item-rectangle query crashes the tab control
    /// (comctl32), so the wheel — the gesture the smoke itself exercises — is the safe one.
    /// </summary>
    public static void Wheel(IntPtr window, int notches)
    {
        var delta = notches > 0 ? WHEEL_DELTA : -WHEEL_DELTA;
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            PostMessage(Strip(window), WM_MOUSEWHEEL, new IntPtr(unchecked((int)(delta << 16))), IntPtr.Zero);
        }
    }

    public static string Title(IntPtr window)
    {
        var text = new System.Text.StringBuilder(256);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    // The panel is a top-level owned window, not a child of the shell window, so it is found
    // among the top-level windows; the class is this application's own and only one shell runs.
    public static IntPtr PanelWindow(IntPtr window) => FindWindowEx(IntPtr.Zero, IntPtr.Zero, "BrezzaInternalPanel", null);

    public static void ClosePanelWindow(IntPtr panel) => PostMessage(panel, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
}
'@

$runRoot = Join-Path $projectRoot ('.test-data\baseline-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

# ---------- the shell's own data files ----------

function Write-SeededSettings([string]$theme, [string]$language, [bool]$restoreSession)
{
    $languageElement = if ([string]::IsNullOrEmpty($language)) { '' } else { "<language>$language</language>" }
    $startup = if ($restoreSession) { 'RestoreSession' } else { 'NewTab' }
    [System.IO.File]::WriteAllText((Join-Path $runRoot 'settings.xml'),
        "<settings schemaVersion=`"3`"><searchTemplate>https://www.bing.com/search?q={query}</searchTemplate>$languageElement<theme>$theme</theme><startupBehavior>$startup</startupBehavior></settings>")
}

function Write-SeededHistory()
{
    # Two rows, so the history panel shows its row layout rather than its empty state.
    [System.IO.File]::WriteAllText((Join-Path $runRoot 'history.xml'),
        '<history schemaVersion="1">' +
        '<entry><url>https://example.test/</url><title>Example Seeded</title><lastVisitedUtc>2026-10-01T00:00:00Z</lastVisitedUtc><visitCount>1</visitCount></entry>' +
        '<entry><url>https://brezza.test/docs</url><title>Brezza Docs</title><lastVisitedUtc>2026-10-02T00:00:00Z</lastVisitedUtc><visitCount>3</visitCount></entry>' +
        '</history>')
}

function Write-SeededSession([string]$url)
{
    # The shell's own session restore opens this page on launch, so a page-dependent state needs
    # no typing: the restored tab navigates itself and its favicon, loading, or failure follows.
    [System.IO.File]::WriteAllText((Join-Path $runRoot 'session.xml'),
        '<session schemaVersion="1" activeTabId="tab-1"><tab id="tab-1"><url>' + $url + '</url><title>Restored</title></tab></session>')
}

function Write-FaviconPage()
{
    # The smoke page's own favicon: a test-tube emoji in an inline SVG data URI, so the tab strip
    # decodes and paints a real favicon without any asset.
    $faviconUri = 'data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 100 100%22><text y=%22.9em%22 font-size=%2290%22>&#129514;</text></svg>'
    [System.IO.File]::WriteAllText((Join-Path $runRoot 'favicon.html'),
        '<!doctype html><html><head><meta charset="utf-8"><title>Favicon Page</title><link rel="icon" href="' + $faviconUri + '"></head><body><h1>Favicon</h1></body></html>')
}

function Write-SlowPage()
{
    # A page that paints and names itself but never finishes: the image points at an address that
    # does not answer, so the document's load event waits and the tab keeps its loading marker.
    # (A bare hanging address would load silently — nothing about the tab changes, so no render
    # happens while it loads and the marker is never painted.)
    [System.IO.File]::WriteAllText((Join-Path $runRoot 'slow.html'),
        '<!doctype html><html><head><meta charset="utf-8"><title>Slow Page</title></head>' +
        '<body><h1>Loading</h1><img src="http://10.255.255.1/hang.png" alt=""></body></html>')
}

function Get-FaviconPageUrl { ([System.Uri]::new((Join-Path $runRoot 'favicon.html')).AbsoluteUri) }
function Get-MissingPageUrl { ([System.Uri]::new((Join-Path $runRoot 'missing.html')).AbsoluteUri) }
function Get-SlowPageUrl { ([System.Uri]::new((Join-Path $runRoot 'slow.html')).AbsoluteUri) }

# ---------- driving ----------

$script:app = $null
$script:main = [IntPtr]::Zero

function Start-App([int]$startupSeconds = 3)
{
    $env:BREZZA_DATA_DIR = $runRoot
    $script:app = Start-Process -FilePath $exe -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw 'The shell exited during startup.' }
        if ($script:app.MainWindowHandle -ne 0)
        {
            $script:main = $script:app.MainWindowHandle
            Start-Sleep -Seconds $startupSeconds
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'The shell window never appeared.'
}

function Stop-App
{
    if ($script:app -and -not $script:app.HasExited)
    {
        Stop-Process -Id $script:app.Id -Force
        # WebView2 holds the profile briefly; let the folder go before the next launch.
        Start-Sleep -Milliseconds 700
    }
    $script:app = $null
    $script:main = [IntPtr]::Zero
}

function Wait-TabCount([int]$expected, [int]$seconds = 20)
{
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ([BaselineDrive]::TabCount($script:main) -ge $expected) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "The tab count never reached $expected (now $([BaselineDrive]::TabCount($script:main)))."
}

# Selects a tab by wheel — the gesture the shell itself exercises. A click sent from outside the
# process right after an item-rectangle query crashes the tab control, so clicks are not used.
function Select-Tab([int]$index)
{
    [BaselineDrive]::Wheel($script:main, [BaselineDrive]::SelectedTab($script:main) - $index)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ([BaselineDrive]::SelectedTab($script:main) -eq $index)
        {
            Start-Sleep -Milliseconds 400   # the strip settles at the new offset before the shot
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "The selection never reached tab $index (now $([BaselineDrive]::SelectedTab($script:main)))."
}

function Wait-Title([string]$contains, [int]$seconds = 25){
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ([BaselineDrive]::Title($script:main).IndexOf($contains, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "The window title never showed '$contains' (now '$([BaselineDrive]::Title($script:main))')."
}

function Open-Panel([int]$controlId)
{
    [BaselineDrive]::Command($script:main, $controlId)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ([BaselineDrive]::PanelWindow($script:main) -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 400; return }
        Start-Sleep -Milliseconds 100
    }
    throw "The panel for command $controlId never opened."
}

function Close-Panel
{
    $panel = [BaselineDrive]::PanelWindow($script:main)
    if ($panel -ne [IntPtr]::Zero)
    {
        [BaselineDrive]::ClosePanelWindow($panel)
        Start-Sleep -Milliseconds 400
    }
}

function Capture([string]$scenario)
{
    if ($Only.Count -gt 0 -and $Only -notcontains $scenario) { return }
    Write-Host "  -> $scenario"
    & (Join-Path $toolsRoot 'capture-window.ps1') -Scenario $scenario
}

try
{
    # ---------- launch 1: dark shell, English, new-tab start ----------
    Write-Host '== dark shell, English (startup states and native panels)'
    Write-SeededSettings 'Dark' '' $false
    Write-SeededHistory
    Start-App

    Capture 'NewTabDark'
    Capture 'MinimumWindow'   # the preset pins 640x420; later captures pin 1120x760 again

    # Two plain tabs: the active one carries the close affordance and the built-in globe.
    [BaselineDrive]::Command($script:main, 1002)   # IdNewTab
    Wait-TabCount 2
    Capture 'CloseAffordance'
    Capture 'TabGlobe'

    # One wheel notch moves the active tab without disturbing the row.
    [BaselineDrive]::Wheel($script:main, 1)
    Start-Sleep -Milliseconds 500
    Capture 'TabsWheel'

    # The native panels, each through its own command.
    Open-Panel 1017   # IdSettings
    Capture 'SettingsPanel'
    Capture 'SettingsDarkControls'
    Close-Panel
    Open-Panel 1017
    Capture 'SettingsFocus'
    Close-Panel
    Open-Panel 1015   # IdHistory
    Capture 'HistoryPanel'
    Close-Panel
    Open-Panel 1016   # IdBookmarks
    Capture 'BookmarksEmpty'
    Close-Panel
    Open-Panel 1025   # IdBrowsingData
    Capture 'DataPanel'
    Close-Panel
    [BaselineDrive]::Command($script:main, 1018)   # IdMenu
    Start-Sleep -Milliseconds 600
    Capture 'ToolbarMenu'
    [BaselineDrive]::Command($script:main, 1018)   # dismiss the menu the same way

    Stop-App

    # ---------- launch 2: dark shell, English, restored favicon page ----------
    Write-Host '== dark shell, English (restored favicon page)'
    Write-FaviconPage
    Write-SeededSettings 'Dark' '' $true
    Write-SeededSession (Get-FaviconPageUrl)
    Start-App 4
    Wait-Title 'Favicon Page'
    Start-Sleep -Milliseconds 2000   # the runtime fetches the icon after the document loads

    # Nineteen tabs put the indicator between its ends, thirty crowd it fully.
    1..18 | ForEach-Object { [BaselineDrive]::Command($script:main, 1002); Start-Sleep -Milliseconds 90 }
    Wait-TabCount 19
    Start-Sleep -Milliseconds 600
    Select-Tab 0
    Capture 'TabsIndicatorLeft'
    Select-Tab 9
    Capture 'TabsIndicatorMiddle'
    Select-Tab 18
    Capture 'TabsIndicatorRight'

    1..11 | ForEach-Object { [BaselineDrive]::Command($script:main, 1002); Start-Sleep -Milliseconds 90 }
    Wait-TabCount 30
    Start-Sleep -Milliseconds 600
    Capture 'TabsCrowded'
    Select-Tab 0   # the tab whose page has the favicon
    Capture 'TabFavicon'

    Stop-App

    # ---------- launch 3: dark shell, English, restored page that never finishes ----------
    Write-Host '== dark shell, English (restored page that never finishes)'
    Remove-Item (Join-Path $runRoot 'favicon.html') -ErrorAction SilentlyContinue
    Write-SlowPage
    Write-SeededSettings 'Dark' '' $true
    Write-SeededSession (Get-SlowPageUrl)
    Start-App 4
    Wait-Title 'Slow Page'   # the document painted and named itself, and its image still hangs
    Start-Sleep -Milliseconds 800
    Capture 'TabLoading'
    Stop-App

    # ---------- launch 4: dark shell, English, restored page that does not exist ----------
    Write-Host '== dark shell, English (restored page that does not exist)'
    Remove-Item (Join-Path $runRoot 'slow.html') -ErrorAction SilentlyContinue
    Write-SeededSettings 'Dark' '' $true
    Write-SeededSession (Get-MissingPageUrl)
    Start-App 4
    Start-Sleep -Milliseconds 1500
    Capture 'FailureNotice'

    # The failure notice belongs to the failed tab and outlives a tab switch, so it is dismissed
    # before the private tab is opened: the private-tab shot shows the private tab alone.
    [BaselineDrive]::Command($script:main, 1022)   # IdNoticeDismiss
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if (-not [BaselineDrive]::NoticeVisible($script:main)) { break }
        Start-Sleep -Milliseconds 100
    }

    # A private tab leads with its private glyph alone and keeps the page's own title.
    [BaselineDrive]::Command($script:main, 1003)   # IdNewPrivateTab
    Wait-TabCount 2
    Start-Sleep -Milliseconds 700
    Capture 'TabPrivate'

    Stop-App

    # ---------- launch 5: light shell, English ----------
    Write-Host '== light shell, English'
    Write-SeededSettings 'Light' '' $false
    Start-App
    Capture 'NewTabLight'
    Stop-App

    # ---------- launch 6: dark shell, Simplified Chinese ----------
    Write-Host '== dark shell, Simplified Chinese'
    Write-SeededSettings 'Dark' 'zh-Hans' $false
    Start-App
    Capture 'ToolbarZh'
    Stop-App
}
finally
{
    Stop-App
    Remove-Item Env:BREZZA_DATA_DIR -ErrorAction SilentlyContinue
    if (Test-Path $runRoot)
    {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while ((Test-Path $runRoot) -and [DateTime]::UtcNow -lt $deadline)
        {
            try { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
        }
    }
}

Write-Host ''
Write-Host 'PASS: baseline captured.'
$manifest = Join-Path $projectRoot 'artifacts\capture-manifest.json'
if (Test-Path $manifest)
{
    $entries = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    "manifest entries: $($entries.Count) -> $(($entries | ForEach-Object { $_.scenario }) -join ', ')"
}
'Dpi150 and LongNotice are not automated: see docs/UI_VISUAL_BASELINE.md.'
