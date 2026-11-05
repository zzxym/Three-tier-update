; 天一网络三层更新 —— 局域网服务端（Repel 端）安装包
; 需要管理员权限，安装到 Program Files
; 由 CI 调用 ISCC 编译，源码与二进制均相对本脚本所在目录（installer\）解析。

#define MyAppName "天一网络三层更新 - 服务端"
#define MyAppVersion "26.1.0"
#define MyPublisher "漳州市天一网络"

[Setup]
AppId={{A2B3C4D5-E6F7-4B8C-9D0E-1F2A3B4C5D6E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\installer-output
OutputBaseFilename=ThreeTier-RepelServer-setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
LicenseFile=..\LICENSE

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; —— 局域网服务端（Repel 端）：RepelService + RepelControl + repelView ——
Source: "..\stage\repel\*"; DestDir: "{app}\repel"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 开始菜单
Name: "{group}\天一缓存服务器"; Filename: "{app}\repel\RepelControl.exe"
Name: "{group}\天一更新查看器"; Filename: "{app}\repel\repelView.exe"
; 桌面快捷方式
Name: "{autodesktop}\天一缓存服务器"; Filename: "{app}\repel\RepelControl.exe"
Name: "{autodesktop}\天一更新查看器"; Filename: "{app}\repel\repelView.exe"
