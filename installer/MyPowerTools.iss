#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef MyReleaseChannel
  #define MyReleaseChannel "stable"
#endif
#ifndef MyRepositoryUrl
  #define MyRepositoryUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools"
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
; Shares the AppId and install directory with the web installer: never run two at once.
SetupMutex=MyPowerToolsSetupMutex,Global\MyPowerToolsSetupMutex
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "autostart"; Description: "Start MyPowerTools Runner after sign-in"; GroupDescription: "Additional options:"; Flags: unchecked

[InstallDelete]
Type: files; Name: "{app}\dev-update.manifest.json"

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
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -StartAfterInstall"; StatusMsg: "Installing and starting SmartBird Thermostat..."; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -SettingsFile ""{localappdata}\MyPowerTools\SmartBird\settings.json"""; StatusMsg: "Registering SmartBird Energy Server..."; Flags: runhidden waituntilterminated
Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; Description: "Launch MyPowerTools"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; RunOnceId: "RemoveMyPowerToolsUserServices"; Flags: runhidden waituntilterminated
Filename: "{app}\Shell\MyPowerTools.Shell.Avalonia.exe"; Parameters: "--doubao-runtime stop --doubao-runtime-root ""{app}\Runtimes\Doubao"" --doubao-data-root ""{localappdata}\MyPowerTools\Doubao"""; RunOnceId: "StopDoubaoComputerUse"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdEnergyServerTask"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdThermostatTask"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MyPowerTools\Doubao"
Type: filesandordirs; Name: "{app}"

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

  { Python reads pyvenv.cfg as UTF-8; an ASCII write breaks a non-ASCII user profile path. }
  if not SaveStringsToUTF8FileWithoutBOM(ConfigPath, Lines, False) then
    RaiseException('Unable to write Doubao virtual environment configuration: ' + ConfigPath);
end;

function JsonEscape(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure SeedOtaState;
var
  OtaDir: String;
  ManifestSource: String;
  ManifestTarget: String;
  PublicKeySource: String;
  ReleaseText: String;
  ReleaseLines: TArrayOfString;
begin
  SetArrayLength(ReleaseLines, 1);
  OtaDir := ExpandConstant('{localappdata}\MyPowerTools\ota-state');
  if not ForceDirectories(OtaDir) then
    RaiseException('Unable to create the OTA state directory: ' + OtaDir);

  ManifestSource := ExpandConstant('{app}\MyPowerTools-win-x64.manifest.json');
  ManifestTarget := OtaDir + '\installed-files.manifest.json';
  if not FileCopy(ManifestSource, ManifestTarget, False) then
    RaiseException('Unable to seed the OTA file manifest from ' + ManifestSource);

  PublicKeySource := ExpandConstant('{app}\ota-signing-public-key.txt');
  if FileExists(PublicKeySource) then
    FileCopy(PublicKeySource, OtaDir + '\ota-signing-public-key.txt', False);

  ReleaseText := '{' + #13#10 +
    '  "schemaVersion": 1,' + #13#10 +
    '  "product": "MyPowerTools",' + #13#10 +
    '  "version": "{#MyAppVersion}",' + #13#10 +
    '  "channel": "{#MyReleaseChannel}",' + #13#10 +
    '  "installedAt": "' + GetDateTimeString('yyyy-mm-dd', '-', ':') + 'T' +
      GetDateTimeString('hh:nn:ss', '-', ':') + '",' + #13#10 +
    '  "installDir": "' + JsonEscape(ExpandConstant('{app}')) + '",' + #13#10 +
    '  "dataRoot": "' + JsonEscape(ExpandConstant('{localappdata}\MyPowerTools')) + '",' + #13#10 +
    '  "repository": "{#MyRepositoryUrl}",' + #13#10 +
    '  "manifestPath": "installed-files.manifest.json",' + #13#10 +
    '  "manifestSha256": "' + Lowercase(GetSHA256OfFile(ManifestTarget)) + '",' + #13#10 +
    '  "packageKind": "full",' + #13#10 +
    '  "distributionMode": "full"' + #13#10 + '}';
  { UTF-8, not the ANSI code page: the paths above can contain a non-ASCII user name. }
  ReleaseLines[0] := ReleaseText;
  if not SaveStringsToUTF8FileWithoutBOM(OtaDir + '\installed-release.json', ReleaseLines, False) then
    RaiseException('Unable to write installed-release.json.');
end;

{ The full installer overwrites files in place, so every program started from the install
  directory has to be gone first - found by path (service units, python/adb from Runtimes,
  helpers added later), not by a fixed name list. }
procedure StopProcessesUnderRootWithPowerShell(const Prefix: String);
var
  Lines: TArrayOfString;
  Quoted, ScriptPath: String;
  ResultCode: Integer;
begin
  Quoted := Prefix;
  StringChangeEx(Quoted, '''', '''''', True);
  SetArrayLength(Lines, 7);
  Lines[0] := '$root = ''' + Quoted + '''';
  Lines[1] := 'foreach ($process in Get-Process -ErrorAction SilentlyContinue) {';
  Lines[2] := '    $path = $null; try { $path = $process.MainModule.FileName } catch {}';
  Lines[3] := '    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and -not ([IO.Path]::GetFileName($path) -like ''unins*'')) {';
  Lines[4] := '        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue';
  Lines[5] := '    }';
  Lines[6] := '}';
  ScriptPath := ExpandConstant('{tmp}\stop-under-root.ps1');
  if not SaveStringsToUTF8File(ScriptPath, Lines, False) then exit;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('PowerShell path-based stop finished with exit code ' + IntToStr(ResultCode));
end;

procedure StopProcessesUnderRoot(const Root: String);
var
  Locator, Service, Items, Item: Variant;
  Index, Count, ResultCode: Integer;
  Prefix, Path, ProcessIds: String;
begin
  Prefix := AddBackslash(RemoveBackslashUnlessRoot(Root));
  ProcessIds := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process');
    Count := Items.Count;
    for Index := 0 to Count - 1 do begin
      Item := Items.ItemIndex(Index);
      if VarIsNull(Item.ExecutablePath) or VarIsEmpty(Item.ExecutablePath) then
        continue;
      Path := Item.ExecutablePath;
      if (Length(Path) > Length(Prefix)) and
         (CompareText(Copy(Path, 1, Length(Prefix)), Prefix) = 0) and
         (CompareText(Copy(ExtractFileName(Path), 1, 5), 'unins') <> 0) then
        ProcessIds := ProcessIds + ' /PID ' + IntToStr(Item.ProcessId);
    end;
  except
    Log('Process enumeration through WMI failed: ' + GetExceptionMessage);
    StopProcessesUnderRootWithPowerShell(Prefix);
  end;
  if ProcessIds <> '' then
    if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T' + ProcessIds, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
      Log('Path-based taskkill exit code ' + IntToStr(ResultCode));
end;

procedure StopInstalledProduct;
var
  ResultCode: Integer;
  CliPath: String;
begin
  CliPath := ExpandConstant('{app}\Cli\MyPowerTools.Cli.exe');
  if FileExists(CliPath) then
    Exec(CliPath, 'service quiesce', ExtractFileDir(CliPath), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "\MyPowerTools WinSpace Shift"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/F /T /IM "MyPowerTools.ServiceManager.exe" /IM "MyPowerTools.Runner.exe" /IM "MyPowerTools.Shell.Avalonia.exe"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  StopProcessesUnderRoot(ExpandConstant('{app}'));
  Sleep(500);
end;

{ Uninstall never fails on a busy file: what is left is removed by a one-time command at the
  next sign-in (a per-user install cannot use the administrator-only reboot delete list). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
begin
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if CurUninstallStep = usUninstall then
    StopProcessesUnderRoot(AppDir)
  else if (CurUninstallStep = usPostUninstall) and DirExists(AppDir) then begin
    DelTree(AppDir, True, True, True);
    if DirExists(AppDir) then
      RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\RunOnce', 'MyPowerToolsCleanup',
        '"' + ExpandConstant('{sys}\cmd.exe') + '" /d /c rd /s /q "' + AppDir + '"');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopInstalledProduct;
  if CurStep = ssPostInstall then
  begin
    RewriteDoubaoVenvConfig('');
    SeedOtaState;
  end;
end;
