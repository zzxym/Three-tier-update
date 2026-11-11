using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;

namespace RepelUpdater
{
    /// <summary>
    /// Repel 服务端独立自更新器：由 RepelService 在退出前拉起，负责「停服 → 关闭 GUI → 备份 → 替换 → 启动服务 → 重启 GUI → 回滚」。
    /// 零外部依赖（不引用 Newtonsoft.Json）。repel.json 不在替换清单内（跳过）以免覆盖用户配置；其余文件（含 RepelUpdater 自身、RepelControl、repelView）一律切换到新版本——升级前先关闭运行中的 GUI 进程，替换完成后再拉起到当前用户会话。
    /// </summary>
    internal static class Program
    {
        private class Entry { public string Rel; public string Hash; }

        private static string _dir;
        private static string _stage;
        private static string _service;
        private static string _logPath;
        private static readonly List<(string Dest, string Bak)> _replaced = new List<(string, string)>();
        // 升级前在运行的 GUI 进程 exe 路径（仅这些会在升级后重启，避免"未打开的被拉起"）
        private static readonly List<string> _guiToRestart = new List<string>();

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
                CloseGuiProcesses(entries);
                Replace(entries);
                StartService();
                RestartGuiProcesses();

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

        /// <summary>升级前关闭会被替换的 GUI 进程（RepelControl / repelView 等），仅记录"更新前确实在运行"的 exe 以便稍后重启。</summary>
        private static void CloseGuiProcesses(List<Entry> entries)
        {
            foreach (var e in entries)
            {
                if (!e.Rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(e.Rel);
                if (name.Equals("RepelUpdater", StringComparison.OrdinalIgnoreCase)) continue; // 自身不在此关闭
                if (name.Equals("RepelService", StringComparison.OrdinalIgnoreCase)) continue;  // 服务由 StopService 处理
                bool running = false;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    running = true;
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(5000); }
                    }
                    catch { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                }
                if (running) _guiToRestart.Add(SafePath(_dir, e.Rel));
            }
        }

        /// <summary>升级后把此前在运行的 GUI 拉起（以新版本运行）；优先启动到当前交互用户会话，失败回退本会话。</summary>
        private static void RestartGuiProcesses()
        {
            foreach (var exe in _guiToRestart)
            {
                try
                {
                    if (!File.Exists(exe)) continue;
                    if (!StartInUserSession(exe))
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) });
                }
                catch (Exception ex) { Log("重启 " + exe + " 失败：" + ex.Message); }
            }
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        private const int SECURITY_IMPERSONATION = 2;
        private const int TOKEN_PRIMARY = 1;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public string lpDesktop;
            public IntPtr lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        /// <summary>用当前交互用户令牌在用户桌面会话启动进程，避免服务会话 session 0 隔离导致 GUI 不可见。</summary>
        private static bool StartInUserSession(string exe)
        {
            try
            {
                uint sessionId = WTSGetActiveConsoleSessionId();
                if (sessionId == 0xFFFFFFFF) return false;
                if (!WTSQueryUserToken(sessionId, out IntPtr userToken)) return false;
                try
                {
                    uint desired = TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
                    if (!DuplicateTokenEx(userToken, desired, IntPtr.Zero, SECURITY_IMPERSONATION, TOKEN_PRIMARY, out IntPtr primaryToken))
                        return false;
                    try
                    {
                        if (!CreateEnvironmentBlock(out IntPtr env, primaryToken, false))
                            env = IntPtr.Zero;
                        try
                        {
                            var si = new STARTUPINFO();
                            si.cb = Marshal.SizeOf(si);
                            si.lpDesktop = "winsta0\\default";
                            var pi = new PROCESS_INFORMATION();
                            bool ok = CreateProcessAsUser(primaryToken, exe, null, IntPtr.Zero, IntPtr.Zero, false,
                                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, env, Path.GetDirectoryName(exe), ref si, out pi);
                            if (ok)
                            {
                                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                                return true;
                            }
                            return false;
                        }
                        finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
                    }
                    finally { CloseHandle(primaryToken); }
                }
                finally { CloseHandle(userToken); }
            }
            catch { return false; }
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
                if (e.Rel.Equals("repel.json", StringComparison.OrdinalIgnoreCase))
                    continue;   // 不覆盖用户配置
                var dest = SafePath(_dir, e.Rel);
                var src = SafePath(_stage, e.Rel);
                if (!File.Exists(src)) throw new Exception("缺少升级文件：" + e.Rel);
                // 上次升级残留的 .bak（可能被仍在运行的进程锁定）不得阻塞本次升级：删除失败则换唯一名
                var bak = dest + ".bak";
                if (File.Exists(bak))
                {
                    try { File.Delete(bak); }
                    catch { bak = dest + ".bak_" + Guid.NewGuid().ToString("N").Substring(0, 8); }
                }
                // 运行中的 exe 可同卷重命名但不可删：先用 Move 腾出目标名
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
