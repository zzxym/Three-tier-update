using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.ServiceProcess;

namespace RepelUpdater
{
    /// <summary>
    /// Repel 服务端独立自更新器：由 RepelService 在退出前拉起，负责「停服 → 备份 → 替换 → 启动服务 → 回滚」。
    /// 零外部依赖（不引用 Newtonsoft.Json）。RepelUpdater.exe 自身与 repel.json 不在替换清单内（跳过），避免写锁与覆盖用户配置。
    /// </summary>
    internal static class Program
    {
        private class Entry { public string Rel; public string Hash; }

        private static string _dir;
        private static string _stage;
        private static string _service;
        private static string _logPath;
        private static readonly List<string> _replaced = new List<string>();

        private static int Main(string[] args)
        {
            try
            {
                var opt = ParseArgs(args);
                if (!opt.TryGetValue("dir", out _dir) || !opt.TryGetValue("stage", out _stage))
                    return 1;
                opt.TryGetValue("service", out _service);
                _logPath = Path.Combine(_dir, "repelupdate.log");
                Log("=== RepelUpdater 启动 dir=" + _dir + " stage=" + _stage + " service=" + (_service ?? "") + " ===");

                StopService();
                var entries = ReadManifest();
                Replace(entries);
                StartService();

                Commit();
                Log("Repel 自更新成功");
                return 0;
            }
            catch (Exception ex)
            {
                Log("Repel 自更新失败：" + ex.Message);
                Rollback();
                try { StartService(); } catch { }
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

        private static void StopService()
        {
            if (string.IsNullOrEmpty(_service)) return;
            try
            {
                using var sc = new ServiceController(_service);
                if (sc.Status == ServiceControllerStatus.Stopped) { Log("服务已停止"); return; }
                Log("停止服务 " + _service + "…");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                Log("服务已停止");
            }
            catch (Exception ex) { Log("停止服务异常（将尝试继续替换）：" + ex.Message); }
        }

        private static void StartService()
        {
            if (string.IsNullOrEmpty(_service)) return;
            try
            {
                using var sc = new ServiceController(_service);
                if (sc.Status == ServiceControllerStatus.Running) return;
                Log("启动服务 " + _service + "…");
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Log("服务已启动");
            }
            catch (Exception ex) { Log("启动服务异常：" + ex.Message); }
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

        private static void Replace(List<Entry> entries)
        {
            foreach (var e in entries)
            {
                if (Path.GetFileName(e.Rel).Equals("RepelUpdater.exe", StringComparison.OrdinalIgnoreCase))
                    continue;   // 不替换自身（运行中的 exe 不可写）
                if (e.Rel.Equals("repel.json", StringComparison.OrdinalIgnoreCase))
                    continue;   // 不覆盖用户配置
                var dest = SafePath(_dir, e.Rel);
                var src = SafePath(_stage, e.Rel);
                if (!File.Exists(src)) throw new Exception("缺少升级文件：" + e.Rel);
                var bak = dest + ".bak";
                if (File.Exists(bak)) { try { File.Delete(bak); } catch { } }
                // 运行中的 exe 可同卷重命名但不可删：先用 Move 腾出目标名
                if (File.Exists(dest)) File.Move(dest, bak);
                _replaced.Add(dest);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Move(src, dest);
                }
                catch (Exception ex) { throw new Exception("写入 " + e.Rel + " 失败：" + ex.Message); }
            }
        }

        private static void Rollback()
        {
            for (int i = _replaced.Count - 1; i >= 0; i--)
            {
                var dest = _replaced[i];
                var bak = dest + ".bak";
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    if (File.Exists(bak)) File.Move(bak, dest);
                }
                catch (Exception ex) { Log("回滚 " + dest + " 失败：" + ex.Message); }
            }
        }

        private static void Commit()
        {
            foreach (var dest in _replaced)
                try { var bak = dest + ".bak"; if (File.Exists(bak)) File.Delete(bak); } catch { }
            try { Directory.Delete(_stage, true); } catch { }
        }

        private static void Log(string s)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n"); } catch { }
        }
    }
}
