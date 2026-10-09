# Drives the spike host over stdio from PowerShell to confirm framing on Windows.
param([string]$Exe = "C:\juvin-spike\juvin-nm-spike.exe")
$psi = [Diagnostics.ProcessStartInfo]::new($Exe)
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
$p = [Diagnostics.Process]::Start($psi)
$in = $p.StandardInput.BaseStream; $out = $p.StandardOutput.BaseStream
function Send($json) { $b = [Text.Encoding]::UTF8.GetBytes($json); $in.Write([BitConverter]::GetBytes([int]$b.Length), 0, 4); $in.Write($b, 0, $b.Length); $in.Flush() }
function Recv() { $h = [byte[]]::new(4); $out.Read($h, 0, 4) | Out-Null; $n = [BitConverter]::ToInt32($h, 0); $b = [byte[]]::new($n); $got = 0; while ($got -lt $n) { $got += $out.Read($b, $got, $n - $got) }; [Text.Encoding]::UTF8.GetString($b) }
Send '{"type":"hello","sentAt":1}'; Recv
Send '{"type":"ack","roundTripMs":0}'; Recv
$in.Close(); $p.WaitForExit(); "exit $($p.ExitCode)"
