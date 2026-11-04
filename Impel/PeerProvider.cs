using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Impel
{
    /// <summary>
    /// 客户端 P2P 提供端：用 TcpListener 实现最小化 HTTP 服务（绕开 http.sys/urlacl，普通账户即可监听），
    /// 仅暴露「本地 manifest 中记录且 sha256 校验通过」的 App 文件，staging/old/lock 等一律不可被读取。
    /// </summary>
    public class PeerProvider
    {
        private readonly ImpelConfig _cfg;
        private TcpListener _listener;
        private volatile bool _run;
        // 限制并发连接处理数，避免高并发下无限制创建线程导致资源耗尽
        private static readonly SemaphoreSlim _connLimit = new SemaphoreSlim(64);

        public PeerProvider(ImpelConfig cfg) { _cfg = cfg; }

        public void Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _cfg.p2p.port);
                _listener.Start();
                _run = true;
                new Thread(Loop) { IsBackground = true }.Start();
            }
            catch (Exception ex) { Console.WriteLine("PeerProvider 启动失败：" + ex.Message); }
        }

        public void Stop() { _run = false; try { _listener?.Stop(); } catch { } }

        void Loop()
        {
            while (_run)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (!_run) break; continue; }
                var cc = c;
                _ = Task.Run(async () =>
                {
                    await _connLimit.WaitAsync();
                    try { Handle(cc); }
                    finally { _connLimit.Release(); }
                });
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    try { ns.ReadTimeout = 10000; } catch { }   // 防止慢速/半开连接长期占用信号量槽
                    var req = ReadRequest(ns);
                    if (req == null) return;
                    var segs = req.Path.Trim('/').Split('/');
                    if (segs.Length < 2 || string.IsNullOrEmpty(segs[0])) { SendText(ns, 400, "bad request"); return; }
                    var appId = segs[0];
                    var rel = string.Join("/", segs, 1, segs.Length - 1).Replace('\\', '/');

                    // URL 段是数字 id；本地磁盘目录用中文真实名 dir（id→dir 映射来自下发列表）
                    var dir = ResolveDir(appId);
                    var appRoot = Path.Combine(ConfigService.SoftwareRoot(_cfg), dir);
                    var mPath = Path.Combine(appRoot, "manifest.json");
                    if (!File.Exists(mPath)) { SendText(ns, 404, "no manifest"); return; }
                    Manifest m;
                    try { m = Manifest.Parse(File.ReadAllText(mPath)); }
                    catch { SendText(ns, 500, "manifest error"); return; }

                    FileEntry fe = null;
                    foreach (var f in m.files)
                        if (string.Equals(f.path, rel, StringComparison.OrdinalIgnoreCase)) { fe = f; break; }
                    if (fe == null) { SendText(ns, 404, "not listed"); return; }

                    var file = Path.Combine(appRoot, rel.Replace('/', '\\'));
                    if (!File.Exists(file)) { SendText(ns, 404, "missing"); return; }
                    // 防路径逃逸：实际文件必须落在 appRoot 内（rel 来自 URL，可能含 ..）
                    var rootFull = Path.GetFullPath(appRoot).TrimEnd('\\') + "\\";
                    if (!Path.GetFullPath(file).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    { SendText(ns, 400, "bad path"); return; }

                    // 不做逐请求 SHA256：文件哈希在更新落地与注册（RegisterApp）时已校验过，
                    // 每请求全量读盘算哈希会让大文件首字节延迟数秒、磁盘 IO 翻倍，是 P2P 吞吐瓶颈
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + new FileInfo(file).Length + "\r\nConnection: close\r\n\r\n");
                    ns.Write(header, 0, header.Length);
                    // 流式发送（原 File.ReadAllBytes 会把整个文件读进内存才开始发）
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var buf = new byte[65536]; int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) ns.Write(buf, 0, n);
                    }
                }
            }
            catch { }
        }

        /// <summary>把 URL 段（数字 id）解析为本地中文目录名 dir；找不到映射时回退原值（兼容旧布局）。</summary>
        string ResolveDir(string appId)
        {
            if (!string.IsNullOrEmpty(appId) && _cfg?.apps != null)
                foreach (var a in _cfg.apps)
                    if (string.Equals(a.id, appId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.dir))
                        return a.dir;
            return appId;
        }

        static Request ReadRequest(NetworkStream ns)
        {
            using (var r = new StreamReader(ns, Encoding.ASCII, false, 1024, true))
            {
                var line = r.ReadLine();
                if (string.IsNullOrEmpty(line)) return null;
                var sp = line.Split(' ');
                if (sp.Length < 2) return null;
                var raw = sp[1];
                int q = raw.IndexOf('?');
                var path = q >= 0 ? raw.Substring(0, q) : raw;
                return new Request { Path = Uri.UnescapeDataString(path) };
            }
        }

        static void SendText(NetworkStream ns, int code, string body)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var h = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + code + " \r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
            ns.Write(h, 0, h.Length);
            ns.Write(b, 0, b.Length);
        }

        class Request { public string Path; }
    }
}
