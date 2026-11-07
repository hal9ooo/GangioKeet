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

{ Chiude l'app prima di toccare i file. Se l'app ascolta l'evento, chiede lo
  spegnimento pulito e aspetta che l'evento sparisca (l'app lo chiude quando
  esce). Altrimenti, o se non risponde, termina il processo: senza questo
  passaggio l'installazione fallirebbe sui file in uso. }
procedure StopRunningApp;
var
  Attempt: Integer;
  ResultCode: Integer;
  Handle: THandle;
  Signalled: Boolean;
begin
  Signalled := False;
  Handle := OpenEventW(EVENT_MODIFY_STATE, False, ExitEvent);
  if Handle <> 0 then
  begin
    Log('StopRunningApp: evento trovato, chiedo la chiusura pulita');
    SetEvent(Handle);
    CloseHandle(Handle);
    Signalled := True;
  end
  else
    Log('StopRunningApp: evento assente (app vecchia o non in esecuzione)');

  if Signalled then
  begin
    for Attempt := 1 to 24 do
    begin
      Sleep(250);
      Handle := OpenEventW(EVENT_MODIFY_STATE, False, ExitEvent);
      if Handle = 0 then
      begin
        Log('StopRunningApp: app chiusa in modo pulito');
        Exit;
      end;
      CloseHandle(Handle);
    end;
    Log('StopRunningApp: nessuna risposta, termino il processo');
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ChattyNeko.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('StopRunningApp: taskkill ChattyNeko exit=' + IntToStr(ResultCode));
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GangioKeet.Client.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('StopRunningApp: taskkill GangioKeet exit=' + IntToStr(ResultCode));
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
