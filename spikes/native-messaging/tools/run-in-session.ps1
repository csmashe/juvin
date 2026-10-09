# Runs a command in the interactive desktop session via a one-shot scheduled task, for use from SSH.
param([Parameter(Mandatory)][string]$Command, [string]$User = "administrator", [Parameter(Mandatory)][string]$Password, [string]$Name = "juvin-spike")
schtasks /delete /tn $Name /f 2>$null | Out-Null
schtasks /create /tn $Name /tr $Command /sc once /st 00:00 /ru $User /rp $Password /it /rl highest /f | Out-Null
schtasks /run /tn $Name | Out-Null
"started task $Name in the interactive session"
