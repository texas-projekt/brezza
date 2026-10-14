param([string]$Version = '0.1.8', [switch]$SkipLaunchVerification)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distRoot = Join-Path $projectRoot 'dist'
$stageRoot = Join-Path $distRoot ('.stage-' + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $distRoot ("Brezza-$Version-win-x64-native.zip")
$smokeRoot = Join-Path $projectRoot ('.test-data\package-' + [guid]::NewGuid().ToString('N'))

if (Test-Path $archivePath) { throw "Package already exists: $archivePath" }
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

try {
    & dotnet publish (Join-Path $projectRoot 'src\Brezza.App\Brezza.App.csproj') `
        -c Release -f net48 -p:PlatformTarget=x64 -o $stageRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $required = @(
        'Brezza.exe',
        'Brezza.Core.dll',
        'Brezza.Persistence.dll',
        'Brezza.WebView2.dll',
        'Microsoft.Web.WebView2.Core.dll',
        'WebView2Loader.dll',
        'BrowserUi\locales\en.json',
        'BrowserUi\locales\zh-Hans.json'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path (Join-Path $stageRoot $relativePath))) { throw "Publish output is missing $relativePath." }
    }
    $loaderHash = (Get-FileHash (Join-Path $stageRoot 'WebView2Loader.dll') -Algorithm SHA256).Hash
    $x64LoaderHash = (Get-FileHash (Join-Path $stageRoot 'runtimes\win-x64\native\WebView2Loader.dll') -Algorithm SHA256).Hash
    if ($loaderHash -ne $x64LoaderHash) { throw 'Published native loader does not match the x64 target.' }

    # The managed assemblies merge into one executable. The native WebView2 loader and the language
    # packs cannot merge: Windows loads a native DLL from disk and the language packs are content
    # files (ADR 0009). ILRepack runs from the repository's pinned local tool, without /internalize:
    # internalizing the WebView2 interop types breaks its event registration at runtime.
    # The merged output takes its assembly name from the file name, so it must be written as
    # Brezza.exe: merging to any other name leaves the assembly identity wrong and the WebView2
    # interop fails to load it at runtime.
    $mergedDirectory = Join-Path $stageRoot 'merged'
    New-Item -ItemType Directory -Path $mergedDirectory -Force | Out-Null
    $merged = Join-Path $mergedDirectory 'Brezza.exe'
    $mergeInputs = 'Brezza.exe', 'Brezza.Core.dll', 'Brezza.Persistence.dll', 'Brezza.WebView2.dll', 'Microsoft.Web.WebView2.Core.dll' |
        ForEach-Object { Join-Path $stageRoot $_ }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
    dotnet ilrepack /out:$merged $mergeInputs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $merged)) { throw 'The managed assemblies could not be merged.' }
    Move-Item -LiteralPath $merged -Destination (Join-Path $stageRoot 'Brezza.exe') -Force
    Remove-Item -LiteralPath $mergedDirectory -Recurse -Force
    foreach ($merged in @('Brezza.Core.dll', 'Brezza.Persistence.dll', 'Brezza.WebView2.dll', 'Microsoft.Web.WebView2.Core.dll')) {
        Remove-Item -LiteralPath (Join-Path $stageRoot $merged) -Force
        if (Test-Path (Join-Path $stageRoot $merged)) { throw "$merged could not be removed from the package." }
    }

    Get-ChildItem $stageRoot -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    $runtimeFolder = Join-Path $stageRoot 'runtimes'
    if (Test-Path $runtimeFolder) { Remove-Item -LiteralPath $runtimeFolder -Recurse -Force }

    # The native shell hosts WebView2 controllers directly, so the WPF and WinForms wrappers that
    # the package also ships are not used. Removing them keeps the bundle small and proves the
    # application does not depend on a UI framework.
    foreach ($unused in @('Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) {
        $path = Join-Path $stageRoot $unused
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($unused in @('Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Web.WebView2.WinForms.dll')) {
        if (Test-Path (Join-Path $stageRoot $unused)) { throw "The package still contains $unused." }
    }

    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $verification = 'launch verification skipped'
    if (-not $SkipLaunchVerification) {
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $smokeRoot 'app') -Force

    $appDirectory = Join-Path $smokeRoot 'app'
    $dataRoot = Join-Path $smokeRoot 'data'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

    # Seed a restored session with a local page so the packaged app creates a WebView2 content host.
    $pagePath = Join-Path $smokeRoot 'packaged-page.html'
    [System.IO.File]::WriteAllText($pagePath, '<!doctype html><html><head><title>Packaged Page</title></head><body><h1>Packaged</h1></body></html>')
    [System.IO.File]::WriteAllText(
        (Join-Path $dataRoot 'session.xml'),
        '<session schemaVersion="1" activeTabId="tab-1"><tab id="tab-1"><url>' + [System.Uri]::new($pagePath).AbsoluteUri + '</url><title>Packaged Page</title></tab></session>')

    $source = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PackageProbe {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr param);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr param);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  public static IntPtr MainWindow(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => { uint w; GetWindowThreadProcessId(h, out w); if (w == pid) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); if (sb.ToString() == "BrezzaMainWindow") { found = h; } } return true; }, IntPtr.Zero);
    return found;
  }
  public static string Children(IntPtr parent) {
    var list = new List<string>();
    EnumChildWindows(parent, (h, p) => { var sb = new StringBuilder(256); GetClassName(h, sb, 256); list.Add(sb.ToString()); return true; }, IntPtr.Zero);
    return string.Join(",", list.ToArray());
  }
}
'@
    Add-Type -TypeDefinition $source

    $previousDataRoot = $env:BREZZA_DATA_DIR
    $env:BREZZA_DATA_DIR = $dataRoot
    try {
        $process = Start-Process -FilePath (Join-Path $appDirectory 'Brezza.exe') -PassThru
        $mainWindow = [IntPtr]::Zero
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw "Packaged Brezza exited with code $($process.ExitCode)." }
            $mainWindow = [PackageProbe]::MainWindow($process.Id)
            if ($mainWindow -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 200
        }
        if ($mainWindow -eq [IntPtr]::Zero) { throw 'Packaged Brezza did not create its native window.' }

        # The WebView2 controller is created asynchronously after the window appears.
        $webViewHost = [IntPtr]::Zero
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw "Packaged Brezza exited with code $($process.ExitCode)." }
            $webViewHost = [PackageProbe]::FindWindowEx($mainWindow, [IntPtr]::Zero, 'Chrome_WidgetWin_0', $null)
            if ($webViewHost -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 200
        }
        if ($webViewHost -eq [IntPtr]::Zero) { throw 'Packaged Brezza did not create a WebView2 content surface.' }

        $children = [PackageProbe]::Children($mainWindow)
        foreach ($expected in @('SysTabControl32', 'ComboBox', 'Button', 'Static', 'Chrome_WidgetWin_0')) {
            if ($children -notmatch [regex]::Escape($expected)) { throw "Packaged native UI is missing $expected. Children: $children" }
        }
        $buttonCount = ([regex]::Matches($children, 'Button')).Count
        if ($buttonCount -lt 11) { throw "Packaged native UI has too few buttons: $children" }

        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force; throw 'Packaged Brezza did not exit cleanly.' }
    }
    finally {
        if ($null -eq $previousDataRoot) { Remove-Item Env:BREZZA_DATA_DIR -ErrorAction SilentlyContinue }
        else { $env:BREZZA_DATA_DIR = $previousDataRoot }
    }
    $verification = 'extracted app starts with native Win32 controls and a WebView2 content surface'
    }

    $archiveBytes = (Get-Item $archivePath).Length
    "PASS: $archivePath ($([math]::Round($archiveBytes / 1MB, 2)) MiB compressed); managed assemblies merged into Brezza.exe; $verification."
}
finally {
    if (Test-Path $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
    if (Test-Path $smokeRoot) {
        $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(12)
        while ((Test-Path $smokeRoot) -and [DateTime]::UtcNow -lt $cleanupDeadline) {
            try { Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction Stop }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path $smokeRoot) { Write-Warning "WebView2 still holds package smoke data: $smokeRoot" }
    }
}
