@echo off
rem Runs the SharpHook self-test in the interactive session (a low-level keyboard hook and SendInput only work there).
rem Writes C:\juvin-spike\hotkey\results.txt. The named pipe route is covered by windows-pipe-test.ps1 instead.
set "B=C:\juvin-spike\hotkey\juvin-hotkey-spike.exe"
set "OUT=C:\juvin-spike\hotkey\results.txt"
del "%OUT%" 2>nul
"%B%" selftest --backend sharphook --timeout 6 > "%OUT%" 2>&1
echo done >> "%OUT%"
