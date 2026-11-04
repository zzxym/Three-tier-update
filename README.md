# 软件管理菜单与对比更新服务（MenuClient）

一个用于内网环境的绿色软件集中启动 + 版本比对/差异更新/损坏修复系统。
客户端为 Go + Wails 悬浮窗，服务端为 Go HTTP（总服务器 + 分支穿透缓存）。

## 目录结构
```
MenuClient/
  main.go                # 入口：单实例、WebView2 预检、Wails 悬浮窗、前端绑定
  config/                # 配置类型 + 加载/保存 + 启动逻辑（便携数据重定向）
  update/                # 更新引擎：manifest 比对/下载/哈希校验/回退/原子切换/修复
  server/                # 服务端：总服务器(origin) + 分支(branch) + 清单生成(genman)
  frontend/              # 悬浮 UI（index.html/style.css/main.js）+ dist（构建嵌入）
  scripts/               # 辅助脚本（genmanifest.ps1 生成本地基线）
  apps.example.json      # 客户端配置样例
  webview2/              # 放置自带的 WebView2 安装包/本地运行时（可选）
  docs/                  # 开发需求表 / 计划表 / 日志
```

## 客户端构建
```powershell
cd MenuClient
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
# 脚本会先把 frontend\{index.html,style.css,main.js} 复制到 frontend\dist（Wails 嵌入源），
# 再执行 wails build 产出单文件 build\bin\MenuClient.exe
```
- **WebView2 运行时有两种随包方式（二选一即可）：**
  1. **Fixed-Version 本地运行时（推荐，免安装/免管理员/免联网）**：把从微软下载的 Fixed Version 包解压，将其中的 `WebView2Loader.dll` 与 `runtimes\` 放到 `webview2/fixed\` 下，菜单启动时会直接加载本地运行时。
  2. **Evergreen 引导安装包（兜底）**：把 `MicrosoftEdgeWebview2Setup.exe` 放到 `webview2\` 下，首次运行若系统未装 WebView2 会自动尝试静默安装（需联网、通常需管理员）。
- 优先级：本地 Fixed 运行时 > Evergreen 引导安装 > 系统已装运行时。

## 客户端配置（MenuClient.json）
由菜单「设置」界面可视化编辑并落盘，字段见 `apps.example.json`：
- 全局：`tenant` / `servers`（分支优先→总服务器回退）/ `updateIntervalMinutes` / `dock` / `autostart`
- 每个软件：启动规格 `exe` `workdir` `env` `args` `process` + `portableData` `dataDir`

## 服务端运行
```bash
# 总服务器：发布目录 + 租户授权
go run ./server -mode=origin -root=releases -addr=:8080 -tokens=school-xxx:SECRET

# 分支服务器：穿透缓存代理（回源总服务器并磁盘缓存）
go run ./server -mode=branch -origin=http://总服务器:8080 -addr=:8080 -cache=branchcache -tokens=school-xxx:SECRET

# 生成发布清单（对每个绿色包运行一次）
go run ./server -mode=genman -app=<绿色包目录> -tenant=school-xxx -appid=EasiNote -version=5.2.4.11582 -out=releases
```
发布目录布局：`releases/<tenant>/<appid>/manifest.json` 与文件。

## 更新流程
1. 菜单后台定时（或打开时）对每个未运行软件拉取服务器 manifest。
2. 按有序服务器列表回退获取；逐文件 sha256 比对，缺的/不一致的下载。
3. 下载到 `App.staging` 校验通过后，原子切换 `App`（只动 App\，不碰 Data\）。
4. 若软件在更新中启动 → 放弃本次更新，保留旧版本（用户不受影响）。
5. 本地文件损坏（哈希不符）→ 自动重新下载修复。

## 绿色包适配
三个绿色包已写入本地 `manifest.json` 基线（版本 + 文件哈希），作为权威本地版本与修复基线。
菜单启动它们不再依赖 `启动.bat/vbs`，全部由 `apps.json` 启动规格驱动。
