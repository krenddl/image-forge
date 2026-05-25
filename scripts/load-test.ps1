# ImageForge load-test driver (PowerShell version).
#
# Uses .NET HttpClient + Task batches for parallelism (works on PS 5.1+,
# no Start-Job, no ThreadJob module required).
#
# Usage:
#   .\scripts\load-test.ps1 download
#   .\scripts\load-test.ps1 upload
#   .\scripts\load-test.ps1 all
#
# Optional parameters:
#   -Count        how many images           (default 500)
#   -Concurrency  parallel requests         (default 10)
#   -PackDir      where downloads go        (default .\test-pack)
#   -ApiUrl       api base url              (default http://localhost:8080)
#   -Format       target format             (default webp)
#   -MaxDim       max dimension             (default 1280)

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('download', 'upload', 'all')]
    [string]$Mode = 'all',

    [int]$Count       = 500,
    [int]$Concurrency = 10,
    [string]$PackDir  = '.\test-pack',
    [string]$ApiUrl   = 'http://localhost:8080',
    [string]$Format   = 'webp',
    [int]$MaxDim      = 1280
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# --- pretty helpers ----------------------------------------------------------
function Section($t) { Write-Host ''; Write-Host '=== ' -ForegroundColor Cyan -NoNewline; Write-Host $t -NoNewline; Write-Host ' ===' -ForegroundColor Cyan }
function Note($t)    { Write-Host $t -ForegroundColor DarkGray }
function Ok($t)      { Write-Host "OK  $t" -ForegroundColor Green }
function Warn($t)    { Write-Host "!   $t" -ForegroundColor Yellow }
function Fail($t)    { Write-Host "X   $t" -ForegroundColor Red }

# --- download ---------------------------------------------------------------
function Download-Pack {
    Section "Download $Count images from picsum.photos -> $PackDir"
    if (-not (Test-Path $PackDir)) { New-Item -ItemType Directory -Path $PackDir | Out-Null }

    $alreadyHave = 0
    $needList = @()
    for ($i = 1; $i -le $Count; $i++) {
        $fname = Join-Path $PackDir ('img-{0:D4}.jpg' -f $i)
        if ((Test-Path $fname) -and (Get-Item $fname).Length -gt 0) {
            $alreadyHave++
        } else {
            $needList += [PSCustomObject]@{
                Url  = "https://picsum.photos/seed/forge$i/3000/2000.jpg"
                Path = $fname
            }
        }
    }
    Note ("need to fetch {0}, already have {1}" -f $needList.Count, $alreadyHave)

    if ($needList.Count -eq 0) {
        Ok 'nothing to do'
        return
    }

    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $sw = [Diagnostics.Stopwatch]::StartNew()

    # Process in batches of $Concurrency so memory stays bounded.
    $batchStart = 0
    $fetched    = 0
    $failed     = 0

    while ($batchStart -lt $needList.Count) {
        $batchEnd  = [Math]::Min($batchStart + $Concurrency, $needList.Count) - 1
        $batch     = $needList[$batchStart..$batchEnd]
        $tasks     = New-Object System.Collections.Generic.List[Object]

        foreach ($item in $batch) {
            $tasks.Add([PSCustomObject]@{
                Item = $item
                Task = $client.GetAsync($item.Url)
            })
        }

        foreach ($t in $tasks) {
            try {
                $resp = $t.Task.GetAwaiter().GetResult()
                if ($resp.IsSuccessStatusCode) {
                    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                    [IO.File]::WriteAllBytes($t.Item.Path, $bytes)
                    $fetched++
                } else {
                    $failed++
                }
                $resp.Dispose()
            } catch {
                $failed++
            }
        }

        $done = $batchEnd + 1
        Write-Host -NoNewline ("`r  {0,4} / {1}" -f $done, $needList.Count) -ForegroundColor DarkGray
        $batchStart = $batchEnd + 1
    }

    Write-Host ''
    $client.Dispose()

    Note ("elapsed: {0}s" -f [int]$sw.Elapsed.TotalSeconds)
    Ok   ("fetched: $fetched, already had: $alreadyHave")
    if ($failed -gt 0) { Warn "failed: $failed (timeout / rate-limit?)" }
}

# --- upload -----------------------------------------------------------------
function Upload-Pack {
    Section "Upload $PackDir\*.jpg to $ApiUrl"

    if (-not (Test-Path $PackDir)) { Fail "$PackDir not found. Run download first."; return }
    $files = Get-ChildItem -Path $PackDir -Filter '*.jpg' -File
    if ($files.Count -eq 0) { Fail "no .jpg files in $PackDir"; return }

    Note ("found {0} files; concurrency={1}" -f $files.Count, $Concurrency)
    Note ("format={0}, maxDimension={1}" -f $Format, $MaxDim)

    try {
        (Invoke-WebRequest "$ApiUrl/" -UseBasicParsing -TimeoutSec 5).StatusCode | Out-Null
        Ok 'API responsive'
    } catch { Fail "API not reachable at $ApiUrl"; return }

    $before = (Invoke-WebRequest "$ApiUrl/api/lifetime-stats" -UseBasicParsing).Content | ConvertFrom-Json
    Note ("before: processed={0}, bytesIn={1}, bytesOut={2}" -f $before.processed, $before.bytesIn, $before.bytesOut)

    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(120)

    Section 'POSTing'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok200 = 0; $other = 0
    $batchStart = 0

    while ($batchStart -lt $files.Count) {
        $batchEnd = [Math]::Min($batchStart + $Concurrency, $files.Count) - 1
        $batch    = $files[$batchStart..$batchEnd]
        $tasks    = New-Object System.Collections.Generic.List[Object]

        foreach ($f in $batch) {
            $bytes   = [IO.File]::ReadAllBytes($f.FullName)
            $content = New-Object System.Net.Http.MultipartFormDataContent
            $fileContent = New-Object System.Net.Http.ByteArrayContent (,$bytes)
            $fileContent.Headers.ContentType =
                [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/jpeg')
            $content.Add($fileContent, 'file', $f.Name)
            $content.Add((New-Object System.Net.Http.StringContent $Format),       'format')
            $content.Add((New-Object System.Net.Http.StringContent ([string]$MaxDim)), 'maxDimension')

            $tasks.Add($client.PostAsync("$ApiUrl/api/images", $content))
        }

        foreach ($t in $tasks) {
            try {
                $r = $t.GetAwaiter().GetResult()
                if ([int]$r.StatusCode -eq 200) { $ok200++ } else { $other++ }
                $r.Dispose()
            } catch { $other++ }
        }

        $done = $batchEnd + 1
        Write-Host -NoNewline ("`r  posted {0} / {1}" -f $done, $files.Count) -ForegroundColor DarkGray
        $batchStart = $batchEnd + 1
    }

    Write-Host ''
    $client.Dispose()
    Note ("all POSTs returned in {0}s" -f [int]$sw.Elapsed.TotalSeconds)
    Ok ("200 OK: $ok200")
    if ($other -gt 0) { Warn "non-200: $other" }

    Section 'Waiting for workers to drain'
    $idleRounds = 0
    while ($idleRounds -lt 3) {
        Start-Sleep -Seconds 2
        try {
            $s = (Invoke-WebRequest "$ApiUrl/api/stats" -UseBasicParsing).Content | ConvertFrom-Json
            Write-Host ('  ready={0,-4} inflight={1,-3} consumers={2}' -f $s.messagesReady, $s.messagesUnacknowledged, $s.consumers)
            if ($s.messagesReady -eq 0 -and $s.messagesUnacknowledged -eq 0) { $idleRounds++ } else { $idleRounds = 0 }
        } catch { $idleRounds = 99 }
    }

    $sw.Stop()
    $after = (Invoke-WebRequest "$ApiUrl/api/lifetime-stats" -UseBasicParsing).Content | ConvertFrom-Json
    $dProcessed = $after.processed - $before.processed
    $dIn        = $after.bytesIn   - $before.bytesIn
    $dOut       = $after.bytesOut  - $before.bytesOut
    $saved      = $dIn - $dOut

    Section 'Run summary'
    Write-Host ("  files posted          : {0}"  -f $ok200)
    Write-Host ("  tasks completed       : {0}"  -f $dProcessed)
    Write-Host ("  total time (post-done): {0}s" -f [int]$sw.Elapsed.TotalSeconds)
    if ($dProcessed -gt 0) {
        Write-Host ("  throughput            : {0:N2} tasks/sec" -f ($dProcessed / [Math]::Max(1, $sw.Elapsed.TotalSeconds)))
    }
    Write-Host ("  bytes in              : {0:N0}" -f $dIn)
    Write-Host ("  bytes out             : {0:N0}" -f $dOut)
    if ($dIn -gt 0) {
        Write-Host ("  saved                 : {0:N0} bytes ({1}% of original)" -f $saved, [int]($dOut * 100 / $dIn))
    }
}

# --- dispatcher --------------------------------------------------------------
switch ($Mode) {
    'download' { Download-Pack }
    'upload'   { Upload-Pack   }
    'all'      { Download-Pack; Upload-Pack }
}
