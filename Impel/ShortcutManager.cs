using System;
using System.IO;

namespace Impel
{
    /// <summary>快捷方式管理：桌面 / 开始菜单（程序） / 开机自启（启动文件夹）。使用 WScript.Shell 动态创建 .lnk。</summary>
    public static class ShortcutManager
    {
        public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string StartMenuPrograms => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        public static string StartupDir => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        public static string ImpelExe => Path.Combine(ConfigService.ExeDir, "impel.exe");

        public static void CreateShortcut(string linkPath, string target, string workdir, string desc)
        {
            var dir = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic shortcut = shell.CreateShortcut(linkPath);
                shortcut.TargetPath = target;
                if (!string.IsNullOrEmpty(workdir)) shortcut.WorkingDirectory = workdir;
                shortcut.Description = desc ?? "";
                try { shortcut.IconLocation = target + ",0"; } catch { }
                shortcut.Save();
            }
            catch (Exception ex) { throw new Exception("创建快捷方式失败：" + ex.Message); }
        }

        public static string AppLinkPath(ImpelConfig cfg, AppCfg app, string folder)
            => Path.Combine(folder, app.name + ".lnk");

        public static string AppExe(ImpelConfig cfg, AppCfg app) => Path.Combine(ConfigService.AppRoot(cfg, app), app.exe);
        public static string AppWorkdir(ImpelConfig cfg, AppCfg app) => Path.Combine(ConfigService.AppRoot(cfg, app), app.workdir);

        public static void CreateDesktopShortcut(ImpelConfig cfg, AppCfg app)
            => CreateShortcut(AppLinkPath(cfg, app, DesktopDir), AppExe(cfg, app), AppWorkdir(cfg, app), app.name);

        public static void CreateStartMenuShortcut(ImpelConfig cfg, AppCfg app)
            => CreateShortcut(AppLinkPath(cfg, app, StartMenuPrograms), AppExe(cfg, app), AppWorkdir(cfg, app), app.name);

        public static void CreateAutostartShortcut()
            => CreateShortcut(Path.Combine(StartupDir, "impel.lnk"), ImpelExe, ConfigService.ExeDir, "天一网络三层更新菜单");

        public static void RemoveAutostartShortcut()
        {
            try { File.Delete(Path.Combine(StartupDir, "impel.lnk")); } catch { }
        }
    }
}
