param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exePath = Join-Path $projectRoot 'src\Brezza.App\bin\Debug\net48\Brezza.exe'
$runRoot = Join-Path $projectRoot ('.test-data\smoke-' + [guid]::NewGuid().ToString('N'))
if (-not (Test-Path $exePath)) { throw 'Build Brezza.slnx in Debug before running the UI smoke test.' }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$pagePath = Join-Path $runRoot 'smoke.html'
[System.IO.File]::WriteAllText($pagePath, '<!doctype html><html><head><title>Brezza Smoke Page</title></head><body><h1>Smoke Pass</h1></body></html>')
# Trust probe: a remote document must not reach the host bridge, read browser UI assets, frame a
# browser-owned page, or navigate into the trusted origin on its own.
[System.IO.File]::WriteAllText(
    (Join-Path $runRoot 'trust-probe.html'),
    @'
<!doctype html>
<html>
<head><meta charset="utf-8"><title>Brezza Trust Probe</title></head>
<body>
<h1>Trust probe</h1>
<iframe id="frame" src="https://brezza.local/settings.html"></iframe>
<script>
    // A remote document must not reach a browser command bridge, read browser UI assets, frame a
    // browser-owned page, or navigate into a browser-owned origin on its own.
    const report = { bridge: 'pending', asset: 'pending', frame: 'pending', navigation: 'pending' };
    window.trustProbe = report;
    try {
        if (typeof window.chrome === 'undefined' || !window.chrome.webview) {
            report.bridge = 'absent';
        } else {
            window.chrome.webview.postMessage(JSON.stringify({ version: 1, type: 'newTab', args: {} }));
            report.bridge = 'posted';
        }
    } catch (error) {
        report.bridge = 'blocked';
    }
    fetch('https://brezza.local/locales/en.json')
        .then(response => { report.asset = 'status:' + response.status; })
        .catch(() => { report.asset = 'rejected'; })
        .finally(() => {
            // The navigation attempt waits for the asset result, so the report is complete first.
            setTimeout(() => { report.navigation = 'attempted'; location.href = 'https://brezza.local/settings.html'; }, 500);
        });
</script>
</body>
</html>
'@)
[System.IO.File]::WriteAllText(
    (Join-Path $runRoot 'history.xml'),
    '<history schemaVersion="1"><entry><url>https://example.test/</url><title>Example Seeded</title><lastVisitedUtc>2025-01-01T00:00:00</lastVisitedUtc><visitCount>1</visitCount></entry></history>')
[System.IO.File]::WriteAllText(
    (Join-Path $runRoot 'settings.xml'),
    '<settings schemaVersion="1"><searchTemplate>https://www.bing.com/search?q={query}</searchTemplate><language>zh-Hans</language></settings>')

$previous = @{
    DataRoot = $env:BREZZA_DATA_DIR
    SmokeMode = $env:BREZZA_UI_SMOKE
    SmokePage = $env:BREZZA_SMOKE_PAGE
    SmokeResult = $env:BREZZA_SMOKE_RESULT
    SmokeLanguage = $env:BREZZA_SMOKE_LANGUAGE
}
$env:BREZZA_DATA_DIR = $runRoot
$env:BREZZA_SMOKE_PAGE = [System.Uri]::new($pagePath).AbsoluteUri
$env:BREZZA_SMOKE_LANGUAGE = 'zh-Hans'

function Invoke-SmokeProcess([string]$mode, [string]$resultPath) {
    $env:BREZZA_UI_SMOKE = $mode
    $env:BREZZA_SMOKE_RESULT = $resultPath
    if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
    $process = Start-Process -FilePath $exePath -PassThru
    if (-not $process.WaitForExit(45000)) {
        Stop-Process -Id $process.Id -Force
        throw "Brezza smoke process timed out in mode '$mode'."
    }

    if (Test-Path $resultPath) {
        $result = Get-Content -LiteralPath $resultPath -Raw
        if (-not $result.StartsWith('PASS:', [StringComparison]::Ordinal)) {
            # Keep the run root so the failure detail and isolated data can be inspected.
            Write-Warning "Brezza smoke failed in mode '$mode'. Data root kept for inspection: $runRoot"
            throw $result
        }
    }

    return (Get-Content -LiteralPath $resultPath -Raw).Trim()
}

try {
    $first = Invoke-SmokeProcess 'initial' (Join-Path $runRoot 'smoke-initial.txt')
    $sessionDocument = [xml](Get-Content -LiteralPath (Join-Path $runRoot 'session.xml') -Raw)
    $savedUrl = [string]$sessionDocument.session.tab.url
    if ($savedUrl -ne $env:BREZZA_SMOKE_PAGE) { throw "Session did not save the expected page: '$savedUrl'." }

    $restored = Invoke-SmokeProcess 'restore' (Join-Path $runRoot 'smoke-restore.txt')
    "PASS: $first; $restored"
}
finally {
    if ($null -eq $previous.DataRoot) { Remove-Item Env:BREZZA_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:BREZZA_DATA_DIR = $previous.DataRoot }
    if ($null -eq $previous.SmokeMode) { Remove-Item Env:BREZZA_UI_SMOKE -ErrorAction SilentlyContinue }
    else { $env:BREZZA_UI_SMOKE = $previous.SmokeMode }
    if ($null -eq $previous.SmokePage) { Remove-Item Env:BREZZA_SMOKE_PAGE -ErrorAction SilentlyContinue }
    else { $env:BREZZA_SMOKE_PAGE = $previous.SmokePage }
    if ($null -eq $previous.SmokeResult) { Remove-Item Env:BREZZA_SMOKE_RESULT -ErrorAction SilentlyContinue }
    else { $env:BREZZA_SMOKE_RESULT = $previous.SmokeResult }
    if ($null -eq $previous.SmokeLanguage) { Remove-Item Env:BREZZA_SMOKE_LANGUAGE -ErrorAction SilentlyContinue }
    else { $env:BREZZA_SMOKE_LANGUAGE = $previous.SmokeLanguage }

    if (Test-Path $runRoot) {
        if ($env:BREZZA_SMOKE_KEEP -eq '1') {
            Write-Warning "Smoke data root kept for inspection: $runRoot"
        }
        else {
        $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
        while ((Test-Path $runRoot) -and [DateTime]::UtcNow -lt $cleanupDeadline) {
            try { Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction Stop }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path $runRoot) { Write-Warning "WebView2 still holds the isolated smoke profile: $runRoot" }
        }
    }
}
