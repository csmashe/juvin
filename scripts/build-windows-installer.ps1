# Builds the Windows installer of Juvin: the app self-contained for win-x64 with juvin-host.exe and
# juvin-cli.exe beside it as single-file executables, packed by Inno Setup 6 into a per-user setup.
#
# Usage: scripts\build-windows-installer.ps1 <version> [output-dir]
#   version     MAJOR.MINOR.PATCH, optionally followed by -word or -word.number (alpha, beta.2, rc.1)
#   output-dir  where the installer goes (default dist\)
# Runs on Windows PowerShell 5.1 and PowerShell 7. Needs the .NET 10 SDK and Inno Setup 6 (ISCC).

$ErrorActionPreference = 'Stop'

if ($args.Count -lt 1 -or $args.Count -gt 2) {
    [Console]::Error.WriteLine('Usage: scripts\build-windows-installer.ps1 <version> [output-dir]')
    exit 2
}

$version = [string]$args[0]
# The same versions the Linux packages accept, so one tag builds every package.
$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[A-Za-z]+(\.(0|[1-9][0-9]*))?)?$'
if ($version -cnotmatch $versionPattern) {
    [Console]::Error.WriteLine("error: '$version' is not MAJOR.MINOR.PATCH with an optional -word or -word.number prerelease")
    exit 2
}
# The file version resource only takes four numbers.
$numericVersion = ($version -split '-')[0] + '.0'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($args.Count -eq 2) {
    $outDir = [string]$args[1]
} else {
    $outDir = Join-Path $repoRoot 'dist'
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$outDir = (Resolve-Path $outDir).Path

$hostLeftovers = @('juvin-host.dll', 'juvin-host.deps.json', 'juvin-host.runtimeconfig.json', 'juvin-host.pdb')
$issFile = Join-Path $repoRoot 'scripts\windows\juvin.iss'
$iconFile = Join-Path $repoRoot 'assets\brand\ico\juvin.ico'

function Fail([string]$message) {
    [Console]::Error.WriteLine("error: $message")
    exit 1
}

function Find-Iscc {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $default = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path $default) {
        return $default
    }

    Fail 'Inno Setup 6 was not found (ISCC.exe is not on PATH or in Program Files (x86)\Inno Setup 6). Install it from https://jrsoftware.org/isdl.php'
}

function Invoke-Publish([string]$project, [string]$output, [string[]]$extra) {
    & dotnet publish (Join-Path $repoRoot $project) -c Release -r win-x64 --self-contained true "-p:Version=$version" `
        -p:ContinuousIntegrationBuild=true --nologo -o $output @extra
    if ($LASTEXITCODE -ne 0) {
        Fail "dotnet publish $project failed with exit code $LASTEXITCODE"
    }
}

function Test-PeExecutable([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        return $stream.ReadByte() -eq 0x4D -and $stream.ReadByte() -eq 0x5A
    } finally {
        $stream.Dispose()
    }
}

if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
    Fail 'dotnet is required'
}
$iscc = Find-Iscc

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('juvin-installer-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
try {
    Write-Host '==> Publishing the app'
    Invoke-Publish 'src\Juvin.App\Juvin.App.csproj' $payload @(
        '-p:PublishSingleFile=false', '-p:PublishReadyToRun=true', '-p:DebugType=portable', '-p:DebugSymbols=true'
    )

    $single = @(
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false'
    )
    Write-Host '==> Publishing juvin-host and juvin-cli as single files'
    Invoke-Publish 'src\Juvin.NativeHost\Juvin.NativeHost.csproj' (Join-Path $stage 'host') $single
    Invoke-Publish 'src\Juvin.Cli\Juvin.Cli.csproj' (Join-Path $stage 'cli') $single

    # The app's build copies the framework-dependent host beside it; the self-contained one replaces it.
    Copy-Item (Join-Path $stage 'host\juvin-host.exe') (Join-Path $payload 'juvin-host.exe') -Force
    Copy-Item (Join-Path $stage 'cli\juvin-cli.exe') (Join-Path $payload 'juvin-cli.exe') -Force
    foreach ($leftover in $hostLeftovers) {
        Remove-Item (Join-Path $payload $leftover) -Force -ErrorAction SilentlyContinue
    }

    $runtimes = Join-Path $payload 'runtimes'
    if (Test-Path $runtimes) {
        Get-ChildItem $runtimes -Directory | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE') (Join-Path $payload 'LICENSE') -Force

    foreach ($leftover in $hostLeftovers) {
        if (Test-Path (Join-Path $payload $leftover)) {
            Fail "$leftover is still in the payload"
        }
    }
    foreach ($binary in @('juvin-host.exe', 'juvin-cli.exe')) {
        $path = Join-Path $payload $binary
        if (-not (Test-PeExecutable $path)) {
            Fail "$binary is not a Windows executable"
        }
        if ((Get-Item $path).Length -le 20MB) {
            Fail "$binary is not a self-contained single file"
        }
    }
    foreach ($required in @('juvin.exe', 'LICENSE', 'Assets\juvin-mark-color-32.png', 'Resources\Localization\en.json')) {
        if (-not (Test-Path (Join-Path $payload $required))) {
            Fail "$required is missing from the payload"
        }
    }

    Write-Host '==> Building the installer'
    & $iscc /Q "/DAppVersion=$version" "/DNumericVersion=$numericVersion" "/DPayloadDir=$payload" `
        "/DOutputDir=$outDir" "/DIconFile=$iconFile" $issFile
    if ($LASTEXITCODE -ne 0) {
        Fail "ISCC failed with exit code $LASTEXITCODE"
    }

    $installer = Join-Path $outDir "Juvin-$version-x64-setup.exe"
    if (-not (Test-Path $installer)) {
        Fail "ISCC did not produce $installer"
    }
    Write-Host "Built $installer"
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
