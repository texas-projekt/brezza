<#
.SYNOPSIS
Runs the Windows smoke: the Debug build driven through its real native controls.

.DESCRIPTION
Builds Debug, serves the pages the run needs from a local HTTP origin, and starts Brezza twice with
an isolated data root: the main path (start, navigate, tabs, panels, a private session, a download)
and a session restore. The driver is src/Brezza.App/BrowserShell.Smoke.cs. Needs an interactive
desktop, the WebView2 Runtime, and no other Brezza window. A failed run keeps its data root under
.test-data/.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (Get-Process Brezza -ErrorAction SilentlyContinue) { throw 'Close the running Brezza window first: the app is single-instance.' }

dotnet build (Join-Path $root 'Brezza.slnx') -c Debug
if ($LASTEXITCODE -ne 0) { throw 'The Debug build failed.' }
$exe = Join-Path $root 'src\Brezza.App\bin\Debug\net48\Brezza.exe'

$data = Join-Path $root ('.test-data\smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
$page = Join-Path $data 'smoke.html'
[IO.File]::WriteAllText($page, '<!doctype html><title>Brezza Smoke Page</title><h1>Smoke</h1>')
# A seeded history entry, so the new-tab page has a recent page to list.
[IO.File]::WriteAllText((Join-Path $data 'history.xml'),
    '<history schemaVersion="1"><entry><url>https://example.test/</url><title>Example</title><lastVisitedUtc>2025-01-01T00:00:00</lastVisitedUtc><visitCount>1</visitCount></entry></history>')

# A local origin for the HTTP pages: a private page (file pages are never recorded in history, so
# the private check needs HTTP) and an attachment.
$listener = New-Object System.Net.HttpListener
$port = Get-Random -Minimum 49152 -Maximum 65000
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()
$origin = Start-ThreadJob -ArgumentList $listener -ScriptBlock {
    param($listener)
    while ($listener.IsListening) {
        try { $context = $listener.GetContext() } catch { break }
        if ($context.Request.Url.AbsolutePath -eq '/download') {
            $context.Response.ContentType = 'application/octet-stream'
            $context.Response.AddHeader('Content-Disposition', 'attachment; filename="brezza-smoke.txt"')
            $body = [Text.Encoding]::UTF8.GetBytes('Brezza smoke download.')
        } else {
            $context.Response.ContentType = 'text/html; charset=utf-8'
            $body = [Text.Encoding]::UTF8.GetBytes('<!doctype html><title>Brezza Private Page</title><h1>Private</h1>')
        }
        $context.Response.OutputStream.Write($body, 0, $body.Length)
        $context.Response.Close()
    }
}

function Invoke-Run([string]$mode) {
    $result = Join-Path $data "result-$mode.txt"
    $env:BREZZA_UI_SMOKE = $mode
    $env:BREZZA_SMOKE_RESULT = $result
    $process = Start-Process -FilePath $exe -PassThru
    if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id -Force; throw "The $mode run timed out." }
    $text = if (Test-Path $result) { (Get-Content $result -Raw).Trim() } else { 'FAIL: no result written' }
    if (-not $text.StartsWith('PASS')) { throw "Smoke $mode failed. Data kept in $data`n$text" }
    $text
}

$failed = $true
try {
    $env:BREZZA_DATA_DIR = $data
    $env:BREZZA_SMOKE_PAGE = [Uri]::new($page).AbsoluteUri
    $env:BREZZA_SMOKE_PRIVATE_PAGE = "http://127.0.0.1:$port/private"
    $env:BREZZA_SMOKE_DOWNLOAD_PAGE = "http://127.0.0.1:$port/download"
    Invoke-Run 'main'
    # Restoring the session is a setting; the restore run turns it on.
    [IO.File]::WriteAllText((Join-Path $data 'settings.xml'),
        '<settings schemaVersion="4"><startupBehavior>RestoreSession</startupBehavior></settings>')
    Invoke-Run 'restore'
    $failed = $false
    'PASS: Smoke'
}
finally {
    $listener.Stop()
    Remove-Job $origin -Force -ErrorAction SilentlyContinue
    'BREZZA_DATA_DIR', 'BREZZA_UI_SMOKE', 'BREZZA_SMOKE_RESULT', 'BREZZA_SMOKE_PAGE', 'BREZZA_SMOKE_PRIVATE_PAGE', 'BREZZA_SMOKE_DOWNLOAD_PAGE' |
        ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    if (-not $failed) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
