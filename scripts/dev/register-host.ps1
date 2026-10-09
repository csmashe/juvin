# Registers the Debug build of juvin-host as the com.juvin.host native messaging host for
# Firefox, Chrome, and Edge for the current user on Windows. For development only; the setup
# wizard does this for users.
#
# Usage: scripts\dev\register-host.ps1 [-Unregister]
# Build first: dotnet build src\Juvin.NativeHost

param(
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$name = 'com.juvin.host'
$firefoxExtensionId = 'extension@juvin.app'
$chromeOrigin = 'chrome-extension://okfcbcoboinjniofikfnmllkbaeadelo/'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hostDir = Join-Path $repoRoot 'src\Juvin.NativeHost\bin\Debug\net10.0'
$hostPath = Join-Path $hostDir 'juvin-host.exe'
$firefoxManifest = Join-Path $hostDir "$name.firefox.json"
$chromiumManifest = Join-Path $hostDir "$name.chromium.json"

$keys = @{
    'HKCU:\Software\Mozilla\NativeMessagingHosts' = $firefoxManifest
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts' = $chromiumManifest
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts' = $chromiumManifest
}

if ($Unregister) {
    foreach ($parent in $keys.Keys) {
        $key = Join-Path $parent $name
        if (Test-Path $key) {
            Remove-Item -Path $key -Recurse
        }
    }

    Remove-Item -Path $firefoxManifest, $chromiumManifest -ErrorAction SilentlyContinue
    Write-Host "Removed $name from Firefox, Chrome, and Edge."
    exit 0
}

if (-not (Test-Path $hostPath)) {
    Write-Error "juvin-host not found at $hostPath. Run: dotnet build src\Juvin.NativeHost"
    exit 1
}

$firefox = [ordered]@{
    name = $name
    description = 'Juvin browser connector'
    path = $hostPath
    type = 'stdio'
    allowed_extensions = @($firefoxExtensionId)
}
$chromium = [ordered]@{
    name = $name
    description = 'Juvin browser connector'
    path = $hostPath
    type = 'stdio'
    allowed_origins = @($chromeOrigin)
}

# UTF-8 without a byte order mark; browsers reject a manifest that starts with one.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($firefoxManifest, ($firefox | ConvertTo-Json), $utf8NoBom)
[System.IO.File]::WriteAllText($chromiumManifest, ($chromium | ConvertTo-Json), $utf8NoBom)

foreach ($parent in $keys.Keys) {
    $key = Join-Path $parent $name
    New-Item -Path $key -Value $keys[$parent] -Force | Out-Null
}

Write-Host "Registered $name for Firefox, Chrome, and Edge."
Write-Host "Host: $hostPath"
