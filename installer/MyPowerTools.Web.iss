#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef MyReleaseChannel
  #define MyReleaseChannel "stable"
#endif
#ifndef MyRepositoryUrl
  #define MyRepositoryUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools"
#endif
#ifndef MyDownloadBaseUrl
  #if MyReleaseChannel == "nightly"
    #define MyDownloadBaseUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/nightly-" + MyAppVersion + "-" + GetDateTimeString("yyyymmdd", "", "")
  #else
    #define MyDownloadBaseUrl "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/v" + MyAppVersion
  #endif
#endif

#ifndef MyDownloadMirrors
  ; Optional extra download bases (semicolon separated, each ending without "/").
  ; Every file is still verified against the embedded ISSig key, so a mirror can
  ; only speed a download up, never change what gets installed.
  #define MyDownloadMirrors ""
#endif

#define WebCoreAsset "MyPowerTools-core-win-x64.zip"
#define WebDotNetAsset "MyPowerTools-runtime-dotnet-10-win-x64.zip"
#define WebPythonAsset "MyPowerTools-runtime-python-3.12-win-x64.zip"
#define WebSmartBirdAsset "MyPowerTools-runtime-smartbird-" + MyAppVersion + "-win-x64.zip"
#define WebDoubaoAsset "MyPowerTools-runtime-doubao-" + MyAppVersion + "-win-x64.zip"
#define WebAdbAsset "MyPowerTools-runtime-android-platform-tools-win-x64.zip"

#ifndef MyAllowUnsigned
  #include "..\artifacts\release\web-installer-signing-key.iss"
#endif

#ifdef MyInstallerTestMode
  #define MyAppIdGuid "{55A3DD2D-31E1-44EC-89C3-9C75839E1B30}"
#else
  #define MyAppIdGuid "{6A1532EA-A2F5-4C1F-AB7C-B119C9C3B54B}"
#endif

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
WizardSizePercent=135,125
DefaultDialogFontName=Microsoft YaHei UI
UsePreviousSetupType=no
DisableWelcomePage=no
DisableReadyPage=no
CloseApplications=force
CloseApplicationsFilter=MyPowerTools.exe,MyPowerTools.Runner.exe,MyPowerTools.Shell.Avalonia.exe,MyPowerTools.ServiceManager.exe
RestartApplications=no
SetupLogging=yes
AllowCancelDuringInstall=yes
; One installer at a time: a second copy (double-click twice, or an OTA run racing a manual
; run) would fight over the same install directory, cache and transaction journal.
SetupMutex=MyPowerToolsSetupMutex,Global\MyPowerToolsSetupMutex
; .NET 10 and the Avalonia shell need Windows 10 1809 or later.
MinVersion=10.0.17763
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

#ifndef MyAllowUnsigned
[ISSigKeys]
Name: mptrelease; RuntimeID: {#WebISSigRuntimeID}; KeyID: "{#WebISSigKeyID}"; PublicX: "{#WebISSigPublicX}"; PublicY: "{#WebISSigPublicY}"
#endif

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
SetupWindowTitle=安装 %1
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
PreparingDesc=正在准备安装 MyPowerTools
PreparingLabel2=正在安全关闭运行中的 MyPowerTools 组件，最长约 8 秒。完成后会立即开始安装。
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
ErrorCloseApplications=安装器无法关闭正在运行的 MyPowerTools 进程。请关闭 MyPowerTools 后重试。
ExitSetupTitle=退出安装程序
ExitSetupMessage=安装尚未完成。现在退出不会损坏已安装的 MyPowerTools：未完成的更改会被撤销，已下载的文件会保留，下次运行安装器时无需重新下载。%n%n确定退出？
SetupAlreadyRunning=MyPowerTools 安装程序已经在运行。请切换到已打开的安装窗口，或等它结束后再试。
WindowsVersionNotSupported=MyPowerTools 需要 Windows 10（1809）或更高版本。
WinVersionTooLowError=MyPowerTools 需要 %1 %2 或更高版本。
OnlyOnTheseArchitectures=MyPowerTools 只能安装在以下处理器架构的 Windows 上：%n%n%1%n%nARM 设备需要 Windows 11（支持 x64 程序仿真）。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请关闭它，然后点击“确定”继续，或点击“取消”退出。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请关闭它，然后点击“确定”继续，或点击“取消”退出。
LastErrorMessage=%1。%n%n错误 %2：%3
SetupAborted=安装没有完成，原有安装已保持不变。%n%n安装日志保存在：%n%LOCALAPPDATA%\MyPowerTools\logs\installer%n%n请根据提示处理后重新运行安装器；如需求助，请把该日志文件发给开发者。
StatusRollback=正在撤销未完成的更改，恢复原有安装...
StatusCreateDirs=正在创建目录...
StatusCreateIcons=正在创建快捷方式...
StatusCreateRegistryEntries=正在写入注册表...
DownloadingLabel2=正在下载所需组件...
ButtonStopDownload=停止下载(&S)
StopDownload=确定要停止下载吗？已完成的部分会保留，下次无需重新下载。
ErrorDownloadSizeFailed=无法获取文件大小：%1 %2
ErrorProgress=下载进度异常：%1 / %2
ErrorFileSize=文件大小不符：应为 %1，实际为 %2
ExtractingLabel=正在解压...
ButtonStopExtraction=停止解压(&S)
StopExtraction=确定要停止解压吗？
ErrorExtractionAborted=解压已取消
ArchiveIsCorrupted=压缩包已损坏（校验失败）
ArchiveUnsupportedFormat=压缩包格式不受支持
SourceIsCorrupted=源文件已损坏（校验失败）
SourceVerificationFailed=文件签名校验失败：%1
VerificationSignatureDoesntExist=签名文件“%1”不存在（校验失败）
VerificationSignatureInvalid=签名文件“%1”无效（校验失败）
VerificationKeyNotFound=签名文件“%1”使用了未知的密钥（校验失败）
VerificationFileNameIncorrect=文件名与签名不符（校验失败）
VerificationFileTagIncorrect=文件标记与签名不符（校验失败）
VerificationFileSizeIncorrect=文件大小与签名不符（校验失败）
VerificationFileHashIncorrect=文件哈希与签名不符（校验失败）
ErrorDownloading=下载文件时出错：
ErrorExtracting=解压压缩包时出错：
ErrorCopying=复制文件时出错：
ErrorReplacingExistingFile=替换现有文件时出错：
ErrorCreatingTemp=在安装目录中创建文件时出错：
ErrorRenamingTemp=在安装目录中重命名文件时出错：
ErrorCreatingDir=安装程序无法创建目录“%1”
ErrorInternal2=内部错误：%1
ErrorFunctionFailedNoCode=%1 失败
ErrorFunctionFailed=%1 失败；错误代码 %2
ErrorFunctionFailedWithMessage=%1 失败；错误代码 %2。%n%3
ErrorExecutingProgram=无法运行文件：%n%1
AbortRetryIgnoreSelectAction=选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消
FileAbortRetryIgnoreSkipNotRecommended=跳过此文件（不推荐）(&S)
FileAbortRetryIgnoreIgnoreNotRecommended=忽略错误并继续（不推荐）(&I)
ButtonOK=确定
ButtonYes=是(&Y)
ButtonNo=否(&N)
ClickFinish=点击“完成”退出安装程序。
ConfirmUninstall=确定要卸载 %1 吗？%n%n程序文件和后台服务会被移除；你的设置和数据保存在 %LOCALAPPDATA%\MyPowerTools，不会被删除。
UninstallStatusLabel=正在从这台电脑上卸载 %1，请稍候。
UninstalledAll=%1 已成功卸载。
UninstalledMost=%1 卸载完成。%n%n有少量文件无法删除，可以手动删除。
UninstalledAndNeedsRestart=需要重新启动电脑才能完成 %1 的卸载。%n%n现在重新启动吗？
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...
UninstallNotFound=文件“%1”不存在，无法卸载。
UninstallOpenError=无法打开文件“%1”，无法卸载。
UninstallDataCorrupted=文件“%1”已损坏，无法卸载。
ShutdownBlockReasonInstallingApp=正在安装 %1。
ShutdownBlockReasonUninstallingApp=正在卸载 %1。
PrepareToInstallNeedsRestart=需要重新启动电脑。重启后请再次运行安装器以完成 [name] 的安装。%n%n现在重新启动吗？
CannotContinue=安装无法继续。请点击“取消”退出。
WizardPreparing=准备安装
WizardInstalling=正在安装
WizardReady=准备安装
WizardSelectComponents=选择组件
WizardSelectTasks=选择附加任务
ApplicationsFound=以下程序正在使用需要更新的文件。建议让安装程序自动关闭这些程序。
ApplicationsFound2=以下程序正在使用需要更新的文件。建议让安装程序自动关闭这些程序。安装完成后，安装程序会尝试重新启动它们。
CloseApplications=自动关闭这些程序(&A)
DontCloseApplications=不关闭这些程序(&D)

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "启动选项："; Flags: unchecked
Name: "autostart"; Description: "登录 Windows 后自动启动后台 Runner"; GroupDescription: "启动选项："; Flags: unchecked

[InstallDelete]
Type: files; Name: "{app}\dev-update.manifest.json"

[Files]
Source: "{tmp}\{#WebCoreAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; BeforeInstall: BeginCoreInstall; AfterInstall: VerifyCoreLayout
Source: "..\scripts\configure-user-services.ps1"; DestDir: "{code:PayloadDir}"; Flags: ignoreversion
Source: "..\scripts\web-installer-worker.ps1"; Flags: dontcopy
Source: "{tmp}\{#WebDotNetAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedDotNetDownload; BeforeInstall: BeginDotNetInstall; AfterInstall: VerifyDotNetLayout
Source: "{tmp}\{#WebPythonAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; Check: NeedPythonDownload; BeforeInstall: BeginPythonInstall
Source: "{tmp}\{#WebSmartBirdAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: smartbird; BeforeInstall: BeginSmartBirdInstall
Source: "{tmp}\{#WebDoubaoAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: doubao; BeforeInstall: BeginDoubaoInstall
Source: "{tmp}\{#WebAdbAsset}"; DestDir: "{code:PayloadDir}"; Flags: external extractarchive recursesubdirs ignoreversion; Components: android; Check: NeedAdbDownload; BeforeInstall: BeginAdbInstall

[Icons]
Name: "{autoprograms}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Check: ShouldRunPostInstall
Name: "{autodesktop}\MyPowerTools"; Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; WorkingDir: "{app}"; IconFilename: "{app}\assets\MyPowerTools.ico"; Tasks: desktopicon; Check: ShouldRunPostInstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyPowerTools"; ValueData: """{app}\Runner\MyPowerTools.Runner.exe"" --modules ""{app}\modules"" --data-root ""{localappdata}\MyPowerTools"""; Flags: uninsdeletevalue; Tasks: autostart; Check: ShouldRunPostInstall

[Run]
Filename: "{app}\MyPowerTools.exe"; Parameters: "--data-root ""{localappdata}\MyPowerTools"""; Description: "启动 MyPowerTools"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchApp

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\configure-user-services.ps1"" -Mode Uninstall -InstallRoot ""{app}"" -DataRoot ""{localappdata}\MyPowerTools"""; RunOnceId: "RemoveMyPowerToolsUserServices"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\cmd.exe"; Parameters: "/D /C exit /B 0"; RunOnceId: "StopDoubaoComputerUse"; Flags: runhidden waituntilterminated; Check: ShouldRunPostInstall
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-energy-server-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdEnergyServerTask"; Flags: runhidden waituntilterminated; Check: ShouldUninstallSmartBird
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1"" -Mode Uninstall -RepoRoot ""{app}\Runtimes\SmartBird"" -DataRoot ""{localappdata}\MyPowerTools\SmartBird"""; RunOnceId: "RemoveSmartBirdThermostatTask"; Flags: runhidden waituntilterminated; Check: ShouldUninstallSmartBird

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\MyPowerTools\Doubao"; Check: ShouldRunPostInstall
Type: filesandordirs; Name: "{localappdata}\MyPowerTools\installer-cache"; Check: ShouldRunPostInstall
Type: filesandordirs; Name: "{app}"
; Leftovers of an install transaction that could not be cleaned up at the time.
Type: filesandordirs; Name: "{app}.old"
Type: filesandordirs; Name: "{app}.failed"
Type: files; Name: "{app}.install-pending"

[Code]
const
  { The uninstall entry Inno Setup writes for this AppId (per-user install). }
  UninstallRegKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppIdGuid}_is1';
  { Automatic download attempts before the user is asked what to do. }
  AutomaticDownloadAttempts = 3;
  { A worker that never writes its result file (PowerShell blocked, crashed, hung) must not
    leave the wizard waiting forever. }
  QuiesceWorkerTimeoutMs = 120000;
  FinalizeWorkerTimeoutMs = 600000;
  RunOnceRegKey = 'Software\Microsoft\Windows\CurrentVersion\RunOnce';
  FinishUpdateRunOnceName = 'MyPowerToolsFinishUpdate';
  CleanupRunOnceName = 'MyPowerToolsCleanup';

var
  DownloadPage: TDownloadWizardPage;
  ShutdownPage: TWizardPage;
  FinalizePage: TWizardPage;
  InstallingLogMemo: TNewMemo;
  ShutdownLogMemo: TNewMemo;
  FinalizeLogMemo: TNewMemo;
  InstallLogLines: TStringList;
  WorkerTimerID: UINT_PTR;
  WorkerActive: Boolean;
  WorkerSucceeded: Boolean;
  WorkerPhase: Integer;
  WorkerLogPath: String;
  WorkerResultPath: String;
  WorkerStartTick: DWORD;
#ifdef MyInstallerTestAutoDrive
  AutoDriveTimerID: UINT_PTR;
#endif
  NeedPrivateDotNet: Boolean;
  NeedPrivatePython: Boolean;
  NeedPrivateAdb: Boolean;
  DotNetRuntimeSource: String;
  PythonRuntimeSource: String;
  AdbRuntimeSource: String;
  LegacyDotNetRootCleared: Boolean;
  QueuedDownloadCount: Integer;
  DownloadBases: TStringList;
  { Install transaction: the previous installation is moved aside as one directory rename,
    the new version is written into an empty directory, and the old one is only deleted
    once the new files are complete. Any failure, cancel or crash before that point puts
    the previous installation back. }
  TransactionActive: Boolean;
  TransactionCommitted: Boolean;
  TransactionAppDir: String;
  TransactionBackup: String;
  TransactionJournal: String;
  PreviousVersion: String;
  InstallKind: String;
  FinalizeSkipped: Boolean;
  InstallerSessionStamp: String;
  { Inno Setup swallows exceptions raised from BeforeInstall/AfterInstall, so file checks
    record the problem here and ssPostInstall abandons the install before committing. }
  InstallVerificationProblem: String;
  InstallFailed: Boolean;
  { Files that stayed in use (opened without FILE_SHARE_DELETE by a program that could not
    be closed) cannot be replaced now. The payload is then extracted beside the install and
    merged in; whatever is still busy is finished by a one-time task at the next sign-in. }
  UsePendingPayload: Boolean;
  PendingAfterRestart: Boolean;
  { Wine lets a directory be renamed while a file inside is open; Windows does not. Test
    builds can switch directory renames off to exercise the Windows behaviour. }
  TestNoDirectoryRename: Boolean;
#ifndef MyAllowUnsigned
  AllowedKeysRuntimeIDs: TStringList;
#endif

function SetTimer(hWnd: HWND; nIDEvent: UINT_PTR; uElapse: UINT;
  lpTimerFunc: LongWord): UINT_PTR;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd: HWND; uIDEvent: UINT_PTR): BOOL;
  external 'KillTimer@user32.dll stdcall';
function GetTickCount: DWORD;
  external 'GetTickCount@kernel32.dll stdcall';

function ShouldRunPostInstall: Boolean;
begin
#ifdef MyInstallerTestMode
  Result := False;
#else
  Result := True;
#endif
end;

function ShouldLaunchApp: Boolean;
begin
  Result := ShouldRunPostInstall and not InstallFailed;
end;

{ 0 = installed; 1 = nothing changed (verification failed and the previous installation was
  restored); 2 = files installed but background services were not registered. }
function GetCustomSetupExitCode: Integer;
begin
  if InstallFailed then
    Result := 1
  else if FinalizeSkipped then
    Result := 2
  else
    Result := 0;
end;

function ShouldUninstallSmartBird: Boolean;
begin
  Result := ShouldRunPostInstall and
    FileExists(ExpandConstant('{app}\Runtimes\SmartBird\scripts\install-smartbird-thermostat-task.ps1'));
end;

{ ---------- small helpers ---------- }

function IsAsciiText(const Value: String): Boolean;
var
  Index: Integer;
begin
  Result := True;
  for Index := 1 to Length(Value) do
    if Ord(Value[Index]) > 127 then begin
      Result := False;
      exit;
    end;
end;

{ Case-insensitive only for ASCII needles: Lowercase() in Pascal Script can turn CJK text
  into "?" characters, which would make any Chinese needle match any Chinese message. }
function ContainsText(const Haystack, Needle: String): Boolean;
begin
  Result := Pos(Needle, Haystack) > 0;
  if not Result and IsAsciiText(Needle) then
    Result := Pos(Lowercase(Needle), Lowercase(Haystack)) > 0;
end;

function HasCommandLineSwitch(const Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), Name) = 0 then begin
      Result := True;
      exit;
    end;
end;

function InstallerLogDir: String;
begin
  Result := ExpandConstant('{localappdata}\MyPowerTools\logs\installer');
end;

{ Copies Inno Setup's own log (which also receives every worker log) to a fixed, documented
  folder so a user can find it and send it. The temp copy disappears with %TEMP% clean-ups. }
procedure SaveInstallerLog;
var
  LogPath: String;
begin
  LogPath := ExpandConstant('{log}');
  if (LogPath = '') or not FileExists(LogPath) then exit;
  if not ForceDirectories(InstallerLogDir) then exit;
  if not FileCopy(LogPath, AddBackslash(InstallerLogDir) + 'setup-' + InstallerSessionStamp + '.log', False) then
    exit;
  FileCopy(LogPath, AddBackslash(InstallerLogDir) + 'setup-latest.log', False);
end;

function SaveUtf8Text(const FileName, Text: String): Boolean;
var
  Lines: TArrayOfString;
begin
  { SaveStringToFile writes the ANSI code page, which mangles a non-ASCII user name
    (C:\Users\张三) in the JSON paths. Every consumer reads these files as UTF-8. }
  SetArrayLength(Lines, 1);
  Lines[0] := Text;
  Result := SaveStringsToUTF8FileWithoutBOM(FileName, Lines, False);
end;

function ReadTextFile(const FileName: String): String;
var
  Lines: TArrayOfString;
  Index: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(FileName, Lines) then exit;
  for Index := 0 to GetArrayLength(Lines) - 1 do
    Result := Result + Lines[Index] + #10;
end;

{ Minimal "key": "value" lookup; enough for the flat version fields this installer reads. }
function ReadJsonStringValue(const Text, Key: String): String;
var
  Rest: String;
  Position: Integer;
begin
  Result := '';
  Position := Pos('"' + Key + '"', Text);
  if Position = 0 then exit;
  Rest := Copy(Text, Position + Length(Key) + 2, MaxInt);
  Position := Pos(':', Rest);
  if Position = 0 then exit;
  Rest := Copy(Rest, Position + 1, MaxInt);
  Position := Pos('"', Rest);
  if Position = 0 then exit;
  Rest := Copy(Rest, Position + 1, MaxInt);
  Position := Pos('"', Rest);
  if Position = 0 then exit;
  Result := Copy(Rest, 1, Position - 1);
end;

function NextVersionPart(var Value: String): Integer;
var
  Position: Integer;
begin
  Position := Pos('.', Value);
  if Position = 0 then begin
    Result := StrToIntDef(Trim(Value), 0);
    Value := '';
  end else begin
    Result := StrToIntDef(Trim(Copy(Value, 1, Position - 1)), 0);
    Delete(Value, 1, Position);
  end;
end;

function StripVersionSuffix(const Value: String): String;
var
  Position: Integer;
begin
  Result := Trim(Value);
  Position := Pos('-', Result);
  if Position > 0 then Result := Copy(Result, 1, Position - 1);
  Position := Pos('+', Result);
  if Position > 0 then Result := Copy(Result, 1, Position - 1);
end;

function CompareVersionStrings(const Left, Right: String): Integer;
var
  A, B: String;
  Part, PartA, PartB: Integer;
begin
  A := StripVersionSuffix(Left);
  B := StripVersionSuffix(Right);
  Result := 0;
  for Part := 1 to 4 do begin
    PartA := NextVersionPart(A);
    PartB := NextVersionPart(B);
    if PartA < PartB then begin
      Result := -1;
      exit;
    end;
    if PartA > PartB then begin
      Result := 1;
      exit;
    end;
  end;
end;

function CanonicalAppDir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\MyPowerTools');
end;

{ The transaction renames and deletes whole directories, so it only ever runs on the
  product's own directory (or the throwaway directory of the smoke-test build). }
function TransactionAllowed(const AppDir: String): Boolean;
begin
  Result := (not ShouldRunPostInstall) or
    (CompareText(RemoveBackslashUnlessRoot(AppDir), RemoveBackslashUnlessRoot(CanonicalAppDir)) = 0);
end;

function ReadInstalledVersion: String;
begin
  Result := '';
  if not FileExists(AddBackslash(CanonicalAppDir) + 'MyPowerTools.exe') then exit;
  { OTA updates rewrite installed-release.json but not the uninstall entry. }
  Result := ReadJsonStringValue(
    ReadTextFile(ExpandConstant('{localappdata}\MyPowerTools\ota-state\installed-release.json')),
    'version');
  if Result = '' then
    Result := ReadJsonStringValue(ReadTextFile(AddBackslash(CanonicalAppDir) + 'install.manifest.json'), 'version');
  if Result = '' then
    RegQueryStringValue(HKCU, UninstallRegKey, 'DisplayVersion', Result);
  if Result = '' then
    Result := '未知版本';
end;

{ ---------- runtime detection ---------- }

{ A version directory alone proves nothing: an interrupted extraction leaves one behind.
  Require the file that is written last-or-late and without which the runtime cannot load. }
function HasVersionDirectoryWithFile(const Root, FileName: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Root) + '10.0.*', FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
           (FindRec.Name <> '.') and (FindRec.Name <> '..') and
           FileExists(AddBackslash(Root) + FindRec.Name + '\' + FileName) then begin
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
    HasVersionDirectoryWithFile(AddBackslash(Root) + 'host\fxr', 'hostfxr.dll');
  Result := Result and HasVersionDirectoryWithFile(AddBackslash(Root) + 'shared\Microsoft.NETCore.App', 'Microsoft.NETCore.App.deps.json');
  Result := Result and HasVersionDirectoryWithFile(AddBackslash(Root) + 'shared\Microsoft.AspNetCore.App', 'Microsoft.AspNetCore.App.deps.json');
  Result := Result and HasVersionDirectoryWithFile(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App', 'Microsoft.WindowsDesktop.App.deps.json');
end;

function HasCompatibleGlobalDotNet: Boolean;
var
  DotNetRoot: String;
begin
  { The .NET installers record InstalledVersions\x64 in the 32-bit registry view; check both
    views. On Windows on ARM the x64 runtime lives in "dotnet\x64", while "dotnet" itself is
    the native ARM64 runtime that this x64 program cannot load. }
  DotNetRoot := '';
  if not RegQueryStringValue(HKLM32,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', DotNetRoot) then
    RegQueryStringValue(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', DotNetRoot);
  if DotNetRoot = '' then begin
    if IsArm64 then
      DotNetRoot := ExpandConstant('{pf64}\dotnet\x64')
    else
      DotNetRoot := ExpandConstant('{pf64}\dotnet');
  end;
  Result := HasDotNetRuntimeAtRoot(DotNetRoot);
  if Result then
    Log('Compatible global .NET 10 x64 runtime found at ' + DotNetRoot)
  else
    Log('No complete global .NET 10 x64 runtime at ' + DotNetRoot);
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

{ ---------- download cache ---------- }

function CachePath(const Asset: String): String;
begin
  Result := ExpandConstant('{localappdata}\MyPowerTools\installer-cache\{#MyAppVersion}\') + Asset;
end;

function VerifySignedAsset(const AssetPath: String): Boolean;
var
  VerifiedStream: TFileStream;
begin
#ifdef MyAllowUnsigned
  Result := FileExists(AssetPath);
#else
  Result := False;
  VerifiedStream := nil;
  try
    try
      VerifiedStream := ISSigVerify(AllowedKeysRuntimeIDs, AssetPath, True, False);
      Result := True;
    except
      Log('Installer cache signature verification failed for ' + AssetPath + ': ' +
        GetExceptionMessage);
    end;
  finally
    if VerifiedStream <> nil then
      VerifiedStream.Free;
  end;
#endif
end;

function PrepareCachedAsset(const Asset: String): Boolean;
var
  SourcePath: String;
  SourceSignaturePath: String;
  TargetPath: String;
  TargetSignaturePath: String;
begin
  SourcePath := CachePath(Asset);
  SourceSignaturePath := SourcePath + '.issig';
  TargetPath := ExpandConstant('{tmp}\') + Asset;
  TargetSignaturePath := TargetPath + '.issig';

  { A file finished earlier in this session (before another file failed) is kept: retrying
    must never start the whole download over. }
  if FileExists(TargetPath) and VerifySignedAsset(TargetPath) then begin
    Log('Reusing signature-verified download from this session: ' + TargetPath);
    Result := True;
    exit;
  end;

  Log('Checking installer cache: ' + SourcePath);
  Result := FileExists(SourcePath);
#ifndef MyAllowUnsigned
  Result := Result and FileExists(SourceSignaturePath);
#endif
  if not Result then exit;

  DeleteFile(TargetPath);
  DeleteFile(TargetSignaturePath);
  Result := FileCopy(SourcePath, TargetPath, False);
#ifndef MyAllowUnsigned
  Result := Result and FileCopy(SourceSignaturePath, TargetSignaturePath, False);
#endif
  if Result then
    Result := VerifySignedAsset(TargetPath);
  if Result then
    Log('Reusing signature-verified installer cache: ' + SourcePath)
  else begin
    Log('Discarding installer cache entry that failed verification: ' + SourcePath);
    DeleteFile(TargetPath);
    DeleteFile(TargetSignaturePath);
    DeleteFile(SourcePath);
    DeleteFile(SourceSignaturePath);
  end;
end;

procedure QueueAsset(const Asset, BaseUrl: String);
begin
  if not PrepareCachedAsset(Asset) then begin
#ifdef MyAllowUnsigned
    DownloadPage.Add(BaseUrl + '/' + Asset, Asset, '');
#else
    DownloadPage.AddWithISSigVerify(
      BaseUrl + '/' + Asset, '', Asset, AllowedKeysRuntimeIDs);
#endif
    QueuedDownloadCount := QueuedDownloadCount + 1;
  end;
end;

procedure PreserveAssetInCache(const Asset: String);
var
  SourcePath: String;
  SourceSignaturePath: String;
  TargetPath: String;
begin
  SourcePath := ExpandConstant('{tmp}\') + Asset;
  SourceSignaturePath := SourcePath + '.issig';
  TargetPath := CachePath(Asset);
  if FileExists(TargetPath) then exit;
  if FileExists(SourcePath) and VerifySignedAsset(SourcePath) then begin
    ForceDirectories(ExtractFileDir(TargetPath));
    FileCopy(SourcePath, TargetPath, False);
#ifndef MyAllowUnsigned
    FileCopy(SourceSignaturePath, TargetPath + '.issig', False);
#endif
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

procedure SetMemoText(Memo: TNewMemo; const Value: String);
begin
  if Memo = nil then exit;
  Memo.Text := Value;
  Memo.SelStart := Length(Memo.Text);
end;

procedure RefreshInstallLogViews;
begin
  if InstallLogLines = nil then exit;
  SetMemoText(InstallingLogMemo, InstallLogLines.Text);
  if not WorkerActive then begin
    SetMemoText(ShutdownLogMemo, InstallLogLines.Text);
    SetMemoText(FinalizeLogMemo, InstallLogLines.Text);
  end;
end;

procedure AppendInstallLog(const Message: String);
begin
  Log(Message);
  if InstallLogLines <> nil then begin
    InstallLogLines.Add(GetDateTimeString('hh:nn:ss', '-', ':') + '  ' + Message);
    RefreshInstallLogViews;
  end;
end;

function LoadUtf8TextFile(const FileName: String; var Value: String): Boolean;
var
  Lines: TArrayOfString;
  LineIndex: Integer;
begin
  Value := '';
  Result := LoadStringsFromFile(FileName, Lines);
  if not Result then exit;
  for LineIndex := 0 to GetArrayLength(Lines) - 1 do begin
    if LineIndex > 0 then
      Value := Value + #13#10;
    Value := Value + Lines[LineIndex];
  end;
  if GetArrayLength(Lines) > 0 then
    Value := Value + #13#10;
end;

procedure RefreshWorkerLog;
var
  WorkerText: String;
  CombinedText: String;
begin
  WorkerText := '';
  if LoadUtf8TextFile(WorkerLogPath, WorkerText) then
    CombinedText := InstallLogLines.Text + WorkerText
  else
    CombinedText := InstallLogLines.Text;
  if WorkerPhase = 1 then
    SetMemoText(ShutdownLogMemo, CombinedText)
  else if WorkerPhase = 2 then
    SetMemoText(FinalizeLogMemo, CombinedText);
end;

procedure StartWorker(Phase: Integer);
var
  ResultCode: Integer;
  Parameters: String;
begin
  WorkerPhase := Phase;
  WorkerActive := True;
  WorkerSucceeded := False;
  WorkerStartTick := GetTickCount;
  WorkerLogPath := ExpandConstant('{tmp}\MyPowerTools-Web-Setup-worker-' +
    IntToStr(Phase) + '.log');
  WorkerResultPath := ExpandConstant('{tmp}\MyPowerTools-Web-Setup-worker-' +
    IntToStr(Phase) + '.result');
  DeleteFile(WorkerLogPath);
  DeleteFile(WorkerResultPath);
  ExtractTemporaryFile('web-installer-worker.ps1');

  if Phase = 1 then begin
    AppendInstallLog('正在关闭运行中的 MyPowerTools 组件。');
    Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass' +
      ' -File "' + ExpandConstant('{tmp}\web-installer-worker.ps1') + '"' +
      ' -Phase Quiesce';
  end else begin
    AppendInstallLog('正在注册后台服务并完成安装。');
    Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass' +
      ' -File "' + ExpandConstant('{tmp}\web-installer-worker.ps1') + '"' +
      ' -Phase Finalize';
    if WantsSmartBird then
      Parameters := Parameters + ' -InstallSmartBird';
  end;
  Parameters := Parameters +
    ' -InstallRoot "' + ExpandConstant('{app}') + '"' +
    ' -DataRoot "' + ExpandConstant('{localappdata}\MyPowerTools') + '"' +
    ' -LogPath "' + WorkerLogPath + '"' +
    ' -ResultPath "' + WorkerResultPath + '"';

  WizardForm.BackButton.Enabled := False;
  WizardForm.NextButton.Enabled := False;
  WizardForm.CancelButton.Enabled := True;
  { Windows PowerShell 5.1 ships with every supported Windows; PowerShell 7 is never required. }
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewNoWait, ResultCode) then begin
    WorkerActive := False;
    AppendInstallLog('后台安装任务启动失败：' + SysErrorMessage(ResultCode));
    WizardForm.NextButton.Caption := '重试(&R)';
    WizardForm.NextButton.Enabled := True;
  end;
end;

function RunWorkerSynchronously(Phase: Integer): Boolean;
var
  ResultCode: Integer;
  Parameters: String;
  ResultText: String;
  WorkerText: String;
  PhaseName: String;
begin
  if Phase = 1 then
    PhaseName := 'Quiesce'
  else
    PhaseName := 'Finalize';
  WorkerLogPath := ExpandConstant('{tmp}\MyPowerTools-Web-Setup-sync-' +
    IntToStr(Phase) + '.log');
  WorkerResultPath := ExpandConstant('{tmp}\MyPowerTools-Web-Setup-sync-' +
    IntToStr(Phase) + '.result');
  DeleteFile(WorkerLogPath);
  DeleteFile(WorkerResultPath);
  ExtractTemporaryFile('web-installer-worker.ps1');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass' +
    ' -File "' + ExpandConstant('{tmp}\web-installer-worker.ps1') + '"' +
    ' -Phase ' + PhaseName +
    ' -InstallRoot "' + ExpandConstant('{app}') + '"' +
    ' -DataRoot "' + ExpandConstant('{localappdata}\MyPowerTools') + '"' +
    ' -LogPath "' + WorkerLogPath + '"' +
    ' -ResultPath "' + WorkerResultPath + '"';
  if (Phase = 2) and WantsSmartBird then
    Parameters := Parameters + ' -InstallSmartBird';

  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  ResultText := '';
  Result := Result and LoadUtf8TextFile(WorkerResultPath, ResultText) and
    (Trim(ResultText) = '0');
  WorkerText := '';
  if LoadUtf8TextFile(WorkerLogPath, WorkerText) then
    Log(WorkerText);
  if not Result then
    Log('Synchronous installer worker failed in phase ' + PhaseName +
      '; process result ' + IntToStr(ResultCode));
end;

procedure AdvanceAfterWorker;
begin
  WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  WizardForm.NextButton.Enabled := True;
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure HandleWorkerFailure;
var
  Choice: Integer;
begin
  WizardForm.BackButton.Enabled := True;
  WizardForm.NextButton.Caption := '重试(&R)';
  WizardForm.NextButton.Enabled := True;
  if WorkerPhase = 1 then begin
    AppendInstallLog('部分 MyPowerTools 组件没有正常退出。');
    { Closing is best effort: the install step moves the old directory aside in one rename
      and retries (with an elevated option) if something still holds it, so continuing is
      safe and never leaves a half-updated installation. }
    Choice := TaskDialogMsgBox('部分 MyPowerTools 组件没有正常退出',
      '安装器没能确认所有 MyPowerTools 进程都已关闭。' + #13#10#13#10 +
      '可以直接继续：写入文件前安装器会再次检查，如果文件仍被占用，会提示你处理，不会留下半新半旧的安装。' + #13#10#13#10 +
      '安装日志：' + InstallerLogDir,
      mbInformation, MB_RETRYCANCEL, ['重试关闭', '继续安装'], 0);
    if Choice = IDRETRY then
      StartWorker(1)
    else begin
      AppendInstallLog('继续安装；写入文件前会再次确认没有进程占用安装目录。');
      WorkerSucceeded := True;
      AdvanceAfterWorker;
    end;
  end else begin
    AppendInstallLog('后台服务注册失败。完整错误已经显示在日志末尾。');
    Choice := TaskDialogMsgBox('后台服务注册失败',
      'MyPowerTools 的程序文件已经安装完成，但后台服务（通知、远程工具等）没有注册成功。' + #13#10#13#10 +
      '可以点击“重试”。也可以先跳过：主程序可以正常打开，依赖后台服务的功能可能暂时不可用；之后重新运行本安装器即可修复。' + #13#10#13#10 +
      '安装日志：' + InstallerLogDir,
      mbError, MB_RETRYCANCEL, ['重试', '跳过并完成安装'], 0);
    if Choice = IDRETRY then
      StartWorker(2)
    else begin
      FinalizeSkipped := True;
      WorkerSucceeded := True;
      AppendInstallLog('已跳过后台服务注册；重新运行安装器即可再次尝试。');
      AdvanceAfterWorker;
    end;
  end;
end;

{ The quiesce worker lists MyPowerTools processes it could not end because they run as
  administrator. Only then, ask once and end them through a single UAC prompt. Declining is
  fine: running program files can still be moved aside, so the install continues anyway. }
procedure HandleElevatedSurvivors;
var
  Lines: TArrayOfString;
  Index, Separator, Choice, ErrorCode: Integer;
  ProcessIds, Names: String;
begin
  if not LoadStringsFromFile(WorkerResultPath + '.elevated', Lines) then exit;
  ProcessIds := '';
  Names := '';
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    Separator := Pos(#9, Lines[Index]);
    if Separator <= 1 then continue;
    ProcessIds := ProcessIds + ' /PID ' + Copy(Lines[Index], 1, Separator - 1);
    Names := Names + #13#10 + '  ' + Copy(Lines[Index], Separator + 1, MaxInt);
  end;
  if ProcessIds = '' then exit;
  AppendInstallLog('以下 MyPowerTools 组件以管理员身份运行，普通权限无法关闭：' + Names);
  if WizardSilent then exit;
  Choice := TaskDialogMsgBox('需要管理员权限关闭 MyPowerTools 后台组件',
    '下面这些 MyPowerTools 组件是以管理员身份运行的，需要你确认一次才能关闭：' + Names + #13#10#13#10 +
    '点“关闭这些组件”后，Windows 会弹出一次权限确认。' + #13#10 +
    '也可以直接继续：安装仍会完成，个别仍被占用的文件会在重启电脑后自动更新。',
    mbConfirmation, MB_OKCANCEL, ['关闭这些组件', '直接继续'], IDOK);
  if Choice <> IDOK then begin
    AppendInstallLog('未关闭管理员权限组件，继续安装。');
    exit;
  end;
  if ShellExec('runas', ExpandConstant('{sys}\taskkill.exe'), '/F /T' + ProcessIds, '',
    SW_HIDE, ewWaitUntilTerminated, ErrorCode) then
    AppendInstallLog('已通过管理员权限关闭这些组件。')
  else
    AppendInstallLog('没有获得管理员权限（' + SysErrorMessage(ErrorCode) + '），继续安装。');
end;

procedure WorkerTimerProc(Arg1: HWND; Arg2: UINT; Arg3: UINT_PTR; Arg4: DWORD);
var
  ResultText: String;
  WorkerText: String;
  TimeoutMs: DWORD;
begin
  if not WorkerActive then exit;
  RefreshWorkerLog;
  ResultText := '';
  if not LoadUtf8TextFile(WorkerResultPath, ResultText) then begin
    if WorkerPhase = 1 then TimeoutMs := QuiesceWorkerTimeoutMs
    else TimeoutMs := FinalizeWorkerTimeoutMs;
    if GetTickCount - WorkerStartTick < TimeoutMs then exit;
    { PowerShell never produced a result (blocked by policy, crashed or hung). }
    ResultText := '1';
    AppendInstallLog('后台安装任务在规定时间内没有返回结果（PowerShell 可能被系统策略或安全软件阻止）。');
  end;

  WorkerActive := False;
  WorkerText := '';
  LoadUtf8TextFile(WorkerLogPath, WorkerText);
  if WorkerText <> '' then begin
    Log(WorkerText);
    InstallLogLines.Add(WorkerText);
  end;
  WorkerSucceeded := Trim(ResultText) = '0';
  if WorkerSucceeded then begin
    if WorkerPhase = 1 then begin
      HandleElevatedSurvivors;
      AppendInstallLog('运行中组件已经关闭，开始写入安装文件。');
    end
    else
      AppendInstallLog('后台服务注册完成，MyPowerTools 已经可以使用。');
    AdvanceAfterWorker;
  end else
    HandleWorkerFailure;
end;

#ifdef MyInstallerTestAutoDrive
procedure AutoDriveTimerProc(Arg1: HWND; Arg2: UINT; Arg3: UINT_PTR; Arg4: DWORD);
begin
  if WorkerActive then exit;
  if (WizardForm.CurPageID <> wpPreparing) and
    (WizardForm.CurPageID <> wpInstalling) and
    WizardForm.NextButton.Enabled then
    WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;
#endif

{ ---------- install transaction ---------- }

function TransactionJournalPath(const AppDir: String): String;
begin
  Result := RemoveBackslashUnlessRoot(AppDir) + '.install-pending';
end;

function AppendJournalLine(const Line: String): Boolean;
var
  Lines: TArrayOfString;
begin
  SetArrayLength(Lines, 1);
  Lines[0] := Line;
  Result := SaveStringsToUTF8FileWithoutBOM(TransactionJournal, Lines, True);
  if not Result then
    Log('Unable to append to the install transaction journal: ' + Line);
end;

function PathExists(const Path: String): Boolean;
begin
  Result := FileExists(Path) or DirExists(Path);
end;

function MovePath(const Source, Target: String): Boolean;
begin
  Result := False;
  if not ForceDirectories(ExtractFileDir(Target)) then exit;
  Result := RenameFile(Source, Target);
  if not Result then
    Log('Unable to move ' + Source + ' to ' + Target);
end;

{ A rename works for running .exe/.dll images and for files opened with FILE_SHARE_DELETE.
  It fails for files opened without that share mode, and for a directory while anything
  inside it is open or it is some process's working directory (an Explorer window, a
  console). Antivirus and indexer handles are short-lived, so retry briefly. }
function TryRename(const Source, Target: String; const Attempts: Integer): Boolean;
var
  Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to Attempts do begin
    if RenameFile(Source, Target) then begin
      Result := True;
      exit;
    end;
    if Attempt < Attempts then
      Sleep(150 * Attempt);
  end;
end;

{ Moves every entry of Source into Target, replacing files that already exist there. A
  directory that cannot be renamed as a whole is recreated in Target and moved entry by
  entry, so one busy file or an open folder window never blocks the rest. Entries that
  cannot be moved stay where they are; their paths (relative to the first call) are added
  to Stuck when it is not nil. Returns the number of entries left behind. }
function MergeTree(const Source, Target, Relative: String; const Stuck: TStringList): Integer;
var
  FindRec: TFindRec;
  Names: TStringList;
  Index: Integer;
  SourcePath, TargetPath, ChildRelative: String;
begin
  Result := 0;
  Names := TStringList.Create;
  try
    if FindFirst(AddBackslash(Source) + '*', FindRec) then begin
      try
        repeat
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
            Names.Add(FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
    if Names.Count > 0 then
      ForceDirectories(Target);
    for Index := 0 to Names.Count - 1 do begin
      SourcePath := AddBackslash(Source) + Names[Index];
      TargetPath := AddBackslash(Target) + Names[Index];
      if Relative = '' then
        ChildRelative := Names[Index]
      else
        ChildRelative := Relative + '\' + Names[Index];
      if DirExists(SourcePath) then begin
        if not PathExists(TargetPath) and not TestNoDirectoryRename then
          if TryRename(SourcePath, TargetPath, 2) then
            continue;
        Result := Result + MergeTree(SourcePath, TargetPath, ChildRelative, Stuck);
        RemoveDir(SourcePath);
      end else begin
        if FileExists(TargetPath) and not DeleteFile(TargetPath) then begin
          Log('Cannot replace (in use): ' + TargetPath);
          Result := Result + 1;
          if Stuck <> nil then Stuck.Add(ChildRelative);
          continue;
        end;
        if not TryRename(SourcePath, TargetPath, 3) then begin
          Log('Cannot move (in use): ' + SourcePath);
          Result := Result + 1;
          if Stuck <> nil then Stuck.Add(ChildRelative);
        end;
      end;
    end;
  finally
    Names.Free;
  end;
end;

function PendingPayloadDirFor(const AppDir: String): String;
begin
  Result := RemoveBackslashUnlessRoot(AppDir) + '.new';
end;

function FinishUpdateScriptPath: String;
begin
  Result := ExpandConstant('{localappdata}\MyPowerTools\finish-update.ps1');
end;

{ An update staged earlier that still waits for a restart is superseded by a new install
  (or removed by uninstall). }
procedure DiscardPendingUpdate(const AppDir: String);
begin
  RegDeleteValue(HKCU, RunOnceRegKey, FinishUpdateRunOnceName);
  if DirExists(PendingPayloadDirFor(AppDir)) then begin
    Log('Discarding an update that was waiting for a restart: ' + PendingPayloadDirFor(AppDir));
    DelTree(PendingPayloadDirFor(AppDir), True, True, True);
  end;
  DeleteFile(FinishUpdateScriptPath);
end;

{ Puts the previous installation back. Safe to run repeatedly: every step checks what is
  already in place, so a crash half-way is finished by the next run.
  What cannot be restored: nothing is lost, but a file that was in use the whole time was
  never moved, so it simply keeps its (old) content; a restore that meets a file still in use
  leaves the backup and the journal in place and the next installer run completes it. }
procedure RestoreFromJournal(const AppDir, JournalPath: String);
var
  Lines: TArrayOfString;
  Index, Separator, Left: Integer;
  Backup, Pending, Line, Relative, Aside: String;
begin
  if not LoadStringsFromFile(JournalPath, Lines) then begin
    Log('Install transaction journal is unreadable; leaving it for inspection: ' + JournalPath);
    exit;
  end;
  Backup := '';
  Pending := '';
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    if Pos('backup=', Lines[Index]) = 1 then
      Backup := Copy(Lines[Index], 8, MaxInt);
    if Pos('pending=', Lines[Index]) = 1 then
      Pending := Copy(Lines[Index], 9, MaxInt);
    if Lines[Index] = 'committed' then begin
      Log('Install transaction was already committed; removing the old backup.');
      DeleteFile(JournalPath);
      if (Backup <> '') and DirExists(Backup) then
        DelTree(Backup, True, True, True);
      exit;
    end;
  end;
  if (Pending <> '') and DirExists(Pending) then
    DelTree(Pending, True, True, True);
  if (Backup = '') or not DirExists(Backup) then begin
    Log('The previous installation was never moved aside; nothing to restore.');
    DeleteFile(JournalPath);
    exit;
  end;

  Log('Restoring the previous installation from ' + Backup);
  for Index := GetArrayLength(Lines) - 1 downto 0 do begin
    Line := Lines[Index];
    if (Pos('dir=', Line) = 1) or (Pos('file=', Line) = 1) then begin
      Relative := Copy(Line, Pos('=', Line) + 1, MaxInt);
      if PathExists(AddBackslash(AppDir) + Relative) then begin
        if not PathExists(AddBackslash(Backup) + Relative) then
          MovePath(AddBackslash(AppDir) + Relative, AddBackslash(Backup) + Relative)
        else if DirExists(AddBackslash(AppDir) + Relative) then
          MergeTree(AddBackslash(AppDir) + Relative, AddBackslash(Backup) + Relative, '', nil);
      end;
    end else if Pos('stuck=', Line) = 1 then begin
      { Never moved because it was in use; if it is free now, keep it with the backup so
        clearing the new files below cannot take it. }
      Relative := Copy(Line, 7, MaxInt);
      if FileExists(AddBackslash(AppDir) + Relative) and
         not FileExists(AddBackslash(Backup) + Relative) then
        MovePath(AddBackslash(AppDir) + Relative, AddBackslash(Backup) + Relative);
    end else if Pos('aside=', Line) = 1 then begin
      Relative := Copy(Line, 7, MaxInt);
      Separator := Pos('|', Relative);
      Aside := Copy(Relative, Separator + 1, MaxInt);
      Relative := Copy(Relative, 1, Separator - 1);
      if DirExists(AddBackslash(Backup) + Aside) then begin
        if DirExists(AddBackslash(Backup) + Relative) then
          DelTree(AddBackslash(Backup) + Relative, True, True, True);
        MovePath(AddBackslash(Backup) + Aside, AddBackslash(Backup) + Relative);
      end;
    end;
  end;

  { Clear what the new version wrote (anything still in use stays), then bring the old
    files back: in one rename when possible, otherwise entry by entry. }
  if DirExists(AppDir) then
    DelTree(AppDir, True, True, True);
  if not DirExists(AppDir) and RenameFile(Backup, AppDir) then begin
    Log('The previous installation was restored to ' + AppDir);
    DeleteFile(JournalPath);
    exit;
  end;
  Left := MergeTree(Backup, AppDir, '', nil);
  if Left = 0 then begin
    DelTree(Backup, True, True, True);
    DeleteFile(JournalPath);
    Log('The previous installation was restored to ' + AppDir + ' (entry by entry).');
  end else
    Log(IntToStr(Left) + ' entries could not be restored yet because they are in use; ' +
      'the next run of the installer finishes the restore from ' + Backup);
end;

{ Finishes whatever a previous run left behind: an interrupted install is rolled back,
  and old backups of a completed install are removed. }
procedure RecoverInterruptedInstall(const AppDir: String);
var
  Index: Integer;
  Candidate: String;
begin
  if not TransactionAllowed(AppDir) then exit;
  if FileExists(TransactionJournalPath(AppDir)) then begin
    Log('Found an unfinished install transaction: ' + TransactionJournalPath(AppDir));
    RestoreFromJournal(RemoveBackslashUnlessRoot(AppDir), TransactionJournalPath(AppDir));
  end;
  if FileExists(TransactionJournalPath(AppDir)) then exit;
  for Index := 0 to 9 do begin
    if Index = 0 then Candidate := RemoveBackslashUnlessRoot(AppDir) + '.old'
    else Candidate := RemoveBackslashUnlessRoot(AppDir) + '.old' + IntToStr(Index);
    if DirExists(Candidate) then begin
      Log('Removing the backup of an earlier completed install: ' + Candidate);
      DelTree(Candidate, True, True, True);
    end;
  end;
  Candidate := RemoveBackslashUnlessRoot(AppDir) + '.failed';
  if DirExists(Candidate) then
    DelTree(Candidate, True, True, True);
end;

procedure RollbackInstallTransaction;
begin
  if not TransactionActive or TransactionCommitted then exit;
  TransactionActive := False;
  AppendInstallLog('安装没有完成，正在恢复原有安装。');
  RestoreFromJournal(TransactionAppDir, TransactionJournal);
end;

procedure CommitInstallTransaction;
begin
  if not TransactionActive then exit;
  { Deleting the journal is the commit point. If it cannot be deleted, a "committed" line
    tells the next run not to roll a finished install back. }
  if not DeleteFile(TransactionJournal) then
    AppendJournalLine('committed');
  TransactionCommitted := True;
  TransactionActive := False;
  AppendInstallLog('新版本文件已经全部就位，正在清理旧版本文件。');
  if not DelTree(TransactionBackup, True, True, True) then
    Log('The old installation could not be fully removed yet; the next installer run removes it: ' +
      TransactionBackup);
end;

procedure InitializeWizard;
var
  AppDir: String;
  MirrorList: String;
  Position: Integer;
begin
  DownloadPage := CreateDownloadPage(
    '正在准备 MyPowerTools',
    '正在下载并校验所选组件。网络中断时会自动重试，已下载的部分会保留。', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  InstallLogLines := TStringList.Create;

  ShutdownPage := CreateCustomPage(wpReady,
    '正在安全关闭旧组件',
    '窗口保持可移动；下方日志会持续追加。');
  ShutdownLogMemo := TNewMemo.Create(ShutdownPage);
  ShutdownLogMemo.Parent := ShutdownPage.Surface;
  ShutdownLogMemo.SetBounds(0, 0, ShutdownPage.SurfaceWidth,
    ShutdownPage.SurfaceHeight);
  ShutdownLogMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  ShutdownLogMemo.ReadOnly := True;
  ShutdownLogMemo.ScrollBars := ssBoth;
  ShutdownLogMemo.WordWrap := False;
  ShutdownLogMemo.Font.Name := 'Consolas';
  ShutdownLogMemo.Font.Size := 9;

  FinalizePage := CreateCustomPage(wpInstalling,
    '正在完成 MyPowerTools 安装',
    '窗口保持可移动；服务注册输出会实时显示在下方。');
  FinalizeLogMemo := TNewMemo.Create(FinalizePage);
  FinalizeLogMemo.Parent := FinalizePage.Surface;
  FinalizeLogMemo.SetBounds(0, 0, FinalizePage.SurfaceWidth,
    FinalizePage.SurfaceHeight);
  FinalizeLogMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  FinalizeLogMemo.ReadOnly := True;
  FinalizeLogMemo.ScrollBars := ssBoth;
  FinalizeLogMemo.WordWrap := False;
  FinalizeLogMemo.Font.Name := 'Consolas';
  FinalizeLogMemo.Font.Size := 9;

  InstallingLogMemo := TNewMemo.Create(WizardForm.InstallingPage);
  InstallingLogMemo.Parent := WizardForm.InstallingPage;
  InstallingLogMemo.SetBounds(0,
    WizardForm.ProgressGauge.Top + WizardForm.ProgressGauge.Height + ScaleY(16),
    WizardForm.InstallingPage.ClientWidth,
    WizardForm.InstallingPage.ClientHeight - WizardForm.ProgressGauge.Top -
      WizardForm.ProgressGauge.Height - ScaleY(16));
  InstallingLogMemo.Anchors := [akLeft, akTop, akRight, akBottom];
  InstallingLogMemo.ReadOnly := True;
  InstallingLogMemo.ScrollBars := ssBoth;
  InstallingLogMemo.WordWrap := False;
  InstallingLogMemo.Font.Name := 'Consolas';
  InstallingLogMemo.Font.Size := 9;

  WorkerTimerID := SetTimer(0, 0, 200, CreateCallback(@WorkerTimerProc));
#ifdef MyInstallerTestAutoDrive
  AutoDriveTimerID := SetTimer(0, 0, 300, CreateCallback(@AutoDriveTimerProc));
#endif
  AppendInstallLog('安装器已启动（版本 {#MyAppVersion}）。');
#ifndef MyAllowUnsigned
  AllowedKeysRuntimeIDs := TStringList.Create;
  AllowedKeysRuntimeIDs.Add('{#WebISSigRuntimeID}');
#endif

  { Download sources: an explicit /MIRROR=<base url> first, then the release itself, then
    any build-time mirrors. Signatures are verified regardless of where a file came from. }
  DownloadBases := TStringList.Create;
  MirrorList := ExpandConstant('{param:MIRROR|}') + ';{#MyDownloadBaseUrl};{#MyDownloadMirrors}';
  while MirrorList <> '' do begin
    Position := Pos(';', MirrorList);
    if Position = 0 then Position := Length(MirrorList) + 1;
    AppDir := Trim(Copy(MirrorList, 1, Position - 1));
    Delete(MirrorList, 1, Position);
    while (Length(AppDir) > 0) and (AppDir[Length(AppDir)] = '/') do
      Delete(AppDir, Length(AppDir), 1);
    if (AppDir <> '') and (DownloadBases.IndexOf(AppDir) < 0) then
      DownloadBases.Add(AppDir);
  end;

  AppDir := RemoveBackslashUnlessRoot(WizardDirValue);
  RecoverInterruptedInstall(AppDir);

  if InstallKind = 'upgrade' then
    WizardForm.WelcomeLabel2.Caption :=
      '检测到已安装 MyPowerTools ' + PreviousVersion + '，将升级到 {#MyAppVersion}。' + #13#10#13#10 +
      '你的设置和数据会保留。升级过程中如果出现任何问题，安装器会自动恢复到原来的版本。'
  else if InstallKind = 'repair' then
    WizardForm.WelcomeLabel2.Caption :=
      '这台电脑上已经安装了 MyPowerTools {#MyAppVersion}。' + #13#10#13#10 +
      '继续将修复安装：重新下载并校验程序文件、重新注册后台服务。你的设置和数据会保留。'
  else if InstallKind = 'downgrade' then
    WizardForm.WelcomeLabel2.Caption :=
      '将把 MyPowerTools 从 ' + PreviousVersion + ' 降级到 {#MyAppVersion}。你的设置和数据会保留。';

  { Keep optional components that are already installed selected, so an upgrade updates them
    together with the core instead of leaving an older copy behind. }
  if ShouldRunPostInstall then begin
    if DirExists(AddBackslash(AppDir) + 'Runtimes\SmartBird') then WizardSelectComponents('smartbird');
    if DirExists(AddBackslash(AppDir) + 'Runtimes\Doubao') then WizardSelectComponents('doubao');
    if DirExists(AddBackslash(AppDir) + 'Tools\AndroidPlatformTools') then WizardSelectComponents('android');
  end;
end;

function InitializeSetup: Boolean;
var
  Comparison: Integer;
begin
  Result := True;
  InstallerSessionStamp := GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
  InstallKind := 'fresh';
  PreviousVersion := '';
  Log('Installer version {#MyAppVersion}; administrator: ' + IntToStr(Ord(IsAdmin)) +
    '; ARM64: ' + IntToStr(Ord(IsArm64)));
  if not ShouldRunPostInstall then exit;

  PreviousVersion := ReadInstalledVersion;
  if PreviousVersion = '' then exit;
  Comparison := CompareVersionStrings(PreviousVersion, '{#MyAppVersion}');
  if PreviousVersion = '未知版本' then Comparison := -1;
  Log('Installed version: ' + PreviousVersion + '; comparison with this installer: ' + IntToStr(Comparison));
  if Comparison < 0 then
    InstallKind := 'upgrade'
  else if Comparison = 0 then
    InstallKind := 'repair'
  else begin
    InstallKind := 'downgrade';
    if HasCommandLineSwitch('/ALLOWDOWNGRADE') then
      Log('Downgrade allowed by /ALLOWDOWNGRADE.')
    else if SuppressibleMsgBox(
      '这台电脑上已经安装了更新的 MyPowerTools ' + PreviousVersion + '，而这个安装器是较旧的 {#MyAppVersion}。' + #13#10#13#10 +
      '继续会把程序降级到 {#MyAppVersion}。你的设置和数据会保留，但较新版本写入的数据不一定能被旧版本读取。' + #13#10#13#10 +
      '通常应该下载最新的安装器。仍要降级安装吗？',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then begin
      Log('Downgrade declined; setup exits without changes.');
      Result := False;
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  if WorkerTimerID <> 0 then
    KillTimer(0, WorkerTimerID);
#ifdef MyInstallerTestAutoDrive
  if AutoDriveTimerID <> 0 then
    KillTimer(0, AutoDriveTimerID);
#endif
  RollbackInstallTransaction;
  SaveInstallerLog;
  if InstallLogLines <> nil then
    InstallLogLines.Free;
  if DownloadBases <> nil then
    DownloadBases.Free;
#ifndef MyAllowUnsigned
  if AllowedKeysRuntimeIDs <> nil then
    AllowedKeysRuntimeIDs.Free;
#endif
end;

function RequestInstalledExit(const FileName, Parameters, FailureMessage: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not FileExists(FileName) then begin
    Log('Shutdown client is absent: ' + FileName);
    exit;
  end;
  Log('Running product shutdown client: ' + FileName + ' ' + Parameters);
  if not Exec(FileName, Parameters, ExtractFileDir(FileName), SW_HIDE, ewNoWait,
    ResultCode) then begin
    Log(FailureMessage + ': unable to start client.');
    Result := False;
  end;
end;

function ProductImageKillParameters: String;
begin
  Result :=
    '/F /T' +
    ' /IM "MyPowerTools.Shell.Avalonia.exe"' +
    ' /IM "MyPowerTools.Runner.exe"' +
    ' /IM "MyPowerTools.ServiceManager.exe"' +
    ' /IM "MyPowerTools.WebToolHost.exe"' +
    ' /IM "MyPowerTools.InputRemapHost.exe"' +
    ' /IM "MyPowerTools.Broker.exe"' +
    ' /IM "MyPowerTools.ElevatedBroker.exe"' +
    ' /IM "MyPowerTools.Cli.exe"' +
    ' /IM "AdbForwarder.Service.exe"' +
    ' /IM "DoubaoAgent.Controller.Service.exe"' +
    ' /IM "RemoteNotifications.Service.exe"' +
    ' /IM "ScreenEase.Service.exe"';
end;

procedure ForceStopProductImages;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), ProductImageKillParameters,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Batched taskkill exit code ' + IntToStr(ResultCode))
  else
    Log('Unable to start batched taskkill.');
end;

{ Fallback when WMI cannot be used: the same path-based stop, done by Windows PowerShell. }
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
    Log('PowerShell path-based stop finished with exit code ' + IntToStr(ResultCode))
  else
    Log('PowerShell path-based stop could not run: ' + SysErrorMessage(ResultCode));
end;

{ Ends every process whose program file lives under Root, found by path rather than by a
  fixed name list (service units, python/pythonw and adb from Runtimes, helpers added in
  later versions). Uses WMI so it also works in the uninstaller, where no PowerShell worker
  is available. Processes started "as administrator" cannot be ended from here; the caller
  handles those. }
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
         (CompareText(Copy(ExtractFileName(Path), 1, 5), 'unins') <> 0) then begin
        Log('Stopping process under the install directory: ' + Path + ' (PID ' + IntToStr(Item.ProcessId) + ')');
        ProcessIds := ProcessIds + ' /PID ' + IntToStr(Item.ProcessId);
      end;
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

procedure QuiesceInstalledProduct;
var
  ResultCode: Integer;
  ShellPath: String;
  CliPath: String;
begin
  ShellPath := ExpandConstant('{app}\Shell\MyPowerTools.Shell.Avalonia.exe');
  CliPath := ExpandConstant('{app}\Cli\MyPowerTools.Cli.exe');

  RequestInstalledExit(ShellPath, '--shutdown-shell', 'Graceful Shell shutdown failed');
  RequestInstalledExit(ShellPath,
    '--smoke --timeout-ms 5000 --quit-runner --modules "' +
    ExpandConstant('{app}\modules') + '" --data-root "' +
    ExpandConstant('{localappdata}\MyPowerTools') + '"',
    'Graceful Runner shutdown failed');
  if DirExists(ExpandConstant('{app}\Runtimes\Doubao')) then
    RequestInstalledExit(ShellPath,
      '--doubao-runtime stop --doubao-runtime-root "' +
      ExpandConstant('{app}\Runtimes\Doubao') + '" --doubao-data-root "' +
      ExpandConstant('{localappdata}\MyPowerTools\Doubao') + '"',
      'Graceful Doubao runtime shutdown failed');
  RequestInstalledExit(CliPath, 'service quiesce',
    'Graceful ServiceManager quiesce failed');
  { Compatibility requests for installations whose CLI predates `service quiesce`. }
  RequestInstalledExit(CliPath, 'service stop remote-notifications.service',
    'Stopping Remote Notifications failed');
  RequestInstalledExit(CliPath, 'service stop screenease.service',
    'Stopping ScreenEase failed');
  RequestInstalledExit(CliPath, 'service stop adb-forwarder.service',
    'Stopping ADB Forwarder failed');
  RequestInstalledExit(CliPath, 'service stop ddns.service',
    'Stopping DDNS failed');
  RequestInstalledExit(CliPath, 'service stop doubao-agent.controller.service',
    'Stopping Doubao Agent Controller failed');
  RequestInstalledExit(CliPath, 'service shutdown',
    'Legacy ServiceManager shutdown failed');

  if Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/End /TN "\MyPowerTools WinSpace Shift"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
    Log('Stopped elevated InputRemap task; exit code ' + IntToStr(ResultCode));

  Sleep(1500);
  ForceStopProductImages;
  StopProcessesUnderRoot(ExpandConstant('{app}'));
end;

{ Uninstall must never fail on a busy file. Whatever could not be deleted now is removed by
  a one-time command at the next sign-in (per-user installs cannot use the administrator-only
  "delete on reboot" list). }
procedure ScheduleLeftoverCleanup(const AppDir: String);
begin
  if not DirExists(AppDir) then exit;
  DelTree(AppDir, True, True, True);
  if not DirExists(AppDir) then exit;
  Log('Some files are still in use; they are removed at the next sign-in: ' + AppDir);
  RegWriteStringValue(HKCU, RunOnceRegKey, CleanupRunOnceName,
    '"' + ExpandConstant('{sys}\cmd.exe') + '" /d /c rd /s /q "' + AppDir + '"');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
begin
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if (CurUninstallStep = usUninstall) and ShouldRunPostInstall then
    QuiesceInstalledProduct
  else if (CurUninstallStep = usPostUninstall) and TransactionAllowed(AppDir) then begin
    DiscardPendingUpdate(AppDir);
    ScheduleLeftoverCleanup(AppDir);
  end;
end;

{ Stops the install before any file is written (or, after the old version was moved aside,
  lets DeinitializeSetup put it back) with a plain message instead of a script error. }
procedure FailBeforeInstall(const Message: String);
begin
  AppendInstallLog(Message);
  SaveInstallerLog;
  SuppressibleMsgBox(Message + #13#10#13#10 + '安装日志：' + InstallerLogDir, mbCriticalError, MB_OK, IDOK);
  Abort;
end;

procedure CarryFromBackup(const Relative: String; const ShouldCarry: Boolean);
var
  Source, Target: String;
begin
  if not ShouldCarry then exit;
  Source := AddBackslash(TransactionBackup) + Relative;
  Target := AddBackslash(TransactionAppDir) + Relative;
  if not PathExists(Source) then exit;
  AppendJournalLine('dir=' + Relative);
  { A folder that stayed behind (it was in use) is merged into instead of replaced. }
  if PathExists(Target) or not MovePath(Source, Target) then
    if DirExists(Source) then
      MergeTree(Source, Target, '', nil);
  Log('Kept existing component: ' + Relative);
end;

procedure CarryUninstallerFiles;
var
  FindRec: TFindRec;
  Names: TStringList;
  Index: Integer;
begin
  { Keeping unins000.* lets Inno Setup append to the existing uninstall log instead of
    starting a second uninstaller. }
  Names := TStringList.Create;
  try
    if FindFirst(AddBackslash(TransactionBackup) + 'unins???.*', FindRec) then begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
            Names.Add(FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
    for Index := 0 to Names.Count - 1 do begin
      AppendJournalLine('file=' + Names[Index]);
      if not MovePath(AddBackslash(TransactionBackup) + Names[Index], AddBackslash(TransactionAppDir) + Names[Index]) then
        Log('Could not keep uninstaller file ' + Names[Index] + '; a new uninstaller is created.');
    end;
  finally
    Names.Free;
  end;
end;

function DescribeStuckFiles(const Stuck: TStringList): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 0 to Stuck.Count - 1 do begin
    if Index = 5 then begin
      Result := Result + #13#10 + '  …（共 ' + IntToStr(Stuck.Count) + ' 个）';
      break;
    end;
    Result := Result + #13#10 + '  ' + Stuck[Index];
  end;
end;

{ Moves the current installation out of the way so the new version is written into an empty
  directory and a failure can put the old one back.
  1. One directory rename (fast, atomic) when nothing inside is in use.
  2. Otherwise entry by entry. Running programs' .exe/.dll files can still be moved; a folder
     open in Explorer or used as a working directory simply stays (empty) in place.
  3. Files that even then stay in use never block the install: the new files are extracted
     beside the installation and merged in, and anything still busy is completed by a
     one-time task at the next sign-in. The dialog is only shown in that last case, and it
     always offers to continue. }
procedure BeginInstallTransaction;
var
  Index, Pass, Choice: Integer;
  Candidate: String;
  Moved: Boolean;
  Stuck: TStringList;
begin
  TransactionAppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if not TransactionAllowed(TransactionAppDir) then begin
    Log('Install transaction skipped for a non-standard directory: ' + TransactionAppDir);
    exit;
  end;
  DiscardPendingUpdate(TransactionAppDir);
  if not DirExists(TransactionAppDir) then begin
    Log('Fresh installation; there is no previous installation to protect.');
    exit;
  end;

  TransactionBackup := '';
  for Index := 0 to 9 do begin
    if Index = 0 then Candidate := TransactionAppDir + '.old'
    else Candidate := TransactionAppDir + '.old' + IntToStr(Index);
    if DirExists(Candidate) then
      DelTree(Candidate, True, True, True);
    if not PathExists(Candidate) then begin
      TransactionBackup := Candidate;
      break;
    end;
  end;
  if TransactionBackup = '' then
    FailBeforeInstall('无法创建旧版本的备份目录（' + TransactionAppDir + '.old）。请删除该目录后重新运行安装器。');

  TransactionJournal := TransactionJournalPath(TransactionAppDir);
  DeleteFile(TransactionJournal);
  if not AppendJournalLine('backup=' + TransactionBackup) then
    FailBeforeInstall('无法写入安装记录文件：' + TransactionJournal + '。请检查磁盘空间和文件夹权限。');
  TransactionActive := True;

  AppendInstallLog('正在把当前版本移到备份位置，以便出错时自动恢复。');
  Moved := False;
#ifdef MyInstallerTestMode
  { Test builds can force the entry-by-entry path, which Wine never needs on its own. }
  TestNoDirectoryRename := ExpandConstant('{param:TESTNOWHOLEMOVE|0}') = '1';
#endif
  if not TestNoDirectoryRename then
    Moved := TryRename(TransactionAppDir, TransactionBackup, 3);
  if not Moved then begin
    ForceStopProductImages;
    StopProcessesUnderRoot(TransactionAppDir);
    if not TestNoDirectoryRename then
      Moved := TryRename(TransactionAppDir, TransactionBackup, 2);
  end;

  if not Moved then begin
    AppendInstallLog('安装目录中有文件夹正被其他程序使用（例如打开着的资源管理器窗口），改为逐个移动文件。');
    AppendJournalLine('mode=entries');
    Stuck := TStringList.Create;
    try
      Pass := 0;
      while True do begin
        Pass := Pass + 1;
        Stuck.Clear;
        MergeTree(TransactionAppDir, TransactionBackup, '', Stuck);
        if Stuck.Count = 0 then
          break;
        Log(IntToStr(Stuck.Count) + ' file(s) are still in use after pass ' + IntToStr(Pass));
        if Pass = 1 then begin
          { Something may have restarted in the meantime; stop it and try once more. }
          ForceStopProductImages;
          StopProcessesUnderRoot(TransactionAppDir);
          Sleep(1000);
          continue;
        end;
        Choice := SuppressibleTaskDialogMsgBox('有 ' + IntToStr(Stuck.Count) + ' 个文件正被其他程序使用',
          '以下文件暂时无法替换：' + DescribeStuckFiles(Stuck) + #13#10#13#10 +
          '可以直接继续：安装会正常完成，MyPowerTools 可以马上使用，这几个文件会在下次重新启动电脑后自动更新。' + #13#10#13#10 +
          '也可以先关闭可能在使用这些文件的程序（例如打开着的文件夹窗口、命令行窗口），然后点“重试”。',
          mbInformation, MB_RETRYCANCEL, ['重试', '继续安装（重启电脑后完成更新）'], 0, IDCANCEL);
        { Closing the dialog also continues: there is no dead end here. }
        if Choice = IDRETRY then begin
          ForceStopProductImages;
          StopProcessesUnderRoot(TransactionAppDir);
          Sleep(500);
          continue;
        end;
        UsePendingPayload := True;
        for Index := 0 to Stuck.Count - 1 do
          AppendJournalLine('stuck=' + Stuck[Index]);
        DelTree(PendingPayloadDirFor(TransactionAppDir), True, True, True);
        AppendJournalLine('pending=' + PendingPayloadDirFor(TransactionAppDir));
        AppendInstallLog('有 ' + IntToStr(Stuck.Count) + ' 个文件正在使用中；新文件先解压到 ' +
          PendingPayloadDirFor(TransactionAppDir) + '，其余部分重启电脑后自动完成。');
        break;
      end;
    finally
      Stuck.Free;
    end;
  end;

  ForceDirectories(TransactionAppDir);
  { Runtimes that are not being downloaded this time move into the new directory as-is. }
  CarryFromBackup('Runtime\dotnet', DotNetRuntimeSource = 'private-existing');
  if not NeedPythonDownload then begin
    if WantsSmartBird and DirExists(TransactionBackup + '\Runtimes\Python312\Lib\site-packages') then begin
      { The SmartBird package supplies site-packages; keep the old copy aside (inside the
        backup) so a rollback can restore it untouched. }
      AppendJournalLine('aside=Runtimes\Python312\Lib\site-packages|Runtimes\Python312.site-packages');
      MovePath(TransactionBackup + '\Runtimes\Python312\Lib\site-packages',
        TransactionBackup + '\Runtimes\Python312.site-packages');
    end;
    CarryFromBackup('Runtimes\Python312', True);
  end;
  CarryFromBackup('Runtimes\SmartBird', not WantsSmartBird);
  CarryFromBackup('Runtimes\Doubao', not WantsDoubao);
  CarryFromBackup('Tools\AndroidPlatformTools', not NeedAdbDownload);
  CarryUninstallerFiles;
  AppendInstallLog('旧版本已备份，开始写入新版本文件。');
end;

{ Where the [Files] entries extract to: the install directory, or - only when some old files
  stayed in use - a sibling directory that is merged in after extraction. }
function PayloadDir(Param: String): String;
begin
  if UsePendingPayload then
    Result := PendingPayloadDirFor(ExpandConstant('{app}'))
  else
    Result := ExpandConstant('{app}');
end;

procedure MergePendingPayload;
var
  Pending: String;
  Left: Integer;
begin
  if not UsePendingPayload then exit;
  Pending := PendingPayloadDirFor(ExpandConstant('{app}'));
  Left := MergeTree(Pending, ExpandConstant('{app}'), '', nil);
  if Left = 0 then begin
    DelTree(Pending, True, True, True);
    AppendInstallLog('之前被占用的文件现在已经可以替换，更新已全部完成。');
  end else begin
    PendingAfterRestart := True;
    AppendInstallLog(IntToStr(Left) + ' 个文件仍在使用中，会在下次登录 Windows 时自动更新。');
  end;
end;

function PowerShellQuote(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

{ Per-user installs cannot use the administrator-only "replace on reboot" list, so a one-time
  task at the next sign-in finishes the update: it stops MyPowerTools programs that sign-in
  started from the old files, moves the remaining new files in, and starts them again. }
procedure RegisterFinishAfterRestart;
var
  Lines: TArrayOfString;
  Command: String;
begin
  if not PendingAfterRestart then exit;
  Command := '"' + ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe') +
    '" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
    FinishUpdateScriptPath + '"';
  SetArrayLength(Lines, 32);
  Lines[0] := '$ErrorActionPreference = ''Continue''';
  Lines[1] := '$app = ' + PowerShellQuote(ExpandConstant('{app}'));
  Lines[2] := '$pending = ' + PowerShellQuote(PendingPayloadDirFor(ExpandConstant('{app}')));
  Lines[3] := '$runOnce = ''HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce''';
  Lines[4] := '$command = ' + PowerShellQuote(Command);
  Lines[5] := '$root = [IO.Path]::GetFullPath($app).TrimEnd(''\'') + ''\''';
  Lines[6] := '$stopped = $false';
  Lines[7] := 'foreach ($process in Get-Process -ErrorAction SilentlyContinue) {';
  Lines[8] := '    $path = $null; try { $path = $process.MainModule.FileName } catch {}';
  Lines[9] := '    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {';
  Lines[10] := '        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop; Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue; $stopped = $true } catch {}';
  Lines[11] := '    }';
  Lines[12] := '}';
  Lines[13] := 'if (Test-Path -LiteralPath $pending) {';
  Lines[14] := '    & "$env:SystemRoot\System32\robocopy.exe" $pending $app /E /MOVE /IS /IT /R:10 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null';
  Lines[15] := '    if (Test-Path -LiteralPath $pending) {';
  Lines[16] := '        if (@(Get-ChildItem -LiteralPath $pending -Recurse -File -Force -ErrorAction SilentlyContinue).Count -eq 0) {';
  Lines[17] := '            Remove-Item -LiteralPath $pending -Recurse -Force -ErrorAction SilentlyContinue';
  Lines[18] := '        } else {';
  Lines[19] := '            Set-ItemProperty -LiteralPath $runOnce -Name ''' + FinishUpdateRunOnceName + ''' -Value $command';
  Lines[20] := '        }';
  Lines[21] := '    }';
  Lines[22] := '}';
  Lines[23] := 'if ($stopped) {';
  Lines[24] := '    foreach ($name in @(''MyPowerTools.ServiceManager'', ''MyPowerTools'')) {';
  Lines[25] := '        $value = (Get-ItemProperty -LiteralPath ''HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'' -Name $name -ErrorAction SilentlyContinue).$name';
  Lines[26] := '        if ($value) { Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList (''/d /c start "" '' + $value) -WindowStyle Hidden }';
  Lines[27] := '    }';
  Lines[28] := '}';
  Lines[29] := 'if (-not (Test-Path -LiteralPath $pending)) {';
  Lines[30] := '    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue';
  Lines[31] := '}';
  ForceDirectories(ExtractFileDir(FinishUpdateScriptPath));
  { UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less script in the ANSI code page. }
  if SaveStringsToUTF8File(FinishUpdateScriptPath, Lines, False) and
     RegWriteStringValue(HKCU, RunOnceRegKey, FinishUpdateRunOnceName, Command) then
    Log('Registered the one-time finish step for the next sign-in: ' + Command)
  else
    Log('Could not register the one-time finish step.');
end;


function FriendlyDownloadError(const Message: String): String;
begin
  if ContainsText(Message, '校验') or ContainsText(Message, 'verif') or
     ContainsText(Message, 'hash') or ContainsText(Message, 'signature') then
    Result := '下载的文件没有通过完整性和签名校验（可能下载不完整，或被网络设备改动），已经自动丢弃。重试会重新下载。'
  else if ContainsText(Message, '404') then
    Result := '下载服务器上找不到这个文件。这个版本可能已经下架，请到 GitHub Releases 页面下载最新的安装器。'
  else if ContainsText(Message, '403') or ContainsText(Message, '429') then
    Result := '下载服务器暂时拒绝了请求（可能是访问过于频繁）。请等几分钟后重试。'
  else if ContainsText(Message, '12007') then
    Result := '无法解析下载服务器（github.com）的地址。请检查网络连接和 DNS 设置。'
  else if ContainsText(Message, '12175') or ContainsText(Message, '12045') or
     ContainsText(Message, '12057') or ContainsText(Message, '12157') or
     ContainsText(Message, 'certificate') or ContainsText(Message, '证书') then
    Result := 'HTTPS 安全连接校验失败。请确认电脑的日期和时间正确，并检查安全软件或代理是否拦截了 HTTPS。'
  else if ContainsText(Message, '12002') or ContainsText(Message, 'timeout') or
     ContainsText(Message, 'timed out') or ContainsText(Message, '超时') then
    Result := '连接下载服务器超时。网络可能较慢或不稳定，请稍后重试。'
  else if ContainsText(Message, '12029') or ContainsText(Message, '12030') or
     ContainsText(Message, '12031') or ContainsText(Message, '12152') then
    Result := '无法连接到下载服务器（github.com），或连接被中断。请检查网络；如果需要通过代理上网，请在 Windows“设置 → 网络和 Internet → 代理”中配置，安装器会自动使用系统代理。'
  else
    Result := '下载时发生错误。请检查网络连接后重试；如果需要通过代理上网，安装器会自动使用 Windows 系统代理设置。';
end;

procedure QueueRequiredAssets(const BaseUrl: String);
begin
  QueueAsset('{#WebCoreAsset}', BaseUrl);
  if NeedPrivateDotNet then QueueAsset('{#WebDotNetAsset}', BaseUrl);
  if NeedPythonDownload then QueueAsset('{#WebPythonAsset}', BaseUrl);
  if WantsSmartBird then QueueAsset('{#WebSmartBirdAsset}', BaseUrl);
  if WantsDoubao then QueueAsset('{#WebDoubaoAsset}', BaseUrl);
  if NeedAdbDownload then QueueAsset('{#WebAdbAsset}', BaseUrl);
end;

{ Every verified file goes to the per-version cache as soon as it exists, so closing the
  installer (or a crash) after a partial download never throws finished files away. }
procedure PreserveDownloadedAssets;
begin
  PreserveAssetInCache('{#WebCoreAsset}');
  if NeedPrivateDotNet then PreserveAssetInCache('{#WebDotNetAsset}');
  if NeedPythonDownload then PreserveAssetInCache('{#WebPythonAsset}');
  if WantsSmartBird then PreserveAssetInCache('{#WebSmartBirdAsset}');
  if WantsDoubao then PreserveAssetInCache('{#WebDoubaoAsset}');
  if NeedAdbDownload then PreserveAssetInCache('{#WebAdbAsset}');
end;

function DownloadRequiredAssets: Boolean;
var
  Attempt: Integer;
  BaseIndex: Integer;
  Choice: Integer;
  Downloaded: Boolean;
  ErrorText: String;
  FailedAsset: String;
begin
  Result := False;
  Attempt := 0;
  BaseIndex := 0;
  DownloadPage.Show;
  try
    while True do begin
      DownloadPage.Clear;
      QueuedDownloadCount := 0;
      QueueRequiredAssets(DownloadBases[BaseIndex]);
      if QueuedDownloadCount = 0 then begin
        Result := True;
        break;
      end;

      Downloaded := False;
      ErrorText := '';
      FailedAsset := '';
      try
        DownloadPage.Download;
        Downloaded := True;
      except
        ErrorText := GetExceptionMessage;
        FailedAsset := DownloadPage.LastBaseNameOrUrl;
      end;
      if Downloaded then begin
        Result := True;
        break;
      end;

      PreserveDownloadedAssets;
      if DownloadPage.AbortedByUser then begin
        AppendInstallLog('已停止下载。已完成的文件已经保留，再次点击“安装”会从中断处继续。');
        break;
      end;

      Attempt := Attempt + 1;
      AppendInstallLog('下载 ' + FailedAsset + ' 失败（第 ' + IntToStr(Attempt) + ' 次）：' + ErrorText);
      if DownloadBases.Count > 1 then
        BaseIndex := (BaseIndex + 1) mod DownloadBases.Count;
      if Attempt < AutomaticDownloadAttempts then begin
        DownloadPage.SetText('网络不稳定，正在自动重试（第 ' + IntToStr(Attempt) + ' 次）...',
          FriendlyDownloadError(ErrorText));
        DownloadPage.SetProgress(0, 0);
        Sleep(2000 * Attempt);
        continue;
      end;

      Choice := SuppressibleTaskDialogMsgBox('无法下载 MyPowerTools 组件',
        FriendlyDownloadError(ErrorText) + #13#10#13#10 +
        '出错的文件：' + FailedAsset + #13#10 +
        '详细信息：' + ErrorText + #13#10#13#10 +
        '已经下载完成的文件会保留，重试或下次运行安装器时不会重新下载。' + #13#10 +
        '安装日志：' + InstallerLogDir,
        mbError, MB_RETRYCANCEL, ['重试', '取消'], 0, IDCANCEL);
      if Choice <> IDRETRY then
        break;
      Attempt := 0;
    end;
  finally
    DownloadPage.Hide;
  end;
  if Result then
    PreserveDownloadedAssets
  else
    SaveInstallerLog;
end;

function RequiredFreeMegabytes: Integer;
begin
  { Download + cached copy + extracted files, with headroom. The previous version stays on
    disk until the new one is complete, which is already accounted for as used space. }
  Result := 600;
  if NeedPrivateDotNet then Result := Result + 450;
  if NeedPythonDownload then Result := Result + 150;
  if WantsSmartBird then Result := Result + 120;
  if WantsDoubao then Result := Result + 450;
  if NeedAdbDownload then Result := Result + 30;
end;

function HasEnoughSpaceOn(const Path, Purpose: String): Boolean;
var
  FreeBytes, TotalBytes, FreeMegabytes: Int64;
  Drive: String;
begin
  Result := True;
  Drive := ExtractFileDrive(Path);
  if Drive = '' then exit;
  if not GetSpaceOnDisk64(AddBackslash(Drive), FreeBytes, TotalBytes) then exit;
  FreeMegabytes := FreeBytes div 1048576;
  Log('Free space on ' + Drive + ': ' + IntToStr(FreeMegabytes) + ' MB; required: ' +
    IntToStr(RequiredFreeMegabytes) + ' MB (' + Purpose + ')');
  if FreeMegabytes < RequiredFreeMegabytes then begin
    SuppressibleMsgBox('磁盘空间不足。' + #13#10#13#10 +
      '安装所选组件需要 ' + Drive + ' 盘至少约 ' + IntToStr(RequiredFreeMegabytes) + ' MB 可用空间（' + Purpose + '），' +
      '目前只有 ' + IntToStr(FreeMegabytes) + ' MB。' + #13#10#13#10 +
      '请清理磁盘（例如清空回收站、删除“下载”文件夹里的大文件），或减少所选组件，然后再点击“安装”。',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function CheckInstallPreconditions: Boolean;
begin
  Result := True;
  if ShouldRunPostInstall and not TransactionAllowed(WizardDirValue) then begin
    SuppressibleMsgBox('MyPowerTools 只能安装到当前用户的固定位置：' + #13#10 + CanonicalAppDir + #13#10#13#10 +
      '请不要使用 /DIR 参数指定其他目录。', mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  Result := HasEnoughSpaceOn(ExpandConstant('{app}'), '安装目录') and
    HasEnoughSpaceOn(ExpandConstant('{tmp}'), '临时下载目录');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpReady then begin
    DetectRuntimePlan;
    Result := CheckInstallPreconditions;
    if Result then
      Result := DownloadRequiredAssets;
    if Result and ShouldRunPostInstall and WizardSilent then
      if not RunWorkerSynchronously(1) then begin
        { Best effort: the install step verifies the directory is free before touching it. }
        Log('Quiesce worker failed; falling back to the built-in shutdown sequence.');
        QuiesceInstalledProduct;
      end;
  end else if (CurPageID = ShutdownPage.ID) or
    (CurPageID = FinalizePage.ID) then begin
    if WorkerSucceeded then
      Result := True
    else begin
      if not WorkerActive then begin
        if CurPageID = ShutdownPage.ID then
          StartWorker(1)
        else
          StartWorker(2);
      end;
      Result := False;
    end;
  end else
    Result := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((not ShouldRunPostInstall or WizardSilent) and
    ((PageID = ShutdownPage.ID) or (PageID = FinalizePage.ID))) or
    (InstallFailed and (PageID = FinalizePage.ID));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = ShutdownPage.ID) and not WorkerActive then begin
    WorkerSucceeded := False;
    StartWorker(1);
  end else if (CurPageID = FinalizePage.ID) and not WorkerActive then begin
    WorkerSucceeded := False;
    StartWorker(2);
  end else if (CurPageID = wpFinished) and InstallFailed then begin
    WizardForm.FinishedHeadingLabel.Caption := 'MyPowerTools 安装没有完成';
    WizardForm.FinishedLabel.Caption := InstallVerificationProblem + #13#10#13#10 +
      '没有留下不完整的安装。请重新运行安装器；如需求助，请发送安装日志：' + #13#10 + InstallerLogDir;
  end else if (CurPageID = wpFinished) and PendingAfterRestart then
    WizardForm.FinishedLabel.Caption :=
      'MyPowerTools 已经安装完成，可以立即使用。' + #13#10#13#10 +
      '有少数文件安装时正被其他程序使用，会在下次重新启动电脑（登录 Windows）时自动更新，无需任何操作。'
  else if (CurPageID = wpFinished) and FinalizeSkipped then
    WizardForm.FinishedLabel.Caption :=
      'MyPowerTools 程序文件已经安装，但后台服务没有注册成功。' + #13#10#13#10 +
      '主程序可以正常打开；需要后台服务的功能恢复前，请重新运行本安装器完成修复。' + #13#10 +
      '安装日志：' + InstallerLogDir;
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
  Result := Result + NewLine + NewLine + '需要的磁盘空间：约 ' + IntToStr(RequiredFreeMegabytes) + ' MB';
  if InstallKind = 'upgrade' then
    Result := Result + NewLine + NewLine + '升级：' + PreviousVersion + ' → {#MyAppVersion}（出错会自动恢复原版本）'
  else if InstallKind = 'repair' then
    Result := Result + NewLine + NewLine + '修复安装：{#MyAppVersion}'
  else if InstallKind = 'downgrade' then
    Result := Result + NewLine + NewLine + '降级：' + PreviousVersion + ' → {#MyAppVersion}';
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

procedure SetInstallPhase(const Message: String);
begin
  WizardForm.StatusLabel.Caption := Message;
  WizardForm.FilenameLabel.Caption := '';
  AppendInstallLog(Message);
end;

procedure BeginCoreInstall;
begin
  SetInstallPhase('正在安装核心程序（第 1 阶段）...');
end;

procedure RequireInstalledFile(const Relative: String; var Missing: String);
begin
  if not FileExists(AddBackslash(PayloadDir('')) + Relative) then begin
    if Missing <> '' then Missing := Missing + '、';
    Missing := Missing + Relative;
  end;
end;

{ A corrupt or truncated core archive must fail the install (and roll back) instead of
  producing an installation that cannot start. }
procedure VerifyCoreLayout;
var
  Missing: String;
begin
  Missing := '';
  RequireInstalledFile('MyPowerTools.exe', Missing);
  RequireInstalledFile('Shell\MyPowerTools.Shell.Avalonia.exe', Missing);
  RequireInstalledFile('Runner\MyPowerTools.Runner.exe', Missing);
  RequireInstalledFile('Cli\MyPowerTools.Cli.exe', Missing);
  RequireInstalledFile('ServiceManager\MyPowerTools.ServiceManager.exe', Missing);
  if ShouldRunPostInstall then
    RequireInstalledFile('MyPowerTools-core-win-x64.manifest.json', Missing);
  if Missing <> '' then begin
    InstallVerificationProblem := '核心程序包不完整，缺少：' + Missing + '。下载的文件可能已损坏，或被安全软件删除。';
    AppendInstallLog(InstallVerificationProblem);
  end else
    AppendInstallLog('核心程序文件检查通过。');
end;

procedure VerifyDotNetLayout;
begin
  if not HasDotNetRuntimeAtRoot(AddBackslash(PayloadDir('')) + 'Runtime\dotnet') then begin
    InstallVerificationProblem := '.NET 运行时解压不完整。下载的文件可能已损坏，或被安全软件删除。';
    AppendInstallLog(InstallVerificationProblem);
  end;
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
    '  }' + #13#10 + '}';
  if not SaveUtf8Text(ExpandConstant('{app}\install.manifest.json'), ManifestText) then
    RaiseException('Unable to write install.manifest.json.');
end;

procedure SeedOtaState;
var
  OtaDir: String;
  ManifestSource: String;
  ManifestTarget: String;
  PublicKeySource: String;
  ReleaseText: String;
begin
  OtaDir := ExpandConstant('{localappdata}\MyPowerTools\ota-state');
  if not ForceDirectories(OtaDir) then
    RaiseException('Unable to create the OTA state directory: ' + OtaDir);

  ManifestSource := ExpandConstant('{app}\MyPowerTools-core-win-x64.manifest.json');
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
    '  "packageKind": "core",' + #13#10 +
    '  "distributionMode": "web"' + #13#10 + '}';
  if not SaveUtf8Text(OtaDir + '\installed-release.json', ReleaseText) then
    RaiseException('Unable to write installed-release.json.');
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
  { Python reads pyvenv.cfg as UTF-8; an ASCII write breaks a non-ASCII user profile path. }
  if not SaveStringsToUTF8FileWithoutBOM(ConfigPath, Lines, False) then
    RaiseException('Unable to update Doubao Python runtime configuration.');
end;

{ Downloads of other versions are never reused; keep only this version's verified files
  (they make a later repair of this version work offline). }
procedure CleanupOtherCachedVersions;
var
  CacheRoot: String;
  FindRec: TFindRec;
  Names: TStringList;
  Index: Integer;
begin
  CacheRoot := ExpandConstant('{localappdata}\MyPowerTools\installer-cache');
  Names := TStringList.Create;
  try
    if FindFirst(CacheRoot + '\*', FindRec) then begin
      try
        repeat
          if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
             (FindRec.Name <> '.') and (FindRec.Name <> '..') and
             (CompareText(FindRec.Name, '{#MyAppVersion}') <> 0) then
            Names.Add(FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
    for Index := 0 to Names.Count - 1 do begin
      Log('Removing installer cache of another version: ' + Names[Index]);
      DelTree(CacheRoot + '\' + Names[Index], True, True, True);
    end;
  finally
    Names.Free;
  end;
end;

{ The files are on disk but failed verification: put the previous installation back (or
  remove the incomplete fresh one, including its shortcuts and uninstall entry). }
procedure AbandonInstall;
var
  AppDir: String;
  Outcome: String;
begin
  InstallFailed := True;
  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if TransactionActive then begin
    RollbackInstallTransaction;
    Outcome := '原有安装已经恢复，可以继续使用。';
  end else begin
    if TransactionAllowed(AppDir) and DirExists(AppDir) then
      DelTree(AppDir, True, True, True);
    if TransactionAllowed(AppDir) then
      DelTree(PendingPayloadDirFor(AppDir), True, True, True);
    RegDeleteKeyIncludingSubkeys(HKCU, UninstallRegKey);
    if ShouldRunPostInstall then begin
      DeleteFile(ExpandConstant('{autoprograms}\MyPowerTools.lnk'));
      DeleteFile(ExpandConstant('{autodesktop}\MyPowerTools.lnk'));
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MyPowerTools');
    end;
    Outcome := '不完整的文件已经清理。';
  end;
  AppendInstallLog('安装校验失败，已撤销：' + InstallVerificationProblem);
  SaveInstallerLog;
  SuppressibleMsgBox('MyPowerTools 安装没有完成。' + #13#10#13#10 + InstallVerificationProblem + #13#10#13#10 +
    Outcome + '请暂时关闭安全软件的实时扫描（或把安装目录加入信任），然后重新运行安装器。' + #13#10#13#10 +
    '安装日志：' + InstallerLogDir, mbCriticalError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    AppendInstallLog('开始写入 MyPowerTools 安装文件。');
    BeginInstallTransaction;
    ClearLegacyDotNetRoot;
  end;
  if CurStep = ssPostInstall then begin
    if InstallVerificationProblem <> '' then begin
      AbandonInstall;
      exit;
    end;
    MergePendingPayload;
    CommitInstallTransaction;
    RewriteDoubaoVenvConfig;
    WriteInstallManifest;
    if ShouldRunPostInstall then
      SeedOtaState;
    RegisterFinishAfterRestart;
    AppendInstallLog('核心文件与运行时组件安装完成。');
    if ShouldRunPostInstall and WizardSilent and
      not RunWorkerSynchronously(2) then begin
      { The files are complete and usable; report the partial result through the exit code
        instead of a script error dialog. Re-running the installer repairs it. }
      FinalizeSkipped := True;
      AppendInstallLog('MyPowerTools 后台服务注册失败；重新运行安装器即可修复。安装日志：' + InstallerLogDir);
    end;
  end;
  if (CurStep = ssDone) and ShouldRunPostInstall and not InstallFailed then
    CleanupOtherCachedVersions;
end;
