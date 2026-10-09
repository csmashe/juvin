# Launches Chrome with a temp profile, loads the unpacked spike extension over CDP, reads the stored result.
param([string]$Extension, [string]$Profile, [string]$Chrome = "chrome", [int]$HoldSeconds = 10)
$port = 9333
$proc = Start-Process -PassThru -FilePath $Chrome -ArgumentList @(
    "--user-data-dir=$Profile", "--no-first-run", "--no-default-browser-check",
    "--remote-debugging-port=$port", "--enable-unsafe-extension-debugging", "about:blank")
$version = $null
for ($i = 0; $i -lt 30 -and -not $version; $i++) {
    Start-Sleep -Milliseconds 500
    try { $version = Invoke-RestMethod "http://127.0.0.1:$port/json/version" } catch {}
}
if (-not $version) { throw "Chrome did not open the debugging port" }

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.ConnectAsync([Uri]$version.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
$pending = ""
function Send-Cdp($obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 5
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).Wait()
}
function Receive-Cdp($id) {
    $buf = [byte[]]::new(1MB)
    while ($true) {
        $sb = [Text.StringBuilder]::new()
        do {
            $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result
            $sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) | Out-Null
        } while (-not $r.EndOfMessage)
        $m = $sb.ToString() | ConvertFrom-Json
        if ($m.id -eq $id) { return $m }
    }
}
Send-Cdp @{ id = 1; method = "Extensions.loadUnpacked"; params = @{ path = $Extension } }
$load = Receive-Cdp 1
"loadUnpacked -> " + ($load | ConvertTo-Json -Compress)
$extId = $load.result.id
Start-Sleep -Seconds 5
Send-Cdp @{ id = 2; method = "Target.getTargets" }
$targets = (Receive-Cdp 2).result.targetInfos | Where-Object { $_.type -eq "service_worker" -and $_.url -like "*$extId*" }
if ($targets) {
    Send-Cdp @{ id = 3; method = "Target.attachToTarget"; params = @{ targetId = $targets[0].targetId; flatten = $true } }
    $session = (Receive-Cdp 3).result.sessionId
    Send-Cdp @{ id = 4; sessionId = $session; method = "Runtime.evaluate"; params = @{
        expression = "chrome.storage.local.get('result').then(r => JSON.stringify(r))"; awaitPromise = $true; returnByValue = $true } }
    "stored result: " + (Receive-Cdp 4).result.result.value
} else {
    "no service worker target found"
}
Start-Sleep -Seconds $HoldSeconds
Stop-Process -Id $proc.Id -Force
