param([string]$Version = '0.1.7')

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$distRoot = Join-Path $projectRoot 'dist'
$stageRoot = Join-Path $distRoot ('.stage-' + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $distRoot ("Brezza-$Version-win-x64.zip")
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
        'Microsoft.Web.WebView2.Wpf.dll',
        'WebView2Loader.dll',
        'BrowserUi\index.html',
        'BrowserUi\newtab.html',
        'BrowserUi\app.js',
        'BrowserUi\styles.css',
        'BrowserUi\internal.css',
        'BrowserUi\internal.js',
        'BrowserUi\locales\en.json',
        'BrowserUi\locales\zh-Hans.json'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path (Join-Path $stageRoot $relativePath))) { throw "Publish output is missing $relativePath." }
    }

    $loaderHash = (Get-FileHash (Join-Path $stageRoot 'WebView2Loader.dll') -Algorithm SHA256).Hash
    $x64LoaderHash = (Get-FileHash (Join-Path $stageRoot 'runtimes\win-x64\native\WebView2Loader.dll') -Algorithm SHA256).Hash
    if ($loaderHash -ne $x64LoaderHash) { throw 'Published native loader does not match the x64 target.' }

    Get-ChildItem $stageRoot -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    $runtimeFolder = Join-Path $stageRoot 'runtimes'
    if (Test-Path $runtimeFolder) { Remove-Item -LiteralPath $runtimeFolder -Recurse -Force }

    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $smokeRoot 'app') -Force

    $appDirectory = Join-Path $smokeRoot 'app'
    $dataRoot = Join-Path $smokeRoot 'data'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $previousDataRoot = $env:BREZZA_DATA_DIR
    $env:BREZZA_DATA_DIR = $dataRoot
    try {
        $process = Start-Process -FilePath (Join-Path $appDirectory 'Brezza.exe') -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(25)
        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw "Packaged Brezza exited with code $($process.ExitCode)." }
            if ($process.MainWindowHandle.ToInt64() -ne 0 -and (Test-Path (Join-Path $dataRoot 'ChromeProfile'))) { break }
            Start-Sleep -Milliseconds 200
        }
        if ($process.MainWindowHandle.ToInt64() -eq 0) { throw 'Packaged Brezza did not create a window.' }
        if (-not (Test-Path (Join-Path $dataRoot 'ChromeProfile'))) { throw 'Packaged trusted chrome WebView2 did not initialize.' }
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
    finally {
        if ($null -eq $previousDataRoot) { Remove-Item Env:BREZZA_DATA_DIR -ErrorAction SilentlyContinue }
        else { $env:BREZZA_DATA_DIR = $previousDataRoot }
    }

    $archiveBytes = (Get-Item $archivePath).Length
    "PASS: $archivePath ($([math]::Round($archiveBytes / 1MB, 2)) MiB compressed); extracted x64 app starts with WebView2 chrome."
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
