; 天一网络三层更新 —— 客户端（Impel 端）安装包
; 按当前用户安装（无需管理员/UAC），固定安装到 %LOCALAPPDATA%\Impel
; 由 CI 调用 ISCC 编译，源码与二进制均相对本脚本所在目录（installer\）解析。

#define MyAppName "天一网络三层更新 - 客户端"
#define MyAppVersion "26.1.0"
#define MyPublisher "漳州市天一网络"

[Setup]
AppId={{F1A2B3C4-D5E6-4A7B-8C9D-0E1F2A3B4C5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
; 按用户安装：固定到当前用户的 LOCALAPPDATA\Impel（可写、无需提权）
DefaultDirName={localappdata}\Impel
DefaultGroupName={#MyAppName}
OutputDir=..\installer-output
OutputBaseFilename=ThreeTier-Client-setup
Compression=lzma2
SolidCompression=yes
; 按当前用户安装，不请求管理员权限（无 UAC 弹窗）
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
; 抑制"安装到用户区域"的提示
UsedUserAreasWarning=no
; 安装向导内嵌许可（CC BY-NC-SA 4.0，禁止商用）
LicenseFile=..\LICENSE

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; —— 客户端（Impel 端）——
Source: "..\stage\impel\*"; DestDir: "{localappdata}\Impel"; Excludes: "*.pdb;*.config;*.ico"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单（当前用户）
Name: "{group}\天一软件菜单"; Filename: "{localappdata}\Impel\impel.exe"
; 桌面快捷方式（当前用户）
Name: "{autodesktop}\天一软件菜单"; Filename: "{localappdata}\Impel\impel.exe"
