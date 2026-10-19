<#
.SYNOPSIS
Runs the Brezza test suite. This is the single entry point developers and CI both call.

.DESCRIPTION
Runs the test layers in order and stops at the first failure:
- Unit: the Core and Persistence xUnit tests (dotnet test), TRX results in artifacts/test-results.
- Smoke: builds Debug and runs the Windows native-control smoke (tools/smoke.ps1).
- Package: the x64 packager with its extracted-package verification (tools/package.ps1).
- Visual: builds Debug and captures the whole native UI visual baseline
  (tools/capture-baseline.ps1). Not part of All: it needs an interactive desktop, takes a few
  minutes, and produces screenshots for review rather than a pass or fail.

.PARAMETER Stage
Which layer to run. The default 'All' runs Unit, Smoke, then Package.

.PARAMETER Configuration
The build configuration for the unit tests. The smoke always builds Debug because its
driver is compiled into Debug builds only.

.PARAMETER VisualOnly
Forwarded to the Visual stage: capture only these scenarios instead of the whole baseline
(tools/capture-baseline.ps1 -Only). Empty captures every automatable scenario.

.PARAMETER PackageVersion
Forwarded to the Package stage: the version in the produced archive's name. When omitted,
tools/package.ps1 keeps its own default.

.PARAMETER SkipLaunchVerification
Forwarded to the Package stage: skips the extracted-package launch check, which needs an
interactive desktop and the WebView2 Runtime.
#>
param(
    [ValidateSet('All', 'Unit', 'Smoke', 'Package', 'Visual')]
    [string]$Stage = 'All',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    # Capture only these visual scenarios instead of the whole baseline.
    [string[]]$VisualOnly = @(),
    # The version in the package archive's name; the packager's own default is kept when omitted.
    [string]$PackageVersion = '',
    # Skip the extracted-package launch check: a CI runner has no interactive desktop.
    [switch]$SkipLaunchVerification
)

$ErrorActionPreference = 'Stop'
$toolsRoot = $PSScriptRoot
$projectRoot = (Resolve-Path (Join-Path $toolsRoot '..')).Path
$resultsRoot = Join-Path $projectRoot 'artifacts\test-results'
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null

function Test-NativeExit([string]$stage) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Stage '$stage' failed with exit code $LASTEXITCODE."
    }
}

$ran = New-Object System.Collections.Generic.List[string]

# A stage's forwarded switches travel in a splat, so an unset one reaches the called script as
# absent rather than as an empty value that overrides its default.
$visualArgs = @{ }
if ($VisualOnly.Count -gt 0) { $visualArgs['Only'] = $VisualOnly }
$packageArgs = @{ }
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) { $packageArgs['Version'] = $PackageVersion }
if ($SkipLaunchVerification) { $packageArgs['SkipLaunchVerification'] = $true }

if ($Stage -in 'All', 'Unit') {
    Write-Host '==> Language packs' -ForegroundColor Cyan
    $localeRoot = Join-Path $projectRoot 'src\Brezza.App\BrowserUi\locales'
    $packs = @{}
    foreach ($name in @('en', 'zh-Hans', 'zh-Hant')) {
        $path = Join-Path $localeRoot ($name + '.json')
        if (-not (Test-Path -LiteralPath $path)) { throw "Language pack $name.json is missing." }
        $parsed = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $hash = @{}
        foreach ($property in $parsed.PSObject.Properties) { $hash[$property.Name] = $property.Value }
        $packs[$name] = $hash
    }
    $referenceKeys = @($packs['en'].Keys | Sort-Object)
    foreach ($name in @('zh-Hans', 'zh-Hant')) {
        $keys = @($packs[$name].Keys | Sort-Object)
        $missing = @($referenceKeys | Where-Object { -not $packs[$name].ContainsKey($_) })
        $extra = @($keys | Where-Object { -not $packs['en'].ContainsKey($_) })
        if ($missing.Count -gt 0) { throw "$name.json is missing: $($missing -join ', ')" }
        if ($extra.Count -gt 0) { throw "$name.json has unknown keys: $($extra -join ', ')" }
        foreach ($key in $referenceKeys) {
            $referencePlaceholders = @([regex]::Matches([string]$packs['en'][$key], '\{[A-Za-z]+\}') | ForEach-Object Value | Sort-Object)
            $translatedPlaceholders = @([regex]::Matches([string]$packs[$name][$key], '\{[A-Za-z]+\}') | ForEach-Object Value | Sort-Object)
            if (($referencePlaceholders -join ',') -ne ($translatedPlaceholders -join ',')) {
                throw "$name.json has different placeholders for '$key'."
            }
        }
    }

    Write-Host '==> Unit tests' -ForegroundColor Cyan
    $coverageStart = (Get-Date).AddSeconds(-2)
    & dotnet test (Join-Path $projectRoot 'tests\Brezza.Core.Tests') -c $Configuration `
        --collect:"XPlat Code Coverage" `
        --logger trx --results-directory $resultsRoot
    Test-NativeExit 'Unit'

    # Coverage is collected per target framework. The baseline is stable above these floors; a
    # regression below them fails the stage instead of only changing an uploaded artifact.
    $minimumLineCoverage = 0.85
    $minimumBranchCoverage = 0.70
    $coverageFiles = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -Filter 'coverage.cobertura.xml' |
        Where-Object { $_.LastWriteTime -ge $coverageStart })
    if ($coverageFiles.Count -eq 0) { throw 'The Unit stage produced no coverage report.' }

    $lineCoverage = 1.0
    $branchCoverage = 1.0
    foreach ($coverageFile in $coverageFiles) {
        [xml]$coverage = Get-Content -LiteralPath $coverageFile.FullName -Raw
        $line = [double]::Parse([string]$coverage.coverage.'line-rate', [System.Globalization.CultureInfo]::InvariantCulture)
        $branch = [double]::Parse([string]$coverage.coverage.'branch-rate', [System.Globalization.CultureInfo]::InvariantCulture)
        if ($line -lt $lineCoverage) { $lineCoverage = $line }
        if ($branch -lt $branchCoverage) { $branchCoverage = $branch }
    }
    Write-Host ("Coverage: line {0:P1}, branch {1:P1} (floors {2:P0} / {3:P0})" -f `
        $lineCoverage, $branchCoverage, $minimumLineCoverage, $minimumBranchCoverage)
    if ($lineCoverage -lt $minimumLineCoverage) {
        throw "Line coverage $($lineCoverage.ToString('P1')) is below the $($minimumLineCoverage.ToString('P0')) floor."
    }
    if ($branchCoverage -lt $minimumBranchCoverage) {
        throw "Branch coverage $($branchCoverage.ToString('P1')) is below the $($minimumBranchCoverage.ToString('P0')) floor."
    }
    $ran.Add('Unit')
}

if ($Stage -in 'All', 'Smoke') {
    Write-Host '==> Windows smoke' -ForegroundColor Cyan
    # The smoke driver is compiled into Debug builds only, so the stage builds Debug itself
    # instead of expecting a pre-built executable.
    & dotnet build (Join-Path $projectRoot 'Brezza.slnx') -c Debug
    Test-NativeExit 'Smoke build'
    & (Join-Path $toolsRoot 'smoke.ps1')
    $ran.Add('Smoke')
}

if ($Stage -in 'All', 'Package') {
    Write-Host '==> Package verification' -ForegroundColor Cyan
    & (Join-Path $toolsRoot 'package.ps1') @packageArgs
    $ran.Add('Package')
}

if ($Stage -eq 'Visual') {
    Write-Host '==> Visual baseline capture' -ForegroundColor Cyan
    # The capture drives the Debug shell, the same build the smoke stage produces.
    & dotnet build (Join-Path $projectRoot 'Brezza.slnx') -c Debug
    Test-NativeExit 'Visual build'
    & (Join-Path $toolsRoot 'capture-baseline.ps1') @visualArgs
    $ran.Add('Visual')
}

Write-Host "PASS: $($ran -join ', ')" -ForegroundColor Green
