param([string]$SignFile)
# Uses an ephemeral, untrusted certificate; never adds it to Trusted Roots.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'verify.ps1')
if ($SignFile) {
    if (-not $env:JUVIN_TEST_CERTIFICATE -or -not $env:JUVIN_TEST_SIGN_LOG) { throw 'Missing fixture context' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($SignFile)
    if ($info.ProductName -ne 'Juvin' -or $info.ProductVersion -ne '1.2.3' -or $info.CompanyName -ne 'Excel on the Web') {
        throw 'Inno did not preserve the product metadata required by SignPath'
    }
    $certificate = Get-Item "Cert:\CurrentUser\My\$env:JUVIN_TEST_CERTIFICATE"
    $null = Set-AuthenticodeSignature -FilePath $SignFile -Certificate $certificate -HashAlgorithm SHA256
    Test-JuvinSignature $SignFile $certificate.Thumbprint 'test-signing'
    Add-Content $env:JUVIN_TEST_SIGN_LOG $SignFile
    exit 0
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('juvin-trust-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$cert = $null
function Must-Fail([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw 'An invalid signature was accepted' }
}
try {
    $file = Join-Path $work 'fixture.dll'
    Add-Type -OutputAssembly $file -TypeDefinition @'
using System.Reflection;
[assembly: AssemblyProduct("Juvin")]
[assembly: AssemblyCompany("Excel on the Web")]
[assembly: AssemblyInformationalVersion("1.2.3")]
public class JuvinSigningFixture { public static int Value => 42; }
'@
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Juvin CI fixture' -CertStoreLocation 'Cert:\CurrentUser\My'
    Must-Fail { Test-JuvinSignature $file $cert.Thumbprint 'test-signing' }
    $null = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256
    Test-JuvinSignature $file $cert.Thumbprint 'test-signing'
    Must-Fail { Test-JuvinSignature $file ('0' * 40) 'test-signing' }
    Must-Fail { Test-JuvinSignature $file $cert.Thumbprint 'release-signing' }
    $bytes = [IO.File]::ReadAllBytes($file)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $sections = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $code = [BitConverter]::ToInt32($bytes, $sections + 20)
    $bytes[$code + 64] = $bytes[$code + 64] -bxor 1
    [IO.File]::WriteAllBytes($file, $bytes)
    Must-Fail { Test-JuvinSignature $file $cert.Thumbprint 'test-signing' }
    Write-Host 'Unsigned, wrong-certificate, untrusted-production, and tampered-file checks passed'

    $payload = Join-Path $work 'payload'
    $output = Join-Path $work 'output'
    $uninstallers = Join-Path $work 'uninstallers'
    New-Item -ItemType Directory $payload, $output, $uninstallers | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '..\..\..\LICENSE') (Join-Path $payload 'LICENSE')
    $env:JUVIN_TEST_CERTIFICATE = $cert.Thumbprint
    $env:JUVIN_TEST_SIGN_LOG = Join-Path $work 'callbacks.txt'
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $callback = '$q' + (Get-Process -Id $PID).Path + '$q -NoProfile -File $q' + $PSCommandPath + '$q -SignFile $f'
    & $iscc /Q '/DAppVersion=1.2.3' '/DNumericVersion=1.2.3.0' '/DInformationalVersion=1.2.3' '/DLauncherName=Juvin' `
        "/DPayloadDir=$payload" "/DOutputDir=$output" "/DIconFile=$root\assets\brand\ico\juvin.ico" `
        /DJuvinSignpath "/DSignedUninstallerDir=$uninstallers" "/Ssignpath=$callback" "$root\scripts\windows\juvin.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Inno signing callback contract failed' }
    $calls = @(Get-Content $env:JUVIN_TEST_SIGN_LOG)
    if ($calls.Count -ne 2 -or -not ($calls | Where-Object { [IO.Path]::GetDirectoryName($_) -eq $uninstallers }) -or
        -not ($calls | Where-Object { $_ -eq (Join-Path $output 'Juvin-1.2.3-x64-setup.exe') })) {
        throw 'Unexpected Inno signing callback paths'
    }
    Write-Host 'Inno installer/uninstaller metadata, callback paths and signing passed'
} finally {
    if ($cert) { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -Force }
    Remove-Item $work -Recurse -Force
    Remove-Item Env:\JUVIN_TEST_CERTIFICATE, Env:\JUVIN_TEST_SIGN_LOG -ErrorAction SilentlyContinue
}
