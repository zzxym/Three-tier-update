using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace PropelControl
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private string _lastStatus = "";
        private const string SvcName = "Propel";
        private const string SvcExe = "PropelService.exe";

        // 发布升级包时排除的用户数据 / 临时文件（按文件名，大小写不敏感）
        private static readonly HashSet<string> _publishExcludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "impel.json", "repel.json", "hashcache.json", "selfupdate.log", "repel.log", "app.config"
        };
        private static readonly HashSet<string> _publishExcludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obj", "bin", ".git", "impelpkg", "cache", ".vs", "selfupdate.staging"
        };

        public MainWindow()
        {
            InitializeComponent();
            // 标题追加程序集版本号
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)
                    Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                if (a != null && !string.IsNullOrEmpty(a.InformationalVersion))
                {
                    // InformationalVersion 在 CI 构建时会带 "+<commit>" 后缀（SourceRevisionId），截去只留版本号
                    var v = a.InformationalVersion;
                    var plus = v.IndexOf('+');
                    if (plus >= 0) v = v.Substring(0, plus);
                    Title = Title + " v" + v;
                }
            }
            catch { }
            // 高分屏（150% 缩放）下可用高度不足：将窗口高度钳制到工作区内，避免底部被任务栏截断
            Loaded += (s, e) =>
            {
                double max = SystemParameters.WorkArea.Height - 24;
                if (Height > max) Height = max;
                MaxHeight = max;
            };
            LoadCfg();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => RefreshStatus();
            _timer.Start();
            RefreshStatus();
            // 增量 tail 同目录 propel.log：把 PropelService 的文件下载日志实时显示在运行日志里
            var logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            logTimer.Tick += (s, e) => PollServiceLog();
            logTimer.Start();
        }

        private long _logPos = -1;

        /// <summary>增量读取 propel.log 新增内容（首次从文件尾开始，不回放历史；服务日志被重建时自动复位）。</summary>
        private void PollServiceLog()
        {
            try
            {
                var path = Path.Combine(AppDir, "propel.log");
                if (!File.Exists(path)) { _logPos = 0; return; }
                var len = new FileInfo(path).Length;
                if (_logPos < 0) { _logPos = len; return; } // 首次只看新增
                if (len < _logPos) _logPos = 0;
                if (len == _logPos) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs))
                    {
                        var text = sr.ReadToEnd();
                        _logPos = fs.Position;
                        if (text.Length > 0) Log(text.TrimEnd('\r', '\n'));
                    }
                }
            }
            catch { }
        }

        private static string AppDir
        {
            get
            {
                var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(loc) ?? ".";
            }
        }

        // ---------------- 配置 ----------------
        private void LoadCfg()
        {
            try
            {
                var path = Path.Combine(AppDir, "propel.json");
                if (!File.Exists(path)) return;
                var cfg = JsonConvert.DeserializeObject<PropelCfg>(File.ReadAllText(path));
                if (cfg == null) return;
                if (!string.IsNullOrEmpty(cfg.root)) txtRoot.Text = cfg.root;
                if (!string.IsNullOrEmpty(cfg.addr)) txtAddr.Text = cfg.addr;
                if (cfg.tokens != null) txtTokens.Text = cfg.tokens;
                Log("已读取 propel.json");
            }
            catch (Exception ex) { Log("读取配置失败：" + ex.Message); }
        }

        private void BtnBrowseRoot_Click(object s, RoutedEventArgs e)
        {
            var p = FolderPicker.Pick("选择发布根目录", txtRoot.Text);
            if (!string.IsNullOrEmpty(p)) txtRoot.Text = p;
        }

        private void SaveCfg()
        {
            try
            {
                var obj = new { root = txtRoot.Text, addr = txtAddr.Text, tokens = txtTokens.Text };
                File.WriteAllText(Path.Combine(AppDir, "propel.json"),
                    JsonConvert.SerializeObject(obj, Formatting.Indented));
                Log("已写入 propel.json");
            }
            catch (Exception ex) { Log("写入配置失败：" + ex.Message); }
        }

        // ---------------- 发布升级包（供 Repel 自动拉取）----------------
        private void BtnPublishImpel_Click(object s, RoutedEventArgs e)
        {
            var dir = FolderPicker.Pick("选择新版 Impel 客户端目录（需含 impel.exe）", "");
            if (string.IsNullOrEmpty(dir)) return;
            PublishPackage("impel", dir);
        }

        private void BtnPublishRepel_Click(object s, RoutedEventArgs e)
        {
            var dir = FolderPicker.Pick("选择新版 Repel 服务端目录（需含 RepelService.exe）", "");
            if (string.IsNullOrEmpty(dir)) return;
            PublishPackage("repel", dir);
        }

        /// <summary>把构建产物目录发布到 Root/&lt;type&gt;/：拷贝程序文件并生成带 sha256 的 version.json。
        /// Repel 会自动从 Propel 拉取该包（Impel 基线 / Repel 自身自更新），无需手动逐台导入。</summary>
        private void PublishPackage(string type, string srcDir)
        {
            if (string.IsNullOrEmpty(txtRoot.Text.Trim())) { Log("请先填写发布根目录 root 并保存配置"); return; }
            var root = txtRoot.Text.Trim();
            if (!Directory.Exists(root)) { Log("发布根目录不存在：" + root); return; }
            if (!Directory.Exists(srcDir)) { Log("源目录不存在：" + srcDir); return; }

            var destDir = Path.Combine(root, type);
            Directory.CreateDirectory(destDir);

            var mainExe = Path.Combine(srcDir, type == "impel" ? "impel.exe" : "RepelService.exe");
            string version = "";
            if (File.Exists(mainExe))
            {
                var s = (FileVersionInfo.GetVersionInfo(mainExe).ProductVersion ?? "").Trim();
                int plus = s.IndexOf('+'); version = plus > 0 ? s.Substring(0, plus) : s;
            }
            if (string.IsNullOrEmpty(version)) { Log("无法确定版本（缺少 " + Path.GetFileName(mainExe) + " 或 ProductVersion 为空），已中止"); return; }

            var files = new List<object>();
            foreach (var f in SafeWalk(srcDir))
            {
                var rel = MakeRel(srcDir, f);
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(f);
                if (_publishExcludeFiles.Contains(name)) continue;
                if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bak", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lst", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] data = File.ReadAllBytes(f);
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
                var dest = Path.Combine(destDir, rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(f, dest, true);
                files.Add(new { path = rel, size = data.Length, sha256 = hash });
            }
            if (files.Count == 0) { Log("源目录无可发布文件，已中止"); return; }
            File.WriteAllText(Path.Combine(destDir, "version.json"),
                JsonConvert.SerializeObject(new { version = version, files = files }, Formatting.Indented));
            Log($"已发布 {type} 升级包 v{version}：{files.Count} 个文件 -> {destDir}（Repel 将自动拉取）");
        }

        private static IEnumerable<string> SafeWalk(string dir)
        {
            foreach (var f in Directory.GetFiles(dir)) yield return f;
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (_publishExcludeDirs.Contains(Path.GetFileName(d))) continue;
                foreach (var f in SafeWalk(d)) yield return f;
            }
        }

        private static string MakeRel(string baseDir, string file)
        {
            var root = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + "\\";
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(file);
            return full.Substring(root.Length).Replace('\\', '/');
        }

        // ---------------- 工具 ----------------
        private static string Q(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Contains(" ") ? "\"" + s + "\"" : s);

        private string BuildArgs() =>
            $"-root {Q(txtRoot.Text)} -addr {Q(txtAddr.Text)} -tokens {Q(txtTokens.Text)}";

        private void BtnManage_Click(object s, RoutedEventArgs e)
        {
            var f = new ManifestForm { Owner = this };
            f.ShowDialog();
        }

        private void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtLog.AppendText(line + "\r\n");
                txtLog.ScrollToEnd();   // 自动跳转到最新一行
            }));
        }

        // ---------------- 服务状态与按钮切换 ----------------
        /// <summary>查找服务；未安装返回 null（不能用 new+Refresh 判断，对不存在的服务不抛异常）。</summary>
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

        private void BtnSave_Click(object s, RoutedEventArgs e) => SaveCfg();

        // ---------------- 服务操作（后台执行，避免界面卡顿）----------------
        private void InstallSvc()
        {
            Log("开始安装服务...");
            Task.Run(() =>
            {
                var exe = Path.Combine(AppDir, SvcExe);
                if (!File.Exists(exe)) { Log("未找到 " + SvcExe + "（需与本程序放在同一目录）"); return; }
                SaveCfg();
                Log("sc create ...");
                Log(RunCmd("sc", $"create {SvcName} binPath= \"{exe}\" start= auto DisplayName= \"Propel 服务\""));
                Log(RunCmd("sc", $"description {SvcName} \"天一网络三层更新源服务：用于发布版本更新，向Repel节点与Impel终端提供版本清单与文件分发\""));
                Log("安装完成");
                Dispatcher.Invoke(RefreshStatus);
            });
        }

        private void UninstallSvc()
        {
            Log("开始卸载服务...");
            Task.Run(() =>
            {
                try
                {
                    using (var sc = new ServiceController(SvcName))
                    {
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                    }
                }
                catch (Exception ex) { Log("停止服务（卸载前）：" + ex.Message); }
                Log(RunCmd("sc", "delete " + SvcName));
                Log("卸载完成");
                Dispatcher.Invoke(RefreshStatus);
            });
        }

        private void StartSvc()
        {
            Log("正在启动服务...");
            Task.Run(() =>
            {
                try
                {
                    using (var sc = new ServiceController(SvcName))
                    {
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Running)
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                        }
                        Log("服务已启动");
                    }
                }
                catch (Exception ex) { Log("启动失败（需以管理员身份运行）：" + ex.Message); }
                Dispatcher.Invoke(RefreshStatus);
            });
        }

        private void StopSvc()
        {
            Log("正在停止服务...");
            Task.Run(() =>
            {
                try
                {
                    using (var sc = new ServiceController(SvcName))
                    {
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                        Log("服务已停止");
                    }
                }
                catch (Exception ex) { Log("停止失败（需以管理员身份运行）：" + ex.Message); }
                Dispatcher.Invoke(RefreshStatus);
            });
        }

        private static string RunCmd(string cmd, string args)
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
                    var er = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return (o + er).Trim();
                }
            }
            catch (Exception ex) { return "执行失败：" + ex.Message; }
        }
    }
}
