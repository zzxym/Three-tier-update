using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace ImpelUpdater
{
    /// <summary>
    /// Impel 客户端独立升级器：由 impel.exe 在退出前拉起，负责"旧进程完全退出 → 备份 → 替换 → 启动新版 → 校验 → 回滚"。
    /// 零外部依赖（不引用 Newtonsoft.Json），清单为纯文本 files.lst：每行「相对路径&lt;TAB&gt;sha256」。
    /// 运行中的 impel.exe 不能删除/覆盖，但 Windows 允许同卷重命名，因此采用 Move 到 .bak 再搬入新文件的方式。
    /// </summary>
    internal static class Program
    {
        private const string SingleMutex = "Local\\Impel_Single";

        private class Entry { public string Rel; public string Hash; }

        private static string _dir;
        private static string _stage;
        private static string _logPath;
        // 已动过的目标文件（含实际备份名），用于失败时逐个回滚（含"原本不存在的新增文件"）
        private static readonly List<(string Dest, string Bak)> _replaced = new List<(string, string)>();

        private static int Main(string[] args)
        {
            try
            {
                var opt = ParseArgs(args);
                if (!opt.TryGetValue("dir", out _dir) || !opt.TryGetValue("stage", out _stage))
                    return 1;
                int pid = 0;
                if (opt.TryGetValue("pid", out var pidStr)) int.TryParse(pidStr, out pid);
                _logPath = Path.Combine(_dir, "selfupdate.log");
                Log("=== ImpelUpdater 启动 dir=" + _dir + " stage=" + _stage + " pid=" + pid + " ===");

                WaitOldProcessExit(pid);
                MigrateIfNeeded();
                var entries = ReadManifest();
                VerifyStage(entries);
                Replace(entries);

                var proc = LaunchImpel();
                if (proc == null) throw new Exception("替换后无法启动 impel.exe");

                // 给 WPF 进程足够的启动时间：若它立刻崩溃（缺 DLL / 版本不匹配），立即回滚到旧版
                Thread.Sleep(8000);
                if (proc.HasExited) throw new Exception("新版 impel.exe 启动后退出（退出码 " + proc.ExitCode + "）");

                Commit();
                Log("升级成功，新版 impel.exe 已运行");
                return 0;
            }
            catch (Exception ex)
            {
                Log("升级失败：" + ex.Message);
                Rollback();
                TryLaunchOld();
                return 1;
            }
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length - 1; i++)
            {
                var a = args[i];
                if (a != null && a.StartsWith("--", StringComparison.Ordinal)) d[a.Substring(2)] = args[i + 1];
            }
            return d;
        }

        /// <summary>先等旧进程退出，再等单实例互斥体真正释放，避免新进程撞 Mutex 后自行退出。</summary>
        private static void WaitOldProcessExit(int pid)
        {
            if (pid > 0)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        if (!p.WaitForExit(30000)) Log("旧进程 30s 未退出，继续等待互斥体释放");
                }
                catch (ArgumentException) { /* 已退出 */ }
                catch (Exception ex) { Log("等待旧进程异常：" + ex.Message); }
            }
            for (int i = 0; i < 60; i++)
            {
                bool created;
                using (var m = new Mutex(true, SingleMutex, out created))
                {
                    if (created)
                    {
                        try { m.ReleaseMutex(); } catch { }
                        return;
                    }
                }
                Thread.Sleep(250);
            }
            throw new Exception("旧 impel 进程未释放单实例互斥体，放弃替换");
        }

        private static List<Entry> ReadManifest()
        {
            var mf = Path.Combine(_stage, "files.lst");
            if (!File.Exists(mf)) throw new Exception("缺少 files.lst");
            var list = new List<Entry>();
            foreach (var line in File.ReadAllLines(mf))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = line.Split('\t');
                if (p.Length < 2 || string.IsNullOrWhiteSpace(p[0]) || string.IsNullOrWhiteSpace(p[1])) continue;
                list.Add(new Entry { Rel = p[0].Trim(), Hash = p[1].Trim() });
            }
            if (list.Count == 0) throw new Exception("files.lst 为空");
            return list;
        }

        private static string SafePath(string baseDir, string rel)
        {
            var root = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + "\\";
            var full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', '\\')));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new Exception("路径越界：" + rel);
            return full;
        }

        /// <summary>替换前再次校验 staging 全量哈希：磁盘写入损坏或半截下载都不允许进入安装目录。</summary>
        private static void VerifyStage(List<Entry> entries)
        {
            foreach (var e in entries)
            {
                var src = SafePath(_stage, e.Rel);
                if (!File.Exists(src)) throw new Exception("缺少升级文件：" + e.Rel);
                if (!Sha256(src).Equals(e.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("升级文件校验失败：" + e.Rel);
            }
            if (!entries.Exists(x => x.Rel.Equals("impel.exe", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("升级包未包含 impel.exe");
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }

        private static void Replace(List<Entry> entries)
        {
            foreach (var e in entries)
            {
                var dest = SafePath(_dir, e.Rel);
                var src = SafePath(_stage, e.Rel);
                // 上次升级残留的 .bak（可能被其他进程锁定）不得阻塞本次升级：删除失败则换唯一名
                var bak = dest + ".bak";
                if (File.Exists(bak))
                {
                    try { File.Delete(bak); }
                    catch { bak = dest + ".bak_" + Guid.NewGuid().ToString("N").Substring(0, 8); }
                }
                // 运行中的 exe 可以同卷重命名，但不能删除：用 Move 腾出目标名
                if (File.Exists(dest)) File.Move(dest, bak);
                _replaced.Add((dest, bak));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Move(src, dest);
                }
                catch (Exception ex) { throw new Exception("写入 " + e.Rel + " 失败：" + ex.Message); }
            }
        }

        private static Process LaunchImpel()
        {
            try
            {
                var exe = Path.Combine(_dir, "impel.exe");
                if (!File.Exists(exe)) return null;
                return Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = _dir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) { Log("启动 impel.exe 异常：" + ex.Message); return null; }
        }

        private static void Rollback()
        {
            for (int i = _replaced.Count - 1; i >= 0; i--)
            {
                var (dest, bak) = _replaced[i];
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    if (File.Exists(bak)) File.Move(bak, dest);
                }
                catch (Exception ex) { Log("回滚 " + dest + " 失败：" + ex.Message); }
            }
        }

        private static void TryLaunchOld()
        {
            // 回滚后旧版必须还能起来，否则用户会失去菜单且无法自助恢复
            try { LaunchImpel(); } catch { }
        }

        /// <summary>升级时若 impel 仍在软件根目录（非 impel\ 子目录），则整体迁入 impel\ 子目录（含配置文件与依赖），
        /// 并把更新器自身复制过去；同时把已知快捷方式的 Target 改成新的 impel.exe。仅迁移一次。</summary>
        private static void MigrateIfNeeded()
        {
            var exeAtRoot = Path.Combine(_dir, "impel.exe");
            if (!File.Exists(exeAtRoot)) return; // 已在 impel\ 子目录（已迁移）
            var sub = Path.Combine(_dir, "impel");
            if (Directory.Exists(sub) && File.Exists(Path.Combine(sub, "impel.exe"))) return;
            try
            {
                Directory.CreateDirectory(sub);
                // 更新器自身正在根目录运行，不可移动：复制到子目录以保证下次自更新可拉起
                var self = Path.Combine(_dir, "ImpelUpdater.exe");
                if (File.Exists(self)) { try { File.Copy(self, Path.Combine(sub, "ImpelUpdater.exe"), true); } catch { } }
                // 移动其余所有条目到子目录（排除 staging 临时目录、更新器自身、目标子目录本身）
                foreach (var entry in Directory.GetFileSystemEntries(_dir))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals("SelfUpdate.staging", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("ImpelUpdater.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("impel", StringComparison.OrdinalIgnoreCase)) continue;
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
                _dir = sub;
                UpdateShortcuts(Path.Combine(sub, "impel.exe"), sub);
                Log("impel 已迁移至 " + sub);
            }
            catch (Exception ex) { Log("迁移 impel 至子目录失败：" + ex.Message); }
        }

        /// <summary>把已知快捷方式（桌面 / 开始菜单 / 开机自启）的 Target 指向新的 impel.exe，避免迁移后失效。</summary>
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

        private static void Commit()
        {
            foreach (var (dest, bak) in _replaced)
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            try { Directory.Delete(_stage, true); } catch { }
        }

        private static void Log(string s)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n"); } catch { }
        }
    }
}
