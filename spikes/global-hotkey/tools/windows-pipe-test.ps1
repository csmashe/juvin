# Named pipe route: start the socket backend, wait, run the trigger, print both outputs. Does not need the interactive session.
$dir = "C:\juvin-spike\hotkey"
$exe = Join-Path $dir "juvin-hotkey-spike.exe"
Remove-Item "$dir\socket.txt", "$dir\socket-err.txt", "$dir\trigger.txt" -ErrorAction SilentlyContinue
$server = Start-Process -FilePath $exe -ArgumentList "selftest --backend socket --no-inject --timeout 10" -RedirectStandardOutput "$dir\socket.txt" -RedirectStandardError "$dir\socket-err.txt" -PassThru -NoNewWindow
Start-Sleep -Seconds 3
$trigger = Start-Process -FilePath $exe -ArgumentList "trigger" -RedirectStandardError "$dir\trigger.txt" -PassThru -NoNewWindow -Wait
"trigger exit $($trigger.ExitCode)"
$server.WaitForExit(15000) | Out-Null
"--socket"; Get-Content "$dir\socket.txt"
"--trigger"; Get-Content "$dir\trigger.txt"
