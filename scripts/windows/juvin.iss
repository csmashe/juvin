; Inno Setup 6.3+ script for the per-user Juvin installer; scripts\build-windows-installer.ps1 builds it
; and passes AppVersion, NumericVersion, PayloadDir, OutputDir, and IconFile with /D.
;
; The installer only lays files down under %LOCALAPPDATA%\Programs\Juvin. The app writes the
; native messaging manifests and the browsers' HKCU NativeMessagingHosts keys itself when the
; wizard registers the connector, pointing them at {app}\juvin-host.exe; an upgrade installs into
; the same folder, so they stay valid. Uninstall removes {app}, the shortcuts, and the App Paths
; key, and leaves %LOCALAPPDATA%\Juvin (settings, history, secrets, the manifests) and the browser
; keys in place, as the Linux packages leave the data folder.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef NumericVersion
  #error Pass /DNumericVersion=<a.b.c.d>
#endif
#ifndef PayloadDir
  #error Pass /DPayloadDir=<publish folder>
#endif
#ifndef OutputDir
  #error Pass /DOutputDir=<folder>
#endif
#ifndef IconFile
  #error Pass /DIconFile=<juvin.ico>
#endif

[Setup]
AppId={{6E0B71C2-4F3D-4C8E-9A57-2D1B8E4F6A90}
AppName=Juvin
AppVersion={#AppVersion}
AppVerName=Juvin {#AppVersion}
AppPublisher=Excel on the Web
AppPublisherURL=https://github.com/csmashe/juvin
AppSupportURL=https://github.com/csmashe/juvin/issues
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Juvin
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Juvin-{#AppVersion}-x64-setup
SetupIconFile={#IconFile}
UninstallDisplayName=Juvin
UninstallDisplayIcon={app}\juvin.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; WindowsSingleInstanceGuard's mutex: setup and uninstall ask the user to quit a Juvin in the tray first.
AppMutex=Local\Juvin.SingleInstance
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Juvin"; Filename: "{app}\juvin.exe"
Name: "{autodesktop}\Juvin"; Filename: "{app}\juvin.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\juvin.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\juvin.exe"; Flags: uninsdeletekey
; The app writes this value when Launch at login is on; Setup never creates it, the uninstaller removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Juvin"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\juvin.exe"; Description: "{cm:LaunchProgram,Juvin}"; Flags: postinstall nowait skipifsilent
