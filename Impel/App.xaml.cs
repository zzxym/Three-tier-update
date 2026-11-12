using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace Impel
{
    public partial class App : Application
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "impel.log");

        public static bool EnableLogging { get; private set; } = false; // 默认关，不产生日志

        private static Mutex _single;          // 单实例互斥体
        private static EventWaitHandle _showEvt; // 跨进程“呼出”信号

        public App()
        {
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
        }

        /// <summary>设置日志开关；关闭时同时删除已存在的 impel.log，确保不残留日志。</summary>
        public static void SetLogging(bool on)
        {
            EnableLogging = on;
            if (!on)
            {
                try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
            }
        }

        static readonly object _logLock = new object();
        public static void Log(string s)
        {
            if (!EnableLogging) return;
            try { lock (_logLock) File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " " + s + "\r\n"); } catch { }   // 串行写，避免多线程日志交错
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, ev) =>
            {
                Log("未处理异常: " + ev.Exception);
                ev.Handled = true;
            };

            // 单实例：已运行时仅呼出已有窗口，然后退出本进程
            bool first;
            _single = new Mutex(true, "Local\\Impel_Single", out first);
            _showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Impel_Show");
            if (!first)
            {
                _showEvt.Set();   // 通知已运行实例显示软件坞
                Shutdown();
                return;
            }

            // 一次性迁移：本体不在 <softwareRoot>\impel 时，从服务端拉取发布的 impel 到该目录（无发布则回退复制自身），
            // 并迁移配置/依赖/缓存到该目录，随后切换到该目录运行。放在 impel.exe 自身启动期，升级后首次启动即生效。
            if (!IsInTargetLayout())
            {
                BootstrapMigration(ConfigService.Load());
                return; // BootstrapMigration 内部已拉起目标目录实例并关闭本进程
            }

            // 首实例监听呼出信号
            ThreadPool.RegisterWaitForSingleObject(_showEvt,
                (s, t) => Dispatcher.Invoke(() => Impel.MainWindow.Current?.ShowDock()),
                null, Timeout.Infinite, false);

            try
            {
                var w = new MainWindow();
                w.Show();
            }
            catch (Exception ex)
            {
                Log("启动失败: " + ex);
                MessageBox.Show("Impel 启动失败：\n" + ex.Message, "Impel", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            base.OnStartup(e);
        }

        /// <summary>impel 的期望安装目录：&lt;softwareRoot&gt;\impel。softwareRoot 来自 Impel.json（用户可配置，缺省 d:\soft）；
        /// 读不到配置时回退为当前目录下的 impel 子目录。</summary>
        private static string TargetImpelDir()
        {
            try
            {
                var cfg = ConfigService.Load();
                return Path.Combine(ConfigService.SoftwareRoot(cfg), "impel");
            }
            catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "impel"); }
        }

        /// <summary>当前 exe 是否已位于期望布局：&lt;softwareRoot&gt;\impel\impel.exe。</summary>
        private static bool IsInTargetLayout()
        {
            var self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var targetExe = Path.Combine(TargetImpelDir(), "impel.exe");
            return string.Equals(Path.GetFullPath(self), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>迁移切换退出码：ImpelUpdater 据此区分“迁移后正常切换”与“启动崩溃”（后者触发回滚）。</summary>
        internal const int MigratedExitCode = 3010;

        /// <summary>若本进程由 ImpelUpdater 拉起（升级流程），等它完成提交/清理并退出后再迁移：
        /// 避免在它的 8 秒崩溃监控期内退出而被误判回滚（存量 ImpelUpdater 不识别迁移退出码 3010）。返回其是否已退出。</summary>
        private static bool WaitForUpdaterExit()
        {
            try
            {
                var self = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImpelUpdater.exe");
                for (int i = 0; i < 120; i++)
                {
                    bool running = false;
                    foreach (var p in Process.GetProcessesByName("ImpelUpdater"))
                    {
                        try
                        {
                            // 取不到模块路径（权限/退出时机）时按存在处理，保守等待
                            var path = p.MainModule == null ? null : p.MainModule.FileName;
                            if (string.IsNullOrEmpty(path) || string.Equals(Path.GetFullPath(path), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
                                running = true;
                        }
                        catch { running = true; }
                        try { p.Dispose(); } catch { }
                    }
                    if (!running) return true;
                    Thread.Sleep(500);
                }
                Log("等待 ImpelUpdater 退出超时（60s），继续执行迁移");
                return false;
            }
            catch (Exception ex) { Log("等待 ImpelUpdater 异常：" + ex.Message); return false; }
        }

        /// <summary>迁移到 &lt;softwareRoot&gt;\impel：优先从服务端拉取发布的 impel 包，否则复制自身；随后迁移配置/依赖并切换到目标运行。</summary>
        private static void BootstrapMigration(ImpelConfig cfg)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var sub = TargetImpelDir();
            try { Directory.CreateDirectory(sub); } catch { }

            // 先等拉起本进程的 ImpelUpdater 完成提交/清理并退出（若有），再开始迁移
            bool updaterGone = WaitForUpdaterExit();

            bool fetched = false;
            try { fetched = SelfUpdater.TryFetchTo(cfg, sub, Log); } catch (Exception ex) { Log("拉取发布版失败：" + ex.Message); }
            if (!fetched)
            {
                // 服务端无发布或拉取失败：回退为复制当前本体与依赖
                try { File.Copy(Path.Combine(root, "impel.exe"), Path.Combine(sub, "impel.exe"), true); } catch { }
                var dll = Path.Combine(root, "Newtonsoft.Json.dll");
                if (File.Exists(dll)) { try { File.Copy(dll, Path.Combine(sub, "Newtonsoft.Json.dll"), true); } catch { } }
            }

            // 迁移当前目录的配置/缓存/依赖到目标（程序文件若目标已有则丢弃源，避免覆盖发布版）
            MoveConfigTo(root, sub, updaterGone);
            UpdateShortcuts(Path.Combine(sub, "impel.exe"), sub);

            try { _single?.ReleaseMutex(); } catch { }
            _single = null;
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(sub, "impel.exe"))
                {
                    WorkingDirectory = sub,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { Log("启动目标实例失败：" + ex.Message); }
            Application.Current?.Shutdown();
            // 以约定退出码告知 ImpelUpdater：本进程并非启动崩溃，而是完成迁移后已切换到 <softwareRoot>\impel 运行
            Environment.ExitCode = MigratedExitCode;
        }

        /// <summary>将当前目录的用户数据与依赖整体迁入目标目录；程序文件（impel.exe/Newtonsoft/Updater）若目标已存在则删除源，避免覆盖发布版。</summary>
        private static void MoveConfigTo(string root, string target, bool deleteStaging)
        {
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("SelfUpdate.staging", StringComparison.OrdinalIgnoreCase))
                {
                    // 升级临时目录：ImpelUpdater 成功路径已删；失败残留或等待超时时，确认其已退出再清理
                    if (deleteStaging) { try { Directory.Delete(entry, true); } catch { } }
                    continue;
                }
                if (Directory.Exists(entry) && string.Equals(Path.GetFullPath(entry), target, StringComparison.OrdinalIgnoreCase)) continue; // 目标自身
                if (Directory.Exists(entry) && name.Equals("impel", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(entry), target, StringComparison.OrdinalIgnoreCase))
                {
                    // 旧版迁移在旧目录内留下的 impel\ 残留：内容并入目标后删除
                    MoveInto(entry, target);
                    try { Directory.Delete(entry, true); } catch (Exception ex) { Log("清理旧 impel 残留失败：" + ex.Message); }
                    continue;
                }
                if (name.Equals("impel.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ImpelUpdater.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(target, name);
                    if (File.Exists(dest)) { try { File.Delete(entry); } catch { } continue; } // 目标已有发布版，丢弃源
                    MoveEntryTo(entry, target);
                    continue;
                }
                MoveEntryTo(entry, target);
            }
        }

        /// <summary>把单个文件/目录移动到目标目录；.NET Framework 的 Move 不支持跨卷，失败时降级为复制+删除。</summary>
        private static void MoveEntryTo(string entry, string destDir)
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(destDir, name);
            try
            {
                if (Directory.Exists(entry))
                {
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    try { Directory.Move(entry, dest); }
                    catch { Directory.CreateDirectory(dest); MoveInto(entry, dest); Directory.Delete(entry, true); }
                }
                else
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    try { File.Move(entry, dest); } catch { File.Copy(entry, dest, true); File.Delete(entry); }
                }
            }
            catch (Exception ex) { Log("迁移条目失败 " + name + "：" + ex.Message); }
        }

        /// <summary>把目录内全部条目移入目标目录。</summary>
        private static void MoveInto(string srcDir, string destDir)
        {
            foreach (var entry in Directory.GetFileSystemEntries(srcDir))
                MoveEntryTo(entry, destDir);
        }

        /// <summary>把已知快捷方式（桌面 / 开始菜单 / 开机自启）的 Target 指向新的 impel.exe；不存在的 .lnk 不创建不修改。</summary>
        private static void UpdateShortcuts(string newExe, string newWorkdir)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                var shell = Activator.CreateInstance(shellType);
                var create = shellType.GetMethod("CreateShortcut", new[] { typeof(string) });
                string[] links = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "天一软件菜单.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "天一软件菜单.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "impel.lnk"),
                };
                foreach (var link in links)
                {
                    if (!File.Exists(link)) continue;
                    try
                    {
                        var sc = create.Invoke(shell, new object[] { link });
                        var scType = sc.GetType();
                        var targetProp = scType.GetProperty("TargetPath");
                        var t = (string)targetProp.GetValue(sc, null);
                        if (string.IsNullOrEmpty(t) || !t.Equals(newExe, StringComparison.OrdinalIgnoreCase))
                        {
                            targetProp.SetValue(sc, newExe, null);
                            scType.GetProperty("WorkingDirectory").SetValue(sc, newWorkdir, null);
                            scType.GetMethod("Save").Invoke(sc, null);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
