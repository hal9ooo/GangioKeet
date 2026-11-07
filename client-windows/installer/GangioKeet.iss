#define MyAppName "ChattyNeko"
#define MyAppVersion "0.2.2"
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
; L'app è tray-only: Restart Manager non riesce a chiuderla e Setup
; mostrerebbe "impossibile chiudere le applicazioni". La chiusura è gestita
; dal blocco [Code] qui sotto, che chiede lo spegnimento pulito e solo come
; ultima risorsa termina il processo.
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\..\artifacts\package\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Avvia {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  AppMutex = 'Local\ChattyNeko_SingleInstance';
  LegacyMutex = 'Local\GangioKeet_SingleInstance';
  ExitEvent = 'Local\ChattyNeko.ExitRequested';

function OpenEventW(dwDesiredAccess: Cardinal; bInheritHandle: Boolean;
  lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ Chiede all'app di chiudersi in modo pulito. }
procedure RequestGracefulExit;
var
  Handle: THandle;
begin
  Handle := OpenEventW(EVENT_MODIFY_STATE, False, ExitEvent);
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;
end;

function AppIsRunning: Boolean;
begin
  Result := CheckForMutexes(AppMutex) or CheckForMutexes(LegacyMutex);
end;

{ Chiude l'app prima di toccare i file. Prima chiede, poi attende, infine
  termina: senza questo passaggio l'installazione fallirebbe sui file in uso. }
procedure StopRunningApp;
var
  Attempt: Integer;
  ResultCode: Integer;
begin
  if not AppIsRunning then
    Exit;

  RequestGracefulExit;

  for Attempt := 1 to 16 do
  begin
    if not AppIsRunning then
      Exit;
    Sleep(500);
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ChattyNeko.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GangioKeet.Client.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopRunningApp;
end;
