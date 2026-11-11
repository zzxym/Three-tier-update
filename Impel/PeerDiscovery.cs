using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace Impel
{
    /// <summary>客户端 P2P 协调：向 Repel(branch) 注册本机已校验文件，并按 hash 查询局域网可用 peer 源。</summary>
    public static class PeerDiscovery
    {
        /// <summary>取本机局域网 IP；多网卡时优先选与 Repel 服务器（preferHost）同网段的地址，
        /// 保证上报的 providerUrl 对其他客户端可达。</summary>
        public static string LocalLanIP(string preferHost)
        {
            var ips = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("169.254")) continue;
                        ips.Add(ip);
                    }
                }
            }
            catch { }
            if (ips.Count == 0) return "127.0.0.1";
            if (ips.Count > 1 && !string.IsNullOrEmpty(preferHost))
            {
                IPAddress t;
                if (IPAddress.TryParse(preferHost, out t))
                {
                    var tb = t.GetAddressBytes();
                    string best = null; int bestScore = 0;
                    foreach (var s in ips)
                    {
                        int score = 0;
                        try
                        {
                            var b = IPAddress.Parse(s).GetAddressBytes();
                            if (b[0] == tb[0] && b[1] == tb[1] && b[2] == tb[2]) score = 3;
                            else if (b[0] == tb[0] && b[1] == tb[1]) score = 2;
                            else if (b[0] == tb[0]) score = 1;
                        }
                        catch { }
                        if (score > bestScore) { bestScore = score; best = s; }
                    }
                    if (best != null) return best;
                }
            }
            return ips[0];
        }

        /// <summary>Repel 服务器主机名/IP（用于同网段 IP 选择）。</summary>
        static string RepelHost(ImpelConfig cfg)
        {
            try { return new Uri(UpdateEngine.RepelBase(cfg)).Host; } catch { return ""; }
        }

        public static void RegisterApp(ImpelConfig cfg, AppCfg app)
        {
            var root = ConfigService.AppRoot(cfg, app);
            var mPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(mPath)) return;
            Manifest m;
            try { m = Manifest.Parse(File.ReadAllText(mPath)); } catch { return; }
            var hashes = new List<string>();
            foreach (var f in m.files)
            {
                // 客户端自维护文件与 seed 种子数据不参与共享注册：
                // 前者内容由 Impel 改写、后者每台机器可能不同且会被应用改写，注册了也永远校验不上。
                // 判据与 UpdateEngine 一致，保证清单即唯一策略源。
                if (UpdateEngine.IsSelfProtected(f.path) || f.seed) continue;
                var lp = Path.Combine(root, f.path.Replace('/', '\\'));
                if (File.Exists(lp) && Utils.Sha256File(lp).Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                    hashes.Add(f.sha256);
            }
            if (hashes.Count == 0) return;
            var body = new
            {
                appId = app.id,
                version = m.version,
                hashes = hashes,
                providerUrl = "http://" + (string.IsNullOrEmpty(cfg.p2p.advertiseIp) ? LocalLanIP(RepelHost(cfg)) : cfg.p2p.advertiseIp.Trim()) + ":" + cfg.p2p.port,
                host = Environment.MachineName,   // 客户端本地计算机名，repelView 显示用
                clientId = cfg.clientId ?? ""     // 稳定标识：Repel 据此把 clientId 与计算机名关联
            };
            PostRegister(cfg, body);
        }

        static void PostRegister(ImpelConfig cfg, object body)
        {
            var url = UpdateEngine.RepelBase(cfg) + "/peer/register";
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "?token=" + Uri.EscapeDataString(cfg.server.token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 8000;
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
            }
            catch (Exception ex) { Impel.App.Log("P2P注册失败: " + ex.Message); }
        }

        /// <summary>周期注册：把本地已校验的所有应用向 Repel 重新上报（注册 20 分钟过期，每 5 分钟续期，
        /// 这样多台客户端无需再次点击即可在 repelView 上互相可见；哈希有缓存，续期几乎零开销）。</summary>
        public static void RegisterAll(ImpelConfig cfg)
        {
            if (cfg.apps != null)
                foreach (var a in cfg.apps)
                {
                    if (a == null || string.IsNullOrEmpty(a.id) || !a.enabled) continue;
                    try { RegisterApp(cfg, a); } catch { }
                }
            OnlinePing(cfg);   // 始终上报在线：即便未下载任何应用，也让 repelView 列出本机（覆盖"零 app 客户端不显示"）
        }

        /// <summary>纯在线上报：即使本地没有任何已装应用，也向 Repel 上报本机 providerUrl，
        /// 使 repelView 能列出"在线但零 app"的客户端（仅注册已装应用无法覆盖此场景）。</summary>
        public static void OnlinePing(ImpelConfig cfg)
        {
            var providerUrl = "http://" + (string.IsNullOrEmpty(cfg.p2p.advertiseIp) ? LocalLanIP(RepelHost(cfg)) : cfg.p2p.advertiseIp.Trim()) + ":" + cfg.p2p.port;
            PostOnline(cfg, providerUrl, Environment.MachineName);
        }

        static void PostOnline(ImpelConfig cfg, string providerUrl, string host)
        {
            var url = UpdateEngine.RepelBase(cfg) + "/peer/online";
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "?token=" + Uri.EscapeDataString(cfg.server.token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 8000;
                var body = new { providerUrl = providerUrl, host = host, clientId = cfg.clientId ?? "" };
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
            }
            catch (Exception ex) { Impel.App.Log("在线心跳失败: " + ex.Message); }
        }

        public static List<string> QueryPeers(ImpelConfig cfg, string appId, string hash)
        {
            var url = UpdateEngine.RepelBase(cfg) + "/peer/query?appId=" + Uri.EscapeDataString(appId)
                + "&hash=" + Uri.EscapeDataString(hash);
            if (!string.IsNullOrEmpty(cfg.server.token)) url += "&token=" + Uri.EscapeDataString(cfg.server.token);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 8000;
                req.Proxy = null;                                    // 局域网直连：绕开系统代理/WPAD 自动检测（每个请求可省几百 ms）
                req.ServicePoint.ConnectionLimit = 16;               // 并行下载时查询不被默认连接池(2)限流
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return new List<string>();
                    var arr = JsonConvert.DeserializeObject<List<string>>(sr.ReadToEnd());
                    return arr ?? new List<string>();
                }
            }
            catch { return new List<string>(); }
        }
    }
}
