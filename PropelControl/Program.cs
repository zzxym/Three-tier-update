using System;
using System.IO;
using System.Windows;

namespace PropelControl
{
    /// <summary>
    /// Propel 服务（中心源）图形管理前端：配置管理、软件管理（清单生成）、Windows 服务安装与启停。
    /// WPF 主界面（MainWindow）；清单生成器子窗口沿用 WinForms 的 ManifestForm。
    /// </summary>
    class PropelCfg { public string root; public string addr; public string tokens; }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // 单实例：已运行时呼出并置顶已有窗口，然后退出本进程
            if (!SingleInstance.Ensure("Local\\PropelControl_Single", true)) return;
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow());
        }
    }
}
