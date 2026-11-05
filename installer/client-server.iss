; 天一网络三层更新 —— 客户端 / 局域网服务端 一体安装包
; 安装时二选一：客户端（Impel 端）或 局域网服务端（Repel 端）
; 由 CI 调用 ISCC 编译，源码与二进制均相对本脚本所在目录（installer\）解析。

#define MyAppName "天一网络三层更新"
#define MyAppVersion "26.1.0"
#define MyPublisher "漳州市天一网络"

[Setup]
AppId={{8F4C9A2B-1E3D-4C5A-9B6E-2A1F3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\installer-output
OutputBaseFilename=ThreeTier-ClientServer-setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
; 安装向导内嵌许可（CC BY-NC-SA 4.0，禁止商用）
LicenseFile=..\LICENSE

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; —— 客户端（Impel 端）——
Source: "..\stage\impel\*"; DestDir: "{localappdata}\Impel"; Check: IsClient; Flags: ignoreversion recursesubdirs createallsubdirs
; —— 局域网服务端（Repel 端）：RepelService + RepelControl + repelView ——
Source: "..\stage\repel\*"; DestDir: "{app}\repel"; Check: IsServer; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单（按组件二选一）
Name: "{group}\天一软件菜单"; Filename: "{localappdata}\Impel\impel.exe"; Check: IsClient
Name: "{group}\天一缓存服务器"; Filename: "{app}\repel\RepelControl.exe"; Check: IsServer
Name: "{group}\天一更新查看器"; Filename: "{app}\repel\repelView.exe"; Check: IsServer
; 桌面快捷方式（按组件二选一，使用指定名称）
Name: "{autodesktop}\天一软件菜单"; Filename: "{localappdata}\Impel\impel.exe"; Check: IsClient
Name: "{autodesktop}\天一缓存服务器"; Filename: "{app}\repel\RepelControl.exe"; Check: IsServer
Name: "{autodesktop}\天一更新查看器"; Filename: "{app}\repel\repelView.exe"; Check: IsServer

[Code]
var
  ModePage: TInputOptionWizardPage;

procedure InitializeWizard();
begin
  // 单选页：客户端 / 局域网服务端 二选一
  ModePage := CreateInputOptionPage(wpWelcome,
    '选择安装类型',
    '请选择要安装的组件',
    '本安装包包含「客户端」与「局域网服务端」，二者二选一安装。',
    True,   { AExclusive = 单选 }
    False); { ALeft }
  ModePage.Add('客户端（Impel 端）');
  ModePage.Add('局域网服务端（Repel 端）');
  ModePage.Values[0] := True;  // 默认选客户端
end;

function IsClient(): Boolean;
begin
  Result := ModePage.Values[0];
end;

function IsServer(): Boolean;
begin
  Result := ModePage.Values[1];
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // 客户端固定安装到 %LOCALAPPDATA%\Impel，无需再让用户选择 Program Files 目标目录
  if (PageID = wpSelectDir) and IsClient() then
    Result := True;
end;
