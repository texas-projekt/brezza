param(
    # Switches the smoke driver reads from the environment. They are parameters here so a run sets
    # them deliberately; the driver itself still reads the variables.
    [switch]$LongNotice,
    [int]$PauseAfterFailureMs = 0,
    [int]$PauseAfterSettingsMs = 0,
    [int]$PauseAfterHistoryMs = 0,
    [int]$PauseAfterBookmarksMs = 0,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exePath = Join-Path $projectRoot 'src\Brezza.App\bin\Debug\net48\Brezza.exe'
if (@(Get-Process -Name 'Brezza' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'A Brezza window is already running. Close it before the smoke run: the app is single-instance.'
}
$runRoot = Join-Path $projectRoot ('.test-data\smoke-' + [guid]::NewGuid().ToString('N'))
if (-not (Test-Path $exePath)) { throw 'Build Brezza.slnx in Debug before running the UI smoke test.' }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$pagePath = Join-Path $runRoot 'smoke.html'
# The page's favicon is an emoji in an inline SVG data URI: the tab strip decodes and paints it,
# which the smoke uses to verify the favicon reaches the tab end to end without depending on a
# separate favicon fetch. An emoji is the usual icon for a test page and needs no asset or license.
$faviconUri = 'data:image/svg+xml,<svg xmlns=%22http://www.w3.org/2000/svg%22 viewBox=%220 0 100 100%22><text y=%22.9em%22 font-size=%2290%22>🧪</text></svg>'
[System.IO.File]::WriteAllText($pagePath, '<!doctype html><html><head><meta charset="utf-8"><title>Brezza Smoke Page</title><link rel="icon" href="' + $faviconUri + '"></head><body><h1>Smoke Pass</h1></body></html>')
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
# A local origin the harness serves itself. The search provider points at it, so a search the
# smoke types (a brezza:// address is searched like any other text) stays on this machine instead
# of loading a real search engine that would leave its own cookies in the isolated profile. The
# same origin plants the cookies the cookies panel is driven against: WebView2 keeps no cookies
# for file:// pages, and the isolated profile has none of its own.
# The origin chooses its own port inside the job and reports it once the listener is running.
# Allocating a port in this process and releasing it for the job would leave a race in which
# another process could take the port before HttpListener binds it.
$portFile = Join-Path $runRoot 'origin-port.txt'
$portErrorFile = Join-Path $runRoot 'origin-port-error.txt'
$cookieHtml = '<!doctype html><html><head><title>Brezza Cookie Page</title></head><body><h1>Cookie source</h1></body></html>'
$searchHtml = '<!doctype html><html><head><title>Brezza Search Result</title></head><body><h1>Search result</h1></body></html>'
# The same origin serves the attachments the downloads smoke drives: the browser writes them to the
# downloads folder, reports the completion, and lists them in the downloads panel. The slow one
# streams in chunks, so a download stays running long enough to pause, resume, and cancel.
$downloadBody = 'Brezza smoke download.'
$slowDownloadBody = 'Brezza slow smoke download. ' + ('x' * 80)

$previous = @{
    DataRoot = $env:BREZZA_DATA_DIR
    SmokeMode = $env:BREZZA_UI_SMOKE
    SmokePage = $env:BREZZA_SMOKE_PAGE
    SmokeResult = $env:BREZZA_SMOKE_RESULT
    SmokeLanguage = $env:BREZZA_SMOKE_LANGUAGE
    SmokeCookiePage = $env:BREZZA_SMOKE_COOKIE_PAGE
    SmokeDownloadPage = $env:BREZZA_SMOKE_DOWNLOAD_PAGE
    SmokeSlowDownloadPage = $env:BREZZA_SMOKE_SLOW_DOWNLOAD_PAGE
    LongNotice = $env:BREZZA_SMOKE_LONG_NOTICE
    PauseAfterFailure = $env:BREZZA_SMOKE_PAUSE_AFTER_FAILURE_MS
    PauseAfterSettings = $env:BREZZA_SMOKE_PAUSE_AFTER_SETTINGS_MS
    PauseAfterHistory = $env:BREZZA_SMOKE_PAUSE_AFTER_HISTORY_MS
    PauseAfterBookmarks = $env:BREZZA_SMOKE_PAUSE_AFTER_BOOKMARKS_MS
}
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

$cookieJob = Start-Job -ScriptBlock {
    param($portFile, $portErrorFile, $smokeHtml, $cookiePageHtml, $searchPageHtml, $downloadBody, $slowDownloadBody)
    $listener = $null
    try {
        # Select and bind in this job. The parent never hands over an unbound port, so another
        # process cannot take it between allocation and HttpListener.Start.
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
            $probe.Start()
            $candidatePort = [int]$probe.LocalEndpoint.Port
            $probe.Stop()

            $candidate = New-Object System.Net.HttpListener
            $candidate.Prefixes.Add("http://127.0.0.1:$candidatePort/")
            try {
                $candidate.Start()
                $listener = $candidate
                [System.IO.File]::WriteAllText($portFile, [string]$candidatePort)
                break
            }
            catch {
                $candidate.Close()
                Start-Sleep -Milliseconds 100
            }
        }
        if ($null -eq $listener) { throw 'The smoke origin could not bind a local port.' }

        while ($true) {
            $context = $listener.GetContext()
            $path = $context.Request.Url.AbsolutePath
            $isCookiePage = $path -ieq '/cookie.html'
            $isDownload = $path -ieq '/download'
            $isSlowDownload = $path -ieq '/slow-download'
            $isAttachment = $isDownload -or $isSlowDownload
            if ($isAttachment) {
                $context.Response.ContentType = 'application/octet-stream'
                $context.Response.AddHeader('Content-Disposition', 'attachment; filename="brezza-smoke-download.txt"')
            } else {
                $context.Response.ContentType = 'text/html; charset=utf-8'
            }

            $body = [System.Text.Encoding]::UTF8.GetBytes(
                $(if ($isCookiePage) { $cookiePageHtml } elseif ($isDownload) { $downloadBody } elseif ($isSlowDownload) { $slowDownloadBody } elseif ($path -ieq '/search') { $searchPageHtml } else { $smokeHtml }))
            if ($isCookiePage) {
                $context.Response.Headers.Add('Set-Cookie', 'smokecookie=1; path=/')
                $context.Response.Headers.Add('Set-Cookie', 'persistcookie=3; path=/; expires=Fri, 13 Feb 2049 12:00:00 GMT')
            }

            if ($isSlowDownload) {
                # One chunk at a time, so the browser reports progress it can be paused in the
                # middle of. The content length is sent up front, so the total size is known.
                $context.Response.ContentLength64 = $body.Length
                $chunkSize = 2
                for ($offset = 0; $offset -lt $body.Length; $offset += $chunkSize) {
                    $count = [Math]::Min($chunkSize, $body.Length - $offset)
                    $context.Response.OutputStream.Write($body, $offset, $count)
                    $context.Response.OutputStream.Flush()
                    Start-Sleep -Milliseconds 250
                }
            } else {
                $context.Response.ContentLength64 = $body.Length
                $context.Response.OutputStream.Write($body, 0, $body.Length)
            }
            $context.Response.Close()
        }
    }
    catch {
        # A normal stop at the end of the run also reaches this catch; the error file is only read
        # when the port never became ready.
        try { [System.IO.File]::WriteAllText($portErrorFile, $_.ToString()) } catch { }
    }
    finally {
        if ($null -ne $listener) {
            try { $listener.Stop(); $listener.Close() } catch { }
        }
    }
} -ArgumentList $portFile, $portErrorFile, ([System.IO.File]::ReadAllText($pagePath)), $cookieHtml, $searchHtml, $downloadBody, $slowDownloadBody

try {
    $portDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $portFile)) {
        if ($cookieJob.State -in @('Failed', 'Completed')) {
            $detail = if (Test-Path -LiteralPath $portErrorFile) { Get-Content -LiteralPath $portErrorFile -Raw } else { 'the origin job stopped without a port' }
            throw "The smoke origin did not start: $detail"
        }
        if ([DateTime]::UtcNow -ge $portDeadline) { throw 'The smoke origin did not report a port within 30 seconds.' }
        Start-Sleep -Milliseconds 100
    }
    $cookiePort = [int](Get-Content -LiteralPath $portFile -Raw).Trim()
}
catch {
    Stop-Job -Job $cookieJob -ErrorAction SilentlyContinue
    Remove-Job -Job $cookieJob -Force -ErrorAction SilentlyContinue
    throw
}
$searchTemplate = "http://127.0.0.1:$cookiePort/search?q={query}"

try {
    [System.IO.File]::WriteAllText(
        (Join-Path $runRoot 'settings.xml'),
        '<settings schemaVersion="1"><searchTemplate>' + $searchTemplate + '</searchTemplate><language>zh-Hans</language></settings>')

    $env:BREZZA_DATA_DIR = $runRoot
    $env:BREZZA_SMOKE_PAGE = [System.Uri]::new($pagePath).AbsoluteUri
    $env:BREZZA_SMOKE_COOKIE_PAGE = "http://127.0.0.1:$cookiePort/cookie.html"
    $env:BREZZA_SMOKE_DOWNLOAD_PAGE = "http://127.0.0.1:$cookiePort/download"
    $env:BREZZA_SMOKE_SLOW_DOWNLOAD_PAGE = "http://127.0.0.1:$cookiePort/slow-download"
    $env:BREZZA_SMOKE_LANGUAGE = 'zh-Hans'
    if ($LongNotice) { $env:BREZZA_SMOKE_LONG_NOTICE = '1' }
    if ($PauseAfterFailureMs -gt 0) { $env:BREZZA_SMOKE_PAUSE_AFTER_FAILURE_MS = "$PauseAfterFailureMs" }
    if ($PauseAfterSettingsMs -gt 0) { $env:BREZZA_SMOKE_PAUSE_AFTER_SETTINGS_MS = "$PauseAfterSettingsMs" }
    if ($PauseAfterHistoryMs -gt 0) { $env:BREZZA_SMOKE_PAUSE_AFTER_HISTORY_MS = "$PauseAfterHistoryMs" }
    if ($PauseAfterBookmarksMs -gt 0) { $env:BREZZA_SMOKE_PAUSE_AFTER_BOOKMARKS_MS = "$PauseAfterBookmarksMs" }
}
catch {
    Stop-Job -Job $cookieJob -ErrorAction SilentlyContinue
    Remove-Job -Job $cookieJob -Force -ErrorAction SilentlyContinue
    throw
}

try {
    $failed = $false
    try {
        $first = Invoke-SmokeProcess 'initial' (Join-Path $runRoot 'smoke-initial.txt')
        $sessionDocument = [xml](Get-Content -LiteralPath (Join-Path $runRoot 'session.xml') -Raw)
        $savedUrl = [string]$sessionDocument.session.tab.url
        if ($savedUrl -ne $env:BREZZA_SMOKE_PAGE) { throw "Session did not save the expected page: '$savedUrl'." }

        # Restoring the saved session is an opt-in startup behavior, so the second process only sees
        # the saved page once the setting asks for it.
        [System.IO.File]::WriteAllText(
            (Join-Path $runRoot 'settings.xml'),
            '<settings schemaVersion="3"><searchTemplate>' + $searchTemplate + '</searchTemplate><language>zh-Hans</language><startupBehavior>RestoreSession</startupBehavior></settings>')
        $restored = Invoke-SmokeProcess 'restore' (Join-Path $runRoot 'smoke-restore.txt')

        # The default behavior ignores the same saved session and starts from the new-tab page.
        [System.IO.File]::WriteAllText(
            (Join-Path $runRoot 'settings.xml'),
            '<settings schemaVersion="3"><searchTemplate>' + $searchTemplate + '</searchTemplate><language>zh-Hans</language><startupBehavior>NewTab</startupBehavior></settings>')
        $startup = Invoke-SmokeProcess 'startup' (Join-Path $runRoot 'smoke-startup.txt')

        "PASS: $first; $restored; $startup"
    }
    catch {
        $failed = $true
        throw
    }
}
finally {
    Stop-Job -Job $cookieJob -ErrorAction SilentlyContinue
    Remove-Job -Job $cookieJob -Force -ErrorAction SilentlyContinue

    # Every variable the run touched goes back to what it was, or is removed when it was not set.
    $restore = @{
        BREZZA_DATA_DIR = $previous.DataRoot
        BREZZA_UI_SMOKE = $previous.SmokeMode
        BREZZA_SMOKE_PAGE = $previous.SmokePage
        BREZZA_SMOKE_RESULT = $previous.SmokeResult
        BREZZA_SMOKE_LANGUAGE = $previous.SmokeLanguage
        BREZZA_SMOKE_COOKIE_PAGE = $previous.SmokeCookiePage
        BREZZA_SMOKE_DOWNLOAD_PAGE = $previous.SmokeDownloadPage
        BREZZA_SMOKE_SLOW_DOWNLOAD_PAGE = $previous.SmokeSlowDownloadPage
        BREZZA_SMOKE_LONG_NOTICE = $previous.LongNotice
        BREZZA_SMOKE_PAUSE_AFTER_FAILURE_MS = $previous.PauseAfterFailure
        BREZZA_SMOKE_PAUSE_AFTER_SETTINGS_MS = $previous.PauseAfterSettings
        BREZZA_SMOKE_PAUSE_AFTER_HISTORY_MS = $previous.PauseAfterHistory
        BREZZA_SMOKE_PAUSE_AFTER_BOOKMARKS_MS = $previous.PauseAfterBookmarks
    }
    foreach ($name in $restore.Keys) {
        if ($null -eq $restore[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -Path "Env:$name" -Value $restore[$name] }
    }

    if (Test-Path $runRoot) {
        # A failed run keeps its isolated profile: the failure detail and the data it left behind
        # are what the next run needs to be understood.
        if ($Keep -or $failed) {
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
