#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif

[Setup]
AppId={{6A1532EA-A2F5-4C1F-AB7C-B119C9C3B54B}
AppName=MyPowerTools
AppVersion={#MyAppVersion}
AppPublisher=MyPowerTools
DefaultDirName={localappdata}\Programs\MyPowerTools
DefaultGroupName=MyPowerTools
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=MyPowerTools-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\assets\MyPowerTools.ico
UninstallDisplayIcon={app}\MyPowerTools.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "autostart"; Description: "Start MyPowerTools Runner after sign-in"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "..\artifacts\release\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"
Name: "{autodesktop}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyPowerTools"; ValueData: """{app}\Runner\MyPowerTools.Runner.exe"" --modules ""{app}\modules"" --data-root ""{localappdata}\MyPowerTools"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "Stopping existing MyPowerTools user services..."; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Install -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "Installing MyPowerTools user services..."; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -StartAfterInstall"; StatusMsg: "Installing and starting SmartBird Thermostat..."; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -SettingsFile ""{localappdata}\MyPowerTools\SmartBird\settings.json"""; StatusMsg: "Registering SmartBird Energy Server..."; Flags: runhidden waituntilterminated
Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; Description: "Launch MyPowerTools"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; RunOnceId: "RemoveMyPowerToolsUserServices"; Flags: runhidden waituntilterminated
Filename: "{app}\Shell\MyPowerTools.Shell.Avalonia.exe"; Parameters: "--doubao-runtime stop --doubao-runtime-root ""{app}\Runtimes\Doubao"" --doubao-data-root ""{localappdata}\MyPowerTools\Doubao"""; RunOnceId: "StopDoubaoComputerUse"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdEnergyServerTask"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdThermostatTask"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MyPowerTools\Doubao"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExpectedDir: String;
begin
  ExpectedDir := ExpandConstant('{localappdata}\Programs\MyPowerTools');
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), RemoveBackslashUnlessRoot(ExpectedDir)) <> 0 then
    Result := 'MyPowerTools must be installed for the current user at ' + ExpectedDir + '.'
  else
    Result := '';
end;

procedure RewriteDoubaoVenvConfig(const ServiceName: String);
var
  ConfigPath: String;
  PythonHome: String;
  Lines: TArrayOfString;
  LineIndex: Integer;
  HomeFound: Boolean;
begin
  ConfigPath := ExpandConstant('{app}\Runtimes\Doubao\' + ServiceName + '\.venv\pyvenv.cfg');
  PythonHome := ExpandConstant('{app}\Runtimes\Python312');

  if not LoadStringsFromFile(ConfigPath, Lines) then
    RaiseException('Unable to read Doubao virtual environment configuration: ' + ConfigPath);

  HomeFound := False;
  for LineIndex := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos('home = ', Lowercase(Lines[LineIndex])) = 1 then
    begin
      Lines[LineIndex] := 'home = ' + PythonHome;
      HomeFound := True;
      Break;
    end;
  end;

  if not HomeFound then
  begin
    SetArrayLength(Lines, GetArrayLength(Lines) + 1);
    Lines[GetArrayLength(Lines) - 1] := 'home = ' + PythonHome;
  end;

  if not SaveStringsToFile(ConfigPath, Lines, False) then
    RaiseException('Unable to write Doubao virtual environment configuration: ' + ConfigPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RewriteDoubaoVenvConfig('');
  end;
end;
