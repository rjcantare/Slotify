#define AppName "RentalSystem Server"
#ifndef AppVersion
  #define AppVersion "1.2.2"
#endif
#define Publisher "RentalSystem"
#define ExeName "RentalServer.exe"

[Setup]
AppId={{7A0A53A0-B94C-46F1-A47E-0A69DE72C713}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\RentalSystem\Server
DefaultGroupName=RentalSystem
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=RentalSystem-Server-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#ExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Icons\Favicon.ico
WizardImageFile=..\Icons\transparentlogo.png
WizardSmallImageFile=..\Icons\TransparentFavicon.png

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Dirs]
Name: "{commonappdata}\RentalSystem"; Permissions: users-modify

[Files]
; Copy the WHOLE publish folder, not just the EXE: WPF single-file leaves its
; native DLLs (wpfgfx_cor3, vcruntime140_cor3, PresentationNative, e_sqlite3,
; etc.) loose next to the EXE. Shipping only the EXE crashes on a clean machine
; with DllNotFoundException (works only where .NET Desktop is already installed).
Source: "..\artifacts\publish\server\*"; DestDir: "{app}"; Excludes: "*.bak,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\server-install-autostart.cmd"; DestDir: "{app}\scripts"; Flags: ignoreversion
; Bundled broker prerequisites (populate via installer\fetch-redist.ps1 before building).
Source: "redist\mosquitto-install-windows-x64.exe"; DestDir: "{app}\redist"; Flags: ignoreversion
Source: "redist\vc_redist.x64.exe"; DestDir: "{app}\redist"; Flags: ignoreversion
Source: "redist\mosquitto.conf"; DestDir: "{app}\redist"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RentalSystem\RentalServer"; Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\RentalServer"; Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"

[Run]
; Open the broker (1883), OTA update server (8081), and the mDNS responder
; (UDP 5353, so Wi-Fi devices can discover the broker) inbound. profile=any
; covers Public-classified shop networks (Private/Domain-only rules are
; silently dropped there). Delete-then-add keeps these idempotent on upgrade.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Broker (MQTT 1883)"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RentalSystem Broker (MQTT 1883)"" dir=in action=allow protocol=TCP localport=1883 profile=any"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Update Server (HTTP 8081)"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RentalSystem Update Server (HTTP 8081)"" dir=in action=allow protocol=TCP localport=8081 profile=any"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem mDNS (UDP 5353)"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RentalSystem mDNS (UDP 5353)"" dir=in action=allow protocol=UDP localport=5353 profile=any"; Flags: runhidden waituntilterminated
Filename: "{app}\scripts\server-install-autostart.cmd"; Parameters: """{app}\{#ExeName}"""; Flags: runhidden waituntilterminated
Filename: "{app}\{#ExeName}"; Description: "Launch RentalServer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""RentalServer"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalServerTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Broker (MQTT 1883)"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalBrokerFwRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Update Server (HTTP 8081)"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalUpdateFwRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem mDNS (UDP 5353)"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalMdnsFwRule"

[Code]
var
  BrokerPage: TInputQueryWizardPage;

function IsValidBrokerHost(Value: string): Boolean;
var
  I: Integer;
  C: String;
begin
  Value := Trim(Value);
  Result := Length(Value) > 0;

  for I := 1 to Length(Value) do
  begin
    C := Copy(Value, I, 1);
    if Pos(C, 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_') = 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure InitializeWizard();
begin
  BrokerPage := CreateInputQueryPage(
    wpSelectDir,
    'Broker settings',
    'Where is the shop MQTT broker?',
    'Enter the broker address. Leave username/password blank if the broker does not require authentication.');
  BrokerPage.Add('Broker host or IP:', False);
  BrokerPage.Add('Broker username (leave blank if none):', False);
  BrokerPage.Add('Broker password (leave blank if none):', False);
  BrokerPage.Values[0] := '127.0.0.1';
  BrokerPage.Values[1] := '';
  BrokerPage.Values[2] := '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = BrokerPage.ID then
  begin
    if not IsValidBrokerHost(BrokerPage.Values[0]) then
    begin
      MsgBox('Enter a broker host or IP using only letters, numbers, dot, dash, or underscore.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function BuildServerConfigJson(): String;
var
  BrokerHost: String;
  BrokerUser: String;
  BrokerPass: String;
begin
  BrokerHost := Trim(BrokerPage.Values[0]);
  BrokerUser := Trim(BrokerPage.Values[1]);
  BrokerPass := Trim(BrokerPage.Values[2]);
  Result :=
    '{' + #13#10 +
    '  "broker": {' + #13#10 +
    '    "host": "' + BrokerHost + '",' + #13#10 +
    '    "port": 1883,' + #13#10 +
    '    "username": "' + BrokerUser + '",' + #13#10 +
    '    "password": "' + BrokerPass + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;
end;

function MosquittoServicePresent(): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(ExpandConstant('{sys}\sc.exe'), 'query mosquitto', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

// Detect-and-install Mosquitto so a fresh server VM needs no manual broker
// setup. Mosquitto 2.1.x is MSVC-built (needs the VC++ runtime) and defaults
// to localhost-only + anonymous-denied, so we also overwrite mosquitto.conf
// with the LAN + anonymous config and restart the service.
procedure EnsureMosquitto();
var
  ResultCode: Integer;
  VcRedist: String;
  MosqInstaller: String;
  ConfSrc: String;
  MosqDir: String;
begin
  if not MosquittoServicePresent() then
  begin
    // VC++ runtime first â€” the broker will not start without it.
    VcRedist := ExpandConstant('{app}\redist\vc_redist.x64.exe');
    if FileExists(VcRedist) then
      Exec(VcRedist, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Silent Mosquitto install (NSIS /S) -> {pf}\mosquitto + "mosquitto" service.
    MosqInstaller := ExpandConstant('{app}\redist\mosquitto-install-windows-x64.exe');
    if FileExists(MosqInstaller) then
      Exec(MosqInstaller, '/S', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  // Overwrite the broker config with the LAN + anonymous listener and restart
  // so shop clients on the LAN are accepted (the 2.0+ default refuses them).
  MosqDir := ExpandConstant('{pf}\mosquitto');
  ConfSrc := ExpandConstant('{app}\redist\mosquitto.conf');
  if FileExists(ConfSrc) and DirExists(MosqDir) then
  begin
    CopyFile(ConfSrc, MosqDir + '\mosquitto.conf', False);
    Exec(ExpandConstant('{sys}\sc.exe'), 'config mosquitto start= auto', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\net.exe'), 'stop mosquitto', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\net.exe'), 'start mosquitto', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if not MosquittoServicePresent() then
  begin
    MsgBox(
      'The Mosquitto MQTT broker could not be installed automatically.' + #13#10 + #13#10 +
      'Install it manually from {app}\redist\mosquitto-install-windows-x64.exe and set ' +
      'listener 0.0.0.0:1883 / allow_anonymous true before opening the shop.',
      mbInformation,
      MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not SaveStringToFile(ExpandConstant('{app}\appsettings.json'), BuildServerConfigJson(), False) then
    begin
      RaiseException('Failed to write installed appsettings.json.');
    end;

    EnsureMosquitto();
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if MsgBox(
    'Preserve the RentalSystem database and event log in ProgramData?' + #13#10 + #13#10 +
    'Choose Yes for normal uninstall or upgrade.' + #13#10 +
    'Choose No only when you intentionally want to wipe rental history from this machine.',
    mbConfirmation,
    MB_YESNO) = IDNO then
  begin
    DelTree(ExpandConstant('{commonappdata}\RentalSystem'), True, True, True);
  end;
end;
