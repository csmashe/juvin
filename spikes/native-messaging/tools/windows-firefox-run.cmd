@echo off
set "PATH=%PATH%;C:\Program Files\nodejs"
cd /d C:\juvin-spike
del "%TEMP%\juvin-nm-spike\firefox-done.txt" 2>nul
start "" /b cmd /c ""%APPDATA%\npm\web-ext.cmd" run --source-dir C:\juvin-spike\extension --firefox "C:\Program Files\Mozilla Firefox\firefox.exe" --no-reload --no-input > "%TEMP%\juvin-nm-spike\webext-session.txt" 2>&1"
timeout /t 45 /nobreak > nul
taskkill /im firefox.exe /f > nul 2>&1
taskkill /im node.exe /f > nul 2>&1
echo done > "%TEMP%\juvin-nm-spike\firefox-done.txt"
