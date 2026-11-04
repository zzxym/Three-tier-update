using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RepelService
{
    /// <summary>
    /// 内网缓存节点 Repel（P2P 协调器 + 可选常驻缓存兜底）：
    /// - POST /peer/register：写入/续期内存注册表 {appId → {hash → [providerUrl]}}（20min 过期，impel 每 5 分钟续注册）
    /// - GET  /peer/query?appId=&amp;hash=：返回 candidate providerUrl 列表（真实 peer 在前，末尾追加中心缓存自身作为一员）
    /// - GET  /&lt;appid&gt;/...：命中本地缓存直供，未命中回源(Propel) 取来直供并落盘缓存
    /// token 鉴权：任意合法 token 均可（扁平；未配置 tokens 时宽松放行，便于本地测试）
    /// 启动方式：默认读同目录 repel.json，命令行参数可覆盖；直接运行=前台，作为 Windows 服务运行=后台。
    /// </summary>
    class PeerEntry { public string ProviderUrl; public DateTime ExpireAt; }
    class RegisterBody { public string appId = null; public string version = null; public List<string> hashes = null; public string providerUrl = null; }

    class RepelConfig
    {
        public int port = 80;
        public string source = "";
        public string tokens = "";
        public string cache = "cache";
        public string selfUrl = "";   // 中心缓存对外可达地址（如 http://192.168.40.121:81）。为空时由请求 Host 头推导。
                                       // 用于把 Repel 自身作为一个 P2P 节点注入 /peer/query 返回，使其在节点统计与下载候选中与真实 peer 并列。
        public static RepelConfig Load()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.json");
            if (File.Exists(path))
            {
                try { var c = JsonConvert.DeserializeObject<RepelConfig>(File.ReadAllText(path)); if (c is not null) return c; }
                catch { }
            }
            return new RepelConfig();
        }
    }

    class Program
    {
        public static int Port = 80;
        public static string Source = "";
        public static string Tokens = "";
        public static string CacheDir = "cache";
        public static string SelfUrl = "";   // 中心缓存作为 P2P 节点时的对外地址
        static volatile HashSet<string> Auth = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Dictionary<string, List<PeerEntry>>> Registry =
            new(StringComparer.OrdinalIgnoreCase);
        static readonly object RegLock = new();
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        static readonly HttpClient WarmHttp = new() { Timeout = TimeSpan.FromMinutes(10) };  // 预热用：大文件放宽超时
        static HttpListener _listener;
        static FileSystemWatcher _watcher;
        static FileSystemWatcher _lstWatcher;
        static Timer _reloadTimer;
        static long _menuVersion = 0;                                  // 菜单列表版本号（每次变更自增）
        static readonly List<TaskCompletionSource<bool>> _waiters = [];
        static readonly object _wlock = new();
        static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.log");

        // ===== 监控诊断所需状态 =====
        static readonly DateTime _startTime = DateTime.Now;
        static long _propelLastFetchTicks = 0;   // 最近一次向 Propel 同步 all.lst 的时间（Ticks；64 位类型用 Interlocked 读写保证可见性与原子读）
        static volatile bool _propelLastOk = false;                      // 最近一次同步是否成功
        static volatile string _propelErr = "";                          // 最近一次同步错误
        static long _registerTotal = 0, _queryTotal = 0, _fileHits = 0, _fileMiss = 0, _lstRequests = 0, _changesRequests = 0;
        static readonly List<string> _recent = [];  // 最近活动日志（环形，最多 300 条）
        static readonly object _recentLock = new();
        // 客户端下载速率上报：impel 每 1s 上报自身下载速率（KB/s），Repel 按客户端 IP 聚合；
        // 同一客户端可同时更新多个应用，故每个 IP 下按 appId 保留多个应用的 {名称, 速率}；15s 无上报视为停止下载
        class AppSpeed { public string name; public double rate; public DateTime last; }
        class IpSpeed { public Dictionary<string, AppSpeed> apps = new(StringComparer.OrdinalIgnoreCase); public DateTime last; }
        static readonly Dictionary<string, IpSpeed> _speeds = new(StringComparer.OrdinalIgnoreCase);
        static readonly object _speedLock = new();
        class PeerInfo { public string Url; public int HashCount; public HashSet<string> Apps = new(StringComparer.OrdinalIgnoreCase); public DateTime MinExpire = DateTime.MaxValue, MaxExpire = DateTime.MinValue; }

        public static void Log(string s)
        {
            var line = DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + s;
            Console.WriteLine(line);
            try { File.AppendAllText(LogFile, line + "\r\n"); } catch { }
            try { lock (_recentLock) { _recent.Add(line); if (_recent.Count > 300) _recent.RemoveAt(0); } } catch { }
        }

        /// <summary>请求来源 IP（日志用）。</summary>
        static string SafeAddr(HttpListenerRequest req)
        {
            try { return req.RemoteEndPoint.Address.ToString(); } catch { return "?"; }
        }

        /// <summary>客户端断开（下载取消/网络闪断）等连接类异常统一记简短信息，避免日志刷屏。</summary>
        static string Brief(Exception ex)
        {
            if (ex is System.IO.IOException || ex is System.Net.HttpListenerException || ex is OperationCanceledException)
                return "客户端中断连接";
            return ex.Message;
        }

        static int Main(string[] args)
        {
            // 单实例：避免重复监听端口
            if (!SingleInstance.Ensure("Local\\RepelService_Single", false)) return 0;
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };

            var cfg = RepelConfig.Load();
            Port = cfg.port; Source = cfg.source; Tokens = cfg.tokens; CacheDir = cfg.cache; SelfUrl = cfg.selfUrl;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out Port);
                else if (a == "--source" && i + 1 < args.Length) Source = args[++i];
                else if (a == "--tokens" && i + 1 < args.Length) Tokens = args[++i];
                else if (a == "--cache" && i + 1 < args.Length) CacheDir = args[++i];
            }
            ParseTokens();
            // 作为 Windows 服务运行时工作目录是 System32，相对缓存目录必须锚定到 exe 所在目录
            if (!Path.IsPathRooted(CacheDir))
                CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheDir);
            Directory.CreateDirectory(CacheDir);

            // 作为 Windows 服务被 SCM 启动时（非交互）走服务分支；其余（含 --console）走前台
            if (!Environment.UserInteractive && Array.IndexOf(args, "--console") < 0)
            {
                ServiceBase.Run(new RepelService());
                return 0;
            }
            return RunForeground();
        }

        static int RunForeground()
        {
            if (!StartServer()) return 1;
            Log($"Repel 启动 :{Port}，source={Source}，cache={CacheDir}");
            Task.Run(CleanLoop);
            var exit = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; StopServer(); exit.Set(); };
            exit.WaitOne();
            Log("已停止。");
            return 0;
        }

        public static bool StartServer()
        {
            _listener = new HttpListener();
            try
            {
                _listener.Prefixes.Add($"http://*:{Port}/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log($"监听 :{Port} 失败：{ex.Message}");
                Log($"请以管理员运行，或执行：netsh http add urlacl url=http://*:{Port}/ user=Everyone");
                return false;
            }
            _ = Task.Run(() =>
            {
                try { while (_listener.IsListening) { var ctx = _listener.GetContext(); _ = Task.Run(async () => { try { await Handle(ctx); } catch (Exception ex) { Log("ERR " + Brief(ex)); } }); } }
                catch { }
            });
            SetupConfigWatcher();
            Task.Run(LstLoop);
            Task.Run(WarmCache);   // 启动即把 Propel 全部应用镜像到本地缓存（兜底）
            return true;
        }

        static volatile bool _stopping = false;   // 通知常驻循环（LstLoop/CleanLoop）退出
        public static void StopServer()
        {
            _stopping = true;
            try { _watcher?.Dispose(); } catch { }
            try { _lstWatcher?.Dispose(); } catch { }
            try { _reloadTimer?.Dispose(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
        }

        /// <summary>监控同目录 repel.json，文件变化时热更新 tokens/source/cache，无需重启服务（port 变更仍需重启）。</summary>
        static void SetupConfigWatcher()
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var watcher = new FileSystemWatcher(dir, "repel.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                _reloadTimer = new Timer(_ => ReloadConfig(), null, Timeout.Infinite, Timeout.Infinite);
                void Trigger() { try { _reloadTimer.Change(300, Timeout.Infinite); } catch { } }
                watcher.Changed += (s, e) => Trigger();
                watcher.Created += (s, e) => Trigger();
                watcher.Renamed += (s, e) => Trigger();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;

                // 监控同目录 repel.lst（菜单列表）：文件变化即通知所有 Impel 客户端立即刷新
                var lstWatcher = new FileSystemWatcher(dir, "repel.lst")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                void LstTrigger() { BumpMenu(); try { Task.Run(WarmCache); } catch { } }
                lstWatcher.Changed += (s, e) => LstTrigger();
                lstWatcher.Created += (s, e) => LstTrigger();
                lstWatcher.Renamed += (s, e) => LstTrigger();
                lstWatcher.EnableRaisingEvents = true;
                _lstWatcher = lstWatcher;
            }
            catch (Exception ex) { Log("监控配置文件失败：" + ex.Message); }
        }

        static void ReloadConfig()
        {
            try
            {
                var fresh = RepelConfig.Load();
                Source = fresh.source; Tokens = fresh.tokens; CacheDir = fresh.cache; SelfUrl = fresh.selfUrl;
                if (!Path.IsPathRooted(CacheDir))
                    CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheDir);
                ParseTokens();
                try { Directory.CreateDirectory(CacheDir); } catch { }
                Log($"已热更新配置：source={Source}，token数={Auth.Count}，cache={CacheDir}（port 变更需重启）");
            }
            catch (Exception ex) { Log("热更新配置失败：" + ex.Message); }
        }

        static void ParseTokens()
        {
            // 构建新集合后整体替换，避免热更新时请求端读到中间状态
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Tokens))
                foreach (var t in Tokens.Split(','))
                {
                    var tt = t.Trim();
                    if (tt.Length > 0) set.Add(tt);
                }
            Auth = set;
        }

        static async Task Handle(HttpListenerContext ctx)
        {
            var resp = ctx.Response;
            try
            {
                var req = ctx.Request;
                var path = req.Url.AbsolutePath;
                var token = req.QueryString["token"] ?? "";
                if (Auth.Count > 0 && !Auth.Contains(token)) { resp.StatusCode = 401; return; }

                if (path.TrimEnd('/').EndsWith("/peer/register")) { Register(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/peer/query")) { Query(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/peer/speed")) { Speed(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/diag")) { Diag(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/probesource")) { await ProbeSourceAsync(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/changes")) { Interlocked.Increment(ref _changesRequests); await ServeChangesAsync(ctx); return; }
                if (path.TrimEnd('/').EndsWith("/all.lst") || path.TrimEnd('/').EndsWith("/repel.lst")) { Interlocked.Increment(ref _lstRequests); var lstKind = path.TrimEnd('/').EndsWith("/repel.lst") ? "repel.lst" : "all.lst"; await ServeLstAsync(ctx, lstKind == "repel.lst" ? "repel" : "all"); return; }  // 菜单请求频率高，不写日志，仅计入统计
                await ServeFile(ctx);
            }
            catch (Exception ex) { Log("ERR [" + SafeAddr(ctx.Request) + "] " + Brief(ex)); try { resp.StatusCode = 500; } catch { } }
            finally { try { resp.Close(); } catch { } }
        }

        /// <summary>长轮询端点：客户端带 since 版本号挂起；服务端列表变更（bump 版本）时立即返回 changed=true，否则最多挂起 90s。</summary>
        static async Task ServeChangesAsync(HttpListenerContext ctx)
        {
            try
            {
                long.TryParse(ctx.Request.QueryString["since"] ?? "0", out long since);
                long cur = Interlocked.Read(ref _menuVersion);
                if (cur != since) // 版本已不同（含服务端重启归零），立即返回变更
                {
                    WriteJson(ctx.Response, JsonConvert.SerializeObject(new { changed = true, version = cur }));
                    return;
                }
                var tcs = new TaskCompletionSource<bool>();
                lock (_wlock) { _waiters.Add(tcs); }
                bool signaled = false;
                try
                {
                    await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90)));
                    signaled = tcs.Task.IsCompleted;
                }
                catch { }
                lock (_wlock) { _waiters.Remove(tcs); }
                cur = Interlocked.Read(ref _menuVersion);
                WriteJson(ctx.Response, JsonConvert.SerializeObject(new { changed = signaled || cur != since, version = cur }));
            }
            catch (Exception ex)
            {
                // 客户端在长轮询等待期间断开连接（关窗口/刷新/网络抖动），底层连接已失效，属正常，静默忽略，不报 ERR
                if (ex is System.Net.HttpListenerException) return;
                Log("ERR changes " + ex.Message);
                try { ctx.Response.StatusCode = 500; } catch { }
            }
        }

        /// <summary>菜单列表变更：版本号自增并唤醒所有长轮询等待者。</summary>
        static void BumpMenu()
        {
            Interlocked.Increment(ref _menuVersion);
            List<TaskCompletionSource<bool>> list;
            lock (_wlock) { list = [.. _waiters]; _waiters.Clear(); }
            foreach (var t in list) try { t.TrySetResult(true); } catch { }
        }

        static void Register(HttpListenerContext ctx)
        {
            try
            {
                using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var rb = JsonConvert.DeserializeObject<RegisterBody>(r.ReadToEnd());
                if (rb == null || string.IsNullOrEmpty(rb.appId)) { ctx.Response.StatusCode = 400; return; }
                if (string.IsNullOrEmpty(rb.providerUrl)) { ctx.Response.StatusCode = 400; return; }   // providerUrl 缺失会导致 Query 拼出空 baseUrl
                lock (RegLock)
                {
                    if (!Registry.ContainsKey(rb.appId)) Registry[rb.appId] = new Dictionary<string, List<PeerEntry>>(StringComparer.OrdinalIgnoreCase);
                    var appMap = Registry[rb.appId];
                    var expire = DateTime.Now.AddMinutes(20);   // P2P 注册 TTL：20 分钟过期（impel 每 5 分钟续注册，留 15 分钟抖动余量）
                    foreach (var h in rb.hashes ?? [])
                    {
                        if (!appMap.ContainsKey(h)) appMap[h] = [];
                        appMap[h].RemoveAll(p => p.ProviderUrl == rb.providerUrl);
                        appMap[h].Add(new PeerEntry { ProviderUrl = rb.providerUrl, ExpireAt = expire });
                    }
                }
                ctx.Response.StatusCode = 200;
                Interlocked.Increment(ref _registerTotal);
                // P2P 注册频率高（每客户端每 5 分钟 × 每应用），不写日志，仅计入统计
            }
            catch { ctx.Response.StatusCode = 400; }
        }

        static readonly Random _rnd = new Random();
        static readonly object _rndLock = new object();
        static void Query(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _queryTotal);
            var appId = ctx.Request.QueryString["appId"] ?? "";
            var hash = ctx.Request.QueryString["hash"] ?? "";
            var now = DateTime.Now;
            List<string> result = [];
            lock (RegLock)
            {
                if (Registry.TryGetValue(appId, out var appMap) && appMap.TryGetValue(hash, out var list))
                    foreach (var p in list) if (p.ExpireAt > now) result.Add(p.ProviderUrl);  // 真实 peer 在前
            }
            // 中心缓存与真实 peer 同等优先级：把 Repel 自身加入候选后整体打乱顺序，
            // 每次查询的尝试顺序随机——Repel 与每台 peer 平等竞争，且负载均衡分散到多台 peer（而非总压第一个）。
            var my = string.IsNullOrEmpty(SelfUrl)
                ? ("http://" + ctx.Request.Url.Host + ":" + Port)
                : SelfUrl.TrimEnd('/');
            result.Add(my);
            lock (_rndLock) { for (int i = result.Count - 1; i > 0; i--) { int j = _rnd.Next(i + 1); (result[i], result[j]) = (result[j], result[i]); } }
            Log("P2P查询 [" + SafeAddr(ctx.Request) + "] " + appId + " " + (hash.Length > 10 ? hash.Substring(0, 10) + "…" : hash) + " → " + result.Count + " 个共享节点（随机均衡）");
            WriteJson(ctx.Response, JsonConvert.SerializeObject(result));
        }

        /// <summary>从原始 URL query 中按 UTF-8 解码指定参数。
        /// HttpListener 的 QueryString 在请求未声明 charset 时对中文 percent 编码会用错误编码解码导致乱码，
        /// 因此含中文的参数（appName）必须自行解码。</summary>
        static string Qs(HttpListenerRequest req, string key)
        {
            try
            {
                var q = req.Url.Query;   // 原始未解码，如 "?appId=x&appName=%E8%B6%A3..."
                if (string.IsNullOrEmpty(q)) return "";
                foreach (var pair in q.TrimStart('?').Split('&'))
                {
                    int i = pair.IndexOf('=');
                    if (i <= 0) continue;
                    if (string.Equals(pair.Substring(0, i), key, StringComparison.OrdinalIgnoreCase))
                        return Uri.UnescapeDataString(pair.Substring(i + 1).Replace('+', ' '));
                }
            }
            catch { }
            return "";
        }

        /// <summary>客户端下载速率上报：impel 每 1s 上报某应用的下载速率（KB/s），按客户端 IP + 应用聚合。
        /// rate≤0 视为该应用下载结束/暂停，立即从列表移除（避免继续显示"正在下载"）；15s 无上报由 /diag 清理。</summary>
        static void Speed(HttpListenerContext ctx)
        {
            try
            {
                var appId = Qs(ctx.Request, "appId");
                var appName = Qs(ctx.Request, "appName");
                if (appName == "") appName = appId;
                double.TryParse(Qs(ctx.Request, "rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate);
                var ip = SafeAddr(ctx.Request);
                lock (_speedLock)
                {
                    if (rate <= 0)
                    {
                        // 该应用结束/暂停：移除，使"正在下载"立即消失
                        if (_speeds.TryGetValue(ip, out var ipEnd))
                        {
                            ipEnd.apps.Remove(appId);
                            if (ipEnd.apps.Count == 0) _speeds.Remove(ip);
                        }
                        ctx.Response.StatusCode = 200;
                        return;
                    }
                    if (!_speeds.TryGetValue(ip, out var ipS)) { ipS = new IpSpeed(); _speeds[ip] = ipS; }
                    ipS.last = DateTime.Now;
                    ipS.apps[appId] = new AppSpeed { name = appName, rate = rate, last = DateTime.Now };
                }
                ctx.Response.StatusCode = 200;
            }
            catch { try { ctx.Response.StatusCode = 400; } catch { } }
        }

        /// <summary>监控诊断快照：Repel 运行信息 + Propel 同步状态 + 在线 Impel 客户端(peer) + 请求统计 + 最近活动。供 repelView 轮询。</summary>
        /// <summary>中心缓存统计：已缓存的应用数与唯一哈希数。用于监控页把缓存本身作为一员展示。
        /// 哈希数与客户端注册口径一致：按每个应用 manifest 内唯一 sha256 计数（同内容文件只算一次），
        /// 否则按文件个数统计会因重复内容文件而大于 peer 的哈希数，两边数字不可比。</summary>
        static (int apps, int hashes) CacheStats()
        {
            int apps = 0, hashes = 0;
            try
            {
                if (!Directory.Exists(CacheDir)) return (0, 0);
                foreach (var d in Directory.GetDirectories(CacheDir))
                {
                    var name = Path.GetFileName(d.TrimEnd('\\', '/'));
                    if (name.Equals("icons", StringComparison.OrdinalIgnoreCase)) continue;   // 统一图标目录不算应用
                    apps++;
                    var mPath = Path.Combine(d, "manifest.json");
                    if (File.Exists(mPath))
                    {
                        try
                        {
                            var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(mPath));
                            if (obj["files"] is Newtonsoft.Json.Linq.JArray arr)
                            {
                                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var f in arr)
                                {
                                    var p = ((string)f["path"] ?? "").Replace('\\', '/');
                                    var fn = Path.GetFileName(p);
                                    // 与客户端忽略规则一致：运行时文件（app.json/*.log 等）不计数，保证与 peer 注册口径可比
                                    if (fn.Equals("app.json", StringComparison.OrdinalIgnoreCase)
                                        || fn.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
                                        || fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                                        || p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                                        || p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    var h = (string)f["sha256"];
                                    if (!string.IsNullOrEmpty(h)) set.Add(h);
                                }
                                hashes += set.Count;
                                continue;
                            }
                        }
                        catch { }
                    }
                    try { hashes += Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length; } catch { }   // 无清单时退化为文件数
                }
            }
            catch { }
            return (apps, hashes);
        }

        static void Diag(HttpListenerContext ctx)
        {
            var now = DateTime.Now;
            var info = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
            lock (RegLock)
            {
                foreach (var app in Registry)
                    foreach (var h in app.Value)
                        foreach (var p in h.Value)
                        {
                            if (!info.TryGetValue(p.ProviderUrl, out var pi)) { pi = new PeerInfo { Url = p.ProviderUrl }; info[p.ProviderUrl] = pi; }
                            pi.HashCount++;
                            pi.Apps.Add(app.Key);
                            if (p.ExpireAt < pi.MinExpire) pi.MinExpire = p.ExpireAt;
                            if (p.ExpireAt > pi.MaxExpire) pi.MaxExpire = p.ExpireAt;
                        }
            }
            var peers = info.Values.Select(pi => new
            {
                providerUrl = pi.Url,
                apps = pi.Apps.Count,
                hashes = pi.HashCount,
                lastSeenUtc = (pi.MinExpire == DateTime.MaxValue ? null : pi.MinExpire.AddMinutes(-20).ToUniversalTime().ToString("o")),
                expireUtc = (pi.MaxExpire == DateTime.MinValue ? null : pi.MaxExpire.ToUniversalTime().ToString("o")),
                online = pi.MaxExpire > now
            }).ToList<object>();

            string[] recentArr;
            lock (_recentLock) { recentArr = [.. _recent]; }

            var (apps, hashes) = CacheStats();

            // 客户端下载速率（按 IP 聚合多个应用；各应用 30s 无上报视为停止下载并清理；
            // 正常结束由 impel 补报 rate=0 立即移除，30s 容忍仅覆盖重试等待/网络抖动等空窗）
            var speedsOut = new List<object>();
            lock (_speedLock)
            {
                var cutoff = now.AddSeconds(-30);
                foreach (var kv in _speeds.ToList())
                {
                    var ipS = kv.Value;
                    var expired = ipS.apps.Where(a => a.Value.last < cutoff).Select(a => a.Key).ToList();
                    foreach (var k in expired) ipS.apps.Remove(k);
                    if (ipS.apps.Count == 0 || ipS.last < cutoff) { _speeds.Remove(kv.Key); continue; }
                    var names = ipS.apps.Values.Select(a => a.name).ToArray();
                    double total = ipS.apps.Values.Sum(a => a.rate);
                    speedsOut.Add(new { ip = kv.Key, appNames = string.Join(", ", names), rateKBs = total, lastSeenUtc = ipS.last.ToUniversalTime().ToString("o") });
                }
            }

            var outObj = new
            {
                version = 1,
                port = Port,
                source = Source,
                tokenRequired = Auth.Count > 0,
                cacheDir = CacheDir,
                menuVersion = Interlocked.Read(ref _menuVersion),
                uptimeSec = (long)(now - _startTime).TotalSeconds,
                cache = new
                {
                    apps,
                    hashes
                },
                propel = new
                {
                    source = Source,
                    lastFetchUtc = (Interlocked.Read(ref _propelLastFetchTicks) == 0 ? null : new DateTime(Interlocked.Read(ref _propelLastFetchTicks), DateTimeKind.Local).ToUniversalTime().ToString("o")),
                    lastSuccess = _propelLastOk,
                    lastError = _propelErr
                },
                peers,
                stats = new
                {
                    registerTotal = Interlocked.Read(ref _registerTotal),
                    queryTotal = Interlocked.Read(ref _queryTotal),
                    fileHits = Interlocked.Read(ref _fileHits),
                    fileMiss = Interlocked.Read(ref _fileMiss),
                    lstRequests = Interlocked.Read(ref _lstRequests),
                    changesRequests = Interlocked.Read(ref _changesRequests)
                },
                recent = recentArr,
                speeds = speedsOut
            };
            WriteJson(ctx.Response, JsonConvert.SerializeObject(outObj, Formatting.Indented));
        }

        /// <summary>实时探测 Propel 源头是否可达（由 Repel 侧发起回源一次）。
        /// 供 RepelControl 在“测试连接”中显示与 Propel 的连接状态，避免被本地缓存命中误导。</summary>
        static async Task ProbeSourceAsync(HttpListenerContext ctx)
        {
            if (string.IsNullOrEmpty(Source))
            {
                WriteJson(ctx.Response, JsonConvert.SerializeObject(new { ok = false, source = "", err = "未配置 Source(Propel 地址)" }));
                return;
            }
            try { await FetchAllLstAsync(); } catch { }   // 实时回源一次，刷新 _propelLastOk / _propelErr
            WriteJson(ctx.Response, JsonConvert.SerializeObject(new
            {
                ok = _propelLastOk,
                source = Source,
                err = _propelErr,
                lastFetchUtc = (_propelLastFetchTicks == 0 ? null : (object)new DateTime(Interlocked.Read(ref _propelLastFetchTicks), DateTimeKind.Local).ToUniversalTime().ToString("o"))
            }));
        }

        /// <summary>提供 all.lst / repel.lst。repel.lst 不存在时回退到缓存的 all.lst（默认向客户端公布全部）。</summary>
        static async Task ServeLstAsync(HttpListenerContext ctx, string kind)
        {
            try
            {
                string content;
                if (kind == "repel")
                {
                    var repelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.lst");
                    content = File.Exists(repelPath) ? File.ReadAllText(repelPath) : await FetchAllLstCachedAsync();
                }
                else content = await FetchAllLstCachedAsync();
                try { ctx.Response.Headers["X-Propel"] = Source ?? ""; } catch { } // 告知 Impel 可作最后兜底的 Propel 直连地址
                WriteJson(ctx.Response, content);
            }
            catch (Exception ex) { Log("ERR lst " + ex.Message); try { ctx.Response.StatusCode = 500; } catch { } }
        }

        static async Task<string> FetchAllLstCachedAsync()
        {
            var cacheFile = Path.Combine(CacheDir, "all.lst");
            try
            {
                if (File.Exists(cacheFile) && (DateTime.Now - File.GetLastWriteTime(cacheFile)).TotalMinutes < 5)
                    return File.ReadAllText(cacheFile);
            }
            catch { }
            var content = await FetchAllLstAsync();
            // Propel 拉取失败时：绝不覆盖本地缓存（避免被 "[]" 污染），并回退加载最近一次成功的 all.lst
            if (!_propelLastOk)
            {
                try { if (File.Exists(cacheFile)) return File.ReadAllText(cacheFile); } catch { }
                return content;
            }
            try
            {
                bool same = false;
                try { if (File.Exists(cacheFile) && File.ReadAllText(cacheFile) == content) same = true; } catch { }
                if (!same)
                {
                    try { Directory.CreateDirectory(CacheDir); File.WriteAllText(cacheFile, content); } catch { }
                    BumpMenu(); // all.lst（来自 Propel）内容变化，通知客户端刷新
                    try { _ = Task.Run(WarmCache); } catch { } // Propel 菜单有更新：自动刷新本地缓存镜像（后台执行，不阻塞）
                }
            }
            catch { }
            return content;
        }

        static async Task<string> FetchAllLstAsync()
        {
            Interlocked.Exchange(ref _propelLastFetchTicks, DateTime.Now.Ticks);
            if (string.IsNullOrEmpty(Source)) { _propelLastOk = false; _propelErr = "未配置 Source(Propel 地址)"; return "[]"; }
            var url = Source.TrimEnd('/') + "/all.lst";
            var tok = FirstToken();
            if (!string.IsNullOrEmpty(tok)) url += "?token=" + Uri.EscapeDataString(tok);
            try { var data = await Http.GetByteArrayAsync(url); _propelLastOk = true; _propelErr = ""; return Encoding.UTF8.GetString(data); }
            catch (Exception ex) { _propelLastOk = false; _propelErr = ex.Message; Log("获取 all.lst 失败：" + ex.Message); return "[]"; }
        }

        static string FirstToken()
        {
            foreach (var t in Auth) return t;
            return "";
        }

        static async Task LstLoop()
        {
            while (!_stopping)
            {
                await Task.Delay(5 * 60 * 1000);
                if (_stopping) break;
                try { await FetchAllLstCachedAsync(); } catch { }
            }
        }

        // ===== 常驻缓存兜底：把菜单内全部应用的 manifest 与文件主动镜像到 CacheDir =====
        static int _warmBusy = 0;   // 预热忙标志：用 Interlocked 保证重入互斥，避免双重预热
        static volatile bool _warmPending = false;
        // 选中软件集合（来自 repel.lst；未配置 repel.lst 时回退为“全部选中”）
        static HashSet<string> _selIds;
        static bool _selAll = true;
        static DateTime _selIdsTime = DateTime.MinValue;

        /// <summary>判断某 app 是否被选中缓存（依据同目录 repel.lst）。
        /// 未配置 repel.lst 时回退为“全部选中”（与 GetSelectedApps / WarmCache 行为一致）。结果 30s 内缓存。</summary>
        static bool IsSelected(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            var now = DateTime.Now;
            if (_selIds == null || (now - _selIdsTime).TotalSeconds > 30)
            {
                var repelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.lst");
                if (File.Exists(repelPath))
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var arr = JArray.Parse(File.ReadAllText(repelPath));
                        foreach (JObject o in arr.Cast<JObject>())
                        {
                            var id = (string)o["id"];
                            if (!string.IsNullOrEmpty(id)) set.Add(id);
                        }
                    }
                    catch { }
                    _selAll = false; _selIds = set;
                }
                else { _selAll = true; _selIds = new(StringComparer.OrdinalIgnoreCase); }
                _selIdsTime = now;
            }
            return _selAll || _selIds.Contains(appId);
        }

        /// <summary>返回应被缓存的软件列表：
        /// 若同目录 repel.lst 存在，则只缓存其中列出的软件（按 id 选中）；
        /// 否则回退到 Propel 的 all.lst（全部应用），保持向后兼容。</summary>
        static async Task<JArray> GetSelectedAppsAsync()
        {
            var repelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.lst");
            if (File.Exists(repelPath))
            {
                try
                {
                    var arr = JArray.Parse(File.ReadAllText(repelPath));
                    JArray sel = [];
                    foreach (JObject o in arr.Cast<JObject>())
                        if (!string.IsNullOrEmpty((string)o["id"])) sel.Add(o);
                    Log($"缓存预热：依据 repel.lst，选中 {sel.Count} 个应用");
                    return sel;
                }
                catch (Exception ex) { Log("缓存预热：repel.lst 解析失败，回退 all.lst：" + ex.Message); }
            }
            var allLst = await FetchAllLstCachedAsync();
            try { return JArray.Parse(allLst); }
            catch (Exception ex) { Log("缓存预热：all.lst 解析失败：" + ex.Message); return []; }
        }

        /// <summary>清理未被选中的软件缓存目录（repel.lst 中移除的软件不再缓存）。
        /// 仅删除 all.lst 已知、且当前未选中的软件目录，保留 all.lst 元数据文件及其它未知目录。</summary>
        static void PruneCache(JArray selected)
        {
            var selIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject o in selected.Cast<JObject>()) { var id = (string)o["id"]; if (!string.IsNullOrEmpty(id)) selIds.Add(id); }
            var knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var lstFile = Path.Combine(CacheDir, "all.lst");
                if (File.Exists(lstFile))
                {
                    var arr = JArray.Parse(File.ReadAllText(lstFile));
                    foreach (JObject o in arr.Cast<JObject>())
                    {
                        var id = (string)o["id"]; var dir = (string)o["dir"];
                        if (string.IsNullOrEmpty(dir)) continue;
                        knownDirs.Add(dir);
                        if (!string.IsNullOrEmpty(id) && selIds.Contains(id)) selDirs.Add(dir);
                    }
                }
            }
            catch { }
            if (!Directory.Exists(CacheDir)) return;
            foreach (var d in Directory.GetDirectories(CacheDir))
            {
                var name = Path.GetFileName(d.TrimEnd('\\'));
                if (!knownDirs.Contains(name)) continue;   // 只清理已知软件目录
                if (selDirs.Contains(name)) continue;
                try { Directory.Delete(d, true); Log($"缓存清理：移除未选中软件目录 {name}"); }
                catch (Exception ex) { Log($"缓存清理：删除 {name} 失败：" + ex.Message); }
            }
        }

        /// <summary>主动把（repel.lst 选中的）应用镜像到本地缓存（按 sha256 比对，只拉缺失/变更文件）。
        /// 由启动、菜单版本变更、repel.lst 变更触发；带重入保护，避免并发预热。</summary>
        static async Task WarmCache()
        {
            if (Interlocked.CompareExchange(ref _warmBusy, 1, 0) != 0) { _warmPending = true; return; }
            try
            {
                if (string.IsNullOrEmpty(Source)) { Log("缓存预热：未配置 Source(Propel)，跳过"); return; }
                // 确保 all.lst 已落盘（供 DirFor 的 id→dir 映射与 PruneCache 使用），但不据此遍历
                try { await FetchAllLstCachedAsync(); } catch { }
                var apps = await GetSelectedAppsAsync();
                Log($"缓存预热开始：共 {apps.Count} 个应用");
                var sem = new SemaphoreSlim(3);
                var tasks = new List<Task>(apps.Count);
                foreach (JObject o in apps.Cast<JObject>())
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await sem.WaitAsync();
                        try { await WarmAppAsync(o); }
                        catch (Exception ex) { Log("缓存预热应用异常：" + ex.Message); }
                        finally { sem.Release(); }
                    }));
                }
                await Task.WhenAll(tasks);
                Log("缓存预热完成");
                try { PruneCache(apps); } catch (Exception ex) { Log("缓存清理异常：" + ex.Message); }
            }
            catch (Exception ex) { Log("缓存预热异常：" + ex.Message); }
            finally
            {
                Interlocked.Exchange(ref _warmBusy, 0);
                if (_warmPending) { _warmPending = false; _ = Task.Run(WarmCache); }
            }
        }

        static async Task WarmAppAsync(JToken o)
        {
            var id = (string)o["id"];
            if (string.IsNullOrEmpty(id)) return;
            var dir = await DirForAsync(id);
            var baseReq = Source.TrimEnd('/') + "/" + id;
            var tok = FirstToken();
            var q = string.IsNullOrEmpty(tok) ? "" : "?token=" + Uri.EscapeDataString(tok);
            // 1) 拉取远程 manifest
            string remoteManifest;
            try { remoteManifest = Encoding.UTF8.GetString(await WarmHttp.GetByteArrayAsync(baseReq + "/manifest.json" + q)); }
            catch (Exception ex) { Log($"缓存预热 [{id}] manifest 拉取失败：" + ex.Message); return; }
            JObject rm;
            try { rm = JObject.Parse(remoteManifest); }
            catch (Exception ex) { Log($"缓存预热 [{id}] manifest 解析失败：" + ex.Message); return; }
            var cacheDir = Path.Combine(CacheDir, dir);
            var manifestCache = Path.Combine(cacheDir, "manifest.json");
            string localManifest = File.Exists(manifestCache) ? File.ReadAllText(manifestCache) : null;
            var files = rm["files"] as JArray ?? [];
            foreach (JObject f in files.Cast<JObject>())
            {
                var path = (string)f["path"];
                if (string.IsNullOrEmpty(path)) continue;
                var sha = (string)f["sha256"];
                var cacheFile = Path.Combine(cacheDir, path.Replace('/', '\\'));
                // 防目录穿越：path 来自远程 manifest，dir 来自 id→dir 映射，二者皆可能含 ../；落盘必须限定在 CacheDir 内
                var cacheRoot = Path.GetFullPath(CacheDir).TrimEnd('\\') + "\\";
                if (!Path.GetFullPath(cacheFile).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                { Log($"缓存预热 [{id}]/{path} 路径越界，已跳过"); continue; }
                bool need = !File.Exists(cacheFile);
                if (!need && localManifest is not null)
                {
                    // 本地 manifest 若该文件 sha 一致，说明已是最新，跳过（避免重复下载大文件）
                    try
                    {
                        var lm = JObject.Parse(localManifest);
                        var lf = (lm["files"] as JArray)?.FirstOrDefault(x => string.Equals((string)x["path"], path, StringComparison.OrdinalIgnoreCase));
                        var lsha = lf == null ? null : (string)lf["sha256"];
                        if (string.Equals(lsha, sha, StringComparison.OrdinalIgnoreCase)) need = false;
                    }
                    catch { need = true; }
                }
                if (!need) continue;
                try
                {
                    var data = await WarmHttp.GetByteArrayAsync(baseReq + "/" + path.Replace('\\', '/') + q);
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                    File.WriteAllBytes(cacheFile, data);
                }
                catch (Exception ex) { Log($"缓存预热 [{id}]/{path} 下载失败：" + ex.Message); }
            }
            // 最后再写本地 manifest，避免半途被当作完整
            try { Directory.CreateDirectory(cacheDir); File.WriteAllText(manifestCache, remoteManifest); } catch { }
        }

        // id → 中文真实目录名 映射（由缓存的 all.lst 解析），供回源/缓存落盘时用中文目录而非数字 id
        static Dictionary<string, string> _idToDir;
        static DateTime _idToDirTime = DateTime.MinValue;
        static async Task<string> DirForAsync(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return appId;
            var now = DateTime.Now;
            if (_idToDir == null || (now - _idToDirTime).TotalSeconds > 60)
            {
                try { await FetchAllLstCachedAsync(); } catch { } // 确保 all.lst 已落盘缓存
                var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lstFile = Path.Combine(CacheDir, "all.lst");
                    if (File.Exists(lstFile))
                    {
                        var arr = JArray.Parse(File.ReadAllText(lstFile));
                        foreach (JObject o in arr.Cast<JObject>())
                        {
                            var id = (string)o["id"]; var dir = (string)o["dir"];
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(dir) && !m.ContainsKey(id)) m[id] = dir;
                        }
                    }
                }
                catch { }
                _idToDir = m; _idToDirTime = now;
            }
            return _idToDir.TryGetValue(appId, out var d) ? d : appId;
        }

        static async Task ServeFile(HttpListenerContext ctx)
        {
            // URL 路径是百分号编码的：本地缓存文件名用解码后的真实名称，回源 URL 仍会由 HttpClient 重新编码
            var segs = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).Trim('/').Split('/');
            if (segs.Length < 1 || string.IsNullOrEmpty(segs[0])) { ctx.Response.StatusCode = 400; return; }
            var appId = segs[0];
            var rel = segs.Length > 1 ? string.Join("/", segs, 1, segs.Length - 1) : "";
            // URL 段是数字 id；落盘缓存目录用中文真实名 dir（id→dir 来自 all.lst），找不到映射时回退 id
            var cacheFile = Path.Combine(CacheDir, await DirForAsync(appId), rel);
            // 防路径逃逸（.. / %2E%2E），缓存必须落在 CacheDir 内
            var cacheRoot = Path.GetFullPath(CacheDir).TrimEnd('\\') + "\\";
            if (!Path.GetFullPath(cacheFile).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            { ctx.Response.StatusCode = 400; return; }
            // force=1：诊断用，强制回源 Propel 并跳过本地缓存命中（不落盘，避免污染缓存语义）
            var force = (ctx.Request.QueryString["force"] ?? "") == "1";
            string showRel = string.IsNullOrEmpty(rel) ? appId : appId + "/" + rel;
            var isManifest = rel.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase);
            if (!force && File.Exists(cacheFile))
            {
                // 流式发送（原 File.ReadAllBytes 会把整个文件读进内存：大文件既占数百 MB 内存又推迟首字节）
                try
                {
                    ctx.Response.ContentType = isManifest ? "application/json" : "application/octet-stream";
                    ctx.Response.ContentLength64 = new FileInfo(cacheFile).Length;
                    ctx.Response.StatusCode = 200;
                    using (var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await fs.CopyToAsync(ctx.Response.OutputStream, 65536);
                    Interlocked.Increment(ref _fileHits);
                    Log("文件下载 [" + SafeAddr(ctx.Request) + "] " + showRel + "（" + ctx.Response.ContentLength64 + " 字节，缓存命中）");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _fileMiss); ctx.Response.StatusCode = 404;
                    Log("文件下载失败 [" + SafeAddr(ctx.Request) + "] " + showRel + "（缓存读取异常：" + Brief(ex) + "）");
                }
                return;
            }
            // 回源
            if (string.IsNullOrEmpty(Source)) { Interlocked.Increment(ref _fileMiss); ctx.Response.StatusCode = 404; Log("文件下载失败 [" + SafeAddr(ctx.Request) + "] " + showRel + "（未配置 Propel 源）"); return; }
            var token = ctx.Request.QueryString["token"] ?? "";
            var url = $"{Source.TrimEnd('/')}/{appId}/{rel}";
            if (!string.IsNullOrEmpty(token)) url += "?token=" + token; // 透传客户端 token 回源
            try
            {
                // 流式回源：边收边转发并落盘，不整读内存；用 WarmHttp（10 分钟超时）适配大文件
                ctx.Response.ContentType = isManifest ? "application/json" : "application/octet-stream";
                long total = 0;
                var keep = !force && IsSelected(appId);   // 仅选中软件落盘缓存
                using (var src = await WarmHttp.GetStreamAsync(url))
                {
                    FileStream fs = null;
                    try { if (keep) { Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)); fs = File.Create(cacheFile); } } catch { fs = null; }
                    try
                    {
                        var buf = new byte[65536]; int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await ctx.Response.OutputStream.WriteAsync(buf, 0, n);
                            if (fs != null) await fs.WriteAsync(buf, 0, n);
                            total += n;
                        }
                    }
                    finally { fs?.Dispose(); }
                }
                Interlocked.Increment(ref _fileHits);
                Log("文件下载 [" + SafeAddr(ctx.Request) + "] " + showRel + "（" + total + " 字节，回源）");
            }
            catch (Exception ex)
            {
                try { if (!force && File.Exists(cacheFile)) File.Delete(cacheFile); } catch { }   // 清理半成品缓存
                Interlocked.Increment(ref _fileMiss); ctx.Response.StatusCode = 404;
                Log("文件下载失败 [" + SafeAddr(ctx.Request) + "] " + showRel + "（回源异常：" + ex.GetBaseException().Message + "）");
            }
        }

        static void WriteJson(HttpListenerResponse resp, string json)
        {
            resp.ContentType = "application/json";
            var buf = Encoding.UTF8.GetBytes(json);
            resp.OutputStream.Write(buf, 0, buf.Length);
        }

        static void CleanLoop()
        {
            while (true)
            {
                Thread.Sleep(60000);
                var now = DateTime.Now;
                lock (RegLock)
                {
                    foreach (var app in Registry.Keys)
                    {
                        var appMap = Registry[app];
                        foreach (var h in appMap.Keys)
                            appMap[h].RemoveAll(p => p.ExpireAt <= now);
                    }
                }
            }
        }
    }

    public class RepelService : ServiceBase
    {
        public RepelService() { ServiceName = "Repel"; }
        protected override void OnStart(string[] args)
        {
            if (Program.StartServer())
                Program.Log($"Repel 服务启动 :{Program.Port}，source={Program.Source}，cache={Program.CacheDir}");
        }
        protected override void OnStop() { Program.StopServer(); }
    }
}
