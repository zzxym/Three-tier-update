; 天一网络三层更新 —— Propel 发布源安装包
; 包含 PropelControl（发布工具）+ PropelService（发布源 HTTP 服务）
; 由 CI 调用 ISCC 编译，路径相对本脚本所在目录（installer\）解析。

#define MyAppName "天一网络三层更新 - Propel 发布源"
#define MyAppVersion "26.1.0"
#define MyPublisher "漳州市天一网络"

[Setup]
AppId={{7E3B8D1C-2F4A-4B6C-8D5E-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\installer-output
OutputBaseFilename=ThreeTier-Propel-setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; —— Propel 发布源：PropelService + PropelControl ——
Source: "..\stage\propel\*"; DestDir: "{app}\propel"; Excludes: "*.pdb;*.config;*.ico"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\天一源服务器"; Filename: "{app}\propel\PropelControl.exe"
Name: "{autodesktop}\天一源服务器"; Filename: "{app}\propel\PropelControl.exe"
