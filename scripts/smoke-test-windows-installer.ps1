# Checks the installer from build-windows-installer.ps1 on this machine: a silent per-user install,
# the installed files, shortcut, and registry entries, a run of juvin-host and juvin-cli, a start of
# the app until its control pipe answers --trigger, and a silent uninstall.
#
# Usage: scripts\smoke-test-windows-installer.ps1 <version> [package-dir]
# Juvin must not be installed or running. The app uses %LOCALAPPDATA%\Juvin; when that folder did
# not exist before the test it is removed afterwards.

$ErrorActionPreference = 'Stop'

if ($args.Count -lt 1 -or $args.Count -gt 2) {
    [Console]::Error.WriteLine('Usage: scripts\smoke-test-windows-installer.ps1 <version> [package-dir]')
    exit 2
}

$version = [string]$args[0]
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($args.Count -eq 2) {
    $packageDir = (Resolve-Path ([string]$args[1])).Path
} else {
    $packageDir = Join-Path $repoRoot 'dist'
}

# Must match AppId in scripts\windows\juvin.iss.
$appId = '{6E0B71C2-4F3D-4C8E-9A57-2D1B8E4F6A90}'
$installer = Join-Path $packageDir "Juvin-$version-x64-setup.exe"
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Juvin'
$dataDir = Join-Path $env:LOCALAPPDATA 'Juvin'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Juvin.lnk'
$appPathsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\juvin.exe'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$hostMessage = 'could not connect to Juvin'
# Scoped the way Juvin.Platform.Windows.Ipc.PipeNames scopes it.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$controlPipe = "juvin-control-$sid-$sessionId"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('juvin-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$dataDirExisted = Test-Path $dataDir
$app = $null
$installedHere = $false

function Fail([string]$message) {
    [Console]::Error.WriteLine("FAIL: $message")
    exit 1
}

function Pass([string]$message) {
    Write-Host "ok: $message"
}

function Test-ControlPipe {
    return [System.IO.Directory]::GetFiles('\\.\pipe\') -contains "\\.\pipe\$controlPipe"
}

# Stdin is closed so juvin-host sees end of input instead of waiting for a message.
function Invoke-Program([string]$path, [string]$arguments = '', [int]$timeoutSeconds = 120) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $path
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            $process.Kill()
            Fail "$path $arguments did not exit within $timeoutSeconds seconds"
        }
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result; Error = $stderr.Result }
    } finally {
        $process.Dispose()
    }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $condition)
}

try {
    if (-not (Test-Path $installer)) {
        Fail "$installer is missing"
    }
    if ((Get-Item $installer).Length -eq 0) {
        Fail "$installer is empty"
    }
    if (Test-ControlPipe) {
        Fail 'a Juvin is already running in this session; quit it first'
    }
    if (Test-Path $installDir) {
        Fail "$installDir already exists; uninstall Juvin first"
    }
    Pass 'the installer exists and no Juvin is installed or running'

    $installLog = Join-Path $work 'install.log'
    $installedHere = $true
    $install = Invoke-Program $installer "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$installLog`"" 300
    if ($install.ExitCode -ne 0) {
        if (Test-Path $installLog) {
            Get-Content $installLog | ForEach-Object { [Console]::Error.WriteLine("  setup log: $_") }
        }
        Fail "the installer exited with $($install.ExitCode)"
    }
    Pass 'silent install'

    foreach ($file in @(
        'juvin.exe', 'juvin-host.exe', 'juvin-cli.exe', 'LICENSE', 'unins000.exe',
        'Assets\juvin-mark-color-32.png', 'Resources\Localization\en.json'
    )) {
        if (-not (Test-Path (Join-Path $installDir $file))) {
            Fail "$installDir\$file is missing"
        }
    }
    if (Test-Path (Join-Path $installDir 'juvin-host.dll')) {
        Fail 'the install ships juvin-host.dll'
    }
    Pass 'installed files'

    if (-not (Test-Path $startMenuShortcut)) {
        Fail "the Start menu shortcut $startMenuShortcut is missing"
    }
    $shell = New-Object -ComObject WScript.Shell
    $target = $shell.CreateShortcut($startMenuShortcut).TargetPath
    if ($target -ne (Join-Path $installDir 'juvin.exe')) {
        Fail "the Start menu shortcut points at '$target'"
    }
    Pass 'Start menu shortcut'

    $appPath = (Get-ItemProperty -Path $appPathsKey -ErrorAction SilentlyContinue).'(default)'
    if ($appPath -ne (Join-Path $installDir 'juvin.exe')) {
        Fail "App Paths for juvin.exe is '$appPath'"
    }
    $uninstall = Get-ItemProperty -Path $uninstallKey -ErrorAction SilentlyContinue
    if (-not $uninstall) {
        Fail "the uninstall entry $uninstallKey is missing"
    }
    if ($uninstall.DisplayVersion -ne $version) {
        Fail "the uninstall entry's version is '$($uninstall.DisplayVersion)', expected $version"
    }
    Pass 'App Paths and uninstall entries'

    $hostRun = Invoke-Program (Join-Path $installDir 'juvin-host.exe') '' 30
    if ($hostRun.ExitCode -ne 1) {
        Fail "juvin-host exited with $($hostRun.ExitCode), expected 1"
    }
    if (-not $hostRun.Error.Contains($hostMessage)) {
        Fail "juvin-host did not report '$hostMessage'"
    }
    $cliRun = Invoke-Program (Join-Path $installDir 'juvin-cli.exe') '--help' 30
    if ($cliRun.ExitCode -ne 0) {
        Fail "juvin-cli --help exited with $($cliRun.ExitCode)"
    }
    Pass 'juvin-host and juvin-cli'

    $appLog = Join-Path $work 'juvin.log'
    $appErr = Join-Path $work 'juvin.err'
    $app = Start-Process -FilePath (Join-Path $installDir 'juvin.exe') -PassThru `
        -RedirectStandardOutput $appLog -RedirectStandardError $appErr
    if (-not (Wait-Until { (Test-ControlPipe) -or $app.HasExited } 60) -or $app.HasExited) {
        Get-Content $appLog, $appErr -ErrorAction SilentlyContinue | ForEach-Object { [Console]::Error.WriteLine("  juvin log: $_") }
        Fail 'juvin did not open its control pipe within 60 seconds'
    }
    $trigger = Invoke-Program (Join-Path $installDir 'juvin.exe') '--trigger' 30
    if ($trigger.ExitCode -ne 0) {
        Fail "juvin --trigger exited with $($trigger.ExitCode)"
    }
    if ($app.HasExited) {
        Fail 'juvin exited after the trigger'
    }
    Stop-Process -Id $app.Id -Force
    $app.WaitForExit()
    $app = $null
    Pass 'juvin started and answered --trigger'

    # The uninstaller hands over to a copy of itself in %TEMP% and returns at once, so wait for the folder.
    $uninstallRun = Invoke-Program (Join-Path $installDir 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' 120
    if ($uninstallRun.ExitCode -ne 0) {
        Fail "the uninstaller exited with $($uninstallRun.ExitCode)"
    }
    if (-not (Wait-Until { -not (Test-Path $installDir) } 120)) {
        Fail "$installDir is still there after uninstall"
    }
    if (Test-Path $startMenuShortcut) {
        Fail 'the Start menu shortcut is still there after uninstall'
    }
    if (Test-Path $appPathsKey) {
        Fail 'the App Paths key is still there after uninstall'
    }
    if (Test-Path $uninstallKey) {
        Fail 'the uninstall entry is still there after uninstall'
    }
    if (-not (Test-Path $dataDir)) {
        Fail "uninstall removed the data folder $dataDir"
    }
    Pass 'silent uninstall removed the program and kept the data folder'

    Write-Host 'All smoke tests passed.'
} finally {
    if ($app -and -not $app.HasExited) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        $app.WaitForExit()
    }
    # A failed run leaves no install behind for the next one to trip over.
    $leftoverUninstaller = Join-Path $installDir 'unins000.exe'
    if ($installedHere -and (Test-Path $leftoverUninstaller)) {
        Start-Process -FilePath $leftoverUninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        Wait-Until { -not (Test-Path $installDir) } 120 | Out-Null
    }
    if (-not $dataDirExisted) {
        Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
