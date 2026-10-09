# Registers the spike host for Firefox and Chrome on Windows (current user, no admin).
# Run from the directory that holds juvin-nm-spike.exe, chrome-extension-id.txt, and the extension folder.
$root = $PSScriptRoot
$exe = Join-Path $root "juvin-nm-spike.exe"
$chromeId = (Get-Content (Join-Path $root "chrome-extension-id.txt")).Trim()

$firefoxManifest = Join-Path $root "com.juvin.spike.firefox.json"
@{ name = "com.juvin.spike"; description = "Juvin spike host"; path = $exe; type = "stdio"; allowed_extensions = @("spike@juvin.app") } |
    ConvertTo-Json | Set-Content -Encoding ascii $firefoxManifest
$chromeManifest = Join-Path $root "com.juvin.spike.chrome.json"
@{ name = "com.juvin.spike"; description = "Juvin spike host"; path = $exe; type = "stdio"; allowed_origins = @("chrome-extension://$chromeId/") } |
    ConvertTo-Json | Set-Content -Encoding ascii $chromeManifest

foreach ($key in "HKCU:\Software\Mozilla\NativeMessagingHosts\com.juvin.spike") {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(Default)" -Value $firefoxManifest
}
foreach ($key in "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.juvin.spike", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.juvin.spike") {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(Default)" -Value $chromeManifest
}
"registered:"
Get-ItemProperty "HKCU:\Software\Mozilla\NativeMessagingHosts\com.juvin.spike" | Select-Object "(Default)"
Get-ItemProperty "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.juvin.spike" | Select-Object "(Default)"
Get-ItemProperty "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.juvin.spike" | Select-Object "(Default)"
