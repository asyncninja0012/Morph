; Morph's Windows installer (PLAN D12, 4.6): a per-user Setup.exe built by build/installer.ps1 with Inno Setup 6.
; Installs to %LOCALAPPDATA%\Programs\Morph without admin; Start menu entry; uninstaller in Apps > Installed apps.
; Defines passed by installer.ps1: Version (e.g. 0.9.0), Source (the published app folder), Output (where Setup.exe goes), Root (repo).

#ifndef Version
  #error Pass /DVersion=x.y.z
#endif

[Setup]
AppId={{9227DA42-1E05-4514-B689-03342976EFB2}
AppName=Morph
AppVersion={#Version}
AppVerName=Morph {#Version}
AppPublisher=Morph
AppPublisherURL=https://github.com/asyncninja0012/Morph
AppSupportURL=https://github.com/asyncninja0012/Morph/issues
AppUpdatesURL=https://github.com/asyncninja0012/Morph/releases
VersionInfoVersion={#Version}
; {autopf} is %LOCALAPPDATA%\Programs when installing without admin.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Morph
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 only (PRD).
MinVersion=10.0.22000
OutputDir={#Output}
OutputBaseFilename=Morph-{#Version}-setup
SetupIconFile={#Root}\src\Morph.App\morph.ico
UninstallDisplayIcon={app}\Morph.exe
LicenseFile={#Root}\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; A running Morph (tray) is closed for the update and the uninstall, through the Restart Manager.
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Morph"; Filename: "{app}\Morph.exe"

[Run]
Filename: "{app}\Morph.exe"; Description: "Start Morph"; Flags: nowait postinstall skipifsilent

[Registry]
; Settings > Start Morph when I sign in writes this value; nothing is written here, it's only removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Morph"; Flags: uninsdeletevalue
; "Morph" in the classic right-click menu (Windows 11: Show more options) for files and folders (4.7). Morph.exe --wheel hands
; over to the running Morph, which opens the keyboard wheel on the Explorer selection. The Windows 11 menu itself needs package
; identity, so a signed MSIX (R9).
Root: HKCU; Subkey: "Software\Classes\*\shell\Morph"; ValueType: string; ValueName: ""; ValueData: "Morph"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Morph"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Morph.exe"
Root: HKCU; Subkey: "Software\Classes\*\shell\Morph"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKCU; Subkey: "Software\Classes\*\shell\Morph\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Morph.exe"" --wheel"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Morph"; ValueType: string; ValueName: ""; ValueData: "Morph"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Morph"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Morph.exe"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Morph"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Morph\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Morph.exe"" --wheel"

[UninstallDelete]
; The large, re-downloadable parts of Morph's data; settings and logs stay (in %LOCALAPPDATA%\Morph) for a reinstall.
Type: filesandordirs; Name: "{localappdata}\Morph\packs"
Type: filesandordirs; Name: "{localappdata}\Morph\cache"
Type: filesandordirs; Name: "{localappdata}\Morph\WebView2"
Type: filesandordirs; Name: "{localappdata}\Morph\demo"
