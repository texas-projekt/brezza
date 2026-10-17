<#
.SYNOPSIS
Captures one Brezza window to artifacts/<scenario>.png for the native UI visual baseline.

.DESCRIPTION
The single capture tool for the visual baseline (docs/UI_VISUAL_BASELINE.md). Every capture names
a scenario; the scenario preset decides which window class is captured, the canonical window
geometry, an optional control to focus first, and the output file. Nothing about the captured
region is set by hand, so the same scenario produces comparable images on any machine.

The capture renders the window with the system compositor (PrintWindow with
PW_RENDERFULLCONTENT) into a private bitmap that never reads the screen: the screenshot shows the
window's real modern frame, the desktop can never appear in it, and an area the window does not
paint keeps the magenta the bitmap starts with, so an incomplete render is obvious instead of
silent. The tool waits for the window's painting to settle instead of sleeping a fixed time.

Every capture records its state in artifacts/capture-manifest.json: scenario, window class,
rectangle, DPI, geometry, focused control, theme, language, capture time, and build.

.EXAMPLE
pwsh tools/capture-window.ps1 -Scenario HistoryPanel
#>
param(
    [Parameter(Mandatory = $true)][string]$Scenario
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $projectRoot 'artifacts'

# One entry per baseline scenario. Width/Height are logical pixels at 96 DPI, scaled to the
# window's own DPI; 0 leaves the window at the size it owns (the panels and the pop-up size
# themselves from their content, deterministically). Focus is a native control id focused before
# the capture. File is the PNG written under artifacts/.
$presets = [ordered]@{
    NewTabDark          = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-newtab-dark.png' }
    NewTabLight         = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-newtab-light.png' }
    MinimumWindow       = @{ Class = 'BrezzaMainWindow';     Width = 640;  Height = 420; Focus = 0;    File = 'brezza-min-window.png' }
    Dpi150              = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-dpi-150.png' }
    SettingsPanel       = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-settings-panel.png' }
    SettingsDarkControls = @{ Class = 'BrezzaInternalPanel'; Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-settings-combos.png' }
    SettingsFocus       = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 2016; File = 'brezza-settings-focus.png' }
    HistoryPanel        = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-history-panel.png' }
    DownloadsPanel      = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-downloads-panel.png' }
    BookmarksEmpty      = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-bookmarks-empty.png' }
    DataPanel           = @{ Class = 'BrezzaInternalPanel';  Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-data-panel.png' }
    ToolbarMenu         = @{ Class = 'BrezzaFlatPopup';      Width = 0;    Height = 0;   Focus = 0;    File = 'brezza-toolbar-menu.png' }
    FailureNotice       = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-notice.png' }
    LongNotice          = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-notice-ellipsis.png' }
    CloseAffordance     = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-close-tab-button.png' }
    TabsCrowded         = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-floor.png' }
    TabsIndicatorLeft   = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-indicator-left.png' }
    TabsIndicatorMiddle = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-indicator-middle.png' }
    TabsIndicatorRight  = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-indicator-right.png' }
    TabFavicon          = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-favicon.png' }
    TabGlobe            = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-globe.png' }
    TabPrivate          = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-private.png' }
    TabsWheel           = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-tabs-restored.png' }
    ToolbarZh           = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-toolbar-zh.png' }
    TabLoading          = @{ Class = 'BrezzaMainWindow';     Width = 1120; Height = 760; Focus = 0;    File = 'brezza-loading-state.png' }
}

$preset = $presets[$Scenario]
if ($null -eq $preset) {
    throw ("Unknown scenario '$Scenario'. Known scenarios: " + ($presets.Keys -join ', '))
}

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class BrezzaWindowCapture
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hWnd, int controlId);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hDc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDc, IntPtr objectHandle);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hDc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetUpdateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("user32.dll")] static extern bool FillRect(IntPtr hDc, ref RECT rect, IntPtr brush);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public uint size; public int width, height; public ushort planes, bitsPerPixel;
        public uint compression, imageSize, xPixelsPerMeter, yPixelsPerMeter, colorsUsed, importantColors;
    }

    /// <summary>
    /// The one visible top-level window of the class in the process. Zero or more than one is an
    /// error rather than a guess: capturing the wrong window is worse than failing.
    /// </summary>
    public static IntPtr FindWindow(uint processId, string className)
    {
        var matches = new List<IntPtr>();
        bool Collect(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != processId) return true;
            var name = new StringBuilder(256);
            GetClassName(hWnd, name, name.Capacity);
            if (string.Equals(name.ToString(), className, StringComparison.Ordinal)) matches.Add(hWnd);
            return true;
        }

        var callback = new EnumWindowsProc(Collect);
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (matches.Count == 1) return matches[0];
        var found = matches.Count == 0
            ? "none"
            : string.Join(", ", matches.ConvertAll(m => "0x" + m.ToInt64().ToString("X")));
        throw new InvalidOperationException(
            "Process " + processId + " has " + found + " visible top-level window(s) of class '" +
            className + "'; exactly one is required.");
    }

    /// <summary>A window can only be composed while it is visible and not minimized.</summary>
    public static void EnsureCapturable(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd))
        {
            throw new InvalidOperationException("The window is not visible, so there is nothing to compose.");
        }

        if (IsIconic(hWnd))
        {
            throw new InvalidOperationException("The window is minimized; restore it before capturing.");
        }
    }

    public static uint DpiOf(IntPtr hWnd)
    {
        var dpi = GetDpiForWindow(hWnd);
        return dpi == 0 ? 96u : dpi;
    }

    /// <summary>Places the window at the origin and gives it the canonical logical size, scaled to its DPI.</summary>
    public static void NormalizeGeometry(IntPtr hWnd, int logicalWidth, int logicalHeight, uint dpi)
    {
        var width = Math.Max(1, (int)Math.Round(logicalWidth * dpi / 96.0));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * dpi / 96.0));
        if (!SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER))
        {
            throw new InvalidOperationException("The window could not be resized to " + width + "x" + height + ".");
        }
    }

    /// <summary>Waits until the window has no pending paint, twice in a row; false when the wait times out.</summary>
    public static bool WaitForPaintToSettle(IntPtr hWnd, int timeoutMilliseconds)
    {
        var deadline = unchecked(Environment.TickCount + timeoutMilliseconds);
        var quiet = 0;
        while (unchecked(Environment.TickCount < deadline))
        {
            System.Threading.Thread.Sleep(15);
            if (GetUpdateRect(hWnd, IntPtr.Zero, false))
            {
                quiet = 0;
                continue;
            }

            quiet++;
            if (quiet >= 2) return true;
        }

        return false;
    }

    public static Dictionary<string, int> WindowRectangle(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        return new Dictionary<string, int>
        {
            { "x", rect.left }, { "y", rect.top },
            { "width", rect.right - rect.left }, { "height", rect.bottom - rect.top },
        };
    }

    /// <summary>
    /// Composes the window into a private bitmap and returns its BGRA pixels, top-down rows,
    /// alpha forced opaque. The compositor renders the window itself (PW_RENDERFULLCONTENT), so
    /// the capture shows the window's real modern frame; the bitmap is never filled from the
    /// screen, so the desktop cannot appear in it; and anything the window does not paint keeps
    /// the magenta the bitmap starts with, so an incomplete render is visible instead of silent.
    /// </summary>
    public static byte[] Capture(IntPtr hWnd, out int width, out int height)
    {
        EnsureCapturable(hWnd);
        GetWindowRect(hWnd, out var rect);
        width = rect.right - rect.left;
        height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The window has no area to capture.");
        }

        var screenDc = GetWindowDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            var sentinel = CreateSolidBrush(0x00FF00FF);
            var fill = new RECT { left = 0, top = 0, right = width, bottom = height };
            FillRect(memoryDc, ref fill, sentinel);
            DeleteObject(sentinel);

            if (!PrintWindow(hWnd, memoryDc, PW_RENDERFULLCONTENT))
            {
                throw new InvalidOperationException(
                    "PrintWindow failed with error " + Marshal.GetLastWin32Error() + ".");
            }

            var info = new BITMAPINFOHEADER
            {
                size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                width = width, height = height, planes = 1, bitsPerPixel = 32,
            };
            var pixels = new byte[width * height * 4];
            GetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS);
            // The DIB carries no meaningful alpha; PNG with transparent UI pixels is not the capture.
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;

            // GetDIBits hands back a bottom-up buffer — its row 0 is the image's bottom row —
            // while the bitmap the caller builds from it is top-down. Copying the buffer across
            // as it stands renders every capture upside down, so the rows are reversed here.
            var topDown = new byte[pixels.Length];
            var stride = width * 4;
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(pixels, (height - 1 - row) * stride, topDown, row * stride, stride);
            }

            return topDown;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
'@

# Exactly one Brezza process, and exactly one visible top-level window of the scenario's class in
# it: a guess here captures the wrong window.
$processes = @(Get-Process -Name 'Brezza' -ErrorAction SilentlyContinue)
if ($processes.Count -eq 0) {
    throw 'No Brezza process is running. Start the Debug build, or the smoke with a pause, first.'
}
if ($processes.Count -gt 1) {
    $candidates = $processes | ForEach-Object { "  pid $($_.Id): $($_.MainWindowTitle)" }
    throw ("More than one Brezza process is running; close all but one:`n" + ($candidates -join "`n"))
}
$process = $processes[0]
$window = [BrezzaWindowCapture]::FindWindow($process.Id, $preset.Class)

[BrezzaWindowCapture]::EnsureCapturable($window)
$dpi = [BrezzaWindowCapture]::DpiOf($window)
if ($preset.Width -gt 0 -and $preset.Height -gt 0) {
    [BrezzaWindowCapture]::NormalizeGeometry($window, $preset.Width, $preset.Height, $dpi)
}
$settled = [BrezzaWindowCapture]::WaitForPaintToSettle($window, 2000)
if ($preset.Focus -gt 0) {
    $control = [BrezzaWindowCapture]::GetDlgItem($window, $preset.Focus)
    if ($control -eq [IntPtr]::Zero) {
        throw "Scenario '$Scenario' focuses control $($preset.Focus), which the captured window does not have."
    }
    [BrezzaWindowCapture]::SetFocus($control) | Out-Null
    $settled = [BrezzaWindowCapture]::WaitForPaintToSettle($window, 2000)
}

$outputPath = Join-Path $artifactsRoot $preset.File
$captureWidth = 0
$captureHeight = 0
$pixels = [BrezzaWindowCapture]::Capture($window, [ref]$captureWidth, [ref]$captureHeight)

# The PNG encoding lives here, not in the capture: the capture itself is pure Win32, and the
# encoder is the shell's own System.Drawing.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap `
    $captureWidth, $captureHeight, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $data = $bitmap.LockBits(
        (New-Object System.Drawing.Rectangle 0, 0, $captureWidth, $captureHeight),
        [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy($pixels, 0, $data.Scan0, $pixels.Length)
    }
    finally {
        $bitmap.UnlockBits($data)
    }

    $outputDirectory = Split-Path -Parent $outputPath
    if (-not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}
$rectangle = [BrezzaWindowCapture]::WindowRectangle($window)

# The app's own data root says what state the window was in; the manifest records it so a capture
# can be read later without the machine it was taken on.
function Read-AppSetting([string]$element) {
    $dataRoot = $env:BREZZA_DATA_DIR
    if ([string]::IsNullOrWhiteSpace($dataRoot)) { $dataRoot = Join-Path $env:LOCALAPPDATA 'Brezza' }
    $settingsPath = Join-Path $dataRoot 'settings.xml'
    if (-not (Test-Path -LiteralPath $settingsPath)) { return 'unknown' }
    $match = [regex]::Match((Get-Content -LiteralPath $settingsPath -Raw), "<$element>(.*?)</$element>")
    if (-not $match.Success) { return 'unknown' }
    return $match.Groups[1].Value.Trim()
}

$exePath = Join-Path $projectRoot 'src\Brezza.App\bin\Debug\net48\Brezza.exe'
$build = if (Test-Path $exePath) { 'Brezza.exe ' + (Get-Item $exePath).LastWriteTimeUtc.ToString('o') } else { 'unknown' }

$entry = [pscustomobject]@{
    scenario     = $Scenario
    file         = 'artifacts/' + $preset.File
    windowClass  = $preset.Class
    processId    = $process.Id
    dpi          = $dpi
    window       = $rectangle
    geometry     = $(if ($preset.Width -gt 0) { "$($preset.Width)x$($preset.Height) logical" } else { 'window-owned' })
    focus        = $(if ($preset.Focus -gt 0) { $preset.Focus } else { $null })
    paintSettled = $settled
    theme        = Read-AppSetting 'theme'
    language     = Read-AppSetting 'language'
    capturedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    build        = $build
}

$manifestPath = Join-Path $artifactsRoot 'capture-manifest.json'
try {
    $entries = @()
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $entries = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
        }
        catch {
            Write-Warning "The existing manifest could not be read and is replaced: $_"
            $entries = @()
        }
    }

    # A re-capture replaces the entry for the same file instead of stacking duplicates.
    $kept = @($entries | Where-Object { $_.file -ne $entry.file })
    @($kept) + @($entry) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath
}
catch {
    Write-Warning "The capture is saved, but its manifest entry was not: $_"
}

Write-Output ("Saved {0} (scenario={1}, {2}x{3} @ {4} dpi, paintSettled={5})" -f `
    $outputPath, $Scenario, $rectangle['width'], $rectangle['height'], $dpi, $settled)
