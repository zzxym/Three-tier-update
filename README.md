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
  应用清单与文件下载（支持分片续传），并以 token 做租户鉴权。配套 `PropelControl`（发布工具，
  已集成清单 / 索引生成器，原为独立的 `MakeManifest.exe`）。
- **第 2 层 · 分支 / 缓存（Repel）**：`RepelService` 部署在客户那边的一台电脑上，回源 Propel 并做磁盘缓存，
  同时提供 peer 注册与查询，让客户端之间走 P2P 就近分发；`RepelControl` 用于维护
  注册表（`repel.lst`）。对应原架构中「分支缓存」一层。
- **第 3 层 · 客户端（Impel）**：`Impel` 是桌面客户端，悬浮坞式启动菜单，
  内置更新引擎（manifest 比对 / 差异下载 / sha256 校验 / 损坏修复）与 P2P 提供端
  （把已装文件共享给同局域网其他客户端）。对应原架构中挂在无盘工作站上的「游戏菜单」。

## 目录结构（仓库源码）

```
/
  Impel/           客户端：悬浮坞、更新引擎、P2P 提供端、配置、图标、启动器、设置
  ImpelUpdater/    客户端独立升级器：旧进程退出后替换 impel.exe，失败自动回滚
  RepelService/    HTTP P2P 注册 / 缓存服务（回源 Propel 并磁盘缓存，提供 peer 查询）
  RepelControl/    管控台（维护 repel.lst 注册表）
  repelView/       查看器（查看分发清单 / 缓存状态）
  PropelService/   发布源 HTTP 服务（中心源：all.lst + 文件下载 + 令牌鉴权）
  PropelControl/   发布工具（已集成清单 / 索引生成器）
  README.md
  .gitignore
```

## 构建

各子工程均为 .NET Framework 4.8，使用 MSBuild / `dotnet build` 编译：

```powershell
# 客户端（编译后直接产出到 dist/impel）
dotnet build -c Release Impel/Impel.csproj
dotnet build -c Release ImpelUpdater/ImpelUpdater.csproj

# 服务端与工具
dotnet build -c Release RepelService/RepelService.csproj
dotnet build -c Release PropelService/PropelService.csproj
dotnet build -c Release RepelControl/RepelControl.csproj
dotnet build -c Release PropelControl/PropelControl.csproj
dotnet build -c Release repelView/repelView.csproj
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

## 客户端自更新（Repel → Impel）

客户端程序本身的升级**不经过** `all.lst` / `repel.lst`：否则它会作为应用出现在用户菜单里，且可能覆盖 `Impel.json` 等用户配置。改用一组独立端点：

| 端点 | 鉴权 | 用途 |
|---|---|---|
| `GET /impel/version` | 数据 token | 当前升级包版本与文件清单（含 sha256） |
| `GET /impel/file?path=` | 数据 token | 只下发清单内登记的文件 |
| `GET /admin/impel/clients` | `X-Admin-Token` | 在线客户端标识 / 版本 / 计算机名 |
| `POST /admin/impel/upgrade` | `X-Admin-Token` | 立即唤醒所选客户端（或全部）重新检查升级基线，可选 `target` 指定单台 |

**配置**：在 `repel.json` 增加 `adminToken`，或在 `RepelControl` 的「管理密钥」中输入新值并保存。管控台**不会回显**已配置的密钥，只显示「已配置 / 未配置」；密码框留空保存表示不修改已有密钥。该密钥由管理端独占，**不得**与所有 Impel 客户端共享的 `tokens` 相同；未配置时管理端点一律返回 503。**保存配置时若未配置管理密钥，管控台会弹出警告并默认取消保存**，避免误存无鉴权状态。

**发布流程**：

1. 构建新版客户端（产出 `dist/impel`，含 `impel.exe` 与 `ImpelUpdater.exe`）。
2. 在部署 Repel 的机器上打开 `RepelControl` →「导入新版客户端包」→ 选中新版目录下的 `impel.exe`。程序文件会被复制到与 `RepelService.exe` 同目录的 `impelpkg/`，并生成带 sha256 的 `version.json`。
3. 导入升级包即设定「版本基线」（`impelpkg/version.json` 的 `version`）；客户端下一次 `/changes` 长轮询会自动比对并升级，离线机器开机即升。点「立即同步升级」可立即唤醒所选客户端检查基线（免去最长 90 秒等待），无需手动逐个下发。

**客户端执行**：Impel 在 `/changes` 长轮询中上报稳定的 `clientId` 与自身版本；收到 `cmd=upgrade` 后先用 `lastCmdId` 去重，空闲时下载整包到 `SelfUpdate.staging` 并逐文件校验，随后拉起 `ImpelUpdater.exe` 并退出。updater 等待旧进程与单实例互斥体释放，备份原文件（`.bak`）→ 替换 → 启动新版 → 8 秒内确认存活；失败自动回滚并重新启动旧版。过程记录在 `%LOCALAPPDATA%\Impel\selfupdate.log`。

**约束**：

- `ImpelUpdater.exe` 不参与自更新（只随客户端安装包分发），避免替换正在运行的自身；旧客户端若缺此文件会跳过升级。
- 版本基线持久化在 `impelpkg/version.json`，服务重启不丢、无 TTL、无内存队列；离线客户端无论隔多久上线，首次长轮询即被拉平到基线版本。
- 客户端收到 `cmd=upgrade` 后用 `lastCmdId` 去重（cmdId 为基线版本号），升级成功后版本与基线一致即不再收到指令；正在更新第三方应用时暂缓，下一轮再取。
- `impelpkg/` 只含程序文件，`Impel.json`、`*.log`、`hashcache.json`、`*.lst` 等用户数据不入包。

### 由 Propel 自动发起升级

把升级的「发起方」从人工在 RepelControl 导入，上移到 Propel（中心源），实现「发布即全网升级」：

- **PropelControl** 新增「发布 Impel 升级包」「发布 Repel 升级包」：选构建产物目录后，自动计算各文件 sha256 并生成 `Root/impel/version.json`、`Root/repel/version.json`，把程序文件拷贝到 `Root/impel/`、`Root/repel/`（自动排除 `Impel.json`/`repel.json` 等用户数据与 `*.log`/`*.bak` 临时文件）。
- **RepelService** 每 60 秒检查 Propel 上的两个 `version.json`（沿用 `source` 指向的 Propel 地址，并带同样的数据 token）：
  - `impel/`：比本地 `impelpkg/` 基线更新则自动拉取整包覆盖 → Impel 客户端按既有基线机制自动升级（**全自动**，无需管理员逐个导入或点「立即同步」）。
  - `repel/`：比自身版本更新则拉取整包到 `RepelUpdate.staging/`，启动 `RepelUpdater.exe` 停服 → 替换 → 重启服务（**服务端自更新**）。
- **RepelUpdater** 与 **ImpelUpdater** 自身不参与替换（运行中的 exe 不可写）；`repel.json` 等用户配置在替换时被显式跳过，永不覆盖。
- 所有下载文件均经 sha256 校验（与 `version.json` 清单比对），保证完整性。

> 前提：Repel 的 `repel.json` 已配置 `source` 指向 Propel 地址；PropelService **无需改动**（它本就是发布根目录的静态文件服务，可分发任意子目录）。

## 说明

- 哈希缓存使用「大小 + 时间戳 + 首段内容指纹」避免陈旧命中。
- 客户端之间可通过 P2P 直接分发已装文件，降低中心源压力，并提高内网下载速度。
- 本仓库只包含源代码；运行所需二进制请自行构建或取自发布包。

## 许可

本仓库采用 **CC BY-NC-SA 4.0** 授权：允许个人学习 / 非盈利「自用」，**禁止商业使用**，
且修改版本须以相同条款（ShareAlike）发布。任何公司 / 组织将其部署以提供对第三方
（客户）的服务，均视为商业使用，不在授权范围内，须事先获得著作权人书面许可。
详见仓库根目录 `LICENSE`。
