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

            // 一次性迁移：把位于软件根目录的本体迁入 impel\ 子目录（含配置与依赖）。
            // 放在 impel.exe 自身启动期，故即使 ImpelUpdater 尚未更新，升级后首次启动即可完成迁移。
            string migrateRoot = null, migrateTo = null;
            foreach (var a in e.Args)
                if (a.StartsWith("--migrate", StringComparison.OrdinalIgnoreCase))
                    migrateRoot = a.Length > 9 ? a.Substring(10).Trim('"') : null;
                else if (a.StartsWith("--to", StringComparison.OrdinalIgnoreCase))
                    migrateTo = a.Length > 5 ? a.Substring(6).Trim('"') : null;
            if (migrateRoot != null)
            {
                try { FinishMigration(migrateRoot, migrateTo); } catch (Exception ex) { Log("迁移收尾失败：" + ex.Message); }
            }
            else if (NeedsMigration())
            {
                BootstrapMigration();
                return; // BootstrapMigration 内部已启动子目录实例并关闭本进程
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

        /// <summary>是否需要迁移：本体不在期望布局，且目标位置尚无其它 impel 实例（避免覆盖另一份安装）。</summary>
        private static bool NeedsMigration()
        {
            if (IsInTargetLayout()) return false;
            return !File.Exists(Path.Combine(TargetImpelDir(), "impel.exe"));
        }

        /// <summary>第一阶段：复制本体与依赖到 &lt;softwareRoot&gt;\impel，释放互斥体后拉起目标实例完成收尾，并退出本进程。</summary>
        private static void BootstrapMigration()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var sub = TargetImpelDir();
            try { Directory.CreateDirectory(sub); } catch { }
            try { File.Copy(Path.Combine(root, "impel.exe"), Path.Combine(sub, "impel.exe"), true); } catch { }
            var dll = Path.Combine(root, "Newtonsoft.Json.dll");
            if (File.Exists(dll)) { try { File.Copy(dll, Path.Combine(sub, "Newtonsoft.Json.dll"), true); } catch { } }
            try { _single?.ReleaseMutex(); } catch { }
            _single = null;
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(sub, "impel.exe"))
                {
                    Arguments = "--migrate \"" + root + "\" --to \"" + sub + "\"",
                    WorkingDirectory = sub,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { Log("启动迁移实例失败：" + ex.Message); }
            Application.Current?.Shutdown();
        }

        /// <summary>第二阶段（目标目录实例）：把旧目录剩余的配置/缓存/依赖整体移入 &lt;softwareRoot&gt;\impel\，并修正快捷方式指向。</summary>
        private static void FinishMigration(string root, string to)
        {
            var sub = string.IsNullOrEmpty(to) ? Path.Combine(root, "impel") : Path.GetFullPath(to);
            Directory.CreateDirectory(sub);
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("SelfUpdate.staging", StringComparison.OrdinalIgnoreCase)) continue;   // 升级临时目录
                if (Directory.Exists(entry) && string.Equals(Path.GetFullPath(entry), sub, StringComparison.OrdinalIgnoreCase))
                    continue; // 目标目录本身
                if (Directory.Exists(entry) && name.Equals("impel", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(entry), sub, StringComparison.OrdinalIgnoreCase))
                {
                    // 旧版迁移在旧目录内留下的 impel\ 残留：内容并入目标后删除
                    MoveInto(entry, sub);
                    try { Directory.Delete(entry, true); } catch (Exception ex) { Log("清理旧 impel 残留失败：" + ex.Message); }
                    continue;
                }
                if (name.Equals("impel.exe", StringComparison.OrdinalIgnoreCase))
                { try { File.Delete(entry); } catch { } continue; }   // 本体已复制到目标，删除旧文件
                if (name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(sub, name)))
                { try { File.Delete(entry); } catch { } continue; }   // 依赖已复制到目标
                MoveEntryTo(entry, sub);
            }
            UpdateShortcuts(Path.Combine(sub, "impel.exe"), sub);
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
