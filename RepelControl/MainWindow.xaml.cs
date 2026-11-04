using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RepelControl
{
    class RepelCfg { public int port; public string source; public string tokens; public string cache; }

    public partial class MainWindow : Window
    {
        private const string SvcName = "Repel";
        private const string SvcExe = "RepelService.exe";
        private string _lastStatus = "";

        public MainWindow()
        {
            InitializeComponent();
            // 高分屏（150% 缩放）下可用高度不足时，将窗口高度钳制到工作区内，避免底部被任务栏截断
            Loaded += (s, e) =>
            {
                double max = SystemParameters.WorkArea.Height - 24;
                if (Height > max) Height = max;
                MaxHeight = max;
            };
            LoadCfg();
            RefreshStatus();
        }

        // ---------------- 工具 ----------------
        private static string ExeDir => Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";

        private void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtLog.AppendText(line + "\r\n");
                txtLog.ScrollToEnd();
            }));
        }

        private void Log(string fmt, params object[] a) => Log(string.Format(fmt, a));

        // ---------------- 配置读写 ----------------
        private void LoadCfg()
        {
            try
            {
                var path = Path.Combine(ExeDir, "repel.json");
                if (!File.Exists(path)) return;
                var cfg = JsonConvert.DeserializeObject<RepelCfg>(File.ReadAllText(path));
                if (cfg == null) return;
                if (cfg.port != 0) txtPort.Text = cfg.port.ToString();
                if (!string.IsNullOrEmpty(cfg.source)) txtSource.Text = cfg.source;
                if (cfg.tokens != null) txtTokens.Text = cfg.tokens;
                if (!string.IsNullOrEmpty(cfg.cache)) txtCache.Text = cfg.cache;
                Log("已读取 repel.json：" + ExeDir);
            }
            catch (Exception ex) { Log("读取配置失败：" + ex.Message); }
        }

        private void BtnWriteCfg_Click(object s, RoutedEventArgs e) => WriteCfg();

        private void WriteCfg()
        {
            try
            {
                int port = 80; int.TryParse(txtPort.Text.Trim(), out port);
                // 保留已有 selfUrl（若有），避免热重载把自身节点地址复位为空（多网卡/反代场景回退 Host 头可能不准）
                string existingSelfUrl = "";
                try { var ex = JObject.Parse(File.ReadAllText(Path.Combine(ExeDir, "repel.json"))); existingSelfUrl = (string)ex["selfUrl"] ?? ""; } catch { }
                var obj = new { port = port, source = txtSource.Text.Trim(), tokens = txtTokens.Text.Trim(), cache = txtCache.Text.Trim(), selfUrl = existingSelfUrl };
                File.WriteAllText(Path.Combine(ExeDir, "repel.json"),
                    JsonConvert.SerializeObject(obj, Formatting.Indented));
                Log("已写入 repel.json：" + ExeDir);
            }
            catch (Exception ex) { Log("写入配置失败：" + ex.Message); }
        }

        // ---------------- 服务存在性判定（用 GetServices，避免 new+Refresh 对不存在服务不抛异常）----------------
        private static ServiceController FindSvc()
        {
            try
            {
                return ServiceController.GetServices()
                    .FirstOrDefault(x => x.ServiceName.Equals(SvcName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private bool IsInstalled() => FindSvc() != null;

        private bool IsRunning()
        {
            using (var sc = FindSvc())
                return sc != null && sc.Status == ServiceControllerStatus.Running;
        }

        private void RefreshStatus()
        {
            string s; bool installed, running;
            using (var sc = FindSvc())
            {
                if (sc != null)
                {
                    installed = true;
                    running = sc.Status == ServiceControllerStatus.Running;
                    s = sc.Status.ToString();
                }
                else { installed = false; running = false; s = "未安装"; }
            }

            btnInstall.Content = installed ? "卸载服务" : "安装服务";
            btnStart.Content = running ? "停止服务" : "启动服务";
            btnStart.IsEnabled = installed;
            btnTest.IsEnabled = running;   // 服务未运行时测试连接按钮置灰不可用

            if (s != _lastStatus)
            {
                _lastStatus = s;
                Log("当前服务状态：" + s);
            }
        }

        private void BtnInstall_Click(object s, RoutedEventArgs e)
        {
            if (IsInstalled()) UninstallSvc(); else InstallSvc();
        }

        private void BtnStart_Click(object s, RoutedEventArgs e)
        {
            if (IsRunning()) StopSvc(); else StartSvc();
        }

        private void InstallSvc()
        {
            var exe = Path.Combine(ExeDir, SvcExe);
            if (!File.Exists(exe)) { Log("未找到 " + SvcExe); return; }
            WriteCfg();
            var args = $"create {SvcName} binPath= \"{exe}\" start= auto DisplayName= \"Repel 服务\"";
            Log("sc " + args);
            Log(RunCmd("sc", args));
            Log(RunCmd("sc", $"description {SvcName} \"天一网络三层更新网吧服务端：从更新源服务器回源并缓存分发，提供 P2P 协调与局域网文件加速\""));
            RefreshStatus();
        }

        private void UninstallSvc()
        {
            try
            {
                using (var sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (Exception ex) { Log("停止服务（卸载前）：" + ex.Message); }
            Log(RunCmd("sc", "delete " + SvcName));
            RefreshStatus();
        }

        private void StartSvc()
        {
            try
            {
                using (var sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                    Log("服务已启动");
                }
            }
            catch (Exception ex) { Log("启动失败（需以管理员身份运行）：" + ex.Message); }
            RefreshStatus();
        }

        private void StopSvc()
        {
            try
            {
                using (var sc = new ServiceController(SvcName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                    Log("服务已停止");
                }
            }
            catch (Exception ex) { Log("停止失败（需以管理员身份运行）：" + ex.Message); }
            RefreshStatus();
        }

        // ---------------- 连接 / 回源测试 ----------------
        private async void BtnTest_Click(object s, RoutedEventArgs e)
        {
            // 在 UI 线程先读取控件值，后台线程不得直接访问控件（WPF 跨线程限制）
            int port = 80; int.TryParse(txtPort.Text.Trim(), out port);
            var token = txtTokens.Text.Trim();
            var cacheDir = ResolveCache();
            btnTest.IsEnabled = false;
            try
            {
                await Task.Run(() => TestConnection(port, token, cacheDir));
            }
            catch (Exception ex)
            {
                Log("测试过程异常：" + ex.Message);
            }
            finally
            {
                btnTest.IsEnabled = true;
            }
        }

        private string ResolveCache()
        {
            var cache = txtCache.Text.Trim();
            if (string.IsNullOrEmpty(cache)) cache = "cache";
            if (!Path.IsPathRooted(cache)) cache = Path.Combine(ExeDir, cache);
            return cache;
        }

        private void TestConnection(int port, string token, string cacheDir)
        {
            var baseUrl = "http://127.0.0.1:" + port;
            var appId = "test";
            var q = string.IsNullOrEmpty(token) ? "" : "?token=" + Uri.EscapeDataString(token);
            var sep = string.IsNullOrEmpty(q) ? "?" : "&";

            Log("==== 连接测试（appid=" + appId + "）====");

            Log("[1] 测试 Propel 连接...");
            var probeBody = HttpGetBody(baseUrl + "/probesource" + q, out var cp, out var ep);
            if (probeBody == null && cp == 0) { Log("      Propel 连接失败：" + ep); }
            else if (cp != 200) { Log("      Propel 连接失败（HTTP " + cp + "）"); }
            else
            {
                try
                {
                    var pj = JObject.Parse(Encoding.UTF8.GetString(probeBody));
                    var okp = (bool?)pj["ok"] ?? false;
                    var errp = (string)pj["err"] ?? "";
                    if (okp) Log("      Propel 连接成功");
                    else Log("      Propel 连接失败：" + errp);
                }
                catch (Exception ex) { Log("      Propel 连接失败（解析响应：" + ex.Message + "）"); }
            }

            Log("[2] 测试下载...");
            // 强制回源（force=1），绕过 Repel 本地缓存，真实反映 Propel 能否提供文件
            var maniUrl = baseUrl + "/" + appId + "/manifest.json" + q + sep + "force=1";
            var body = HttpGetBody(maniUrl, out var c2, out var e2);
            if (c2 == 200 && body != null) { Log("      下载正常"); }
            else if (body == null && c2 == 0) { Log("      下载失败：" + e2); }
            else if (c2 == 401) { Log("      下载失败：鉴权失败（token 不一致）"); }
            else { Log("      下载失败（HTTP " + c2 + "）"); }

            Log("==== 测试完成 ====");
        }

        public static byte[] HttpGetBody(string url, out int status, out string err)
        {
            status = 0; err = "";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 8000; req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var ms = new MemoryStream())
                { status = (int)resp.StatusCode; resp.GetResponseStream().CopyTo(ms); return ms.ToArray(); }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse r)
                {
                    status = (int)r.StatusCode;
                    using (var s = r.GetResponseStream()) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
                }
                err = we.Message; return null;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        private long CachedSize(string cacheDir, string appId, string rel)
        {
            try { var p = Path.Combine(cacheDir, appId, rel.Replace('/', '\\')); return File.Exists(p) ? new FileInfo(p).Length : 0; }
            catch { return 0; }
        }

        private void ReportCache(string cacheDir)
        {
            try
            {
                var cache = cacheDir;
                if (Directory.Exists(cache))
                {
                    var n = Directory.GetFiles(cache, "*", SearchOption.AllDirectories).Length;
                    Log("  当前缓存目录：" + cache + "，文件数：" + n);
                }
            }
            catch (Exception ex) { Log("  读取缓存目录失败：" + ex.Message); }
        }

        private bool HttpProbe(string url, out int status, out string err)
        {
            status = 0; err = "";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 5000; req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                { status = (int)resp.StatusCode; return true; }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse r) { status = (int)r.StatusCode; return true; }
                err = we.Message; return false;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        private string RunCmd(string cmd, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEnd();
                    var e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return (o + e).Trim();
                }
            }
            catch (Exception ex) { return "执行失败：" + ex.Message; }
        }

        // ---------------- 应用分发列表管理 ----------------
        private void BtnLst_Click(object s, RoutedEventArgs e)
        {
            int port = 80; int.TryParse(txtPort.Text.Trim(), out port);
            var w = new LstWindow(port, txtTokens.Text.Trim(), Log) { Owner = this };
            w.ShowDialog();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // 单实例：已运行时呼出并置顶已有窗口，然后退出本进程
            if (!SingleInstance.Ensure("Local\\RepelControl_Single", true)) return;
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
            var app = new Application();
            app.Run(new MainWindow());
        }
    }
}
