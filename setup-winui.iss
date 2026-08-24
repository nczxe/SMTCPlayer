; SMTC Player (WinUI) - Inno Setup 安装脚本
; 依赖: Inno Setup 6+ (https://jrsoftware.org/isinfo.php)
; WinUI 版本为 SelfContained 部署，输出目录包含完整的 Windows App SDK 运行时

#define MyAppName "SMTC Player"
#define MyAppEdition "WinUI"
; 版本号由 build.bat 从 smtc-ui\Directory.Build.props 解析后经 /DMyAppVersion 传入；
; 此处仅为直接手动运行 ISCC 时的兜底值
#ifndef MyAppVersion
  #define MyAppVersion "1.1.1"
#endif
#define MyAppBuild "1"
#define MyAppPublisher "FR-NEXT"
#define MyAppURL "https://github.com/fr-next/NCM-SMTCPlayer"
#define MyAppExeName "SMTCPlayer.WinUI.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567891}
AppName={#MyAppName} ({#MyAppEdition})
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.{#MyAppBuild}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName} {#MyAppEdition}
DefaultGroupName={#MyAppName} {#MyAppEdition}
AllowNoIcons=yes
OutputDir=dist
OutputBaseFilename=SMTCPlayer_WinUI_Setup_v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他快捷方式"

[Files]
; .NET WinUI UI（SelfContained，整体复制输出目录）
Source: "smtc-ui\SMTCPlayer.WinUI\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,stderr.log,stdout.log"
; Python server（已由 csproj Content 复制到输出目录，上面已包含）

[Icons]
Name: "{autoprograms}\{#MyAppName} {#MyAppEdition}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName} {#MyAppEdition}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName} {#MyAppEdition}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im SMTCPlayer.WinUI.exe"; Flags: runhidden; RunOnceId: "KillSMTCPlayerWinUI"
Filename: "taskkill"; Parameters: "/f /im SMTCPlayerServer.exe"; Flags: runhidden; RunOnceId: "KillSMTCPlayerServer"
Filename: "taskkill"; Parameters: "/f /im NeteaseWatcher.exe"; Flags: runhidden; RunOnceId: "KillNeteaseWatcher"
Filename: "taskkill"; Parameters: "/f /im netease-watcher.exe"; Flags: runhidden; RunOnceId: "KillLegacyWatcher"

[Code]
function InitializeSetup: Boolean;
begin
  Result := True;
end;
