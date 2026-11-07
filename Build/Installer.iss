#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppFileVersion
  #define AppFileVersion "0.0.0.0"
#endif

[Setup]
AppId={{0F1C1EBD-644E-4562-9E5C-8354F4A8F5B1}
AppName=Loot Ledger
AppVersion={#AppVersion}
VersionInfoVersion={#AppFileVersion}
AppPublisher=Loot Ledger contributors
AppComments=Fortnite chest tracker for Windows
DefaultDirName={localappdata}\Programs\Loot Ledger
DefaultGroupName=Loot Ledger
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=LootLedger-Setup
UninstallDisplayIcon={app}\LootLedger.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
AppMutex=Local\LootLedger-Overlay
CloseApplications=no
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: startup; Description: "Start Loot Ledger when I sign in to &Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\dist\LootLedger.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Loot Ledger"; Filename: "{app}\LootLedger.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Loot Ledger"; Filename: "{app}\LootLedger.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\Loot Ledger"; Filename: "{app}\LootLedger.exe"; WorkingDir: "{app}"; Tasks: startup

; Remove a previously selected shortcut when rerunning setup with it unchecked.
[InstallDelete]
Type: files; Name: "{userdesktop}\Loot Ledger.lnk"; Tasks: not desktopicon
Type: files; Name: "{userstartup}\Loot Ledger.lnk"; Tasks: not startup

[Run]
Filename: "{app}\LootLedger.exe"; Description: "Launch Loot Ledger"; Flags: nowait postinstall skipifsilent

; Counts and preferences under LocalAppData are intentionally preserved on uninstall.
