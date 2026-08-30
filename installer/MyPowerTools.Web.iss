#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#ifndef MyReleaseChannel
  #define MyReleaseChannel "stable"
#endif
#ifdef MyAllowUnsigned
  #define MyUnsignedParameter " -AllowUnsigned"
#else
  #define MyUnsignedParameter ""
#endif

[Setup]
AppId={{E79E467D-38A3-4D8C-A469-3913057A2828}
AppName=MyPowerTools Web Setup
AppVersion={#MyAppVersion}
AppPublisher=MyPowerTools
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=MyPowerTools-Web-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\assets\MyPowerTools.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}

[Files]
Source: "..\scripts\install-windows-web.ps1"; DestDir: "{tmp}\MyPowerToolsWebSetup"; Flags: ignoreversion
Source: "..\scripts\ed25519.cs"; DestDir: "{tmp}\MyPowerToolsWebSetup"; Flags: ignoreversion
Source: "..\artifacts\release\runtime-components\runtime-components.json"; DestDir: "{tmp}\MyPowerToolsWebSetup"; Flags: ignoreversion
Source: "..\artifacts\release\runtime-components\runtime-components.json.sig"; DestDir: "{tmp}\MyPowerToolsWebSetup"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\artifacts\release\ota-signing-public-key.txt"; DestDir: "{tmp}\MyPowerToolsWebSetup"; Flags: ignoreversion skipifsourcedoesntexist

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{tmp}\MyPowerToolsWebSetup\install-windows-web.ps1"" -Version ""{#MyAppVersion}"" -Channel ""{#MyReleaseChannel}""{#MyUnsignedParameter}"; StatusMsg: "Downloading and verifying MyPowerTools components..."; Flags: runhidden waituntilterminated
