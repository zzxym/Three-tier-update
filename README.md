# 游戏和软件三层对比更新系统及客户端菜单

一个用于内网环境的绿色软件集中启动 + 版本比对 / 差异更新 / 损坏修复系统。
采用 **C# / .NET Framework 4.8** 实现。

## 背景

本项目的雏形是我早年用 **VB6** 编写的**网吧无盘游戏三层更新游戏菜单**：

- 客户端是挂在网吧无盘工作站上的游戏菜单，负责启动游戏并按需更新；
- 服务端依赖 **Apache** 等通用 HTTP 服务器来分发游戏文件与版本清单；
- 整体采用「中心源 → 分支缓存 → 客户端」的**三层更新**结构。

随着时间推移，这套方案暴露出明显局限：

- **VB6 只能编译 32 位 EXE**，在今天的 **Windows 10 / Windows 11** 上兼容性、性能与稳定性都不再理想；
- 服务端强依赖 **Apache** 等外部 HTTP 服务器，部署、配置与运维成本高，不利于快速落地。

因此本项目用 **C# / .NET Framework 4.8** 对客户端做了完整重写，并**重写服务器端**——
自带轻量 HTTP 服务（不再依赖 Apache 等第三方服务器），让整个三层更新系统的**部署更加简单**。

## 三层架构

设计上延续了最初的三层更新思想，但全部用自研 .NET 服务实现：

```
Propel（中心源 / 发布源）  ──►  Repel（分支 / 缓存 / P2P 注册）  ──►  Impel（客户端）
```

- **第 1 层 · 源（Propel）**：`PropelService` 部署在自己公司，并对外映射端口，提供发布源 HTTP 服务，对外暴露 `all.lst`
  应用清单与文件下载（支持分片续传），并以 token 做租户鉴权。配套 `PropelControl`（发布工具）
  与 `ManifestGen`（清单 / 启动信息生成器 `MakeManifest.exe`）。
- **第 2 层 · 分支 / 缓存（Repel）**：`RepelService` 部署在客户那边的一台电脑上，回源 Propel 并做磁盘缓存，
  同时提供 peer 注册与查询，让客户端之间走 P2P 就近分发；`RepelControl` 用于维护
  注册表（`repel.lst`）。对应原架构中「分支缓存」一层。
- **第 3 层 · 客户端（Impel）**：`Impel` 是桌面客户端，悬浮坞式启动菜单，
  内置更新引擎（manifest 比对 / 差异下载 / sha256 校验 / 损坏修复）与 P2P 提供端
  （把已装文件共享给同局域网其他客户端）。对应原架构中挂在无盘工作站上的「游戏菜单」。

## 目录结构（仓库源码）

```
MenuClient/
  Impel/           客户端：悬浮坞、更新引擎、P2P 提供端、配置、图标、启动器、设置
  RepelService/    HTTP P2P 注册 / 缓存服务（回源 Propel 并磁盘缓存，提供 peer 查询）
  RepelControl/    管控台（维护 repel.lst 注册表）
  repelView/       查看器（查看分发清单 / 缓存状态）
  PropelService/   发布源 HTTP 服务（中心源：all.lst + 文件下载 + 令牌鉴权）
  PropelControl/   发布工具（生成 / 维护发布清单）
  ManifestGen/     清单与启动信息生成器（MakeManifest.exe）
  README.md
  .gitignore
```

## 构建

各子工程均为 .NET Framework 4.8，使用 MSBuild / `dotnet build` 编译：

```powershell
# 客户端（编译后直接产出到 dist/impel）
dotnet build -c Release Impel/Impel.csproj

# 服务端与工具
dotnet build -c Release RepelService/RepelService.csproj
dotnet build -c Release PropelService/PropelService.csproj
dotnet build -c Release RepelControl/RepelControl.csproj
dotnet build -c Release PropelControl/PropelControl.csproj
dotnet build -c Release repelView/repelView.csproj
dotnet build -c Release ManifestGen/ManifestGen.csproj
```

`dist/` 是最终部署布局（含 `impel/`、`repel/`、`propel/` 等可执行目录），由各工程
`bin\Release\net48` 产出归集而来，属构建产物，不入库。

## 配置与运行

- **PropelService（中心源）**：由同目录 `propel.json` 配置发布根目录、监听地址与令牌；
  对外提供 `/all.lst` 与文件下载（自带 HTTP 服务，无需 Apache 等外部服务器）。
- **RepelService（分支 / 缓存）**：由 `repel.json` 配置回源地址（Source = Propel）、
  监听端口与令牌；为客户端提供 `all.lst` / `repel.lst` 与文件下载 / 缓存，并维护 P2P 候选。
- **Impel（客户端）**：由 `Impel.json` 配置服务器地址 / 令牌、`softwareRoot`（安装根目录）、
  悬浮坞与自启动；「设置」界面可可视化编辑。

三者均通过 **token 租户鉴权**，未配置令牌时默认为空（部署时必须填写，避免共享密钥泄露）。

## 更新流程

1. Impel 定时（或打开菜单时）经 Repel（或直接连 Propel）拉取 `all.lst`，得到应用列表与版本。
2. 对每个应用逐文件 `sha256` 比对，缺失 / 不一致的从 Repel（P2P 或回源 Propel）下载。
3. **全部文件下载成功**才落盘 `manifest.json` 并记录已装版本；部分失败不误报「已最新」。
4. 软件正在运行时跳过更新；本地文件损坏（哈希不符）自动重新下载修复。

## 说明

- 哈希缓存使用「大小 + 时间戳 + 首段内容指纹」避免陈旧命中。
- 客户端之间可通过 P2P 直接分发已装文件，降低中心源压力，并提高内网下载速度。
- 本仓库只包含源代码；运行所需二进制请自行构建或取自发布包。

## 许可

本仓库采用 **CC BY-NC-SA 4.0** 授权：允许个人学习 / 非盈利「自用」，**禁止商业使用**，
且修改版本须以相同条款（ShareAlike）发布。任何公司 / 组织将其部署以提供对第三方
（客户）的服务，均视为商业使用，不在授权范围内，须事先获得著作权人书面许可。
详见仓库根目录 `LICENSE`。
