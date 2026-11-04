using System;
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

        private Mutex _single;          // 单实例互斥体
        private EventWaitHandle _showEvt; // 跨进程“呼出”信号

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
    }
}
