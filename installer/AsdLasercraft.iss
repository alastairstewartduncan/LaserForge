; Inno Setup script for A·S·D Lasercraft (https://jrsoftware.org/isinfo.php).
; CI builds it with:  ISCC.exe /DAppVersion=0.3.0 installer\AsdLasercraft.iss
; It packages the self-contained publish folder, so users don't need .NET installed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "A·S·D Lasercraft"
#define AppExe "AsdLasercraft.exe"

[Setup]
AppId={{8C1B6E2E-4E52-4F0B-9A43-7D7E1F3A5D21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Alastair Stewart Duncan
AppPublisherURL=https://github.com/alastairstewartduncan/LaserForge
AppSupportURL=https://github.com/alastairstewartduncan/LaserForge/issues
AppUpdatesURL=https://github.com/alastairstewartduncan/LaserForge/releases
DefaultDirName={autopf}\ASD Lasercraft
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\artifacts\installer
OutputBaseFilename=AsdLasercraft-{#AppVersion}-setup
SetupIconFile=..\src\AsdLasercraft.App\Assets\AsdLasercraft.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc"; Description: "Open .asdl project files with {#AppName}"; GroupDescription: "File types:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.asdl"; ValueType: string; ValueName: ""; ValueData: "AsdLasercraft.Project"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\AsdLasercraft.Project"; ValueType: string; ValueName: ""; ValueData: "{#AppName} project"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\AsdLasercraft.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\AsdLasercraft.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
