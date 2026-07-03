; FirmwareStudio installer (Inno Setup 6)
; Build the app first:
;   dotnet publish src/FirmwareStudio.Wpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
; Then compile this script:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\FirmwareStudio.iss

#define MyAppName "FirmwareStudio"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "David Hucul"
#define MyAppExe "FirmwareStudio.exe"
#define MyAppIcon "..\src\FirmwareStudio.Wpf\FirmwareStudio.ico"
#define PublishDir "..\src\FirmwareStudio.Wpf\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
AppId={{B7F1C2A4-9E3D-4C5A-8B6E-FW0DR1VE0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; SCSI pass-through requires elevation; install per-machine.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
