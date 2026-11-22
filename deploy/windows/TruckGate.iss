; TruckGate - Windows installer (Inno Setup 6).
;
; Build (done by .github/workflows/release.yml):
;   iscc /DAppVersion=1.0.0 /DSourceRoot=..\..\out deploy\windows\TruckGate.iss
; Inputs prepared by the workflow under SourceRoot:
;   server\   dotnet publish -r win-x64 --self-contained (with wwwroot)
;   pgsql\    PostgreSQL 16 Windows x64 binaries (bin, lib, share only)
;   redist\vc_redist.x64.exe   Microsoft Visual C++ 2015-2022 runtime (needed by PostgreSQL)
;
; No password, key or certificate is inside this installer: database passwords are generated on
; the target PC at install time; the server signing key is created by the server on first use.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceRoot
  #define SourceRoot "..\..\out"
#endif

[Setup]
AppId={{6C1E3F0A-6B52-4C8E-9D3A-71B0F0E2A9C4}
AppName=TruckGate
AppVersion={#AppVersion}
AppVerName=TruckGate {#AppVersion}
AppPublisher=TruckGate
VersionInfoVersion={#AppVersion}
VersionInfoDescription=TruckGate server and gate station setup
DefaultDirName={autopf}\TruckGate
DefaultGroupName=TruckGate
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir={#SourceRoot}\installer
OutputBaseFilename=TruckGate-Windows-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
UninstallDisplayName=TruckGate {#AppVersion}
UninstallDisplayIcon={app}\scripts\truckgate.ico
SetupIconFile=truckgate.ico
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Types]
Name: "server"; Description: "{cm:TypeServer}"
Name: "station"; Description: "{cm:TypeStation}"

[Components]
Name: "server"; Description: "{cm:CompServer}"; Types: server
Name: "station"; Description: "{cm:CompStation}"; Types: server station; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceRoot}\server\*"; DestDir: "{app}\server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\pgsql\*"; DestDir: "{app}\pgsql"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\redist\vc_redist.x64.exe"; DestDir: "{tmp}"; Components: server; Flags: deleteafterinstall
Source: "scripts\common.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\configure.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\open-gate-station.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\detect-ip.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "scripts\install-server.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "scripts\uninstall-server.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "scripts\backup.ps1"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "scripts\create-admin.cmd"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "scripts\db-setup-env.sql"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "..\db-setup.sql"; DestDir: "{app}\scripts"; Components: server; Flags: ignoreversion
Source: "truckgate.ico"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
; used by the wizard before installation
Source: "scripts\detect-ip.ps1"; Flags: dontcopy
Source: "scripts\common.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\{cm:IconGate}"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\open-gate-station.ps1"""; IconFilename: "{app}\scripts\truckgate.ico"
Name: "{autodesktop}\{cm:IconGate}"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\open-gate-station.ps1"""; IconFilename: "{app}\scripts\truckgate.ico"; Tasks: desktopicon
Name: "{group}\{cm:IconAddress}"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\configure.ps1"""; IconFilename: "{app}\scripts\truckgate.ico"
Name: "{group}\{cm:IconAdmin}"; Filename: "{app}\scripts\create-admin.cmd"; Components: server
Name: "{group}\{cm:IconBackup}"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\backup.ps1"""; Components: server
Name: "{group}\{cm:IconLogs}"; Filename: "{commonappdata}\TruckGate\logs"; Components: server
Name: "{group}\{cm:IconGuide}"; Filename: "{app}\docs\INSTALLATION_GUIDE.md"; Check: GuideExists

[Run]
Filename: "{app}\scripts\create-admin.cmd"; Description: "{cm:RunAdmin}"; Components: server; Flags: postinstall nowait skipifsilent shellexec
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\scripts\open-gate-station.ps1"""; Description: "{cm:RunGate}"; Flags: postinstall nowait skipifsilent unchecked runhidden

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-server.ps1"""; Components: server; Flags: runhidden waituntilterminated; RunOnceId: "TruckGateServices"

[UninstallDelete]
Type: files; Name: "{app}\gate-station.json"

[CustomMessages]
en.TypeServer=Server + gate station (this PC is the TruckGate server)
fr.TypeServer=Serveur + poste de garde (ce PC est le serveur TruckGate)
en.TypeStation=Gate station only (the TruckGate server is another PC)
fr.TypeStation=Poste de garde seulement (le serveur TruckGate est un autre PC)
en.CompServer=TruckGate server, PostgreSQL 16 database and Windows services
fr.CompServer=Serveur TruckGate, base PostgreSQL 16 et services Windows
en.CompStation=Gate station shortcut (opens TruckGate in Chrome/Edge with silent receipt printing)
fr.CompStation=Raccourci poste de garde (ouvre TruckGate dans Chrome/Edge, impression silencieuse des reçus)
en.TaskDesktop=Desktop shortcut for the gate station
fr.TaskDesktop=Raccourci du poste de garde sur le bureau
en.IconGate=TruckGate Gate Station
fr.IconGate=TruckGate Poste de garde
en.IconAddress=Change server address
fr.IconAddress=Changer l'adresse du serveur
en.IconAdmin=Create administrator account
fr.IconAdmin=Créer le compte administrateur
en.IconBackup=Back up now
fr.IconBackup=Sauvegarder maintenant
en.IconLogs=Log files
fr.IconLogs=Journaux
en.IconGuide=Installation guide
fr.IconGuide=Guide d'installation
en.RunAdmin=Create the first administrator account now
fr.RunAdmin=Créer maintenant le premier compte administrateur
en.RunGate=Open the gate station
fr.RunGate=Ouvrir le poste de garde
en.UrlTitleServer=Address used by the quarry phones
fr.UrlTitleServer=Adresse utilisée par les téléphones de la carrière
en.UrlSubServer=Phones on the factory Wi-Fi reach this server at this address. Give this PC a fixed IP address (DHCP reservation in the router) so that it does not change.
fr.UrlSubServer=Les téléphones sur le Wi-Fi de l'usine joignent ce serveur à cette adresse. Donnez une adresse IP fixe à ce PC (réservation DHCP dans le routeur).
en.UrlTitleStation=Address of the TruckGate server
fr.UrlTitleStation=Adresse du serveur TruckGate
en.UrlSubStation=The PC where the TruckGate server is installed, for example http://192.168.1.10:5080
fr.UrlSubStation=Le PC où le serveur TruckGate est installé, par exemple http://192.168.1.10:5080
en.UrlLabel=Address (http://IP:5080):
fr.UrlLabel=Adresse (http://IP:5080) :
en.UrlBad=The address must look like http://192.168.1.10:5080 (http:// or https://, an IP address or name, optional port, nothing after it).
fr.UrlBad=L'adresse doit avoir la forme http://192.168.1.10:5080 (http:// ou https://, une adresse IP ou un nom, port facultatif, rien après).
en.UrlLoopback=Phones cannot reach "localhost" or 127.0.0.1. Use this PC's IP address on the factory network.%n%nContinue anyway?
fr.UrlLoopback=Les téléphones ne peuvent pas joindre « localhost » ou 127.0.0.1. Utilisez l'adresse IP de ce PC sur le réseau de l'usine.%n%nContinuer quand même ?
en.UrlUnreachable=Connection test failed: %1%n%nCheck that the server PC is on, that it runs TruckGate, that this PC is on the same network, and that the Windows firewall of the server allows TCP 5080.%n%nContinue anyway?
fr.UrlUnreachable=Échec du test de connexion : %1%n%nVérifiez que le PC serveur est allumé, que TruckGate y fonctionne, que ce PC est sur le même réseau et que le pare-feu Windows du serveur autorise TCP 5080.%n%nContinuer quand même ?
en.UrlNotTruckGate=The address answers but it is not a TruckGate server (HTTP %1).
fr.UrlNotTruckGate=L'adresse répond mais ce n'est pas un serveur TruckGate (HTTP %1).
en.UrlDbDown=The TruckGate server answers but its database is not running.
fr.UrlDbDown=Le serveur TruckGate répond mais sa base de données ne fonctionne pas.
en.StatusRuntime=Installing the Microsoft Visual C++ runtime...
fr.StatusRuntime=Installation du runtime Microsoft Visual C++...
en.StatusServer=Setting up the database and the TruckGate service (1-3 minutes)...
fr.StatusServer=Configuration de la base de données et du service TruckGate (1 à 3 minutes)...
en.RuntimeFailed=The Microsoft Visual C++ runtime could not be installed (code %1). The database cannot run without it.
fr.RuntimeFailed=Le runtime Microsoft Visual C++ n'a pas pu être installé (code %1). La base de données ne peut pas fonctionner sans lui.
en.ServerFailed=The TruckGate server could not be set up. The reason is at the end of:%n%1%n%nFix the problem and run this installer again (your data is kept).
fr.ServerFailed=Le serveur TruckGate n'a pas pu être configuré. La raison est à la fin de :%n%1%n%nCorrigez le problème et relancez cet installateur (vos données sont conservées).
en.ServerDone=TruckGate server is running.%n%nPhones enroll with: %1%nGate station on this PC: http://localhost:5080%n%nNext: create the administrator account (option below), then sign in.
fr.ServerDone=Le serveur TruckGate fonctionne.%n%nLes téléphones s'enregistrent avec : %1%nPoste de garde sur ce PC : http://localhost:5080%n%nEnsuite : créez le compte administrateur (option ci-dessous), puis connectez-vous.
en.KeepData=Also delete ALL TruckGate data on this PC (database, server keys, camera pictures, backups in %1)?%n%nThis cannot be undone. Choose No to keep the data for a later reinstall.
fr.KeepData=Supprimer aussi TOUTES les données TruckGate de ce PC (base, clés du serveur, photos, sauvegardes dans %1) ?%n%nIrréversible. Choisissez Non pour garder les données pour une réinstallation.

[Code]
var
  UrlPage: TInputQueryWizardPage;
  DetectedUrl: String;
  ServerFailed: Boolean;
  FinalUrl: String;

function GuideExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\docs\INSTALLATION_GUIDE.md'));
end;

function IsServer: Boolean;
begin
  Result := WizardIsComponentSelected('server');
end;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function DetectUrl: String;
var
  OutFile: String;
  Code: Integer;
  S: AnsiString;
begin
  Result := '';
  ExtractTemporaryFile('detect-ip.ps1');
  ExtractTemporaryFile('common.ps1');
  OutFile := ExpandConstant('{tmp}\detected-url.txt');
  if Exec(PowerShellExe, '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\detect-ip.ps1') +
          '" -OutFile "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    if LoadStringFromFile(OutFile, S) then
      Result := Trim(String(S));
end;

function ExistingStationUrl: String;
var
  S: AnsiString;
  P: Integer;
  T: String;
begin
  Result := '';
  if LoadStringFromFile(ExpandConstant('{autopf}\TruckGate\gate-station.json'), S) then
  begin
    T := String(S);
    P := Pos('"url"', T);
    if P > 0 then
    begin
      T := Copy(T, P + 5, Length(T));
      P := Pos('"', T);
      if P > 0 then
      begin
        T := Copy(T, P + 1, Length(T));
        P := Pos('"', T);
        if P > 0 then Result := Copy(T, 1, P - 1);
      end;
    end;
  end;
end;

{ http(s)://host[:port] with nothing after it }
function IsValidUrl(U: String): Boolean;
var
  Rest: String;
  I: Integer;
  C: Char;
begin
  Result := False;
  if Pos('http://', Lowercase(U)) = 1 then Rest := Copy(U, 8, Length(U))
  else if Pos('https://', Lowercase(U)) = 1 then Rest := Copy(U, 9, Length(U))
  else Exit;
  if (Length(Rest) > 0) and (Rest[Length(Rest)] = '/') then Rest := Copy(Rest, 1, Length(Rest) - 1);
  if Length(Rest) = 0 then Exit;
  for I := 1 to Length(Rest) do
  begin
    C := Rest[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or
            (C = '.') or (C = '-') or (C = ':') or (C = '_')) then Exit;
  end;
  Result := True;
end;

function IsLoopback(U: String): Boolean;
var
  L: String;
begin
  L := Lowercase(U);
  Result := (Pos('://localhost', L) > 0) or (Pos('://127.', L) > 0);
end;

function StripSlash(U: String): String;
begin
  Result := Trim(U);
  if (Length(Result) > 0) and (Result[Length(Result)] = '/') then Result := Copy(Result, 1, Length(Result) - 1);
end;

{ '' when the address answers as a TruckGate server, otherwise the reason }
function TestServer(U: String): String;
var
  Req: Variant;
  Status: Integer;
  Body: String;
begin
  Result := '';
  try
    Req := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Req.SetTimeouts(4000, 4000, 4000, 6000);
    Req.Open('GET', StripSlash(U) + '/health', False);
    Req.Send('');
    Status := Req.Status;
    Body := Req.ResponseText;
    if Status = 503 then Result := CustomMessage('UrlDbDown')
    else if (Status <> 200) or (Pos('"truckgate"', Body) = 0) then
      Result := FmtMessage(CustomMessage('UrlNotTruckGate'), [IntToStr(Status)]);
  except
    Result := GetExceptionMessage;
  end;
end;

procedure InitializeWizard;
begin
  UrlPage := CreateInputQueryPage(wpSelectComponents, '', '', '');
  UrlPage.Add(CustomMessage('UrlLabel'), False);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Given: String;
begin
  if (CurPageID = wpFinished) and IsServer and (not ServerFailed) and (FinalUrl <> '') then
    WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('ServerDone'), [FinalUrl]);
  if CurPageID = UrlPage.ID then
  begin
    Given := ExpandConstant('{param:SERVERURL|}');
    if IsServer then
    begin
      UrlPage.Caption := CustomMessage('UrlTitleServer');
      UrlPage.Description := CustomMessage('UrlSubServer');
      if Given <> '' then UrlPage.Values[0] := Given
      else
      begin
        if DetectedUrl = '' then DetectedUrl := DetectUrl;
        if DetectedUrl <> '' then UrlPage.Values[0] := DetectedUrl;
      end;
    end
    else
    begin
      UrlPage.Caption := CustomMessage('UrlTitleStation');
      UrlPage.Description := CustomMessage('UrlSubStation');
      if Given <> '' then UrlPage.Values[0] := Given
      else if (UrlPage.Values[0] = '') or (UrlPage.Values[0] = DetectedUrl) then
      begin
        UrlPage.Values[0] := ExistingStationUrl;
        if UrlPage.Values[0] = '' then UrlPage.Values[0] := 'http://';
      end;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  U, Why: String;
begin
  Result := True;
  if WizardSilent then Exit;
  if CurPageID = UrlPage.ID then
  begin
    U := StripSlash(UrlPage.Values[0]);
    if not IsValidUrl(U) then
    begin
      MsgBox(CustomMessage('UrlBad'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if IsServer then
    begin
      if IsLoopback(U) then
        Result := MsgBox(CustomMessage('UrlLoopback'), mbConfirmation, MB_YESNO) = IDYES;
    end
    else
    begin
      Why := TestServer(U);
      if Why <> '' then
        Result := MsgBox(FmtMessage(CustomMessage('UrlUnreachable'), [Why]), mbConfirmation, MB_YESNO) = IDYES;
    end;
  end;
end;

function ChosenUrl: String;
begin
  Result := StripSlash(ExpandConstant('{param:SERVERURL|}'));
  if (Result = '') and (not WizardSilent) then Result := StripSlash(UrlPage.Values[0]);
  if (Result <> '') and (not IsValidUrl(Result)) then Result := '';
end;

{ Visual C++ 2015-2022 x64 runtime 14.40 or newer }
function NeedsVCRedist: Boolean;
var
  Installed, Major, Minor: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1) and
     RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Major', Major) and
     RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Minor', Minor) then
    Result := (Major < 14) or ((Major = 14) and (Minor < 40));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  { an upgrade replaces files that the running services keep open }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop TruckGate /y', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop TruckGateDB /y', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure SetupServer;
var
  Code: Integer;
  Url, LogFile: String;
begin
  LogFile := ExpandConstant('{commonappdata}\TruckGate\logs\install.log');
  if NeedsVCRedist then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('StatusRuntime');
    Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('vc_redist exit code ' + IntToStr(Code));
    if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
    begin
      ServerFailed := True;
      SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(Code)]), mbCriticalError, MB_OK, IDOK);
      Exit;
    end;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('StatusServer');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  Url := ChosenUrl;
  if Url = '' then Url := 'auto';
  if not Exec(PowerShellExe, '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\install-server.ps1') +
              '" -AppDir "' + ExpandConstant('{app}') + '" -ServerUrl "' + Url + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  WizardForm.ProgressGauge.Style := npbstNormal;
  Log('install-server.ps1 exit code ' + IntToStr(Code));
  if Code <> 0 then
  begin
    ServerFailed := True;
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServerFailed'), [LogFile]), mbCriticalError, MB_OK, IDOK);
  end
  else
    FinalUrl := DetectUrl;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Url: String;
begin
  if CurStep = ssPostInstall then
  begin
    if IsServer then
    begin
      SaveStringToFile(ExpandConstant('{app}\gate-station.json'), '{ "url": "http://localhost:5080" }', False);
      SetupServer;
    end
    else
    begin
      Url := ChosenUrl;
      if Url <> '' then
        SaveStringToFile(ExpandConstant('{app}\gate-station.json'), '{ "url": "' + Url + '" }', False)
      else if not FileExists(ExpandConstant('{app}\gate-station.json')) then
        SaveStringToFile(ExpandConstant('{app}\gate-station.json'), '{ "url": "" }', False);
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoTypeInfo + NewLine + NewLine + MemoComponentsInfo + NewLine + NewLine +
            CustomMessage('UrlLabel') + NewLine + Space + StripSlash(UrlPage.Values[0]);
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ServerFailed then Result := 10 else Result := 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\TruckGate');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox(FmtMessage(CustomMessage('KeepData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
