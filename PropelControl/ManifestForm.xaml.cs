using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PropelControl
{
    public class AppJsonDraft
    {
        public string exe = "";
        public string workdir = "";
        public string process = "";
        public string args = "";
        public bool portableData;
        public Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 图形化清单/启动信息生成器（原 MakeManifest 独立工具，集成到 Propel 控制台作为弹出子窗口）：
    /// 选择应用目录 → 自动推断应用ID与版本 → 生成 manifest.json / 解析 vbs / 保存 app.json / 提取图标。
    /// WPF 版（与 MainWindow 同一技术栈）。
    /// </summary>
    public partial class ManifestForm : Window
    {
        public ManifestForm()
        {
            InitializeComponent();
            // 高分屏（150% 缩放）下可用高度不足：将窗口高度钳制到工作区内，避免底部被任务栏截断
            Loaded += (s, e) =>
            {
                double max = SystemParameters.WorkArea.Height - 24;
                if (Height > max) Height = max;
                MaxHeight = max;
            };
        }

        // ---------------- 交互 ----------------
        private void TxtScan_LostFocus(object sender, RoutedEventArgs e) => AutoFill();

        private void BtnBrowseScan_Click(object s, RoutedEventArgs e)
        {
            var p = FolderPicker.Pick("选择应用所在目录", txtScan.Text);
            if (!string.IsNullOrEmpty(p)) { txtScan.Text = p; AutoFill(); }
        }

        private void BtnGen_Click(object s, RoutedEventArgs e) => _ = GenerateAsync();

        private void BtnParseVbs_Click(object s, RoutedEventArgs e) => DoParseVbs();

        private void BtnSave_Click(object s, RoutedEventArgs e) => SaveAppJson();

        private void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtLog.AppendText(line + "\r\n");
                txtLog.ScrollToEnd();   // 自动跳转到最新一行
            }));
        }

        // ---------------- 自动填充 ----------------
        private void AutoFill()
        {
            var dir = txtScan.Text.Trim().TrimEnd('\\');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            txtVersion.Text = ReadVersion(dir);

            var appJsonPath = Path.Combine(dir, "app.json");
            if (File.Exists(appJsonPath))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(appJsonPath));
                    txtAppId.Text = (string)o["id"] ?? Path.GetFileName(dir);
                    txtName.Text = (string)o["name"] ?? "";
                    txtExe.Text = (string)o["exe"] ?? "";
                    txtWorkdir.Text = (string)o["workdir"] ?? "";
                    txtProcess.Text = (string)o["process"] ?? "";
                    txtArgs.Text = (string)o["args"] ?? "";
                    chkPortable.IsChecked = (bool?)o["portableData"] == true;
                    var env = o["env"] as JObject;
                    txtEnv.Text = env == null ? "" : string.Join("\r\n", env.Properties().Select(p => p.Name + "=" + (p.Value?.ToString() ?? "")));
                    lblVbs.Text = "（读取已有 app.json）";
                    Log("已读取 app.json：id=" + txtAppId.Text);
                    return;   // 只读加载路径：不再写回 apps.seq（避免副作用降低序号）
                }
                catch { }
            }
            txtAppId.Text = NextNumericId(Path.GetDirectoryName(dir));
            txtName.Text = Path.GetFileName(dir);
            lblVbs.Text = "（未检测）";
            DoParseVbs();
        }

        private static int ReadSeq(string parentDir)
        {
            try
            {
                var p = Path.Combine(parentDir ?? "", "apps.seq");
                if (File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out var v)) return v;
            }
            catch { }
            return 0;
        }

        private static void WriteSeq(string parentDir, int v)
        {
            try { File.WriteAllText(Path.Combine(parentDir ?? "", "apps.seq"), v.ToString()); } catch { }
        }

        private static string NextNumericId(string parentDir)
        {
            int max = ReadSeq(parentDir);
            try
            {
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    foreach (var d in Directory.GetDirectories(parentDir))
                    {
                        var n = Path.GetFileName(d);
                        if (int.TryParse(n, out var v) && v > max) max = v;
                        var aj = Path.Combine(d, "app.json");
                        if (File.Exists(aj))
                        {
                            try
                            {
                                var o = JObject.Parse(File.ReadAllText(aj));
                                var id = (string)o["id"];
                                if (int.TryParse(id, out var v2) && v2 > max) max = v2;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return (max + 1).ToString();
        }

        private static string ReadVersion(string dir)
        {
            var p = Path.Combine(dir, "version.txt");
            if (File.Exists(p))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(p))
                    {
                        var v = line.Trim();
                        if (v.Length > 0) return v;
                    }
                }
                catch { }
            }
            return "1.0.0.0";
        }

        // ---------------- app.json ----------------
        void DoParseVbs()
        {
            var scan = txtScan.Text.Trim().TrimEnd('\\');
            if (!Directory.Exists(scan)) { Log("请先选择软件目录"); return; }
            var vbsFiles = Directory.GetFiles(scan, "*.vbs");
            var d = new AppJsonDraft();
            if (vbsFiles.Length > 0)
            {
                VbsParser.Parse(vbsFiles[0], out d, out var info);
                lblVbs.Text = "已解析 " + Path.GetFileName(vbsFiles[0]) + (vbsFiles.Length > 1 ? $"（共{vbsFiles.Length}个，取首个）" : "");
                Log(info);
            }
            else
            {
                VbsParser.AutoDetectExe(scan, d);
                lblVbs.Text = "未找到 vbs，自动检测 exe";
                Log("未找到 vbs，已尝试自动检测启动程序；如不准确请手工填写后保存。");
            }
            if (string.IsNullOrEmpty(txtName.Text)) txtName.Text = Path.GetFileName(scan);
            txtExe.Text = d.exe;
            txtWorkdir.Text = d.workdir;
            txtProcess.Text = d.process;
            txtArgs.Text = d.args;
            chkPortable.IsChecked = d.portableData;
            txtEnv.Text = string.Join("\r\n", d.env.Select(kv => kv.Key + "=" + kv.Value));
        }

        void SaveAppJson()
        {
            var scan = txtScan.Text.Trim().TrimEnd('\\');
            var appid = txtAppId.Text.Trim();
            if (!Directory.Exists(scan) || string.IsNullOrEmpty(appid)) { Log("请先选择软件目录并填写应用ID"); return; }
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in txtEnv.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = line.IndexOf('=');
                if (i > 0) env[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            ExtractIcon(scan);
            var iconPath = Path.Combine(scan, "icon.ico");
            var obj = new Dictionary<string, object>
            {
                ["id"] = appid,
                ["name"] = string.IsNullOrEmpty(txtName.Text) ? appid : txtName.Text.Trim(),
                ["exe"] = txtExe.Text.Trim(),
                ["workdir"] = txtWorkdir.Text.Trim(),
                ["process"] = txtProcess.Text.Trim(),
                ["portableData"] = chkPortable.IsChecked == true,
                ["env"] = env,
                ["args"] = txtArgs.Text.Trim()
            };
            if (File.Exists(iconPath)) obj["icon"] = "icon.ico";
            var outPath = Path.Combine(scan, "app.json");
            File.WriteAllText(outPath, JsonConvert.SerializeObject(obj, Formatting.Indented));
            var parent = Path.GetDirectoryName(scan);
            if (int.TryParse(appid, out var seqV) && seqV > ReadSeq(parent)) WriteSeq(parent, seqV);
            Log("已写入 app.json：" + outPath + "（id=" + appid + "）");
        }

        // ---------------- 图标提取 ----------------
        void ExtractIcon(string scan)
        {
            try
            {
                var exeRel = txtExe.Text.Trim();
                if (string.IsNullOrEmpty(exeRel)) { Log("未填写启动程序，跳过图标提取"); return; }
                var exePath = Path.Combine(scan, exeRel.Replace('/', '\\'));
                if (!File.Exists(exePath)) { Log("未找到启动程序，跳过图标提取：" + exePath); return; }

                var outPath = Path.Combine(scan, "icon.ico");
                var gotHiRes = IconExtractor.TryExtractBest(exePath, outPath, out var how);
                if (gotHiRes) Log("已生成应用图标（" + how + "）：" + outPath);
                else
                {
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico == null) { Log("未能提取图标：" + exePath); return; }
                        using (var fs = File.Create(outPath)) ico.Save(fs);
                        Log("已生成应用图标（32px 兜底）：" + outPath);
                    }
                }

                var appid = txtAppId.Text.Trim();
                if (!string.IsNullOrEmpty(appid))
                {
                    var iconsRoot = ResolveIconsDir(scan);
                    try
                    {
                        Directory.CreateDirectory(iconsRoot);
                        var central = Path.Combine(iconsRoot, appid + ".ico");
                        File.Copy(outPath, central, true);
                        Log("已写入中央图标：" + central);
                    }
                    catch (Exception ex) { Log("写入中央图标失败：" + ex.Message); }
                }
                else Log("未填写应用ID，跳过写入中央图标目录");
            }
            catch (Exception ex) { Log("图标提取失败：" + ex.Message); }
        }

        /// <summary>中央图标目录固定为应用目录同级下的 icons（原「图标中心目录」模块已删除，取默认 ..\icons）。</summary>
        private static string ResolveIconsDir(string scan)
        {
            var parent = Path.GetDirectoryName(scan.TrimEnd('\\'));
            return string.IsNullOrEmpty(parent) ? Path.Combine(scan, "icons") : Path.Combine(parent, "icons");
        }

        // ---------------- manifest.json ----------------
        private async Task GenerateAsync()
        {
            btnGen.IsEnabled = false;
            try
            {
                var scan = txtScan.Text.Trim().TrimEnd('\\');
                if (!Directory.Exists(scan)) { Log("目标目录不存在"); return; }
                var appid = txtAppId.Text.Trim();
                var version = txtVersion.Text.Trim();
                if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(version))
                { Log("请先浏览/填写 软件目录与应用ID（版本由 version.txt 自动读取）"); return; }

                Log("开始扫描：" + scan);
                Log("提示：生成前请先完全退出被打包的应用（尤其 Chrome 等使用 LevelDB 的程序），否则设置数据可能抓到不一致快照。");
                ExtractIcon(scan);
                var files = new List<Dictionary<string, object>>();
                long total = 0;
                int seedCount = 0;
                await Task.Run(() =>
                {
                    foreach (var f in SafeWalk(scan))
                    {
                        var rel = f.Substring(scan.Length).TrimStart('\\').Replace('\\', '/');
                        if (IsIgnoredForManifest(rel)) continue; // 排除运行时垃圾与客户端自维护文件
                        var fi = new FileInfo(f);
                        var sha = Sha256File(f);
                        var entry = new Dictionary<string, object>
                        {
                            ["path"] = rel,
                            ["size"] = fi.Length.ToString(),
                            ["sha256"] = sha
                        };
                        // 用户设置/状态：首次安装必须下发，但应用运行会改写 → 标 seed，
                        // 客户端仅在本地缺失时下载、不比哈希、不覆盖（保留用户后续设置）。
                        if (IsSeedFile(rel)) { entry["seed"] = true; seedCount++; }
                        lock (files) { files.Add(entry); total += fi.Length; }
                        Log($"  {rel}  ({fi.Length} bytes)");
                    }
                });

                var manifest = new Dictionary<string, object>
                {
                    ["app"] = appid,
                    ["version"] = version,
                    ["files"] = files
                };
                var outPath = Path.Combine(scan, "manifest.json");
                File.WriteAllText(outPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                Log($"完成：appid={appid}，{files.Count} 个文件（其中 seed 种子数据 {seedCount} 个），共 {total} bytes");
                Log("已写入：" + outPath);
            }
            catch (Exception ex) { Log("错误：" + ex.Message); }
            finally { btnGen.IsEnabled = true; }
        }

        // 生成端是分发策略的唯一决策点：客户端只按清单执行，不再二次按路径过滤。
        // 这里仅排除「确定无分发价值的运行时垃圾」与「客户端自维护文件」。
        // 注意：LevelDB 的纯数字 .log(WAL) / .ldb(SST) / CURRENT / MANIFEST-* 是真实数据
        // （扩展设置就存在这里），必须放行并标 seed，见 IsSeedFile。
        private static bool IsIgnoredForManifest(string rel)
        {
            var p = rel.Replace('\\', '/');
            var lower = p.ToLowerInvariant();
            var name = Path.GetFileName(p);

            // 仅应用根目录的两个自维护文件；子目录同名文件（如扩展自带 manifest.json）是程序数据，必须分发
            if (p.IndexOf('/') < 0 &&
                (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("app.json", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                return true;

            // LevelDB 诊断日志 / 锁文件、SQLite 回滚日志、临时与备份文件：无数据价值且运行时改写
            if (name.Equals("LOG", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LOG.old", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LOCK", StringComparison.OrdinalIgnoreCase)
                || lower.EndsWith("-journal") || lower.EndsWith("-wal") || lower.EndsWith("-shm")
                || lower.EndsWith(".tmp") || lower.EndsWith(".bak") || lower.EndsWith(".old"))
                return true;

            // 仅排除真正的文本日志；纯数字命名的 .log 是 LevelDB WAL（数据），放行
            if (lower.EndsWith(".log") && !HasNumericStem(name)) return true;

            // 缓存 / 临时 / 会话等运行时目录（按路径段，大小写不敏感）
            foreach (var seg in p.Split('/'))
            {
                var s = seg.ToLowerInvariant();
                if (s == "logs" || s == "log" || s == "cache" || s == "caches" || s == "temp" || s == "tmp"
                    || s == "app.staging" || s == "__pycache__" || s == ".git" || s == "node_modules"
                    || s == "code cache" || s == "gpucache" || s == "shadercache" || s == "grshadercache"
                    || s == "dawnwebgpucache" || s == "dawngraphitecache" || s == "scriptcache"
                    || s == "manifest resources" || s == "crashpad" || s == "browsermetrics"
                    || s == "sessions" || s == "sessions_encrypted" || s == "session storage"
                    || s == "component_crx_cache" || s == "extensions_crx_cache")
                    return true;
            }
            return false;
        }

        private static bool HasNumericStem(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            return stem.Length > 0 && stem.All(char.IsDigit);
        }

        // 用户设置/状态所在目录：整目录视为种子数据（会被应用运行时改写）
        private static readonly HashSet<string> SeedDirSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Local Extension Settings", "Sync Extension Settings", "Managed Extension Settings",
            "Extension State", "Extension Rules", "Extension Scripts",
            "IndexedDB", "Local Storage", "Sync Data", "shared_proto_db", "WebStorage",
            "Service Worker", "Network", "Site Characteristics Database", "Segmentation Platform"
        };
        // 顶层用户配置文件（无扩展名，运行时会被改写）
        private static readonly HashSet<string> SeedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Preferences", "Secure Preferences", "Local State", "Web Data", "Bookmarks",
            "Login Data", "History", "Network Persistent State", "TransportSecurity", "Trust Tokens"
        };

        /// <summary>种子数据：应用运行会改写，但首次安装必须从发布源带来的用户设置/状态。
        /// 客户端仅在本地缺失时下载、不参与哈希比对、不覆盖已有文件（保留用户后续设置）。
        /// 命中范围：LevelDB 数据文件与元数据、设置目录、顶层用户配置文件。</summary>
        private static bool IsSeedFile(string rel)
        {
            var p = rel.Replace('\\', '/');
            var name = Path.GetFileName(p);
            var lower = p.ToLowerInvariant();
            // ① LevelDB 的 WAL / SST：纯数字命名的 .log / .ldb，无论位于哪个目录都是数据文件
            if ((lower.EndsWith(".log") || lower.EndsWith(".ldb")) && HasNumericStem(name)) return true;
            // ② LevelDB 一致性元数据（必须与 WAL 一同下发，否则库打不开）
            if (name.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase)) return true;
            // ③ 设置 / 状态目录
            foreach (var seg in p.Split('/'))
                if (SeedDirSegments.Contains(seg)) return true;
            // ④ 顶层用户配置文件
            return SeedFileNames.Contains(name);
        }

        private static IEnumerable<string> SafeWalk(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] subs, fs;
                try { subs = Directory.GetDirectories(dir); fs = Directory.GetFiles(dir); }
                catch { continue; }
                foreach (var s in subs) stack.Push(s);
                foreach (var f in fs) yield return f;
            }
        }

        private static string Sha256File(string p)
        {
            using (var fs = File.OpenRead(p))
            using (var sha = SHA256.Create())
                return BytesToHex(sha.ComputeHash(fs));
        }

        private static string BytesToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 高保真图标提取：直接解析 exe 的 RT_GROUP_ICON/RT_ICON 资源，把全部尺寸帧（含 256×256 PNG 帧）
    /// 原样写出到标准 .ico。避免 ExtractAssociatedIcon 只返回 32px 导致的模糊。
    /// </summary>
    internal static class IconExtractor
    {
        const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;
        const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
        static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;
        static readonly IntPtr RT_ICON = (IntPtr)3;

        delegate bool EnumResProc(IntPtr hModule, IntPtr type, IntPtr name, IntPtr param);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LockResource(IntPtr hResData);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType, EnumResProc cb, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHDefExtractIcon(string iconFile, int iconIndex, uint flags, out IntPtr hiconLarge, out IntPtr hiconSmall, uint nIconSize);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        public static bool TryExtractBest(string exePath, string outIco, out string how)
        {
            try { ExtractFromResources(exePath, outIco); how = "PE资源·全尺寸帧"; return true; }
            catch (Exception ex) { how = ex.Message; }
            try { ExtractViaShell(exePath, outIco); how = "Shell·256px"; return true; }
            catch { }
            return false;
        }

        static void ExtractFromResources(string exePath, string outIco)
        {
            var h = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (h == IntPtr.Zero) throw new Exception("无法以数据方式加载 exe");
            try
            {
                IntPtr grpName = IntPtr.Zero;
                EnumResourceNames(h, RT_GROUP_ICON, (m, t, n, p) => { grpName = n; return false; }, IntPtr.Zero);
                if (grpName == IntPtr.Zero) throw new Exception("未找到图标组资源");
                var info = FindResource(h, grpName, RT_GROUP_ICON);
                if (info == IntPtr.Zero) throw new Exception("图标组资源定位失败");
                var data = LoadResource(h, info);
                var ptr = LockResource(data);
                var size = (int)SizeofResource(h, info);
                if (ptr == IntPtr.Zero || size < 6) throw new Exception("图标组数据无效");
                var grp = new byte[size];
                Marshal.Copy(ptr, grp, 0, size);

                int count = BitConverter.ToUInt16(grp, 4);
                if (count <= 0) throw new Exception("图标组为空");
                var metas = new List<byte[]>();
                var images = new List<byte[]>();
                for (int i = 0; i < count; i++)
                {
                    int o = 6 + i * 14;
                    if (o + 14 > size) break;
                    ushort nID = BitConverter.ToUInt16(grp, o + 12);
                    var resInfo = FindResource(h, (IntPtr)nID, RT_ICON);
                    if (resInfo == IntPtr.Zero) continue;
                    var resData = LoadResource(h, resInfo);
                    var resPtr = LockResource(resData);
                    var resSize = (int)SizeofResource(h, resInfo);
                    if (resPtr == IntPtr.Zero || resSize <= 0) continue;
                    var img = new byte[resSize];
                    Marshal.Copy(resPtr, img, 0, resSize);
                    images.Add(img);
                    metas.Add(new[]
                    {
                        grp[o], grp[o + 1], grp[o + 2], grp[o + 3],
                        grp[o + 4], grp[o + 5], grp[o + 6], grp[o + 7],
                        (byte)(resSize & 0xFF), (byte)((resSize >> 8) & 0xFF),
                        (byte)((resSize >> 16) & 0xFF), (byte)((resSize >> 24) & 0xFF)
                    });
                }
                if (images.Count == 0) throw new Exception("未提取到任何图标帧");
                WriteIco(images, metas, outIco);
            }
            finally { FreeLibrary(h); }
        }

        static void ExtractViaShell(string exePath, string outIco)
        {
            if (SHDefExtractIcon(exePath, 0, 0, out var hicon, out var hiconSmall, (uint)((256 << 16) | 48)) != 0 || hicon == IntPtr.Zero)
                throw new Exception("Shell 大图标提取失败");
            try
            {
                using (var icon = System.Drawing.Icon.FromHandle(hicon))
                using (var bmp = icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    var png = ms.ToArray();
                    var metas = new List<byte[]> { new byte[]
                    {
                        0, 0, 0, 0, 1, 0, 32, 0,
                        (byte)(png.Length & 0xFF), (byte)((png.Length >> 8) & 0xFF),
                        (byte)((png.Length >> 16) & 0xFF), (byte)((png.Length >> 24) & 0xFF)
                    } };
                    WriteIco(new List<byte[]> { png }, metas, outIco);
                }
            }
            finally { DestroyIcon(hicon); if (hiconSmall != IntPtr.Zero) DestroyIcon(hiconSmall); }
        }

        static void WriteIco(List<byte[]> images, List<byte[]> metas, string outIco)
        {
            using (var fs = File.Create(outIco))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    bw.Write(metas[i]);
                    bw.Write((uint)offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) bw.Write(img);
            }
        }
    }

    /// <summary>解析启动.vbs：提取 app/dir 赋值、环境变量、Run 启动项，输出相对路径的启动信息。</summary>
    public static class VbsParser
    {
        private static readonly Regex EnvRe = new Regex(@"env\(\s*""([^""]+)""\s*\)\s*=\s*(.+)");
        private static readonly Regex VarRe = new Regex(@"^\s*(\w+)\s*=\s*(.+)");
        private static readonly Regex CurDirRe = new Regex(@"CurrentDirectory\s*=\s*(.+)");

        public static void Parse(string vbsPath, out AppJsonDraft d, out string info)
        {
            d = new AppJsonDraft();
            info = "";
            try
            {
                var rootDir = Path.GetDirectoryName(vbsPath);
                var lines = File.ReadAllLines(vbsPath);
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    var em = EnvRe.Match(line);
                    if (em.Success) { d.env[em.Groups[1].Value] = Eval(em.Groups[2].Value, rootDir, vars); continue; }
                    var cm = CurDirRe.Match(line);
                    if (cm.Success) { d.workdir = Eval(cm.Groups[1].Value, rootDir, vars).TrimEnd('\\'); continue; }
                    var vm = VarRe.Match(line);
                    if (vm.Success)
                    {
                        var name = vm.Groups[1].Value;
                        if (name.Equals("app", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("dir", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("dataDir", StringComparison.OrdinalIgnoreCase))
                            vars[name] = Eval(vm.Groups[2].Value, rootDir, vars);
                    }
                }

                if (vars.TryGetValue("app", out var exe)) { d.exe = exe; d.process = Path.GetFileName(exe); }
                if (string.IsNullOrEmpty(d.workdir) && vars.TryGetValue("dir", out var wd)) d.workdir = wd.TrimEnd('\\');
                if (string.IsNullOrEmpty(d.workdir) && !string.IsNullOrEmpty(d.exe)) d.workdir = Path.GetDirectoryName(d.exe).Replace('/', '\\');

                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.IndexOf(".Run", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf(".Exec", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var comma = line.IndexOf(',');
                    var cmdExpr = comma >= 0 ? line.Substring(0, comma) : line;
                    var argParts = new List<string>();
                    foreach (var tok in cmdExpr.Split('&'))
                    {
                        var t = tok.Trim();
                        if (t == "\"\"") continue;
                        if (t.Equals("app", StringComparison.OrdinalIgnoreCase)) continue;
                        if (t.StartsWith("\""))
                        {
                            var s = t.Trim('"');
                            if (!s.ToLowerInvariant().EndsWith(".exe")) argParts.Add(s);
                        }
                    }
                    d.args = string.Join(" ", argParts).Trim();
                    break;
                }

                d.portableData = d.env.ContainsKey("APPDATA") || d.env.ContainsKey("LOCALAPPDATA");
                info = $"已解析 {Path.GetFileName(vbsPath)}：exe={d.exe}，工作目录={d.workdir}，环境变量 {d.env.Count} 项" + (d.portableData ? "（便携）" : "");
            }
            catch (Exception ex) { info = "解析失败：" + ex.Message; }
        }

        public static void AutoDetectExe(string scan, AppJsonDraft d)
        {
            var appDir = Path.Combine(scan, "App");
            var root = Directory.Exists(appDir) ? appDir : scan;
            string first = null;
            try { first = SafeWalk(root).FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); } catch { }
            if (first != null)
            {
                var rel = first.Substring(scan.Length).TrimStart('\\').Replace('/', '\\');
                d.exe = rel;
                d.process = Path.GetFileName(rel);
                d.workdir = Path.GetDirectoryName(rel).Replace('/', '\\');
            }
        }

        static string Eval(string expr, string rootDir, Dictionary<string, string> vars)
        {
            var parts = new List<string>();
            foreach (var tok in expr.Split('&'))
            {
                var t = tok.Trim();
                if (t.Length == 0) continue;
                if (t.Equals("root", StringComparison.OrdinalIgnoreCase)) parts.Add(rootDir);
                else if (t.Equals("dataDir", StringComparison.OrdinalIgnoreCase)) parts.Add(rootDir.TrimEnd('\\') + "\\Data");
                else if (vars != null && vars.TryGetValue(t, out var v)) parts.Add(v);
                else if (t.StartsWith("\"")) parts.Add(t.Trim('"').TrimStart('\\'));
            }
            var abs = string.Join("\\", parts).Replace("/", "\\");
            var r = rootDir.TrimEnd('\\');
            if (abs.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return abs.Substring(r.Length).TrimStart('\\');
            return abs;
        }

        static IEnumerable<string> SafeWalk(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] subs, fs;
                try { subs = Directory.GetDirectories(dir); fs = Directory.GetFiles(dir); }
                catch { continue; }
                foreach (var s in subs) stack.Push(s);
                foreach (var f in fs) yield return f;
            }
        }
    }
}
