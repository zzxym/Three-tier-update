using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Impel
{
    public class AppStatus
    {
        public string id;
        public string name;
        public string localVersion = "";
        public string remoteVersion = "";
        public string state = "未安装";
        public int filesTotal;
        public int filesNeed;
        public long bytesNeed;
        public bool running;
        /// <summary>版本不一致全量哈希比对得出的“需更新文件相对路径”集合；
        /// UpdateAsync 复用它避免对同一批文件再算一遍哈希（GB 级应用可省数分钟）。</summary>
        public HashSet<string> needPaths;
    }

    public static class UpdateEngine
    {
        /// <summary>保护 cfg.installed 字典的并发读写（更新写 + Evaluate 读 + Save 序列化），避免 Dictionary 并发结构性修改崩溃。</summary>
        static readonly object _installedLock = new object();
        /// <summary>进行中的更新任务（appId）。引擎级防重入：dock 定时重建会让 UI 层的
        /// busy 遮罩判断失效（重建后为 Hidden），导致同一应用被并发触发两次更新、
        /// 同一批文件被重复下载——故在引擎层再挡一道，与 UI 状态无关。</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeUpdates
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        /// <summary>该应用当前是否有更新任务在进行（含 Evaluate 校验阶段）。供 UI 判断以等待现有任务而非重复触发。</summary>
        public static bool IsUpdating(string appId)
        { return !string.IsNullOrEmpty(appId) && _activeUpdates.ContainsKey(appId); }

        // 下载速率上报用的轻量 HTTP 客户端（短超时，不阻塞下载）
        static readonly HttpClient _speedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        /// <summary>向 Repel 上报本客户端某应用的下载速率（KB/s）；fire-and-forget，失败静默忽略。</summary>
        static async void ReportSpeed(ImpelConfig cfg, string appId, string appName, double rateKBs)
        {
            try
            {
                var url = RepelBase(cfg) + "/peer/speed?appId=" + Uri.EscapeDataString(appId)
                    + "&appName=" + Uri.EscapeDataString(appName)
                    + "&rate=" + rateKBs.ToString("F2", CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(cfg.server.token)) url += "&token=" + Uri.EscapeDataString(cfg.server.token);
                await _speedHttp.GetAsync(url);
            }
            catch { }
        }

        /// <summary>
        /// 服务器地址兼容写法：纯主机名（默认 http://主机名:80）、
        /// http://gameserver、https://gameserver（按协议默认端口）、
        /// https://gameserver:8080（显式端口优先）。
        /// </summary>
        public static string RepelBase(ImpelConfig cfg)
        {
            var h = (cfg.server.host ?? "").Trim();
            if (h.Length == 0) h = "gameserver";
            if (!h.StartsWith("http", StringComparison.OrdinalIgnoreCase)) h = "http://" + h;
            Uri u;
            if (!Uri.TryCreate(h, UriKind.Absolute, out u)) return h.TrimEnd('/');
            // 地址显式写了端口 → 用它；否则 http 默认 80，https 默认 443
            int port = u.IsDefaultPort
                ? (string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : u.Port;
            return u.Scheme + "://" + u.Host + ":" + port;
        }

        /// <summary>本地统一图标目录：SoftwareRoot/icons/（按 app id 命名）。Impel 启动时从这里优先加载。</summary>
        public static string IconsDir(ImpelConfig cfg) => Path.Combine(ConfigService.SoftwareRoot(cfg), "icons");

        /// <summary>从 Repel 拉取统一图标目录中的 {id}.ico 到本地 IconsDir。带退避重试；失败返回 false（由上层走降级）。
        /// 本地已有且非空时默认复用（图标可达数百 KB，而菜单列表每 5 分钟刷新一次，全量重拉纯浪费带宽）；
        /// 超过 6 小时才强制刷新一次，以便服务器更换图标后仍能同步。</summary>
        public static bool FetchIcon(ImpelConfig cfg, AppCfg app, Action<string> log = null, int retries = 3)
        {
            if (app == null || string.IsNullOrEmpty(app.id)) return false;
            var dir = IconsDir(cfg);
            try { Directory.CreateDirectory(dir); } catch { }
            var dest = Path.Combine(dir, app.id + ".ico");
            try
            {
                var fi = new FileInfo(dest);
                if (fi.Exists && fi.Length > 0 && (DateTime.Now - fi.LastWriteTime).TotalHours < 6)
                    return true;   // 本地已有可用图标，直接复用（不再请求 Repel）
            }
            catch { }
            var url = RepelBase(cfg) + "/icons/" + Uri.EscapeDataString(app.id) + ".ico";
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "?token=" + Uri.EscapeDataString(cfg.server.token);
            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET"; req.Timeout = 10000; req.ServicePoint.Expect100Continue = false;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.StatusCode != HttpStatusCode.OK)
                        {
                            log?.Invoke("[图标] " + app.name + " HTTP " + (int)resp.StatusCode);
                            System.Threading.Thread.Sleep(300 * (attempt + 1));
                            continue;
                        }
                        using (var rs = resp.GetResponseStream())
                        using (var fs = File.Create(dest)) rs.CopyTo(fs);
                        return true;
                    }
                }
                catch (WebException we)
                {
                    var msg = we.Response is HttpWebResponse r ? "HTTP " + (int)r.StatusCode : we.Message;
                    log?.Invoke("[图标] " + app.name + " 拉取失败(尝试" + (attempt + 1) + "): " + msg);
                }
                catch (Exception ex) { log?.Invoke("[图标] " + app.name + " 拉取失败(尝试" + (attempt + 1) + "): " + ex.Message); }
                System.Threading.Thread.Sleep(300 * (attempt + 1));
            }
            return false;
        }

        /// <summary>长轮询等待 Repel 菜单列表变更：自 since 起有变化立即返回 changed=true；否则服务端挂起至超时（或变更）才返回。
        /// ok=false 表示 Repel 不可用/不支持该端点（调用方据此退避重试）。</summary>
        public static (bool ok, bool changed, long version) WaitMenuChange(ImpelConfig cfg, long since, int timeoutSec = 90)
        {
            var url = RepelBase(cfg) + "/changes?since=" + since;
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "&token=" + Uri.EscapeDataString(cfg.server.token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = (timeoutSec + 10) * 1000; req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return (true, false, since);
                    var json = sr.ReadToEnd();
                    var o = JObject.Parse(json);
                    long ver = (long)(o["version"] ?? since);
                    bool changed = (bool)(o["changed"] ?? false);
                    return (true, changed, ver);
                }
            }
            catch { return (false, false, since); }
        }

        /// <summary>从 Repel 拉取应用列表。返回 (ok, list)：ok 表示 Repel 可达且成功返回；list 为 null 表示拉取失败（用于触发本地兜底）。</summary>
        public static (bool ok, List<AppCfg> list) FetchAppList(ImpelConfig cfg)
        {
            var url = RepelBase(cfg) + "/repel.lst";
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "?token=" + Uri.EscapeDataString(cfg.server.token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 10000; req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return (false, null);
                    var json = sr.ReadToEnd();
                    try { cfg.propelSource = resp.Headers["X-Propel"] ?? ""; } catch { }  // 记录 Propel 兜底直连地址
                    List<AppCfg> list = null;
                    try { list = JsonConvert.DeserializeObject<List<AppCfg>>(json); } catch (Exception ex) { Impel.App.Log("repel.lst 解析失败: " + ex.Message); }
                    if (list == null) { Impel.App.Log("repel.lst 内容无法解析为应用列表，转本地兜底"); return (false, null); }
                    foreach (var a in list)
                    {
                        a.enabled = true;
                        if (string.IsNullOrEmpty(a.id)) a.id = a.name;
                        if (string.IsNullOrEmpty(a.dir)) a.dir = string.IsNullOrEmpty(a.name) ? a.id : a.name;
                        if (a.env == null) a.env = new Dictionary<string, string>();
                    }
                    try { File.WriteAllText(Path.Combine(ConfigService.ExeDir, "repel.cache.lst"), json); } catch { }
                    return (true, list);
                }
            }
            catch (Exception ex) { Impel.App.Log("拉取 repel.lst 失败（检查服务器地址/端口/token）: " + ex.Message); return (false, null); }
        }

        /// <summary>兜底：扫描软件根目录下已安装的应用构建菜单数据（仅在 Repel 不可用时使用）。
        /// 离线唯一兜底：仅当应用目录内存在 app.json（在线时由 Repel 列表缓存于此，含准确 exe/版本等）才显示该应用；
        /// 无 app.json 则该应用不显示（避免靠体积/名字猜主程序而误判）。</summary>
        public static List<AppCfg> ScanLocalApps(ImpelConfig cfg)
        {
            var root = ConfigService.SoftwareRoot(cfg);
            var result = new List<AppCfg>();
            if (!Directory.Exists(root)) return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var dirName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(dirName)) continue;
                var app = LoadAppJson(dir); // 离线唯一兜底：必须存在 app.json，否则不显示该应用
                if (app == null) continue;
                if (string.IsNullOrEmpty(app.version))
                {
                    try { var m = Manifest.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))); if (m != null && !string.IsNullOrEmpty(m.version)) app.version = m.version; } catch { }
                }
                result.Add(app);
            }
            return result;
        }

        /// <summary>读取应用目录内的 app.json（离线兜底用的权威元数据）；不存在/解析失败返回 null。</summary>
        static AppCfg LoadAppJson(string dir)
        {
            try
            {
                var p = Path.Combine(dir, "app.json");
                if (!File.Exists(p)) return null;
                var a = JsonConvert.DeserializeObject<AppCfg>(File.ReadAllText(p));
                if (a == null) return null;
                a.enabled = true;
                if (string.IsNullOrEmpty(a.id)) a.id = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(a.name)) a.name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(a.dir)) a.dir = Path.GetFileName(dir);
                if (a.env == null) a.env = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(a.exe)) a.exe = a.exe.Replace('/', '\\'); // 归一化为本地分隔符
                return a;
            }
            catch { return null; }
        }

        /// <summary>在线时把 Repel 列表逐应用缓存为各目录内的 app.json，供 Repel 失效时离线兜底（无需手动维护）。</summary>
        public static void CacheAppList(ImpelConfig cfg, List<AppCfg> list)
        {
            if (list == null) return;
            var root = ConfigService.SoftwareRoot(cfg);
            foreach (var a in list)
            {
                try
                {
                    var dir = Path.Combine(root, ConfigService.AppDirName(a));
                    // 无条件预建目录并写 app.json：首次安装中的应用目录尚不存在，若跳过会导致
                    // Repel 停机后 ScanLocalApps 扫不到该应用，dock 上图标直接消失
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, "app.json");
                    var json = JsonConvert.SerializeObject(a, Formatting.Indented);
                    var old = File.Exists(p) ? File.ReadAllText(p) : "";
                    if (old != json) File.WriteAllText(p, json); // 仅在变化时写入，避免无谓改动时间戳
                }
                catch { }
            }
        }

        public static bool TryGetManifest(ImpelConfig cfg, string appId, out Manifest mani, out string err)
        {
            mani = null; err = "";
            // 优先 Repel；Repel 出错（含清单缺失）时，直连 Propel 兜底
            if (TryGetManifestFrom(RepelBase(cfg), cfg.server.token, appId, out mani, out err)) return true;
            if (!string.IsNullOrEmpty(cfg.propelSource) && TryGetManifestFrom(cfg.propelSource.TrimEnd('/'), cfg.server.token, appId, out mani, out err)) return true;
            return false;
        }

        static bool TryGetManifestFrom(string baseUrl, string token, string appId, out Manifest mani, out string err)
        {
            mani = null; err = "";
            var url = baseUrl.TrimEnd('/') + "/" + appId + "/manifest.json";
            if (!string.IsNullOrEmpty(token)) url += "?token=" + Uri.EscapeDataString(token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 10000; req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK) { err = "HTTP " + (int)resp.StatusCode; return false; }
                    mani = Manifest.Parse(sr.ReadToEnd());
                    return true;
                }
            }
            catch (WebException we) { if (we.Response is HttpWebResponse r) err = "HTTP " + (int)r.StatusCode; else err = we.Message; return false; }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        public static AppStatus Evaluate(ImpelConfig cfg, AppCfg app)
        {
            var st = new AppStatus { id = app.id, name = app.name };
            st.running = IsRunning(app);
            var root = ConfigService.AppRoot(cfg, app);
            Manifest local = null;
            var localPath = Path.Combine(root, "manifest.json");
            if (File.Exists(localPath)) { try { local = Manifest.Parse(File.ReadAllText(localPath)); } catch { } }

            // 本地已装版本：优先用 Impel 自己持久化的安装记录（成功更新后写入 Impel.json），
            // 与应用运行时是否改写自身 manifest.json 无关，避免版本号被应用改动后误判“可更新”。
            bool hasRecord = false; string rec = "";
            lock (_installedLock)
            {
                if (cfg.installed != null && cfg.installed.ContainsKey(app.id)) { hasRecord = true; rec = cfg.installed[app.id] ?? ""; }
            }
            string localVer = hasRecord ? rec : (local?.version ?? "");
            st.localVersion = localVer;

            if (!TryGetManifest(cfg, app.id, out var remote, out var err))
            {
                st.remoteVersion = "";
                st.state = local == null ? "未安装" : "已最新";
                if (err.Contains("404")) st.state += "（服务器无此应用）";
                else if (!st.state.StartsWith("已最新")) st.state += "（服务器不可达：" + err + "）";
                return st;
            }
            st.remoteVersion = remote.version ?? "";

            // 本地清单缺失：仅当“既无本地清单、也无安装记录”才视为未安装/需全量下载；
            // 若有 installed 记录（Impel.json 已记下版本），即使 manifest.json 被应用误删，也按版本号
            // 走下方的存在性比对（快），避免一次昂贵的全量哈希重校验。
            if (local == null && !hasRecord)
            {
                st.state = "可更新";
                st.filesTotal = remote.files.Count;
                st.filesNeed = remote.files.Count;
                st.bytesNeed = TotalBytes(remote);
                return st;
            }

            // 版本一致：先只做“文件是否存在”的轻量比对（不读内容，极快，数千文件也仅毫秒级）；
            // 无缺失即判“已最新”。缺失文件将被补下载，且不再对“已存在”文件算哈希——既避免运行时
            // 改动文件导致的误报，也省去全量哈希的耗时。仅当版本号变化时才做全量哈希比对。
            bool sameVersion = !string.IsNullOrEmpty(localVer)
                            && localVer.Equals(remote.version ?? "", StringComparison.OrdinalIgnoreCase);
            if (sameVersion)
            {
                int missing = 0; long missBytes = 0; int chkTotal = 0;
                foreach (var f in remote.files)
                {
                    if (IsRuntimeIgnored(f.path)) continue; // 运行时文件（如日志）不参与比对
                    chkTotal++;
                    var lp = Path.Combine(root, f.path.Replace('/', '\\'));
                    if (!File.Exists(lp)) { missing++; missBytes += f.SizeLong; }
                }
                st.filesTotal = chkTotal;
                st.filesNeed = missing;
                st.bytesNeed = missBytes;
                st.state = missing == 0 ? "已最新" : "可更新";
                return st;
            }

            // 版本不一致：逐文件比对（存在 + 哈希），精准定位真正需要更新的文件。
            // 运行时文件（如日志）跳过哈希计算，避免应用软件改写日志后误判“需更新”。
            int need = 0; long bytes = 0; int total = 0;
            var np = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in remote.files)
            {
                if (IsRuntimeIgnored(f.path)) continue;
                total++;
                var lp = Path.Combine(root, f.path.Replace('/', '\\'));
                var ok = File.Exists(lp) && Utils.Sha256File(lp).Equals(f.sha256, StringComparison.OrdinalIgnoreCase);
                if (!ok) { need++; bytes += f.SizeLong; np.Add(f.path); }
            }
            st.filesTotal = total;
            st.filesNeed = need;
            st.bytesNeed = bytes;
            st.needPaths = np;
            st.state = need == 0 ? "已最新" : "可更新";
            return st;
        }

        public static long TotalBytes(Manifest m) { long t = 0; foreach (var f in m.files) t += f.SizeLong; return t; }

        public static bool IsRunning(AppCfg app)
        {
            if (string.IsNullOrEmpty(app.process)) return false;
            try { return System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.process)).Length > 0; }
            catch { return false; }
        }

        /// <summary>更新/校验时跳过的运行时文件：应用运行会改写这些文件（如日志、临时、缓存），
        /// 若纳入哈希比对会误判“需更新/损坏”，导致每次打开应用都重新下载。
        /// 规则与清单生成器（ManifestGen / PropelControl）的 IsIgnoredForManifest 保持一致。</summary>
        internal static bool IsRuntimeIgnored(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            var lower = rel.ToLowerInvariant();
            if (lower.EndsWith(".log") || lower.EndsWith(".tmp") || lower.EndsWith(".bak") || lower.EndsWith(".old"))
                return true;
            var name = Path.GetFileName(rel.Replace('\\', '/'));
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("app.json", StringComparison.OrdinalIgnoreCase)   // 客户端元数据（CacheAppList 维护），内容与服务器打包版不同，纳入比对会每次重复下载
                || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var seg in rel.Replace('\\', '/').Split('/'))
            {
                var s = seg.ToLowerInvariant();
                if (s == "logs" || s == "log" || s == "cache" || s == "caches" || s == "temp" || s == "tmp"
                    || s == "app.staging" || s == "__pycache__" || s == ".git" || s == "node_modules")
                    return true;
            }
            return false;
        }

        /// <summary>确保应用为最新：未安装则全量下载，已安装则对比更新；全程通过 progress 上报进度。</summary>
        public static async Task<AppStatus> UpdateAsync(ImpelConfig cfg, AppCfg app, Action<UpdateProgress> progress, Action<string> log)
        {
            // 引擎级防重入：同一应用同时只允许一个更新任务。必须覆盖 Evaluate 校验阶段
            // （版本不一致时全量哈希校验可能耗时数分钟），否则该窗口内重复点击会并发触发第二个任务
            if (!_activeUpdates.TryAdd(app.id, 0))
            {
                log("[" + app.name + "] 已有更新任务进行中，忽略重复触发");
                return new AppStatus { id = app.id, name = app.name, state = "更新中", running = IsRunning(app) };
            }
            try
            {
            var st = Evaluate(cfg, app);
            if (st.running)
            {
                // 应用正在运行：文件被占用且随时被改写，更新既无意义也易失败，跳过
                progress?.Invoke(new UpdateProgress { doneFiles = 1, totalFiles = 1, currentFile = "运行中", currentTotal = 1, currentBytes = 1 });
                log("[" + app.name + "] 应用正在运行，跳过更新检查");
                return st;
            }
            if (st.state == "已最新" || st.filesNeed == 0)
            {
                progress?.Invoke(new UpdateProgress { doneFiles = 1, totalFiles = 1, currentFile = "已是最新", currentTotal = 1, currentBytes = 1 });
                log("[" + app.name + "] 已是最新，无需更新");
                return st;
            }
            st.state = "更新中";
            var root = ConfigService.AppRoot(cfg, app);
            var stage = Path.Combine(root, "App.staging");
            var remote = (Manifest)null;
            try
            {
                if (!TryGetManifest(cfg, app.id, out remote, out var e2))
                { st.state = "可更新"; log("[" + app.name + "] 获取清单失败：" + e2); return st; }
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                var updateFiles = remote.files.Where(f => !IsRuntimeIgnored(f.path)).ToList();
                int total = updateFiles.Count;
                int done = 0;
                // 并行下载（路数取自配置 downloadParallel，默认 6）：首次全量安装 GB 级应用时显著提速；
                // 各文件独立写入 staging 自身路径，无共享状态，done 用 Interlocked 计数。
                int parallel = cfg.downloadParallel > 0 ? cfg.downloadParallel : 6;   // 配置异常（≤0）时回退默认
                // 下载速率上报：独立任务每 1s 按真实下载字节流算出瞬时速率（KB/s）上报 Repel；
                // 更新结束后（_activeUpdates 移除）自动停止并补报一次 0（repel 端 15s 无上报视为停止）
                long appDlBytes = 0;
                var speedTask = System.Threading.Tasks.Task.Run(async () =>
                {
                    long prev = 0;
                    while (UpdateEngine.IsUpdating(app.id))
                    {
                        await System.Threading.Tasks.Task.Delay(1000);
                        long cur = System.Threading.Interlocked.Read(ref appDlBytes);
                        double rate = (cur - prev) / 1024.0;
                        prev = cur;
                        // 瞬时速率为 0 不上报：下载中换源/调度间隙的空窗秒不代表结束，
                        // 上报 0 会被 Repel 当作"下载结束"立即移除，导致 repelView 上闪烁
                        if (rate > 0) ReportSpeed(cfg, app.id, app.name, rate);
                    }
                    ReportSpeed(cfg, app.id, app.name, 0);   // 循环退出（更新真正结束）才补报 0（Repel 端立即移除该应用的"正在下载"）
                });
                // 失败自动重试：repel 短暂停机（手动重启/维护）时，失败文件等 10s 再重试，最多 3 轮，
                // 避免一次源抖动导致整批"需修复"要用户再次点击；repel 长期不可达时 3 轮后照旧标记需修复
                List<FileEntry> pending = updateFiles;
                for (int round = 0; pending.Count > 0 && round <= 3; round++)
                {
                    if (round > 0)
                    {
                        log("[" + app.name + "] " + pending.Count + " 个文件下载失败，10 秒后第 " + round + "/3 轮重试（等待下载源恢复）");
                        await System.Threading.Tasks.Task.Delay(10000);
                    }
                    var failed = new List<FileEntry>();
                    Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = parallel }, f =>
                    {
                        try
                        {
                            progress?.Invoke(new UpdateProgress { doneFiles = System.Threading.Volatile.Read(ref done), totalFiles = total, currentFile = f.path, currentTotal = f.SizeLong, currentBytes = 0 });
                            var rel = f.path.Replace('/', '\\');
                            var target = Path.Combine(root, rel);
                            var tmp = Path.Combine(stage, rel);
                            // needPaths 非空 = Evaluate 已对全量文件做过哈希比对，直接复用结果，不再重复计算
                            bool needDl;
                            if (st.needPaths != null)
                                needDl = st.needPaths.Contains(f.path);
                            else
                            {
                                var lp = File.Exists(target) ? Utils.Sha256File(target) : "";
                                needDl = !lp.Equals(f.sha256, StringComparison.OrdinalIgnoreCase);
                            }
                            if (!needDl)
                            {
                                System.Threading.Interlocked.Increment(ref done);
                                progress?.Invoke(new UpdateProgress { doneFiles = System.Threading.Volatile.Read(ref done), totalFiles = total, currentFile = f.path, currentTotal = f.SizeLong, currentBytes = f.SizeLong });
                                return;
                            }
                            log("  ↓ " + f.path);
                            var candidates = new List<string>();
                            // 小文件（< 阈值）跳过 P2P 查询直接走 Repel keep-alive 连发：中心缓存是全量汇总必命中，
                            // 每文件一次查询往返的开销对小文件占比过高；大文件才走 P2P 分担带宽
                            long smallTh = (cfg.smallFileThresholdKB > 0 ? cfg.smallFileThresholdKB : 1024) * 1024L;
                            if (cfg.p2p.enabled && f.SizeLong >= smallTh)
                            {
                                // Repel 的 /peer/query 返回里已把中心缓存自身作为“一员”注入（排真实 peer 之后），
                                // 因此 Repel 在此与局域网 peer 并列参与下载候选，无需硬编码。
                                try { var peers = PeerDiscovery.QueryPeers(cfg, app.id, f.sha256); if (peers != null) foreach (var p in peers) candidates.Add(p); }
                                catch { }
                            }
                            // P2P 查询未返回任何节点（Repel 不可达/无 peer/网络异常）时，回退直连 Repel 本身作为保底源
                            if (candidates.Count == 0)
                                candidates.Add(RepelBase(cfg));
                            if (!string.IsNullOrEmpty(cfg.propelSource)) candidates.Add(cfg.propelSource.TrimEnd('/'));  // 绝对最后兜底：直连 Propel
                            long prevLocal = 0;   // 每个文件独立的字节累计基准，用于换算出"增量"
                            bool got = false;
                            foreach (var baseUrl in candidates)
                            {
                                if (TryDownloadFrom(cfg, baseUrl, app.id, f, tmp, b =>
                                    {
                                        var delta = b - prevLocal; prevLocal = b;   // 增量累计到全局下载字节（真实下载速率数据源）
                                        if (delta > 0) System.Threading.Interlocked.Add(ref appDlBytes, delta);
                                        progress?.Invoke(new UpdateProgress { doneFiles = System.Threading.Volatile.Read(ref done), totalFiles = total, currentFile = f.path, currentTotal = f.SizeLong, currentBytes = b });
                                    }, log))
                                { got = true; break; }
                            }
                            if (got)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                if (File.Exists(target)) File.Delete(target);
                                File.Move(tmp, target);
                                System.Threading.Interlocked.Increment(ref done);
                            }
                            else
                            {
                                // 注明有无 Propel 兜底：若无，多半是远程 impel/RepelService 版本过旧（X-Propel 未下发/未解析）
                                log("  ✗ " + f.path + " 所有源均失败" + (string.IsNullOrEmpty(cfg.propelSource) ? "（无 Propel 兜底：请确认 Repel 已下发 X-Propel 且 impel 为最新版）" : ""));
                                lock (failed) failed.Add(f);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 单个文件异常不中止整批更新：记日志继续其余文件（重试轮再补齐）
                            log("  ✗ " + f.path + " 处理异常：" + ex.Message);
                            lock (failed) failed.Add(f);
                        }
                        progress?.Invoke(new UpdateProgress { doneFiles = System.Threading.Volatile.Read(ref done), totalFiles = total, currentFile = f.path, currentTotal = f.SizeLong, currentBytes = f.SizeLong });
                    });
                    pending = failed;
                }
                // 仅当全部文件下载成功时才落盘 manifest 并记录安装版本：
                // 部分失败（full==false）时若仍写入完整 remote 清单却未记 installed，
                // 下次 Evaluate 会误判“已最新”并可能交付不完整版本。
                bool full = (done == total);
                if (full)
                {
                    File.WriteAllText(Path.Combine(root, "manifest.json"),
                        JsonConvert.SerializeObject(remote, Formatting.Indented));
                    // 记录本次安装版本（写入 Impel.json）：之后即使应用改写自身文件/清单，只要版本号不变就判“已最新”，
                    // 避免重复误报；仅当 Propel 版本号变化时才会重新做差异比对。
                    // copy-on-write：复制当前字典→改副本→整体替换引用。避免就地结构性修改（新增 key 触发 Add）
                    // 与 SettingsWindow 在 UI 线程并发调用 ConfigService.Save 序列化同一字典时抛出 "Collection was modified"。
                    try { lock (_installedLock) { var copy = new Dictionary<string, string>(cfg.installed ?? new Dictionary<string, string>()); copy[app.id] = remote.version ?? ""; cfg.installed = copy; ConfigService.Save(cfg); } } catch { }
                }
                st.state = full ? "已最新" : "需修复";
                log("[" + app.name + "] 更新完成 " + done + "/" + remote.files.Count);
            }
            catch (Exception ex) { st.state = "需修复"; log("[" + app.name + "] 异常：" + ex.Message); }
            finally
            {
                // staging 删除重试：残留的大文件可能被杀软扫描短暂锁定，重试几次再放弃（下次点击时仍会先清理）
                for (int i = 0; i < 3; i++)
                {
                    try { if (!Directory.Exists(stage)) break; Directory.Delete(stage, true); break; }
                    catch { System.Threading.Thread.Sleep(1000); }
                }
                try { PeerDiscovery.RegisterApp(cfg, app); } catch { }   // 下载结束立即注册（部分完成也注册，让其他客户端尽快可见）
            }
            return st;
            }
            finally { _activeUpdates.TryRemove(app.id, out _); }
        }

        static bool TryDownloadFrom(ImpelConfig cfg, string baseUrl, string appId, FileEntry f, string destPath, Action<long> onBytes, Action<string> log)
        {
            var url = baseUrl.TrimEnd('/') + "/" + appId + "/" + f.path.TrimStart('/');
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "?token=" + Uri.EscapeDataString(cfg.server.token);
            var sha = SHA256.Create();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 15000; req.ReadWriteTimeout = 60000; req.ServicePoint.Expect100Continue = false;   // 连接 15s / 读 60s：repel 断连后快速判定失败并切下一候选（propel），正常下载每块 64K 即时到达不会被误杀
                req.Proxy = null;                        // 局域网直连：绕开系统代理/WPAD 自动检测（每文件几百 ms 固定开销的元凶）
                req.ServicePoint.ConnectionLimit = 16;   // 并行下载 4 路时默认连接池（2）会成为瓶颈
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    using (var fs = File.Create(destPath))
                    using (var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write))
                    {
                        var rs = resp.GetResponseStream();
                        var buf = new byte[65536]; int n; long totalRead = 0;
                        while ((n = rs.Read(buf, 0, buf.Length)) > 0) { cs.Write(buf, 0, n); totalRead += n; onBytes?.Invoke(totalRead); }
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.Delete(destPath); } catch { }
                log?.Invoke("  · " + HostOfUrl(baseUrl) + " 下载失败：" + ex.Message);   // 逐源记录失败原因，便于排查兜底链
                return false;
            }
            var hash = Utils.BytesToHex(sha.Hash);
            if (!hash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(destPath); } catch { }
                log?.Invoke("  · " + HostOfUrl(baseUrl) + " 哈希不匹配（源文件可能不同步）");
                return false;
            }
            return true;
        }

        /// <summary>从 baseUrl 提取主机:端口 简写，用于失败日志区分下载源。</summary>
        static string HostOfUrl(string baseUrl)
        {
            try
            {
                var u = baseUrl.TrimEnd('/');
                int s = u.IndexOf("://", StringComparison.Ordinal);
                if (s >= 0) u = u.Substring(s + 3);
                return u;
            }
            catch { return baseUrl; }
        }
    }
}
