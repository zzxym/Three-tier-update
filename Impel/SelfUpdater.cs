using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace Impel
{
    /// <summary>Repel 长轮询 /changes 的返回结果（含可选的自更新指令）。</summary>
    public class ChangeResult
    {
        public bool ok;
        public bool changed;
        public long version;
        public string cmd = "";
        public string cmdId = "";
        public string pkgVersion = "";
    }

    public static class ClientInfo
    {
        /// <summary>客户端版本号：与 RepelControl 导入升级包时读取新 exe 的方式保持一致（ProductVersion）。</summary>
        public static string Version
        {
            get
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        var s = v.Trim();
                        // SDK 会把 SourceLink 提交号附加到 ProductVersion（26.1.0+<sha>）；
                        // 版本比对必须剥掉元数据，否则同一版本的不同构建会被反复判定为需要升级
                        int plus = s.IndexOf('+');
                        return plus > 0 ? s.Substring(0, plus) : s;
                    }
                }
                catch { }
                try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); } catch { return "0"; }
            }
        }
    }

    /// <summary>
    /// 客户端自更新（下载侧 + 拉起 updater）。与第三方应用更新完全解耦：
    /// 走 Repel 的独立端点 /impel/version、/impel/file，不进 all.lst / repel.lst，也不写 SoftwareRoot。
    /// Impel.json、impel.log、hashcache.json 等用户数据不在升级包内，不会被覆盖。
    /// </summary>
    public static class SelfUpdater
    {
        public const string StagingDirName = "SelfUpdate.staging";
        public const string UpdaterExe = "ImpelUpdater.exe";

        private static string Url(ImpelConfig cfg, string path)
        {
            var u = UpdateEngine.RepelBase(cfg) + path;
            if (!string.IsNullOrEmpty(cfg.server.token))
                u += (path.Contains("?") ? "&" : "?") + "token=" + Uri.EscapeDataString(cfg.server.token);
            return u;
        }

        private static string GetText(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET"; req.Timeout = 15000; req.Proxy = null;
            req.ServicePoint.Expect100Continue = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static bool Download(string url, string dest, string expectHash, Action<string> log)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET"; req.Timeout = 120000; req.Proxy = null;
                req.ServicePoint.Expect100Continue = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rs = resp.GetResponseStream())
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[65536]; int n;
                    while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, buf, 0);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                    return actual.Equals(expectHash, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { log("下载异常：" + ex.Message); return false; }
        }

        /// <summary>
        /// 下载并校验整个客户端升级包，随后拉起独立 updater 并关闭自身。
        /// 由长轮询线程调用；失败只记日志，不影响菜单运行，管理员可用新 cmdId 重新下发。
        /// </summary>
        /// <summary>
        /// 从服务端拉取客户端发布包到指定目录（destDir），逐文件校验 sha256；成功返回 true。
        /// 与 TryUpgrade 不同：不写 staging、不拉起 ImpelUpdater，而是直接落到 destDir（用于迁移到新位置时先拉取发布版）。
        /// </summary>
        public static bool TryFetchTo(ImpelConfig cfg, string destDir, Action<string> log)
        {
            try
            {
                var obj = JObject.Parse(GetText(Url(cfg, "/impel/version")));
                var version = (string)obj["version"] ?? "";
                var files = obj["files"] as JArray;
                if (string.IsNullOrEmpty(version) || files == null || files.Count == 0)
                { log("未找到客户端发布包（Repel 未导入 impel 升级包）"); return false; }
                foreach (JToken f in files)
                {
                    var rel = (string)f["path"] ?? "";
                    var hash = (string)f["sha256"] ?? "";
                    if (rel.Length == 0 || hash.Length != 64) continue;
                    if (rel.IndexOf("..", StringComparison.Ordinal) >= 0 || Path.IsPathRooted(rel)) continue;
                    if (rel.Equals(UpdaterExe, StringComparison.OrdinalIgnoreCase)) continue; // 更新器随安装包升级，发布包不含
                    var dest = Path.Combine(destDir, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    var url = Url(cfg, "/impel/file?path=" + Uri.EscapeDataString(rel));
                    if (!Download(url, dest, hash, log))
                    { log("拉取发布版失败：下载或校验不通过 " + rel); return false; }
                }
                return true;
            }
            catch (Exception ex) { log("拉取发布版异常：" + ex.Message); return false; }
        }

        public static void TryUpgrade(ImpelConfig cfg, string wantedVersion, Action<string> log)
        {
            var stage = Path.Combine(ConfigService.ExeDir, StagingDirName);
            try
            {
                var updater = Path.Combine(ConfigService.ExeDir, UpdaterExe);
                if (!File.Exists(updater)) { log("自更新中止：缺少 " + UpdaterExe + "（需安装较新的客户端安装包）"); return; }

                var obj = JObject.Parse(GetText(Url(cfg, "/impel/version")));
                var version = (string)obj["version"] ?? "";
                var files = obj["files"] as JArray;
                if (string.IsNullOrEmpty(version) || files == null || files.Count == 0)
                { log("自更新中止：Repel 未导入客户端升级包"); return; }
                if (!string.IsNullOrEmpty(wantedVersion) && !version.Equals(wantedVersion, StringComparison.OrdinalIgnoreCase))
                { log("自更新中止：升级包版本与指令不一致（" + version + " / " + wantedVersion + "）"); return; }
                if (version.Equals(ClientInfo.Version, StringComparison.OrdinalIgnoreCase))
                { log("自更新跳过：当前已是 " + version); return; }

                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);

                var lines = new List<string>();
                foreach (JToken f in files)
                {
                    var rel = (string)f["path"] ?? "";
                    var hash = (string)f["sha256"] ?? "";
                    if (rel.Length == 0 || hash.Length != 64) continue;
                    if (rel.IndexOf("..", StringComparison.Ordinal) >= 0 || Path.IsPathRooted(rel)) continue;
                    // updater 自身不能被替换（它正在运行）；updater 只随客户端安装包升级
                    if (rel.Equals(UpdaterExe, StringComparison.OrdinalIgnoreCase)) continue;

                    var dest = Path.Combine(stage, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    var url = Url(cfg, "/impel/file?path=" + Uri.EscapeDataString(rel));
                    if (!Download(url, dest, hash, log))
                    {
                        log("自更新中止：下载或校验失败 " + rel);
                        try { Directory.Delete(stage, true); } catch { }
                        return;
                    }
                    lines.Add(rel + "\t" + hash);
                }
                if (!lines.Exists(x => x.StartsWith("impel.exe\t", StringComparison.OrdinalIgnoreCase)))
                { log("自更新中止：升级包未包含 impel.exe"); try { Directory.Delete(stage, true); } catch { } return; }

                File.WriteAllLines(Path.Combine(stage, "files.lst"), lines, Encoding.UTF8);
                log("客户端升级包 " + version + " 校验完成，准备重启");

                var psi = new ProcessStartInfo(updater)
                {
                    Arguments = "--dir \"" + ConfigService.ExeDir.TrimEnd('\\', '/') + "\" --stage \"" + stage + "\" --pid " + Process.GetCurrentProcess().Id,
                    WorkingDirectory = ConfigService.ExeDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);

                var app = Application.Current;
                if (app != null) app.Dispatcher.Invoke(() => app.Shutdown());
                else Environment.Exit(0);
            }
            catch (Exception ex)
            {
                log("自更新异常：" + ex.Message);
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            }
        }
    }
}
