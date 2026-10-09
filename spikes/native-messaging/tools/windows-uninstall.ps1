Remove-Item -Recurse -Force "HKCU:\Software\Mozilla\NativeMessagingHosts\com.juvin.spike" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.juvin.spike" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.juvin.spike" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:TEMP "juvin-nm-spike"), (Join-Path $env:TEMP "juvin-chrome-profile") -ErrorAction SilentlyContinue
"removed spike registration"
