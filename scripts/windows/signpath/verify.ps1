param(
    [string]$Directory,
    [string]$ProductVersion,
    [ValidateSet('test-signing', 'release-signing')][string]$Policy = 'release-signing',
    [string]$Thumbprint,
    [switch]$MetadataOnly
)

$ErrorActionPreference = 'Stop'

function Get-JuvinTrustResult([string]$Path) {
    if (-not ('JuvinAuthenticode' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class JuvinAuthenticode {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfo {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle, Subject;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TrustData {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint Ui, Revocation, Choice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State, Url;
        public uint Flags, Context;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static uint Verify(string path) {
        var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData {
                Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1,
                File = pointer, Flags = 0x80
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        } finally {
            Marshal.DestroyStructure<FileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }
}
'@
    }
    return [JuvinAuthenticode]::Verify($Path)
}

function Test-JuvinSignature([string]$Path, [string]$ExpectedThumbprint, [string]$SigningPolicy) {
    if ($ExpectedThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'A certificate thumbprint is required' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint) {
        throw "Unexpected signing certificate: $Path"
    }
    $trust = Get-JuvinTrustResult $Path
    $untrustedRoot = [Convert]::ToUInt32('800B0109', 16)
    $valid = $trust -eq 0 -and $signature.Status -eq 'Valid'
    $expectedTestRoot = $SigningPolicy -eq 'test-signing' -and $trust -eq $untrustedRoot -and
        $signature.Status -notin @('NotSigned', 'HashMismatch')
    if (-not ($valid -or $expectedTestRoot)) {
        throw ('Signature verification failed: {0}, status {1}, trust 0x{2:X8}' -f $Path, $signature.Status, $trust)
    }
    if ($SigningPolicy -eq 'release-signing' -and -not $signature.TimeStamperCertificate) {
        throw "Production signature has no timestamp: $Path"
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $Directory -or -not $ProductVersion) { throw 'Directory and ProductVersion are required' }
    $files = @(Get-ChildItem -LiteralPath $Directory -File)
    if (-not $files.Count) { throw 'No files to verify' }
    foreach ($file in $files) {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
        if ($info.ProductName -ne 'Juvin' -or $info.CompanyName -ne 'Excel on the Web' -or
            $info.ProductVersion -cne $ProductVersion) {
            throw "Unexpected product metadata: $($file.Name)"
        }
        if (-not $MetadataOnly) { Test-JuvinSignature $file.FullName $Thumbprint $Policy }
    }
    Write-Host "Verified $($files.Count) Juvin file(s), policy $Policy, metadata-only=$MetadataOnly"
}
