using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace RepelView
{
    public partial class App : Application
    {
        public App()
        {
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
            InitializeComponent();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例：已运行时呼出并置顶已有窗口，然后退出本进程
            if (!SingleInstance.Ensure("Local\\RepelView_Single", true))
            {
                Shutdown();
                return;
            }
            base.OnStartup(e);
            new MainWindow().Show();
        }
    }
}
