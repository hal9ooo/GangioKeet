#define MyAppName "ChattyNeko"
#define MyAppVersion "0.2.0"
#define MyAppPublisher "ChattyNeko"
#define MyAppExeName "ChattyNeko.exe"
#define MyAppId "{{7C2F5D8E-3A1B-4C6D-9E0F-1A2B3C4D5E6F}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ChattyNeko
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=ChattyNeko-Setup-{#MyAppVersion}
SetupIconFile=..\src\GangioKeet.Client\Assets\chattyneko.ico
Compression=lzma2/max
SolidCompression=yes
DiskSpanning=no
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\..\artifacts\package\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Avvia {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
