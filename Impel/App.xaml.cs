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
            string migrateRoot = null;
            foreach (var a in e.Args)
                if (a.StartsWith("--migrate", StringComparison.OrdinalIgnoreCase))
                    migrateRoot = a.Length > 9 ? a.Substring(10).Trim('"') : null;
            if (migrateRoot != null)
            {
                try { FinishMigration(migrateRoot); } catch (Exception ex) { Log("迁移收尾失败：" + ex.Message); }
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

        /// <summary>当前 exe 是否已位于期望布局：&lt;软件根&gt;\impel\impel.exe（软件根下的 impel 子目录中）。
        /// 不依赖目录名（软件根目录本身也可能叫 impel，例如 d:\impel），只比对实际期望路径。</summary>
        private static bool IsInTargetLayout()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var targetExe = Path.Combine(root, "impel", "impel.exe");
            return string.Equals(Path.GetFullPath(self), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否需要迁移：本体当前不在“软件根\impel\impel.exe”布局，且软件根目录下直接存在 impel.exe（旧布局）。</summary>
        private static bool NeedsMigration()
        {
            if (IsInTargetLayout()) return false;
            var root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            return File.Exists(Path.Combine(root, "impel.exe"));
        }

        /// <summary>第一阶段：复制本体与依赖到 impel\ 子目录，释放互斥体后拉起子目录实例完成收尾，并退出本进程。</summary>
        private static void BootstrapMigration()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            var sub = Path.Combine(root, "impel");
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
                    Arguments = "--migrate \"" + root + "\"",
                    WorkingDirectory = sub,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { Log("启动迁移实例失败：" + ex.Message); }
            Application.Current?.Shutdown();
        }

        /// <summary>第二阶段（子目录实例）：把根目录剩余的配置/缓存/依赖整体移入 impel\，并修正快捷方式指向。</summary>
        private static void FinishMigration(string root)
        {
            var sub = Path.Combine(root, "impel");
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("impel", StringComparison.OrdinalIgnoreCase)) continue;       // 目标子目录本身
                if (name.Equals("impel.exe", StringComparison.OrdinalIgnoreCase)) continue;    // 本体已复制
                if (name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)) continue; // 依赖已复制
                if (name.Equals("SelfUpdate.staging", StringComparison.OrdinalIgnoreCase)) continue;   // 升级临时目录
                var dest = Path.Combine(sub, name);
                try
                {
                    if (File.Exists(entry) && !Directory.Exists(entry))
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(entry, dest);
                    }
                    else if (Directory.Exists(entry))
                    {
                        if (Directory.Exists(dest)) Directory.Delete(dest, true);
                        Directory.Move(entry, dest);
                    }
                }
                catch (Exception ex) { Log("迁移条目失败 " + name + "：" + ex.Message); }
            }
            UpdateShortcuts(Path.Combine(sub, "impel.exe"), sub);
            try { var old = Path.Combine(root, "impel.exe"); if (File.Exists(old)) File.Delete(old); } catch { }
            try { var old = Path.Combine(root, "Newtonsoft.Json.dll"); if (File.Exists(old)) File.Delete(old); } catch { }
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
