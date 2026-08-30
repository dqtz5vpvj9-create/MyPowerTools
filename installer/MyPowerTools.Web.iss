#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef MyReleaseChannel
  #define MyReleaseChannel "stable"
#endif
#ifndef MyDownloadBaseUrl
  #if MyReleaseChannel == "nightly"
    #define MyDownloadBaseUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/nightly-" + MyAppVersion + "-" + GetDateTimeString("yyyymmdd", "", "")
  #else
    #define MyDownloadBaseUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/v" + MyAppVersion
  #endif
#endif

#include "..\artifacts\release\web-installer-components.iss"

[Setup]
#ifdef MyInstallerTestMode
AppId={{55A3DD2D-31E1-44EC-89C3-9C75839E1B30}
AppName=MyPowerTools Web Setup Test
#else
AppId={{6A1532EA-A2F5-4C1F-AB7C-B119C9C3B54B}
AppName=MyPowerTools
#endif
AppVerName=MyPowerTools {#MyAppVersion}
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
OutputBaseFilename=MyPowerTools-Web-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\assets\MyPowerTools.ico
UninstallDisplayIcon={app}\MyPowerTools.exe
Compression=lzma2/max
SolidCompression=yes
ArchiveExtraction=full
WizardStyle=modern dynamic
DisableWelcomePage=no
DisableReadyPage=no
CloseApplications=yes
CloseApplicationsFilter=MyPowerTools.exe,MyPowerTools.Runner.exe,MyPowerTools.Shell.Avalonia.exe,MyPowerTools.ServiceManager.exe
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked
Name: "autostart"; Description: "Start MyPowerTools Runner after sign-in"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{tmp}\{#WebCoreAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion
Source: "{tmp}\{#WebDotNetAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedDotNetDownload
Source: "{tmp}\{#WebPythonAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedPythonDownload
Source: "{tmp}\{#WebSmartBirdAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion
Source: "{tmp}\{#WebDoubaoAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion
Source: "{tmp}\{#WebAdbAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedAdbDownload

[Icons]
Name: "{autoprograms}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Check: ShouldRunPostInstall
Name: "{autodesktop}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Tasks: desktopicon; Check: ShouldRunPostInstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyPowerTools"; ValueData: """{app}\Runner\MyPowerTools.Runner.exe"" --modules ""{app}\modules"" --data-root ""{localappdata}\MyPowerTools"""; Flags: uninsdeletevalue; Tasks: autostart; Check: ShouldRunPostInstall

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "Stopping existing MyPowerTools user services..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Install -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "Installing MyPowerTools user services..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -StartAfterInstall"; StatusMsg: "Installing and starting SmartBird Thermostat..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -SettingsFile ""{localappdata}\MyPowerTools\SmartBird\settings.json"""; StatusMsg: "Registering SmartBird Energy Server..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; Description: "Launch MyPowerTools"; Flags: nowait postinstall skipifsilent; Check: ShouldRunPostInstall

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; RunOnceId: "RemoveMyPowerToolsUserServices"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{app}\Shell\MyPowerTools.Shell.Avalonia.exe"; Parameters: "--doubao-runtime stop --doubao-runtime-root ""{app}\Runtimes\Doubao"" --doubao-data-root ""{localappdata}\MyPowerTools\Doubao"""; RunOnceId: "StopDoubaoComputerUse"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdEnergyServerTask"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdThermostatTask"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MyPowerTools\Doubao"; Check: ShouldRunPostInstall

[Code]
var
  DownloadPage: TDownloadWizardPage;
  NeedPrivateDotNet: Boolean;
  NeedPrivatePython: Boolean;
  NeedPrivateAdb: Boolean;
  DotNetRuntimeSource: String;
  PythonRuntimeSource: String;
  AdbRuntimeSource: String;
  LegacyDotNetRootCleared: Boolean;
  QueuedDownloadCount: Integer;

function ShouldRunPostInstall: Boolean;
begin
#ifdef MyInstallerTestMode
  Result := False;
#else
  Result := True;
#endif
end;

function HasVersionDirectory(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + '10.0.*', FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          Result := True;
          exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasDotNetRuntimeAtRoot(const Root: String): Boolean;
begin
  Result :=
    FileExists(AddBackslash(Root) + 'host\fxr\10.0.0\hostfxr.dll') or
    HasVersionDirectory(AddBackslash(Root) + 'host\fxr');
  Result := Result and HasVersionDirectory(AddBackslash(Root) + 'shared\Microsoft.NETCore.App');
  Result := Result and HasVersionDirectory(AddBackslash(Root) + 'shared\Microsoft.AspNetCore.App');
  Result := Result and HasVersionDirectory(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App');
end;

function HasCompatibleGlobalDotNet: Boolean;
var
  DotNetRoot: String;
begin
  DotNetRoot := '';
  RegQueryStringValue(HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', DotNetRoot);
  if DotNetRoot = '' then
    DotNetRoot := ExpandConstant('{pf64}\dotnet');
  Result := HasDotNetRuntimeAtRoot(DotNetRoot);
end;

function HasPrivatePython: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Runtimes\Python312\python.exe')) and
    FileExists(ExpandConstant('{app}\Runtimes\Python312\python312.dll')) and
    FileExists(ExpandConstant('{app}\Runtimes\Python312\DLLs\_ssl.pyd'));
end;

function HasCompatibleAdb: Boolean;
var
  Root: String;
begin
  Root := GetEnv('ANDROID_SDK_ROOT');
  if (Root <> '') and FileExists(AddBackslash(Root) + 'platform-tools\adb.exe') then begin
    Result := True;
    exit;
  end;
  Root := GetEnv('ANDROID_HOME');
  if (Root <> '') and FileExists(AddBackslash(Root) + 'platform-tools\adb.exe') then begin
    Result := True;
    exit;
  end;
  if FileExists(ExpandConstant('{localappdata}\Android\Sdk\platform-tools\adb.exe')) then begin
    Result := True;
    exit;
  end;
  Result := FileSearch('adb.exe', GetEnv('PATH')) <> '';
end;

function NeedDotNetDownload: Boolean;
begin
  Result := NeedPrivateDotNet;
end;

function NeedPythonDownload: Boolean;
begin
  Result := NeedPrivatePython;
end;

function NeedAdbDownload: Boolean;
begin
  Result := NeedPrivateAdb;
end;

function CachePath(const Asset: String): String;
begin
  Result := ExpandConstant('{localappdata}\MyPowerTools\installer-cache\{#MyAppVersion}\') + Asset;
end;

function PrepareCachedAsset(const Asset, Sha256: String): Boolean;
var
  SourcePath: String;
  TargetPath: String;
begin
  SourcePath := CachePath(Asset);
  TargetPath := ExpandConstant('{tmp}\') + Asset;
  Log('Checking installer cache: ' + SourcePath);
  if FileExists(SourcePath) then
    Log('Installer cache SHA-256: ' + GetSHA256OfFile(SourcePath));
  Result := FileExists(SourcePath) and
    (CompareText(GetSHA256OfFile(SourcePath), Sha256) = 0);
  if Result then begin
    Log('Reusing verified installer cache: ' + SourcePath);
    Result := FileCopy(SourcePath, TargetPath, False);
  end;
end;

procedure QueueAsset(const Asset, Sha256: String);
begin
  if not PrepareCachedAsset(Asset, Sha256) then begin
    DownloadPage.Add('{#MyDownloadBaseUrl}/' + Asset, Asset, Sha256);
    QueuedDownloadCount := QueuedDownloadCount + 1;
  end;
end;

procedure PreserveAssetInCache(const Asset, Sha256: String);
var
  SourcePath: String;
  TargetPath: String;
begin
  SourcePath := ExpandConstant('{tmp}\') + Asset;
  if FileExists(SourcePath) and (CompareText(GetSHA256OfFile(SourcePath), Sha256) = 0) then begin
    ForceDirectories(ExtractFileDir(CachePath(Asset)));
    TargetPath := CachePath(Asset);
    FileCopy(SourcePath, TargetPath, False);
  end;
end;

procedure DetectRuntimePlan;
begin
  if HasDotNetRuntimeAtRoot(ExpandConstant('{app}\Runtime\dotnet')) then begin
    NeedPrivateDotNet := False;
    DotNetRuntimeSource := 'private-existing';
  end else if HasCompatibleGlobalDotNet then begin
    NeedPrivateDotNet := False;
    DotNetRuntimeSource := 'global';
  end else begin
    NeedPrivateDotNet := True;
    DotNetRuntimeSource := 'private-download';
  end;
  NeedPrivatePython := not HasPrivatePython;
  if NeedPrivatePython then PythonRuntimeSource := 'private-download'
  else PythonRuntimeSource := 'private-existing';
  if FileExists(ExpandConstant('{app}\Tools\AndroidPlatformTools\adb.exe')) then begin
    NeedPrivateAdb := False;
    AdbRuntimeSource := 'private-existing';
  end else if HasCompatibleAdb then begin
    NeedPrivateAdb := False;
    AdbRuntimeSource := 'external';
  end else begin
    NeedPrivateAdb := True;
    AdbRuntimeSource := 'private-download';
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing),
    'Downloading and verifying MyPowerTools components...', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function DownloadRequiredAssets: Boolean;
var
  ErrorText: String;
begin
  DownloadPage.Clear;
  QueuedDownloadCount := 0;
  QueueAsset('{#WebCoreAsset}', '{#WebCoreSha256}');
  if NeedPrivateDotNet then QueueAsset('{#WebDotNetAsset}', '{#WebDotNetSha256}');
  if NeedPrivatePython then QueueAsset('{#WebPythonAsset}', '{#WebPythonSha256}');
  QueueAsset('{#WebSmartBirdAsset}', '{#WebSmartBirdSha256}');
  QueueAsset('{#WebDoubaoAsset}', '{#WebDoubaoSha256}');
  if NeedPrivateAdb then QueueAsset('{#WebAdbAsset}', '{#WebAdbSha256}');

  if QueuedDownloadCount > 0 then begin
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('Component download aborted by the user.')
        else begin
          ErrorText := Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage]);
          SuppressibleMsgBox(AddPeriod(ErrorText), mbCriticalError, MB_OK, IDOK);
        end;
        Result := False;
        exit;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;

  PreserveAssetInCache('{#WebCoreAsset}', '{#WebCoreSha256}');
  if NeedPrivateDotNet then PreserveAssetInCache('{#WebDotNetAsset}', '{#WebDotNetSha256}');
  if NeedPrivatePython then PreserveAssetInCache('{#WebPythonAsset}', '{#WebPythonSha256}');
  PreserveAssetInCache('{#WebSmartBirdAsset}', '{#WebSmartBirdSha256}');
  PreserveAssetInCache('{#WebDoubaoAsset}', '{#WebDoubaoSha256}');
  if NeedPrivateAdb then PreserveAssetInCache('{#WebAdbAsset}', '{#WebAdbSha256}');
  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpReady then begin
    DetectRuntimePlan;
    Result := DownloadRequiredAssets;
  end else
    Result := True;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  DotNetPlan: String;
  PythonPlan: String;
  AdbPlan: String;
begin
  DetectRuntimePlan;
  if NeedPrivateDotNet then DotNetPlan := 'download private .NET 10 x64 runtime'
  else if DotNetRuntimeSource = 'private-existing' then DotNetPlan := 'reuse existing MyPowerTools .NET 10 runtime'
  else DotNetPlan := 'reuse registered global .NET 10 x64 runtime';
  if NeedPrivatePython then PythonPlan := 'download private Python 3.12 x64 runtime'
  else PythonPlan := 'reuse existing MyPowerTools Python 3.12 runtime';
  if NeedPrivateAdb then AdbPlan := 'download private Android Platform Tools'
  else AdbPlan := 'reuse compatible Android Platform Tools';

  Result := 'Install directory:' + NewLine + Space + ExpandConstant('{app}') +
    NewLine + NewLine + 'Runtime plan:' +
    NewLine + Space + '.NET: ' + DotNetPlan +
    NewLine + Space + 'Python: ' + PythonPlan +
    NewLine + Space + 'ADB: ' + AdbPlan +
    NewLine + Space + 'SmartBird dependencies: private locked component' +
    NewLine + Space + 'Doubao dependencies: private locked component';
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function IsLegacyDotNetRoot(const Value: String): Boolean;
var
  NormalizedValue: String;
  LegacyRoot: String;
begin
  NormalizedValue := RemoveBackslashUnlessRoot(ExpandConstant(Value));
  LegacyRoot := RemoveBackslashUnlessRoot(ExpandConstant('{app}\Runtime\dotnet'));
  Result := (CompareText(NormalizedValue, LegacyRoot) = 0) or
    (Pos(Lowercase(AddBackslash(LegacyRoot)), Lowercase(AddBackslash(NormalizedValue))) = 1);
end;

procedure ClearLegacyDotNetRoot;
var
  ExistingValue: String;
begin
  LegacyDotNetRootCleared := False;
  if RegQueryStringValue(HKCU, 'Environment', 'DOTNET_ROOT', ExistingValue) and
     IsLegacyDotNetRoot(ExistingValue) then begin
    LegacyDotNetRootCleared := RegDeleteValue(HKCU, 'Environment', 'DOTNET_ROOT');
    if LegacyDotNetRootCleared then
      Log('Cleared legacy MyPowerTools user DOTNET_ROOT: ' + ExistingValue);
  end;
end;

function JsonBoolean(Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

function JsonEscape(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteInstallManifest;
var
  RuntimeSource: String;
  PythonSource: String;
  AdbSource: String;
  ManifestText: String;
begin
  RuntimeSource := DotNetRuntimeSource;
  PythonSource := PythonRuntimeSource;
  AdbSource := AdbRuntimeSource;
  ManifestText := '{' + #13#10 +
    '  "product": "MyPowerTools",' + #13#10 +
    '  "version": "{#MyAppVersion}",' + #13#10 +
    '  "channel": "{#MyReleaseChannel}",' + #13#10 +
    '  "installDir": "' + JsonEscape(ExpandConstant('{app}')) + '",' + #13#10 +
    '  "dataRoot": "' + JsonEscape(ExpandConstant('{localappdata}\MyPowerTools')) + '",' + #13#10 +
    '  "distributionMode": "web",' + #13#10 +
    '  "runtimeSource": "' + RuntimeSource + '",' + #13#10 +
    '  "legacyDotNetRootMigration": { "cleared": ' + JsonBoolean(LegacyDotNetRootCleared) + ' },' + #13#10 +
    '  "runtimeComponents": {' + #13#10 +
    '    "dotnet": "' + RuntimeSource + '",' + #13#10 +
    '    "python": "' + PythonSource + '",' + #13#10 +
    '    "smartbird": "private-download",' + #13#10 +
    '    "doubao": "private-download",' + #13#10 +
    '    "adb": "' + AdbSource + '"' + #13#10 +
    '  }' + #13#10 + '}' + #13#10;
  if not SaveStringToFile(ExpandConstant('{app}\install.manifest.json'), ManifestText, False) then
    RaiseException('Unable to write install.manifest.json.');
end;

procedure RewriteDoubaoVenvConfig;
var
  ConfigPath: String;
  PythonHome: String;
  Lines: TArrayOfString;
  LineIndex: Integer;
  HomeFound: Boolean;
begin
  ConfigPath := ExpandConstant('{app}\Runtimes\Doubao\.venv\pyvenv.cfg');
  PythonHome := ExpandConstant('{app}\Runtimes\Python312');
  if not LoadStringsFromFile(ConfigPath, Lines) then exit;
  HomeFound := False;
  for LineIndex := 0 to GetArrayLength(Lines) - 1 do begin
    if Pos('home = ', Lowercase(Lines[LineIndex])) = 1 then begin
      Lines[LineIndex] := 'home = ' + PythonHome;
      HomeFound := True;
      Break;
    end;
  end;
  if not HomeFound then begin
    SetArrayLength(Lines, GetArrayLength(Lines) + 1);
    Lines[GetArrayLength(Lines) - 1] := 'home = ' + PythonHome;
  end;
  if not SaveStringsToFile(ConfigPath, Lines, False) then
    RaiseException('Unable to update Doubao Python runtime configuration.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    ClearLegacyDotNetRoot;
  if CurStep = ssPostInstall then begin
    RewriteDoubaoVenvConfig;
    WriteInstallManifest;
  end;
end;
