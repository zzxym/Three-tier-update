using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RepelView
{
    public class PeerRow : INotifyPropertyChanged
    {
        public string providerUrl { get; set; }
        public string host;   // 客户端本地计算机名（Repel /diag 下发）
        public int apps { get; set; }
        public int hashes { get; set; }
        public bool online { get; set; }
        public string lastSeenUtc { get; set; }
        public string version { get; set; } = "-";   // 客户端版本号（Diag 按 IP 关联下发，空时显示“-”）
        private string _hostText = "-";
        public string HostText { get { return _hostText; } set { _hostText = value; OnChanged("HostText"); } }
        private string _lastSeenDisplay = "-";
        public string LastSeenDisplay { get { return _lastSeenDisplay; } set { _lastSeenDisplay = value; OnChanged("LastSeenDisplay"); } }
        private string _appsText = "-";
        public string AppsText { get { return _appsText; } set { _appsText = value; OnChanged("AppsText"); } }
        private string _hashesText = "-";
        public string HashesText { get { return _hashesText; } set { _hashesText = value; OnChanged("HashesText"); } }
        private string _downloading = "-";
        public string Downloading { get { return _downloading; } set { _downloading = value; OnChanged("Downloading"); } }
        private string _speedText = "-";
        public string SpeedText { get { return _speedText; } set { _speedText = value; OnChanged("SpeedText"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class PropelInfo { public string source; public string lastFetchUtc; public bool lastSuccess; public string lastError; }
    public class StatsInfo { public long registerTotal; public long queryTotal; public long fileHits; public long fileMiss; public long lstRequests; public long changesRequests; }
    public class CacheInfo { public int apps; public int hashes; }
    public class SpeedRow : INotifyPropertyChanged
    {
        public string ip { get; set; }
        public string appNames { get; set; }   // 正在下载的应用名称（多个逗号分隔，由 Repel 聚合）
        public double rateKBs { get; set; }    // 该客户端所有正在下载应用的速率之和
        public string lastSeenUtc { get; set; }
        private string _rateText = "-";
        public string RateText { get { return _rateText; } set { _rateText = value; OnChanged("RateText"); } }
        private string _lastSeenDisplay = "-";
        public string LastSeenDisplay { get { return _lastSeenDisplay; } set { _lastSeenDisplay = value; OnChanged("LastSeenDisplay"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public class DiagRoot
    {
        public int port; public string source; public bool tokenRequired; public string cacheDir; public string selfUrl;
        public long menuVersion; public long uptimeSec;
        public CacheInfo cache;
        public PropelInfo propel; public List<PeerRow> peers; public StatsInfo stats; public List<string> recent;
        public List<SpeedRow> speeds;
    }

    public partial class MainWindow : Window
    {
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly ObservableCollection<PeerRow> _peers = new ObservableCollection<PeerRow>();
        private int _port = 80;
        private string _token = "";

        public MainWindow()
        {
            InitializeComponent();
            dgPeers.ItemsSource = _peers;
            Loaded += (s, e) =>
            {
                LoadConfig();   // 直接读取同目录 repel.json 的端口/token
                _timer.Interval = TimeSpan.FromSeconds(3);
                _timer.Tick += (t, ev) => _ = RefreshAsync();
                _timer.Start();
                _ = RefreshAsync();

                // 实时日志改为直接 tail 同目录 repel.log（持久化、关掉不丢），而非内存 recent
                var logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                logTimer.Tick += (s, ev) => PollServiceLog();
                logTimer.Start();
                PollServiceLog();
            };
        }

        private long _logPos = -1;

        /// <summary>增量读取 repel.log（首次加载末尾 500 行历史，之后只看新增），倒序显示：最新日志始终在第一行；超 1500 行截掉最旧部分保留 1000 行。</summary>
        private void PollServiceLog()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.log");
                if (!File.Exists(path)) { _logPos = 0; return; }
                var len = new FileInfo(path).Length;
                if (_logPos < 0)
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        long maxRead = Math.Min(len, 64 * 1024);
                        fs.Seek(Math.Max(0, len - maxRead), SeekOrigin.Begin);
                        string tail;
                        using (var sr = new StreamReader(fs)) tail = sr.ReadToEnd();
                        int idx = tail.IndexOf('\n');
                        if (idx >= 0) tail = tail.Substring(idx + 1);   // 丢弃可能跨字节截断的首行
                        var arr = tail.Split('\n');
                        IEnumerable<string> keep = arr.Length > 500 ? new List<string>(arr).GetRange(arr.Length - 500, 500) : arr;
                        txtLog.Text = string.Join("\n", keep.Reverse()).TrimEnd('\r', '\n');   // 倒序：最新在最上
                    }
                    _logPos = len;
                    txtLog.ScrollToHome();
                    return;
                }
                if (len < _logPos) _logPos = 0;   // 日志被截断/重建则从头读
                if (len == _logPos) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs))
                    {
                        var text = sr.ReadToEnd();
                        _logPos = fs.Position;
                        if (text.Length == 0) return;
                        var newLines = text.TrimEnd('\r', '\n').Split('\n');
                        Array.Reverse(newLines);
                        txtLog.Text = string.Join("\n", newLines) + "\n" + txtLog.Text;   // 新日志插到最上面
                        if (txtLog.LineCount > 1500)
                        {
                            var lines = txtLog.Text.Split('\n');
                            txtLog.Text = string.Join("\n", lines, 0, 1000).TrimEnd('\r');   // 保留最新的前 1000 行
                        }
                        txtLog.ScrollToHome();
                    }
                }
            }
            catch { }
        }

        /// <summary>直接读取同目录 repel.json 的 port 与 tokens（第一个）作为连接参数，不在界面显示。</summary>
        private void LoadConfig()
        {
            try
            {
                var cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "repel.json");
                if (!File.Exists(cfg)) return;
                var j = JObject.Parse(File.ReadAllText(cfg));
                if (j["port"] != null) _port = j.Value<int>("port");
                var tokens = (string)j["tokens"];
                if (!string.IsNullOrWhiteSpace(tokens)) _token = tokens.Split(',')[0].Trim();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            var url = $"http://127.0.0.1:{_port}/diag";
            if (!string.IsNullOrEmpty(_token)) url += "?token=" + Uri.EscapeDataString(_token);
            try
            {
                var json = await _http.GetStringAsync(url);
                var d = JsonConvert.DeserializeObject<DiagRoot>(json);
                Dispatcher.Invoke(() => Apply(d));
            }
            catch
            {
                Dispatcher.Invoke(() => { SetRepelState(false); SetConn(false); });
            }
        }

        /// <summary>Repel 服务状态（Repel 服务卡片首行）：正常=绿色，失败=红色。</summary>
        private void SetRepelState(bool ok)
        {
            runState.Text = ok ? "正常" : "失败";
            runState.Foreground = new System.Windows.Media.SolidColorBrush(
                ok ? System.Windows.Media.Color.FromRgb(46, 160, 67) : System.Windows.Media.Color.FromRgb(200, 60, 60));
        }

        /// <summary>服务器（Propel）连接状态：最近一次同步成功=正常（绿），否则失败（红）。</summary>
        private void SetConn(bool ok)
        {
            runConn.Text = ok ? "正常" : "失败";
            runConn.Foreground = new System.Windows.Media.SolidColorBrush(
                ok ? System.Windows.Media.Color.FromRgb(46, 160, 67) : System.Windows.Media.Color.FromRgb(200, 60, 60));
        }

        private void Apply(DiagRoot d)
        {
            SetRepelState(true);

            runUptime.Text = FormatUptime(d.uptimeSec);
            runCache.Text = RelativeToBase(d.cacheDir);

            if (d.stats != null)
            {
                stReg.Text = d.stats.registerTotal.ToString();
                stQry.Text = d.stats.queryTotal.ToString();
                stHit.Text = d.stats.fileHits.ToString();
            }

            if (d.propel != null)
            {
                SetConn(d.propel.lastSuccess);
                runPropelTime.Text = ParseUtc(d.propel.lastFetchUtc);
                if (d.propel.lastSuccess)
                {
                    runPropelStatus.Text = "同步成功";
                    runPropelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 160, 67));
                    badgePropel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 246, 234));
                }
                else
                {
                    runPropelStatus.Text = "同步失败";
                    runPropelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 60, 60));
                    badgePropel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(252, 235, 235));
                }
            }

            _peers.Clear();
            // 下载速率按来源 IP 建索引：填充 peers 时按客户端地址匹配，注入"正在下载/下载速度"两列
            var speedMap = new Dictionary<string, SpeedRow>(StringComparer.OrdinalIgnoreCase);
            if (d.speeds != null)
                foreach (var s in d.speeds) speedMap[s.ip] = s;
            int online = 0;
            if (d.peers != null)
            {
                foreach (var p in d.peers)
                {
                    p.HostText = string.IsNullOrEmpty(p.host) ? "-" : p.host;
                    p.version = string.IsNullOrEmpty(p.version) ? "-" : p.version;
                    p.LastSeenDisplay = ParseUtc(p.lastSeenUtc);
                    p.AppsText = p.apps.ToString();
                    p.HashesText = p.hashes.ToString();
                    SpeedRow sp;
                    if (speedMap.TryGetValue(HostOf(p.providerUrl), out sp))
                    {
                        p.Downloading = string.IsNullOrEmpty(sp.appNames) ? "-" : sp.appNames;
                        p.SpeedText = FmtRateKBs(sp.rateKBs);
                    }
                    if (p.online) online++;
                    _peers.Add(p);
                }
            }
            int realCount = _peers.Count;
            // 置顶一行：中心缓存（Repel）自身，应用数/哈希数取自服务端缓存统计，最新上报以 "-" 表示
            _peers.Insert(0, new PeerRow
            {
                providerUrl = d.selfUrl,
                host = "本机",
                HostText = "本机",
                version = "-",
                apps = d.cache?.apps ?? 0,
                hashes = d.cache?.hashes ?? 0,
                online = true,
                lastSeenUtc = null,
                AppsText = d.cache != null ? d.cache.apps.ToString() : "-",
                HashesText = d.cache != null ? d.cache.hashes.ToString() : "-",
                LastSeenDisplay = "-"
            });
            lblPeers.Text = $"在线客户端 ({realCount}，在线 {online})";
        }

        private static string ParseUtc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "从未/无";
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return s;
        }

        /// <summary>从 providerUrl（http://ip:port）提取主机部分，用于与速率上报的来源 IP 匹配。</summary>
        private static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            var u = url.Trim();
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) u = u.Substring(scheme + 3);
            int slash = u.IndexOf('/');
            if (slash >= 0) u = u.Substring(0, slash);
            int colon = u.IndexOf(':');   // IPv4/主机名带端口时去端口（IPv6 场景极少，不特殊处理）
            if (colon >= 0) u = u.Substring(0, colon);
            return u;
        }

        /// <summary>速率格式化：≥1MB/s 显示 MB/s，否则 KB/s。</summary>
        private static string FmtRateKBs(double kbs)
        {
            return kbs >= 1024 ? (kbs / 1024).ToString("F2") + " MB/s" : kbs.ToString("F1") + " KB/s";
        }

        /// <summary>把绝对路径转为相对程序目录的相对路径（缓存目录显示用，如 "cache"）；
        /// 不在程序目录下时只显示最后一段目录名，避免长路径撑破卡片。</summary>
        private static string RelativeToBase(string full)
        {
            if (string.IsNullOrEmpty(full)) return "-";
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!baseDir.EndsWith("\\") && !baseDir.EndsWith("/")) baseDir += Path.DirectorySeparatorChar;
                var trimmed = full.TrimEnd('\\', '/');
                if (trimmed.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(baseDir.Length);
                return Path.GetFileName(trimmed);
            }
            catch { return full; }
        }

        private static string FormatUptime(long sec)
        {
            if (sec <= 0) return "0秒";
            var ts = TimeSpan.FromSeconds(sec);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}天 {ts.Hours}时 {ts.Minutes}分";
            if (ts.TotalHours >= 1) return $"{ts.Hours}时 {ts.Minutes}分 {ts.Seconds}秒";
            return $"{ts.Minutes}分 {ts.Seconds}秒";
        }
    }
}
