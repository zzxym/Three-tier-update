using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Impel
{
    public class ServerCfg { public string host = ""; public int port = 80; public string token = ""; }
    public class DockCfg { public string side = "left"; public bool autohide = false; public int width = 200; public int collapsedWidth = 22; public bool topmost = false; }
    public class P2PCfg { public bool enabled = true; public int port = 45123; public string advertiseIp = ""; }   // advertiseIp：多网卡机器手动指定对外上报的 providerUrl IP（留空则自动选与 Repel 同网段的 IP）
    public class AppCfg
    {
        public string id = "";
        public string name = "";
        public string dir = "";
        public string exe = "";
        public string workdir = "";
        public string process = "";
        public bool portableData = false;
        public bool enabled = true;
        public Dictionary<string, string> env = new Dictionary<string, string>();
        public string args = "";
        public string version = "";
    }
    public class ImpelConfig
    {
        public ServerCfg server = new ServerCfg();
        public DockCfg dock = new DockCfg();
        public P2PCfg p2p = new P2PCfg();
        public string softwareRoot = "d:\\soft";
        public string propelSource = "";   // 由 Repel 的 repel.lst 响应头 X-Propel 下发：Propel 直连兜底地址
        public int downloadParallel = 6;   // 单应用并行下载路数（默认 6；机械盘/共享 repel 场景可调小，独占千兆可适度调大）
        public int smallFileThresholdKB = 1024;   // 小文件阈值(KB)：小于此值直接从 Repel keep-alive 连发（中心缓存必命中），大于等于才走 P2P 分担带宽；默认 1024KB(1MB)
        public bool autostart = true;   // 开机自动启动（默认开启：登录后自动拉起菜单）
        public bool logging = false;   // 日志开关：默认关，不写 impel.log
        public List<AppCfg> apps = new List<AppCfg>();
        public Dictionary<string, string> installed = new Dictionary<string, string>();   // 各应用已安装版本（key=appId），成功更新后写入，用于稳定判断“已最新”
        // ===== 客户端自更新 =====
        public string clientId = "";     // 稳定客户端标识：Repel 据此定向下发升级指令（重启后不变）
        public string lastCmdId = "";    // 已执行过的升级指令 id：防止重启后重复升级形成循环
    }

    /// <summary>下载/更新进度（文件级 + 当前文件字节级）。</summary>
    public class UpdateProgress
    {
        public int doneFiles;
        public int totalFiles;
        public string currentFile = "";
        public long currentBytes;
        public long currentTotal;
        public int Percent
        {
            get
            {
                if (totalFiles <= 0) return 0;
                long num = (long)doneFiles * currentTotal + (currentTotal > 0 ? currentBytes : 0);
                long den = (long)totalFiles * currentTotal;
                if (den <= 0) return doneFiles * 100 / totalFiles;
                return (int)(num * 100 / den);
            }
        }
    }

    public class FileEntry
    {
        public string path;
        public string size;
        public string sha256;
        public List<BlockEntry> blocks;
        /// <summary>种子数据标记（由 Propel 清单生成器写入）：应用运行会改写这类文件（扩展设置、
        /// LevelDB WAL、Preferences 等），但首次安装时必须从发布源同步过来。语义为：
        /// 仅当本地缺失时下载、不参与哈希比对、不覆盖本地已有文件（保留用户后续设置）。
        /// 缺省 false，旧清单无此字段时按普通文件处理，向后兼容。</summary>
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool seed = false;
        public long SizeLong => long.TryParse(size, out var v) ? v : 0;
    }
    public class BlockEntry { public long off; public long len; public string sha256; }
    public class Manifest
    {
        public string app;
        public string version;
        public List<FileEntry> files = new List<FileEntry>();
        public static Manifest Parse(string json) => JsonConvert.DeserializeObject<Manifest>(json);
    }

    public static class Utils
    {
        class HashEntry { public long size; public DateTime mtimeUtc; public string head; public string hash; }
        static readonly Dictionary<string, HashEntry> HashCache = new Dictionary<string, HashEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly object HashLock = new object();
        static bool _hashDirty;
        static readonly string HashCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hashcache.json");

        static Utils()
        {
            // 启动时载入上次会话的哈希缓存：重启后校验/注册无需重新全量计算
            try
            {
                if (File.Exists(HashCachePath))
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, HashEntry>>(File.ReadAllText(HashCachePath));
                    if (d != null) lock (HashLock) { foreach (var kv in d) HashCache[kv.Key] = kv.Value; }
                }
            }
            catch { }
            // 每 30 秒：期间有新算的哈希才落盘
            new System.Threading.Timer(_ => { if (_hashDirty) SaveHashCache(); }, null, 30000, 30000);
        }

        static void SaveHashCache()
        {
            try
            {
                string json;
                lock (HashLock) { json = JsonConvert.SerializeObject(HashCache); _hashDirty = false; }
                File.WriteAllText(HashCachePath, json);
            }
            catch { }
        }

        /// <summary>文件 SHA256，按 (大小, mtime, 内容指纹) 缓存：内容未变直接复用结果。
        /// P2P 注册（每分钟续报全部文件）与更新前校验因此从"每次全量重算 GB 级文件"降为毫秒级；
        /// 缓存定期落盘 hashcache.json，重启后免重算（低配机首次注册再也不会因算不完而错过续期）。
        /// 内容指纹取文件前 1MB 的哈希，与 size+mtime 共同作为失效判据，避免"大小与时间戳恰好相同但内容已变"时
        /// 命中陈旧哈希（否则会导致更新装出损坏版本 / 向其它 peer 注册错误哈希）。</summary>
        public static string Sha256File(string path)
        {
            var fi = new FileInfo(path);
            long size = fi.Length; var mtime = fi.LastWriteTimeUtc;
            lock (HashLock)
                if (HashCache.TryGetValue(path, out var e) && e.size == size && e.mtimeUtc == mtime && e.head != null)
                    return e.hash;
            string hash, head;
            using (var s = File.OpenRead(path))
            {
                // 内容指纹：文件前 1MB 的哈希；与 size+mtime 共同判定缓存有效性
                long headLen = Math.Min(size, 1024 * 1024);
                var headBuf = new byte[headLen];
                int got = 0;
                while (got < headLen) { int n = s.Read(headBuf, got, (int)Math.Min(headLen - got, int.MaxValue)); if (n == 0) break; got += n; }
                using (var hh = SHA256.Create()) head = BytesToHex(hh.ComputeHash(headBuf, 0, got));
                s.Seek(0, SeekOrigin.Begin);
                using (var h = SHA256.Create()) hash = BytesToHex(h.ComputeHash(s));
            }
            lock (HashLock) { HashCache[path] = new HashEntry { size = size, mtimeUtc = mtime, head = head, hash = hash }; _hashDirty = true; }
            return hash;
        }
        public static string BytesToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
