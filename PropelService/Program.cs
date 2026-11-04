using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PropelService
{
    /// <summary>
    /// 版本服务器（Propel 中心源）配置：从同目录 propel.json 读取，命令行参数可覆盖。
    /// </summary>
    public class PropelServerConfig
    {
        public string Root { get; set; } = "d:\\soft";
        public string Addr { get; set; } = "8080";
        public string Tokens { get; set; } = "";   // 不再内置默认口令：部署时必须在配置中填写，避免随代码泄露共享密钥
        [JsonIgnore] public bool ConsoleMode { get; set; }

        public HashSet<string> AuthSet()
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Tokens))
                foreach (var t in Tokens.Split(','))
                {
                    var x = t.Trim();
                    if (x.Length > 0) s.Add(x);
                }
            return s;
        }

        public static PropelServerConfig Load()
        {
            var cfg = new PropelServerConfig();
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "propel.json");
            if (File.Exists(path))
            {
                try
                {
                    var j = JsonConvert.DeserializeObject<PropelServerConfig>(File.ReadAllText(path));
                    if (j != null) cfg = j;
                }
                catch { }
            }
            return cfg;
        }

        public void ApplyArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--console" || a == "-console") ConsoleMode = true;
                else if (a == "-root" && i + 1 < args.Length) Root = args[++i];
                else if (a == "-addr" && i + 1 < args.Length) Addr = args[++i];
                else if (a == "-tokens" && i + 1 < args.Length) Tokens = args[++i];
            }
        }

        /// <summary>作为 Windows 服务运行时工作目录是 System32，相对路径需锚定到 exe 所在目录。</summary>
        public void NormalizePaths()
        {
            if (!string.IsNullOrEmpty(Root) && !Path.IsPathRooted(Root))
                Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Root);
        }
    }

    /// <summary>
    /// HTTP 文件服务核心：扁平 token 鉴权 + /&lt;appid&gt;/&lt;path&gt; 文件服务（含 manifest.json 与 ?off=&amp;len= 块级）。
    /// 使用原生 TcpListener（绕开 HTTP.SYS），无需 netsh urlacl，普通账户即可绑定高位端口。
    /// </summary>
    public class HttpServer
    {
        private TcpListener _listener;
        private volatile PropelServerConfig _cfg;
        private volatile HashSet<string> _auth;
        private volatile bool _run;
        private FileSystemWatcher _watcher;
        private Timer _reloadTimer;
        private readonly string _logPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "propel.log");

        private static readonly Dictionary<int, string> Reasons = new Dictionary<int, string>
        {
            { 200, "OK" }, { 400, "Bad Request" }, { 401, "Unauthorized" },
            { 404, "Not Found" }, { 500, "Internal Server Error" }
        };

        public void Start(PropelServerConfig cfg)
        {
            _cfg = cfg;
            _auth = cfg.AuthSet();
            if (!int.TryParse(cfg.Addr, out var port)) port = 8080;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _run = true;
            Log("服务器启动：" + port + "，发布目录=" + cfg.Root + "，token数=" + _auth.Count);
            EnsureIcons();
            EnsureTestDir(cfg.Root);   // 播种 test 测试目录（连接测试用）
            SetupWatcher();
            ThreadPool.QueueUserWorkItem(_ => Loop());
        }

        public void Stop()
        {
            _run = false;
            try { _watcher?.Dispose(); } catch { }
            try { _reloadTimer?.Dispose(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        /// <summary>监控同目录 propel.json，文件变化时热重载 token 与发布目录，无需重启服务。</summary>
        private void SetupWatcher()
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var watcher = new FileSystemWatcher(dir, "propel.json")
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
            }
            catch (Exception ex) { Log("监控配置文件失败：" + ex.Message); }
        }

        private void ReloadConfig()
        {
            try
            {
                var fresh = PropelServerConfig.Load();
                fresh.NormalizePaths();
                var newAuth = fresh.AuthSet();
                _cfg = fresh;
                _auth = newAuth;
                Log("已热更新配置：发布目录=" + fresh.Root + "，token数=" + newAuth.Count);
            }
            catch (Exception ex) { Log("热更新配置失败：" + ex.Message); }
        }

        private static readonly SemaphoreSlim _connLimit = new SemaphoreSlim(200);  // 并发连接上限，防止慢速/半开连接耗尽线程池
        private void Loop()
        {
            while (_run)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_run) break; continue; }
                var c = client;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    _connLimit.Wait();
                    try { Handle(c); }
                    finally { _connLimit.Release(); }
                });
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    try { ns.ReadTimeout = 10000; } catch { }   // 防止慢速/半开连接长期占用线程池线程
                    var req = ReadRequest(ns);
                    if (req == null) return;
                    var token = req.Query.Get("token") ?? "";
                    if (_auth.Count > 0 && !_auth.Contains(token))
                    {
                        SendText(ns, 401, "unauthorized");
                        return;
                    }

                    if (string.IsNullOrEmpty(req.Path) || req.Path == "/")
                    {
                        SendText(ns, 200, "propel ok");
                        return;
                    }

                    // 公布已拥有软件总列表（自动扫描 Root/<appid>/app.json + manifest.json）
                    if (req.Path.TrimEnd('/').EndsWith("/all.lst", StringComparison.OrdinalIgnoreCase))
                    {
                        SendJson(ns, 200, GenerateAllLst());
                        return;
                    }

                    var segs = req.Path.Trim('/').Split('/');
                    if (segs.Length < 1 || string.IsNullOrEmpty(segs[0]))
                    {
                        SendText(ns, 400, "bad request");
                        return;
                    }

                    // 统一图标目录：GET /icons/{id}.ico（确保各应用 icon.ico 已聚合到 Root/icons/）
                    if (segs[0].Equals("icons", StringComparison.OrdinalIgnoreCase) && segs.Length == 2
                        && segs[1].EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var iconsDir = Path.Combine(_cfg.Root, "icons");
                        var iconFile = Path.Combine(iconsDir, segs[1]);
                        var iconsRoot = Path.GetFullPath(iconsDir).TrimEnd('\\') + "\\";
                        if (!File.Exists(iconFile)) EnsureIcons();
                        if (File.Exists(iconFile) && Path.GetFullPath(iconFile).StartsWith(iconsRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var icoData = File.ReadAllBytes(iconFile);
                                var icoHeader = Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Type: image/x-icon\r\nContent-Length: {icoData.Length}\r\nConnection: close\r\n\r\n");
                                ns.Write(icoHeader, 0, icoHeader.Length);
                                ns.Write(icoData, 0, icoData.Length);
                                return;
                            }
                            catch (Exception ex) { Log("图标发送异常：" + ex.Message); }
                        }
                        SendText(ns, 404, "not found");
                        return;
                    }

                    var appId = segs[0];
                    var rel = segs.Length > 1 ? string.Join("/", segs, 1, segs.Length - 1) : "";

                    // appid 既可以是物理目录名，也可以是 app.json 中声明的逻辑 id（如自增数字）
                    var appDir = ResolveAppDir(appId);
                    var file = string.IsNullOrEmpty(rel) ? appDir : Path.Combine(appDir, rel);
                    var rootFull = Path.GetFullPath(_cfg.Root).TrimEnd('\\') + "\\";
                    var fileFull = Path.GetFullPath(file);
                    if (!fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        SendText(ns, 400, "invalid path");
                        return;
                    }
                    if (!File.Exists(file))
                    {
                        SendText(ns, 404, "not found");
                        return;
                    }

                    long off = 0, len = -1;
                    var fileSize = new FileInfo(file).Length;
                    if (long.TryParse(req.Query.Get("off"), out var o)) off = o;
                    if (long.TryParse(req.Query.Get("len"), out var l)) len = l;
                    if (off < 0) off = 0;
                    if (off > fileSize) off = fileSize;
                    if (len < 0 || off + len > fileSize) len = fileSize - off;

                    var ctype = rel.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)
                        ? "application/json" : "application/octet-stream";
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: {ctype}\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n");
                    ns.Write(header, 0, header.Length);
                    using (var fs = File.OpenRead(file))
                    {
                        fs.Seek(off, SeekOrigin.Begin);
                        var remaining = len;
                        var chunk = new byte[65536];
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(chunk.Length, remaining);
                            var n = fs.Read(chunk, 0, toRead);
                            if (n == 0) break;
                            ns.Write(chunk, 0, n);
                            remaining -= n;
                        }
                    }
                    Log("文件下载 " + appId + "/" + rel + "（" + len + " 字节" + (off > 0 ? "，续传@" + off : "") + "）← " + client.Client.RemoteEndPoint);
                }
            }
            catch (Exception ex) { Log("处理异常：" + ex.Message); }
        }

        private class Request
        {
            public string Path;
            public QueryDict Query = new QueryDict();
        }

        private class QueryDict
        {
            private readonly Dictionary<string, string> _m =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public void Add(string k, string v) { if (!string.IsNullOrEmpty(k)) _m[k] = v; }
            public string Get(string k) => _m.TryGetValue(k, out var v) ? v : null;
        }

        private Request ReadRequest(NetworkStream ns)
        {
            using (var reader = new StreamReader(ns, Encoding.ASCII, false, 1024, true))
            {
                var requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine)) return null;
                var sp = requestLine.Split(' ');
                if (sp.Length < 2) return null;
                var rawUrl = sp[1];
                // 读取头直到空行
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }
                int q = rawUrl.IndexOf('?');
                var path = q >= 0 ? rawUrl.Substring(0, q) : rawUrl;
                // 仅查询字符串才把 '+' 视为空格；路径中的 '+' 是合法字符，必须用 Uri.UnescapeDataString，
                // 否则 chrome++.ini 这类含 '+' 的文件名会被误解码成空格而 404。
                try { path = Uri.UnescapeDataString(path); }
                catch { path = WebUtility.UrlDecode(path); }
                var req = new Request { Path = path };
                if (q >= 0)
                {
                    foreach (var kv in rawUrl.Substring(q + 1).Split('&'))
                    {
                        if (kv.Length == 0) continue;
                        var eq = kv.IndexOf('=');
                        var k = eq >= 0 ? kv.Substring(0, eq) : kv;
                        var v = eq >= 0 ? kv.Substring(eq + 1) : "";
                        req.Query.Add(WebUtility.UrlDecode(k), WebUtility.UrlDecode(v));
                    }
                }
                return req;
            }
        }

        // ---- 逻辑 appid → 物理目录 映射（app.json 中的 id 可为自增数字，目录名可保留中文名）----
        private Dictionary<string, string> _idMap;
        private DateTime _idMapTime = DateTime.MinValue;

        private Dictionary<string, string> GetIdMap()
        {
            if (_idMap != null && (DateTime.Now - _idMapTime).TotalSeconds < 30) return _idMap;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var dir in Directory.GetDirectories(_cfg.Root))
                {
                    var name = Path.GetFileName(dir);
                    if (!map.ContainsKey(name)) map[name] = name; // 物理目录名始终可用
                    var appJson = Path.Combine(dir, "app.json");
                    if (File.Exists(appJson))
                    {
                        try
                        {
                            var o = JObject.Parse(File.ReadAllText(appJson));
                            var id = (string)o["id"];
                            if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id)) map[id] = name;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _idMap = map; _idMapTime = DateTime.Now;
            return map;
        }

        private string ResolveAppDir(string appId)
        {
            var rootFull = Path.GetFullPath(_cfg.Root);
            var direct = Path.Combine(rootFull, appId);
            if (Directory.Exists(direct)) return direct;
            if (GetIdMap().TryGetValue(appId, out var name))
            {
                var p = Path.Combine(rootFull, name);
                if (Directory.Exists(p)) return p;
            }
            return direct;
        }

        /// <summary>扫描发布目录：对每个含 manifest.json 的子目录，合并 app.json（启动元数据）与版本，生成软件总列表 JSON。</summary>
        static string _allLstCache;
        static DateTime _allLstCacheTime = DateTime.MinValue;
        static readonly object _allLstLock = new object();
        private string GenerateAllLst()
        {
            const int ttlSec = 30; // all.lst 30s 内复用，避免高频轮询（Repel 每 5 分钟 + 每次刷新）全量重扫发布树
            lock (_allLstLock)
            {
                if (_allLstCache != null && (DateTime.Now - _allLstCacheTime).TotalSeconds < ttlSec) return _allLstCache;
            }
            var content = GenerateAllLstCore();
            lock (_allLstLock) { _allLstCache = content; _allLstCacheTime = DateTime.Now; }
            return content;
        }
        private string GenerateAllLstCore()
        {
            EnsureIcons();
            var root = _cfg.Root;
            var arr = new JArray();
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var appId = Path.GetFileName(dir.TrimEnd('\\'));
                        var mani = Path.Combine(dir, "manifest.json");
                        if (!File.Exists(mani)) continue; // 仅公布已生成清单的软件
                        JObject entry;
                        var appJson = Path.Combine(dir, "app.json");
                        if (File.Exists(appJson))
                        {
                            try { entry = JObject.Parse(File.ReadAllText(appJson)); }
                            catch { entry = new JObject(); }
                        }
                        else entry = new JObject();
                        if (string.IsNullOrEmpty((string)entry["id"])) entry["id"] = appId;
                        if (string.IsNullOrEmpty((string)entry["name"])) entry["name"] = appId;
                        string ver = "";
                        try { var m = JObject.Parse(File.ReadAllText(mani)); ver = (string)m["version"] ?? ""; }
                        catch { }
                        entry["version"] = ver;
                        // 真实物理目录名（中文）：供 Repel/Impel 按 id 收到请求后，落盘使用中文目录而非数字 id
                        entry["dir"] = appId;
                        arr.Add(entry);
                    }
                }
            }
            catch (Exception ex) { Log("生成 all.lst 失败：" + ex.Message); }
            return arr.ToString(Formatting.Indented);
        }

        /// <summary>把各应用目录内的 icon.ico 聚合到统一的 Root/icons/{id}.ico（id 优先取 app.json 的 id，回退物理目录名）。
        /// 该目录作为图标分发的唯一来源，避免中文目录名出现在 URL 中。</summary>
        private void EnsureIcons()
        {
            try
            {
                var iconsDir = Path.Combine(_cfg.Root, "icons");
                Directory.CreateDirectory(iconsDir);
                if (!Directory.Exists(_cfg.Root)) return;
                foreach (var dir in Directory.GetDirectories(_cfg.Root))
                {
                    var name = Path.GetFileName(dir.TrimEnd('\\'));
                    if (name.Equals("icons", StringComparison.OrdinalIgnoreCase)) continue;
                    var src = Path.Combine(dir, "icon.ico");
                    if (!File.Exists(src)) continue;
                    var id = name;
                    var appJson = Path.Combine(dir, "app.json");
                    if (File.Exists(appJson))
                    {
                        try
                        {
                            var o = JObject.Parse(File.ReadAllText(appJson));
                            var i = (string)o["id"];
                            if (!string.IsNullOrEmpty(i)) id = i;
                        }
                        catch { }
                    }
                    var dest = Path.Combine(iconsDir, id + ".ico");
                    try { File.Copy(src, dest, true); }
                    catch (Exception ex) { Log("聚合图标失败 " + id + "：" + ex.Message); }
                }
            }
            catch (Exception ex) { Log("聚合图标目录失败：" + ex.Message); }
        }

        private void SendJson(NetworkStream ns, int code, string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {code} {Reason(code)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
            ns.Write(header, 0, header.Length);
            ns.Write(b, 0, b.Length);
        }

        private void SendText(NetworkStream ns, int code, string body)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {code} {Reason(code)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n");
            ns.Write(header, 0, header.Length);
            ns.Write(b, 0, b.Length);
        }

        private static string Reason(int code) => Reasons.TryGetValue(code, out var r) ? r : "OK";

        /// <summary>播种 test 测试目录（供 Repel 端做连接/带宽/更新测试）：若不存在则自动创建，并写入
        /// 1MB、33MB 两个空文件、app.json（id=1,name=test）与 manifest.json（含两文件清单，使连接测试的“更新测试”有文件可校验）。已存在则补建缺失项。</summary>
        private void EnsureTestDir(string root)
        {
            try
            {
                var testDir = Path.Combine(root, "test");
                Directory.CreateDirectory(testDir); // 同时会创建缺失的发布根目录
                var f1 = Path.Combine(testDir, "1Mb.test");
                if (!File.Exists(f1))
                    using (var fs = new FileStream(f1, FileMode.Create)) fs.SetLength(1L * 1024 * 1024); // 1MB 空文件
                var f33 = Path.Combine(testDir, "33MB.test");
                if (!File.Exists(f33))
                    using (var fs = new FileStream(f33, FileMode.Create)) fs.SetLength(33L * 1024 * 1024); // 33MB 空文件
                var appJson = Path.Combine(testDir, "app.json");
                if (!File.Exists(appJson))
                    File.WriteAllText(appJson, JsonConvert.SerializeObject(new { id = "1", name = "test" }, Formatting.Indented));
                WriteTestManifest(testDir); // 清单：让连接测试的“更新测试”有文件可校验
            }
            catch (Exception ex) { Log("初始化 test 测试目录失败：" + ex.Message); }
        }

        /// <summary>为 test 目录生成 manifest.json（含 1Mb.test / 33MB.test 的 path/size/sha256）。已存在则跳过（避免每次启动都哈希 33MB）。</summary>
        private void WriteTestManifest(string testDir)
        {
            try
            {
                var maniPath = Path.Combine(testDir, "manifest.json");
                if (File.Exists(maniPath)) return;
                var files = new JArray();
                foreach (var name in new[] { "1Mb.test", "33MB.test" })
                {
                    var fp = Path.Combine(testDir, name);
                    if (!File.Exists(fp)) continue;
                    var size = new FileInfo(fp).Length;
                    string sha;
                    using (var fs = File.OpenRead(fp))
                    using (var alg = SHA256.Create())
                    {
                        var h = alg.ComputeHash(fs);
                        var sb = new StringBuilder(h.Length * 2);
                        foreach (var b in h) sb.Append(b.ToString("x2"));
                        sha = sb.ToString();
                    }
                    files.Add(new JObject { ["path"] = name, ["size"] = size.ToString(), ["sha256"] = sha });
                }
                var mani = new JObject
                {
                    ["app"] = "test",
                    ["version"] = "1.0",
                    ["files"] = files
                };
                File.WriteAllText(maniPath, mani.ToString(Formatting.Indented));
            }
            catch (Exception ex) { Log("生成 test 清单失败：" + ex.Message); }
        }

        static readonly object _logLock = new object();
        private void Log(string s)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
            try { Console.WriteLine(line); } catch { }
            try { lock (_logLock) File.AppendAllText(_logPath, line + "\r\n"); } catch { }   // 串行写，避免多线程日志交错
        }
    }

    /// <summary>Windows 服务宿主（正规服务，可被 SCM 优雅启停）。</summary>
    public class PropelService : ServiceBase
    {
        private HttpServer _server;
        public PropelServerConfig Config { get; set; }

        protected override void OnStart(string[] args)
        {
            _server = new HttpServer();
            _server.Start(Config);
        }

        protected override void OnStop()
        {
            _server?.Stop();
        }
    }

    public static class Program
    {
        public static PropelServerConfig Config;

        public static void Main(string[] args)
        {
            // 单实例：避免重复监听端口
            if (!SingleInstance.Ensure("Local\\PropelService_Single", false)) return;
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
            Config = PropelServerConfig.Load();
            Config.ApplyArgs(args);
            Config.NormalizePaths();
            if (Environment.UserInteractive || Config.ConsoleMode)
            {
                var srv = new HttpServer();
                srv.Start(Config);
                Console.WriteLine("Propel (console) 运行中，按 Ctrl+C 停止。");
                var exit = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; srv.Stop(); exit.Set(); };
                exit.WaitOne();
                Console.WriteLine("已停止。");
            }
            else
            {
                ServiceBase.Run(new PropelService { Config = Config });
            }
        }
    }
}
