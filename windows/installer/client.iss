#define AppName "RentalSystem Client"
#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define Publisher "RentalSystem"
#define ExeName "RentalClient.Windows.exe"

[Setup]
AppId={{5F1F1D58-3268-4D75-AFD7-28F9E7A50E24}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\RentalSystem\Client
DefaultGroupName=RentalSystem
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=RentalSystem-Client-Setup
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

[Files]
Source: "..\artifacts\publish\client\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\client-install-autostart.cmd"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RentalSystem\RentalClient"; Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"

[Run]
; Open inbound UDP 1884 so this PC can receive the server's LAN discovery
; broadcast (broker.host="auto"). profile=any covers Public-classified shop
; networks, which silently drop Private/Domain-only rules. Delete-then-add
; keeps it idempotent across upgrades.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Client Discovery"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RentalSystem Client Discovery"" dir=in action=allow protocol=UDP localport=1884 profile=any"; Flags: runhidden waituntilterminated
Filename: "{app}\scripts\client-install-autostart.cmd"; Parameters: """{app}\{#ExeName}"""; Flags: runhidden waituntilterminated
; The autostart task above fires AtLogOn, so it won't start the client during this
; session if the operator is already signed in. This Finish-page checkbox launches it
; now; uncheck it to start the client on the next logon/reboot instead.
Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Description: "Launch RentalClient.Windows now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\scripts\client-install-autostart.cmd"; Parameters: "/uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalClientTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RentalSystem Client Discovery"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteRentalClientFwRule"

[Code]
var
  BrokerPage: TInputQueryWizardPage;
  ShellPage: TInputOptionWizardPage;

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
    'Server connection',
    'How should this PC find the shop server?',
    'Leave the first box as "auto" to detect the server automatically over the LAN (recommended).' + #13#10 +
    'Only type the server IP if auto-detect is blocked on your network. The client keeps stationId=auto so each PC uses its Windows hostname.');
  BrokerPage.Add('Server (keep "auto" to detect automatically, or enter the server IP):', False);
  BrokerPage.Add('Broker username (leave blank if none):', False);
  BrokerPage.Add('Broker password (leave blank if none):', False);
  BrokerPage.Values[0] := 'auto';
  BrokerPage.Values[1] := '';
  BrokerPage.Values[2] := '';

  ShellPage := CreateInputOptionPage(
    BrokerPage.ID,
    'Kiosk shell',
    'Should this image replace Explorer?',
    'Leave this off for ordinary installs. Turn it on only while baking the iCafe8 master image, after you have verified the client starts correctly.',
    False,
    False);
  ShellPage.Add('Replace Explorer with RentalClient.Windows for this Windows user');
  ShellPage.Values[0] := False;
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

function BuildClientConfigJson(): String;
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
    '  "stationId": "auto",' + #13#10 +
    '  "deviceType": "pc",' + #13#10 +
    '  "appVersion": "1.2.0",' + #13#10 +
    '  "debug": {' + #13#10 +
    '    "disableLockOverlay": false' + #13#10 +
    '  },' + #13#10 +
    '  "connection": {' + #13#10 +
    '    "reconnectIntervalSec": 5,' + #13#10 +
    '    "offlineUntrustedAfterSec": 120' + #13#10 +
    '  },' + #13#10 +
    '  "countdownWarning": {' + #13#10 +
    '    "enabled": true,' + #13#10 +
    '    "firstWarningSec": 60,' + #13#10 +
    '    "finalCountdownStartSec": 10' + #13#10 +
    '  },' + #13#10 +
    '  "broker": {' + #13#10 +
    '    "host": "' + BrokerHost + '",' + #13#10 +
    '    "port": 1883,' + #13#10 +
    '    "username": "' + BrokerUser + '",' + #13#10 +
    '    "password": "' + BrokerPass + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;
end;

procedure WriteClientConfig();
begin
  if not SaveStringToFile(ExpandConstant('{app}\appsettings.json'), BuildClientConfigJson(), False) then
  begin
    RaiseException('Failed to write installed appsettings.json.');
  end;
end;

procedure ApplyShellReplacement();
begin
  if ShellPage.Values[0] then
  begin
    RegWriteStringValue(
      HKCU,
      'Software\Microsoft\Windows NT\CurrentVersion\Winlogon',
      'Shell',
      ExpandConstant('{app}\{#ExeName}'));
  end;
end;

procedure ApplyLockdownPolicies();
begin
  RegWriteDWordValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableTaskMgr',
    1);
  RegWriteDWordValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableLockWorkstation',
    1);
  RegWriteDWordValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableChangePassword',
    1);
  RegWriteDWordValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer',
    'NoLogoff',
    1);

  // Compatibility copies for environments that read the S30-documented
  // machine policy path. The ADMX-backed Ctrl+Alt+Del policies above are HKCU.
  RegWriteDWordValue(
    HKLM,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableTaskMgr',
    1);
  RegWriteDWordValue(
    HKLM,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableLockWorkstation',
    1);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteClientConfig();
    ApplyLockdownPolicies();
    ApplyShellReplacement();
  end;
end;

procedure RemoveLockdownPolicies();
begin
  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableTaskMgr');
  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableLockWorkstation');
  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableChangePassword');
  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer',
    'NoLogoff');
  RegDeleteValue(
    HKLM,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableTaskMgr');
  RegDeleteValue(
    HKLM,
    'Software\Microsoft\Windows\CurrentVersion\Policies\System',
    'DisableLockWorkstation');
end;

function InitializeUninstall(): Boolean;
var
  ShellValue: String;
begin
  Result := True;
  RemoveLockdownPolicies();

  if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows NT\CurrentVersion\Winlogon',
      'Shell',
      ShellValue) then
  begin
    if CompareText(ShellValue, ExpandConstant('{app}\{#ExeName}')) = 0 then
    begin
      RegDeleteValue(
        HKCU,
        'Software\Microsoft\Windows NT\CurrentVersion\Winlogon',
        'Shell');
    end;
  end;
end;
