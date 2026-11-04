using System.Diagnostics;
using System.IO;
using Impel;

namespace Impel
{
    /// <summary>启动应用：复用 Impel 的目录/环境变量/便携数据重定向逻辑。</summary>
    public static class Launcher
    {
        public static void Launch(ImpelConfig cfg, AppCfg app)
        {
            try
            {
                var root = ConfigService.AppRoot(cfg, app);
                var exe = Path.Combine(root, app.exe ?? "");
                if (!File.Exists(exe))
                {
                    App.Log("启动失败 [" + app.name + "] 未找到主程序: " + exe);
                    System.Windows.MessageBox.Show("未找到：" + exe + "\n该软件尚未下载完成，请稍后重试或检查网络。",
                        "Impel", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                var psi = new ProcessStartInfo(exe, app.args ?? "")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.Combine(root, app.workdir ?? "")
                };
                if (app.portableData)
                {
                    var roaming = Path.Combine(root, "Data", "Roaming");
                    var local = Path.Combine(root, "Data", "Local");
                    Directory.CreateDirectory(roaming);
                    Directory.CreateDirectory(local);
                    psi.EnvironmentVariables["APPDATA"] = roaming;
                    psi.EnvironmentVariables["LOCALAPPDATA"] = local;
                }
                if (app.env != null)
                    foreach (var kv in app.env)
                        psi.EnvironmentVariables[kv.Key] = Path.Combine(root, kv.Value);
                var p = Process.Start(psi);
                App.Log("启动 [" + app.name + "] " + app.exe + (p != null ? " (PID " + p.Id + ")" : ""));
                p?.Dispose();   // 释放进程句柄，避免句柄泄漏直至 GC
            }
            catch (System.Exception ex)
            {
                App.Log("启动异常 [" + app.name + "]: " + ex.Message);
                System.Windows.MessageBox.Show("启动失败：" + ex.Message, "Impel",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
