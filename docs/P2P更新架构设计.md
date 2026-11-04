# P2P 更新架构设计（客户端互为更新源）

> 状态：设计稿（待评审）。本文档描述在现有「总服务器(origin) → 分支服务器(branch)」两级之上，叠加「客户端 peer 互为更新源」的局域网 P2P 分发层。
> 最后更新：2026-10-27
> 关联决策：R03/R19/R32（三级拓扑）、R04/R18/R40（sha256 标识与跨源校验、回退安全、抗污染）

---

## 1. 目标与原则

- **降服压力、提速**：客户端只向服务器拉取「本局域网内尚无的其他客户端已拥有的文件」，其余尽量从局域网 peer 直取，使总/分支服务器仅承担「权威基准 + 兜底回退」。
- **不削弱安全性**：沿用既有原则——**官方 sha256 是唯一可信基准**。任何来源（peer / branch / origin）提供的文件都必须通过官方 manifest 的 sha256 校验，否则丢弃并回退。peer 不可信，但哈希可信 → 天然抗污染。
- **故障开放、可回退**：peer 离线 / 不可达 / 校验失败 → 自动回退 branch → origin。任一环节不可用都不影响「本地已可正常运行」的软件。
- **与 Go 版完全兼容**：manifest 文件格式、目录布局、token 约定保持不变，Go 与 C# 节点可混部。

---

## 2. 总体拓扑

```
                 ┌──────────────┐
                 │ 总服务器 origin│  ← 权威基准：发布带 sha256 的 manifest + 文件
                 │ (广域网)      │     仅「首次 / 全网缺失」时作为兜底
                 └──────┬───────┘
                        │ 回源（带 tenant token）
                 ┌──────┴───────┐
                 │ 分支服务器 branch│  ← 协调 + 鉴权 + 可选常驻缓存兜底（默认开）
                 │ (客户局域网)   │     维护在线 peer 注册表；缓存作为常驻超级 peer 兜底
                 └──────┬───────┘
        ┌───────────────┼────────────────┐
   ┌────┴────┐     ┌────┴────┐      ┌────┴────┐
   │客户端 A  │◄───►│客户端 B  │◄────►│客户端 C  │   ← 局域网 peer：既是消费者，也是提供者
   │(peer)   │     │(peer)   │      │(peer)   │      只暴露「已通过官方校验、属当前版本」的文件
   └─────────┘     └─────────┘      └─────────┘
```

**关键定位**：总服务器仍是唯一权威基准（发布哈希、版本号）；分支服务器是**协调 + 鉴权 + 可选常驻缓存兜底（默认开）**，本质是一个保证在局、保证在线的「超级 peer」；客户端 peer 仅是「局域网分发加速层」，不改变「以官方哈希为准」的校验模型。

---

## 3. 数据流与回退顺序

以「客户端需要文件 `X`（某 app 的某个 path）」为例，下载器按如下候选源顺序尝试，直到 sha256 命中：

```
candidates = [ peerSource₁, peerSource₂, …, branch, origin ]
for src in candidates:
    data = GET(src.url)            # peer/branch/origin 同一 URL 形态
    if sha256(data) == manifest[X].sha256:
        accept; break
    else:
        discard; continue          # 任何源校验失败都不接受，试下一源
if 全部失败:
    标记该 app 为 "updatable/needfix"，保留本地旧版本（失败开放）
```

- **manifest 获取**同样走该回退链：优先向 branch 取（branch 若已缓存则更快，否则回源一次直供），缺失再回 origin。
- peer 候选由「分支服务器协调器」返回（见 §5.4），不含 origin，origin 固定作为链尾兜底。

---

## 4. 数据结构（复用现有，仅增量）

### 4.1 既有 Manifest / FileEntry（不变，Go↔C# 共用）

```json
{
  "app": "easicare",
  "version": "1.2.3",
  "files": [
    { "path": "bin/easicare.exe", "size": 123456, "sha256": "abcd…" },
    { "path": "res/icon.png",     "size": 2048,   "sha256": "ef01…" }
  ]
}
```
- `path`：`App\` 下的相对路径，统一 `/` 分隔（与 Go 版 `ToSlashPath/FromSlashPath` 一致）。
- 目录布局：`root/App/`（运行目录）、`root/App.staging/`（暂存）、`root/App.old/`（回滚）、`root/manifest.json`、`root/.updating.lock`。**与 Go 版 `engine.go` 完全一致**，保证混部。

### 4.2 新增：Peer 注册 / 查询协议（在 branch 上）

```jsonc
// 客户端上线/心跳：向 branch 上报自己「可提供」的内容
POST /peer/register
{
  "appId": "easicare",
  "version": "1.2.3",
  "hashes": ["abcd…", "ef01…", …],   // 本机已校验通过的文件 sha256 集合
  "providerUrl": "http://192.168.1.23:45123"
}

// 查询某文件可用源（不含 origin，origin 由下载器固定兜底）
GET /peer/query?appId=easicare&hash=abcd…
→ { "sources": [ "http://192.168.1.23:45123", "http://192.168.1.45:45123" ] }
```

- `providerUrl`：该客户端本地提供端的局域网 HTTP 地址。
- 注册带 TTL / 心跳续期；branch 定期清理离线 peer。
- 查询仅返回「声明拥有该 hash 且在线」的 peer，下载器再逐个尝试（§3 校验逻辑）。

### 4.3 大文件块级扩展（>32MB，混合同步策略）
- **阈值**：单文件 `size > 32MB` 采用块级同步；`≤ 32MB` 仍整文件同步。块大小固定 2MB（`BLOCK=2MB`），末块不足补满。
- **manifest 扩展**：大文件在 `FileEntry` 中追加 `blocks` 字段（旧客户端忽略未知字段，自动退化为整文件同步，向后兼容）：
  ```json
  { "path": "bin/big.exe", "size": 67108864, "sha256": "<整文件哈希>",
    "blocks": [ { "off": 0, "len": 8388608, "sha256": "b1…" },
                { "off": 8388608, "len": 8388608, "sha256": "b2…" }, … ] }
  ```
  `blocks[].sha256` 为每块独立哈希；整文件 `sha256` 保留用于整体校验兜底。
- **块级 URL**：`GET /{tenant}/{appid}/{filepath}?off={off}&len={len}` 取单块（提供端/branch/origin 同一形态）；整文件 URL 不含查询串，向后兼容。
- **去重**：相同块（如多 exe 共用运行时）跨文件按块哈希复用，进一步减少总传输。

---

## 5. 组件设计

### 5.1 总服务器 Origin（不变）
现有 `server/origin.go` 静态文件服务 + tenant token 鉴权，保持原样，作为权威基准与链尾兜底。

### 5.2 分支服务器 Branch（协调 + 鉴权 + 可选常驻缓存兜底，默认开）
branch 的核心职责是**协调**与**鉴权**，文件缓存从核心降级为「可选的常驻兜底」（默认开启）。它本质是一个保证在局、保证在线的「超级 peer」——因为 peer 终端会关机/离线，branch 常驻缓存可兜底「首包 peer 全离线」后的重复请求，避免反复回 origin；成本仅磁盘。
- `POST /peer/register`：写入/续期内存（或磁盘）注册表 `{appId → {hash → [providerUrl]}}`。
- `GET /peer/query`：按 appId+hash 返回 providerUrl 列表（可随机/轮询打散以均衡）。
- **可选常驻缓存**：对 `GET /<tenant>/<appid>/...` 命中本地缓存则直供（局域网内常驻源）；未命中则回源 origin 取来直供，并可落盘长期保留，作为跨 peer 离线 / branch 重启的兜底。缓存逻辑在 C# 分支服务器内实现（参考既有 Go `server/branch.go` 的穿透缓存行为平移）；若选「形态 A（去持久缓存）」，则仅临时缓存或不缓存，缺失时每次回源一次直供。
- 与现有 token 鉴权复用，防止伪造注册污染候选表；branch 持有 origin 的 long-term secret，对客户端不暴露。
- **监听端口**：默认 `80`（可在配置/设置中修改，与客户端 PeerProvider 的 `45123` 区分）。注意 80 端口在 Windows 上可能需 `netsh http add urlacl` 授权，且易与其他 Web 服务冲突，故支持手动修改。
- **branch 地址（手动设置）**：设置界面**仅填「主机名或 IP」**，**默认填充 `update`**（不带 `http://` 前缀），程序内部按 `http://<host>:<port>` 拼装；主机名 `update` 经局域网 DNS 或各机 `hosts` 解析到 branch 机，端口默认 80（可在设置中修改）。由部署者/用户手动填写，**不做自动发现**。peer 发现依赖「branch 地址已手动配好」这一前提。

### 5.3 客户端 Peer 提供端（新增，C#：PeerProvider）
- 用 `HttpListener` 监听局域网端口：**固定 `45123`**；若端口被占用则回退随机端口；端口可在设置中修改。首次可能需 `netsh http add urlacl` 授权，或绑定具体 LAN IP 降低权限要求。
- 仅实现一条路由：`GET /{tenant}/{appid}/{filepath}`，**严格白名单**：
  - 仅允许 `root/App/` 下的文件；
  - 仅当该文件在本地 `manifest.json` 中记录、且**本地实际 sha256 == 官方 sha256**（即「已校验通过」）才返回；
  - 暂存 (`App.staging`)、旧版 (`App.old`)、锁文件、manifest 自身一律不暴露。
- 响应复用与 origin/branch 相同的 URL 形态 → 下载器无差别处理。

### 5.4 客户端发现（新增，C#：PeerDiscovery）
- 启动时 / 每隔 N 秒：向 branch `POST /peer/register`，上报本机各 app 的 `version + 已校验 hash 集合 + providerUrl`（仅上报当前「已 latest」的 app；正在更新或版本不全的不报）。
- 下载某文件前：向 branch `GET /peer/query?appId&hash` 取得候选 peer 列表，并入下载器候选源（置于 branch 之前）。
- 容错：branch 不支持该 API（旧版/未部署）时，查询返回空 → 自动退化为「仅 branch+origin」，无副作用。

### 5.5 客户端下载器（改写，C#：PeerDownloader / UpdateEngine）
- 将现有「按 `Servers` 有序回退」升级为「`[peers…, branch, origin]` 多源回退」。
- **混合同步策略（阈值 32MB）**：
  - `size ≤ 32MB`：整文件拉取（§3 逻辑），边下边算 sha256，命中落盘（写 `App.staging` 或 repair 直写 `App`）。
  - `size > 32MB`：块级同步——按 `blocks` 列表逐块从多源拉取，**每块独立算 sha256**，命中即写入暂存对应偏移；单块可从不同 peer 并行拉取（多 peer 带宽聚合），失败自动下一源。全部块就绪后整体校验文件 sha256 兜底。
- 单文件/单块失败（连接/超时/404/校验失败）均不阻塞，自动下一源；全失败则该文件标记失败（失败开放）。
- 块级天然支持断点续传：本地已落盘且块哈希匹配的块跳过，仅补缺失/损坏块。

### 5.6 更新编排（复用既有原子流程）
保持 Go 版 `engine.go` 的成熟做法，C# 平移：
- 写 `.updating.lock` 防并发/重复；
- 进程复检（运行中不更新，保留旧 `App\`）；
- `App.staging` 暂存全量 → 校验 → 原子切换 `App → App.old → staging → App` → 回写本地 `manifest.json`；
- 损坏修复（`repair`）：版本一致但哈希不符 → **混合同步**：`≤32MB` 单文件直修；`>32MB` 仅重下变动块（按块哈希比对，缺/坏块从多源补，省带宽）；均过 sha256。
- 版本更新（`update`）：大文件亦走块级暂存（`App.staging` 内按偏移拼装），其余原子切换流程一致。

---

## 6. 安全设计

- **权威校验不下移**：peer 提供的字节必须 == 官方 manifest 的 sha256（整文件或逐块）；提供端无法伪造可信内容（哈希不符即丢弃）。块级下每块独立校验，安全性与文件级等同。这是 R04/R18/R40 的直接落地。
- **协调器防污染**：branch 的 `/peer/register` 走 tenant token 鉴权；可选对注册负载做 HMAC 签名，防止伪造「我拥有某 hash」误导他人。
- **暴露面最小化**：提供端仅暴露已校验文件白名单，且只监听局域网；manifest、锁、暂存、旧版均不可经提供端读取。
- **失败开放**：peer/branch 任何异常都不导致本地软件不可用，最终回退 origin 或保留本地。

---

## 7. 边界与容错

| 场景 | 行为 |
|---|---|
| peer 离线 / 防火墙拦截 | 查询返回空或连接失败 → 该源跳过，回退 branch/origin |
| peer 返回错误/损坏字节 | sha256 不匹配 → 丢弃，试下一源 |
| 本机在「提供端」与「下载」同时发生 | 提供端只读白名单、与暂存目录隔离，互不影响 |
| 局域网无 branch（纯 origin 直连） | 发现 API 不可用 → 退化为仅 origin；P2P 关闭但功能不受影响 |
| 多 peer 版本不一致 | 仅声明拥有「官方某版本 hash」的 peer 才被查询命中；旧版本客户端不会污染新版候选 |
| 用户不愿共享 | 设置「允许我作为更新源」开关，默认开；关闭则只注册空 hash 集（仅消费） |

---

## 8. C# 落地模块划分

在 `WpfDock/Services/Update/` 下新增（不破坏现有 LauncherService / ConfigService）：

| 文件 | 职责 |
|---|---|
| `Manifest.cs` | `Manifest` / `FileEntry` 模型 + 加载/保存本地 manifest + sha256 计算 + `Diff`（对齐 Go `manifest.go`） |
| `UpdateEngine.cs` | 后台定时检查 + `CheckAndRun` + `update`/`repair` 原子流程（对齐 Go `engine.go`） |
| `PeerProvider.cs` | `HttpListener` 提供端 + 白名单校验 |
| `PeerDiscovery.cs` | 向 branch 注册/查询、心跳 |
| `PeerDownloader.cs` | 多源（peers+branch+origin）下载 + 逐源 sha256 校验 + 回退 |
| `PeerConfig.cs` | 设置项：是否作为源、提供端端口 |

- `AppConfig` 增量加：`peer: { enabled, port(默认45123) }`、`branch: { host(默认"update"), port(默认80) }`。UI 仅填主机名/IP（默认 `update`，不带协议前缀），程序内部拼 `http://<host>:<port>`；需局域网 DNS/hosts 将 `update` 解析到 branch 机，保持与 Go `MenuClient.json` 兼容（缺省即默认）。
- `AppInfo` 已有 `root/exe/process/version/id`，需补充 `ResolveRoot()`（对齐 Go 的 root 解析，处理相对/便携 dataDir），供 `root/App` 定位。

---

## 9. 实施阶段（建议顺序）

1. **A. 设计评审**：本文档确认（拓扑、发现机制、粒度、端口策略）。
2. **B. 数据结构与校验**：`Manifest.cs`（含 sha256、`Diff`、大文件 `blocks` 块结构），单元自测。
3. **C. 下载器多源化 + 混合同步**：`PeerDownloader` 实现 `[peers, branch, origin]` 多源回退；`≤32MB` 整文件、`>32MB` 按 8MB 块并行多源拉取、逐块 sha256 校验与拼装、断点续传；先用 `[branch, origin]` 跑通再叠加 peer。
4. **D. 客户端提供端**：`PeerProvider` + 白名单；设置开关。
5. **E. 发现与协调**：`PeerDiscovery`（C# 客户端）+ 新建 C# 分支服务器，新增 `/peer/register`、`/peer/query`（与客户端同栈）。
6. **F. 接入主流程**：菜单打开时/定时后台触发 `CheckAndRun`，状态回显到 Dock（复用现有 `AppStatus`）。
7. **G. 联调与混部验证**：Go 服务器 + C# 客户端 + 多 C# peer 局域网互通；断网/离线/损坏回退验证。

---

## 10. 待评审决策点

1. **发现机制（已定：branch 协调器）**：借力 branch 做协调器（`/peer/register`、`/peer/query`）。备选 mDNS/UDP 广播因组播环境脆弱不采用，复用现有鉴权与部署。
2. **分发粒度（已定：混合同步）**：单文件 `size > 32MB` 采用块级同步（2MB 定长块、逐块 sha256、多 peer 并行聚合带宽）；`≤ 32MB` 仍整文件同步。块大小 2MB、阈值 32MB，均为固定常量。
3. **提供端端口策略（已定）**：固定 `45123`；若端口被占用则回退随机端口；端口可在设置中修改。
4. **默认是否开启共享（已定：默认开）**：默认开启共享（局域网同组织内利大于弊），设置可关。
5. **branch 侧实现语言（已定：C#）**：新建 C# 分支服务器（与 C# 客户端同技术栈、统一部署）；协调/鉴权/可选常驻缓存逻辑参考既有 Go `server/branch.go` 行为平移。origin 仍用 Go 不变。

---

## 11. 与既有设计的衔接说明

- 不改变总服务器作为「权威基准」的定位；分支服务器降级为「协调 + 鉴权 + 可选常驻缓存兜底（默认开）」，P2P 仅是**客户端侧的分发加速**，逻辑闭环在下载器的「多源 + sha256 校验 + 回退」。
- 不改变 manifest 格式与目录布局，因此 Go 与 C# 节点、新旧版本可无缝混部、渐进迁移。
- 复用已验证的原子更新/锁/运行检测，避免重复踩坑。
