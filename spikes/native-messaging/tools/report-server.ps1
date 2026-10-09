# Collects result POSTs from the spike extension into $env:TEMP\juvin-nm-spike\reports.jsonl.
$dir = Join-Path $env:TEMP "juvin-nm-spike"
New-Item -ItemType Directory -Force $dir | Out-Null
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:18765/")
$listener.Start()
Write-Host "listening on 18765, writing to $dir\reports.jsonl"
while ($true) {
    $ctx = $listener.GetContext()
    $body = [System.IO.StreamReader]::new($ctx.Request.InputStream).ReadToEnd()
    Add-Content -Path (Join-Path $dir "reports.jsonl") -Value $body
    $ctx.Response.StatusCode = 204
    $ctx.Response.Close()
}
