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
DefaultDialogFontName=Microsoft YaHei UI
UsePreviousSetupType=no
DisableWelcomePage=no
DisableReadyPage=no
CloseApplications=yes
CloseApplicationsFilter=MyPowerTools.exe,MyPowerTools.Runner.exe,MyPowerTools.Shell.Avalonia.exe,MyPowerTools.ServiceManager.exe
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

[Types]
Name: "core"; Description: "核心安装（推荐）"
Name: "full"; Description: "完整安装"
Name: "custom"; Description: "自定义安装"; Flags: iscustom

[Components]
Name: "core"; Description: "MyPowerTools 核心程序"; Types: core full custom; Flags: fixed
Name: "smartbird"; Description: "SmartBird 温控与能耗服务（约 16 MiB 下载，约 1,800 个文件）"; Types: full
Name: "doubao"; Description: "Doubao 桌面自动化依赖（约 28 MiB 下载，约 5,100 个文件）"; Types: full
Name: "android"; Description: "Android Platform Tools（系统缺少 ADB 时约 4 MiB）"; Types: full

[Messages]
SetupAppTitle=MyPowerTools 安装程序
SetupWindowTitle=安装 MyPowerTools %1
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ClickNext=点击“下一步”继续，或点击“取消”退出安装程序。
WelcomeLabel1=欢迎安装 MyPowerTools
WelcomeLabel2=此安装器将检查本机运行时，并按需下载所选组件。%n%n安装期间会显示下载、校验和解压进度。
SelectComponentsDesc=选择需要安装的功能
SelectComponentsLabel2=核心程序为推荐配置。SmartBird、Doubao 和 Android 工具可按需添加；以后可重新运行安装器修改。
SelectTasksDesc=选择启动方式
SelectTasksLabel2=选择要创建的快捷方式和登录启动项，然后点击“下一步”。
ReadyLabel1=MyPowerTools 已准备好安装。
ReadyLabel2a=确认下面的组件与运行时方案，点击“安装”开始；需要修改时点击“上一步”。
ReadyLabel2b=点击“安装”开始。
InstallingLabel=正在安装 MyPowerTools。大组件会显示名称和预计文件数量。
FinishedHeadingLabel=MyPowerTools 安装完成
FinishedLabel=核心程序已经安装。勾选下方选项即可立即启动。
FinishedLabelNoIcons=MyPowerTools 已安装完成。
StatusClosingApplications=正在关闭需要更新的 MyPowerTools 进程...
StatusExtractFiles=正在解压所选组件...
StatusDownloadFiles=正在下载所选组件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成服务注册...
ErrorDownloadAborted=下载已取消
ErrorDownloadFailed=下载失败：%1 %2
ErrorExtractionFailed=解压失败：%1
ExitSetupTitle=退出安装程序
ExitSetupMessage=安装尚未完成。现在退出会保留原有安装。%n%n确定退出？

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "启动选项："; Flags: unchecked
Name: "autostart"; Description: "登录 Windows 后自动启动后台 Runner"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{tmp}\{#WebCoreAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; BeforeInstall: BeginCoreInstall
Source: "{tmp}\{#WebDotNetAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedDotNetDownload; BeforeInstall: BeginDotNetInstall
Source: "{tmp}\{#WebPythonAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedPythonDownload; BeforeInstall: BeginPythonInstall
Source: "{tmp}\{#WebSmartBirdAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: smartbird; BeforeInstall: BeginSmartBirdInstall
Source: "{tmp}\{#WebDoubaoAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: doubao; BeforeInstall: BeginDoubaoInstall
Source: "{tmp}\{#WebAdbAsset}"; DestDir: "{app}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: android; Check: NeedAdbDownload; BeforeInstall: BeginAdbInstall

[Icons]
Name: "{autoprograms}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Check: ShouldRunPostInstall
Name: "{autodesktop}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Tasks: desktopicon; Check: ShouldRunPostInstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyPowerTools"; ValueData: """{app}\Runner\MyPowerTools.Runner.exe"" --modules ""{app}\modules"" --data-root ""{localappdata}\MyPowerTools"""; Flags: uninsdeletevalue; Tasks: autostart; Check: ShouldRunPostInstall

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "正在停止旧版后台服务..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Install -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; StatusMsg: "正在注册 MyPowerTools 后台服务..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; StatusMsg: "正在注册 SmartBird 温控任务..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall; Components: smartbird
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Install -RepoRoot ""{app}\Runtimes\SmartBird"" -PythonPath ""{app}\Runtimes\Python312\python.exe"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"" -SettingsFile ""{localappdata}\MyPowerTools\SmartBird\settings.json"""; StatusMsg: "正在注册 SmartBird 能耗服务..."; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall; Components: smartbird
Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; Description: "启动 MyPowerTools"; Flags: nowait postinstall skipifsilent; Check: ShouldRunPostInstall

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; RunOnceId: "RemoveMyPowerToolsUserServices"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{app}\Shell\MyPowerTools.Shell.Avalonia.exe"; Parameters: "--doubao-runtime stop --doubao-runtime-root ""{app}\Runtimes\Doubao"" --doubao-data-root ""{localappdata}\MyPowerTools\Doubao"""; RunOnceId: "StopDoubaoComputerUse"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdEnergyServerTask"; Flags: runhidden waituntilterminated; Check: ShouldUninstallSmartBird
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdThermostatTask"; Flags: runhidden waituntilterminated; Check: ShouldUninstallSmartBird

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

function ShouldUninstallSmartBird: Boolean;
begin
  Result := ShouldRunPostInstall and
    FileExists(ExpandConstant('{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1'));
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

function WantsSmartBird: Boolean;
begin
  Result := WizardIsComponentSelected('smartbird');
end;

function WantsDoubao: Boolean;
begin
  Result := WizardIsComponentSelected('doubao');
end;

function WantsAndroidTools: Boolean;
begin
  Result := WizardIsComponentSelected('android');
end;

function WantsPythonFeatures: Boolean;
begin
  Result := WantsSmartBird or WantsDoubao;
end;

function NeedPythonDownload: Boolean;
begin
  Result := WantsPythonFeatures and NeedPrivatePython;
end;

function NeedAdbDownload: Boolean;
begin
  Result := WantsAndroidTools and NeedPrivateAdb;
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
  if not WantsPythonFeatures then begin
    NeedPrivatePython := False;
    PythonRuntimeSource := 'not-selected';
  end else begin
    NeedPrivatePython := not HasPrivatePython;
    if NeedPrivatePython then PythonRuntimeSource := 'private-download'
    else PythonRuntimeSource := 'private-existing';
  end;
  if not WantsAndroidTools then begin
    NeedPrivateAdb := False;
    AdbRuntimeSource := 'not-selected';
  end else if FileExists(ExpandConstant('{app}\Tools\AndroidPlatformTools\adb.exe')) then begin
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
    '正在准备 MyPowerTools',
    '正在下载并校验所选组件，下载中断后可以重新运行安装器。', nil);
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
  if NeedPythonDownload then QueueAsset('{#WebPythonAsset}', '{#WebPythonSha256}');
  if WantsSmartBird then QueueAsset('{#WebSmartBirdAsset}', '{#WebSmartBirdSha256}');
  if WantsDoubao then QueueAsset('{#WebDoubaoAsset}', '{#WebDoubaoSha256}');
  if NeedAdbDownload then QueueAsset('{#WebAdbAsset}', '{#WebAdbSha256}');

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
  if NeedPythonDownload then PreserveAssetInCache('{#WebPythonAsset}', '{#WebPythonSha256}');
  if WantsSmartBird then PreserveAssetInCache('{#WebSmartBirdAsset}', '{#WebSmartBirdSha256}');
  if WantsDoubao then PreserveAssetInCache('{#WebDoubaoAsset}', '{#WebDoubaoSha256}');
  if NeedAdbDownload then PreserveAssetInCache('{#WebAdbAsset}', '{#WebAdbSha256}');
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
  if NeedPrivateDotNet then DotNetPlan := '下载应用私有 .NET 10 x64 运行时'
  else if DotNetRuntimeSource = 'private-existing' then DotNetPlan := '复用已有的 MyPowerTools 私有运行时'
  else DotNetPlan := '复用 Windows 已注册的全局 .NET 10 x64 运行时';
  if PythonRuntimeSource = 'not-selected' then PythonPlan := '所选功能无需 Python'
  else if NeedPrivatePython then PythonPlan := '下载应用私有 Python 3.12 x64 运行时'
  else PythonPlan := '复用已有的 MyPowerTools Python 3.12 运行时';
  if AdbRuntimeSource = 'not-selected' then AdbPlan := '未选择 Android Platform Tools'
  else if NeedPrivateAdb then AdbPlan := '下载应用私有 Android Platform Tools'
  else AdbPlan := '复用兼容的 Android Platform Tools';

  Result := '安装目录：' + NewLine + Space + ExpandConstant('{app}') +
    NewLine + NewLine + '运行时方案：' +
    NewLine + Space + '.NET：' + DotNetPlan +
    NewLine + Space + 'Python：' + PythonPlan +
    NewLine + Space + 'ADB：' + AdbPlan;
  if WantsSmartBird then
    Result := Result + NewLine + Space + 'SmartBird：安装私有锁定依赖';
  if WantsDoubao then
    Result := Result + NewLine + Space + 'Doubao：安装私有锁定依赖';
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

procedure SetInstallPhase(const Message: String);
begin
  WizardForm.StatusLabel.Caption := Message;
  WizardForm.FilenameLabel.Caption := '';
end;

procedure BeginCoreInstall;
begin
  SetInstallPhase('正在安装核心程序（第 1 阶段）...');
end;

procedure BeginDotNetInstall;
begin
  SetInstallPhase('正在安装 .NET 10 私有运行时...');
end;

procedure BeginPythonInstall;
begin
  SetInstallPhase('正在安装 Python 3.12 私有运行时...');
end;

procedure BeginSmartBirdInstall;
begin
  SetInstallPhase('正在安装 SmartBird 依赖（约 1,800 个文件）...');
end;

procedure BeginDoubaoInstall;
begin
  SetInstallPhase('正在安装 Doubao 依赖（约 5,100 个文件，可能需要一分钟）...');
end;

procedure BeginAdbInstall;
begin
  SetInstallPhase('正在安装 Android Platform Tools...');
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
  SmartBirdSource: String;
  DoubaoSource: String;
  ManifestText: String;
begin
  RuntimeSource := DotNetRuntimeSource;
  PythonSource := PythonRuntimeSource;
  AdbSource := AdbRuntimeSource;
  if WantsSmartBird then SmartBirdSource := 'private-download'
  else SmartBirdSource := 'not-selected';
  if WantsDoubao then DoubaoSource := 'private-download'
  else DoubaoSource := 'not-selected';
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
    '    "smartbird": "' + SmartBirdSource + '",' + #13#10 +
    '    "doubao": "' + DoubaoSource + '",' + #13#10 +
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
