using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ManifestGen
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
    /// 图形化清单/启动信息生成工具：
    /// 1. 选择应用目录（&lt;Propel根&gt;/&lt;应用文件夹&gt;），自动推断应用ID与版本（version.txt，缺省 1.0.0.0）
    /// 2. 新目录自动分配自增数字 appid（扫描同级 app.json/目录名取最大值+1）；已含 app.json 的目录沿用其 id
    /// 3. 生成 manifest.json（写入所选目录）；&gt;32MB 文件额外生成 2MB 块哈希
    /// 4. 若目录下存在 *.vbs，自动解析并填充启动信息；无 vbs 则自动检测 exe 供手工补全；保存为 app.json
    /// appid 可为数字（与物理目录名解耦，由 Propel 通过 app.json 建立映射）。
    /// </summary>
    public class MainForm : Form
    {
        private TextBox txtScan, txtAppId, txtVersion, txtIconsDir;
        private TextBox txtName, txtExe, txtWorkdir, txtProcess, txtArgs, txtEnv;
        private Label lblVbs;
        private CheckBox chkPortable;
        private TextBox txtLog;
        private Button _btnGen;   // 生成按钮引用：禁用期间防止重复触发生成

        public MainForm()
        {
            Text = "索引生成";
            Width = 700; Height = 560;
            AutoScroll = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { }
            InitControls();
        }

        private void InitControls()
        {
            int y = 12;
            AddLabel("应用目录：", 16, y);
            txtScan = AddTextBox("", 90, y - 2, 420);
            txtScan.Leave += (s, e) => AutoFill();
            AddButton("浏览...", 520, y - 4, 140, () =>
            {
                var d = new FolderBrowserDialog { Description = "选择应用所在目录" };
                if (d.ShowDialog() == DialogResult.OK) { txtScan.Text = d.SelectedPath; AutoFill(); }
            });
            y += 30;

            AddLabel("应用ID：", 16, y);
            txtAppId = AddTextBox("", 90, y - 2, 150);
            AddLabel("版本：", 260, y);
            txtVersion = AddTextBox("1.0.0.0", 330, y - 2, 130);
            AddLabel("（新目录自动分配自增数字ID）", 480, y);
            y += 30;

            AddLabel("图标中心目录：", 16, y);
            txtIconsDir = AddTextBox("..\\icons", 110, y - 2, 380);
            txtIconsDir.Leave += (s, e) => { if (string.IsNullOrWhiteSpace(txtIconsDir.Text)) txtIconsDir.Text = "..\\icons"; };
            AddButton("浏览...", 500, y - 4, 160, () =>
            {
                var d = new FolderBrowserDialog { Description = "选择图标中心目录（Propel 根下的 icons）" };
                if (d.ShowDialog() == DialogResult.OK) txtIconsDir.Text = d.SelectedPath;
            });
            y += 30;

            _btnGen = AddButton("生成索引", 16, y, 150, () => { _ = GenerateAsync(); });
            AddButton("从 vbs 解析", 176, y, 110, () => DoParseVbs());
            AddButton("保存 app.json", 296, y, 130, () => SaveAppJson());
            y += 36;

            // ---- 启动信息 app.json ----
            var gbApp = new GroupBox { Left = 16, Top = y, Width = 650, Height = 214, Text = "启动信息 (app.json)" };
            int ay = 16;
            gbApp.Controls.Add(LblIn("vbs：", 10, ay)); lblVbs = LblIn("（未检测）", 46, ay); gbApp.Controls.Add(lblVbs);
            ay += 22;
            gbApp.Controls.Add(LblIn("应用名称：", 10, ay)); txtName = TxtIn("", 84, ay - 2, 160); gbApp.Controls.Add(txtName);
            gbApp.Controls.Add(LblIn("启动程序：", 256, ay)); txtExe = TxtIn("", 330, ay - 2, 300); gbApp.Controls.Add(txtExe);
            ay += 26;
            gbApp.Controls.Add(LblIn("工作目录：", 10, ay)); txtWorkdir = TxtIn("", 84, ay - 2, 160); gbApp.Controls.Add(txtWorkdir);
            gbApp.Controls.Add(LblIn("进程名：", 256, ay)); txtProcess = TxtIn("", 330, ay - 2, 300); gbApp.Controls.Add(txtProcess);
            ay += 26;
            gbApp.Controls.Add(LblIn("启动参数：", 10, ay)); txtArgs = TxtIn("", 84, ay - 2, 546); gbApp.Controls.Add(txtArgs);
            ay += 26;
            chkPortable = new CheckBox { Left = 10, Top = ay, Text = "便携数据(重定向 APPDATA/LOCALAPPDATA)", AutoSize = true }; gbApp.Controls.Add(chkPortable);
            ay += 24;
            gbApp.Controls.Add(LblIn("环境变量(每行 KEY=VALUE)：", 10, ay));
            ay += 18;
            txtEnv = new TextBox { Left = 10, Top = ay, Width = 620, Height = 40, Multiline = true, ScrollBars = ScrollBars.Vertical }; gbApp.Controls.Add(txtEnv);
            Controls.Add(gbApp);
            y += 222;

            txtLog = new TextBox
            {
                Left = 16, Top = y, Width = 650, Height = Height - y - 40,
                Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true
            };
            Controls.Add(txtLog);
        }

        private void AutoFill()
        {
            var dir = txtScan.Text.Trim().TrimEnd('\\');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            txtVersion.Text = ReadVersion(dir);

            var appJsonPath = Path.Combine(dir, "app.json");
            if (File.Exists(appJsonPath))
            {
                // 已有 app.json：沿用其中 id / 启动信息
                try
                {
                    var o = JObject.Parse(File.ReadAllText(appJsonPath));
                    txtAppId.Text = (string)o["id"] ?? Path.GetFileName(dir);
                    txtName.Text = (string)o["name"] ?? "";
                    txtExe.Text = (string)o["exe"] ?? "";
                    txtWorkdir.Text = (string)o["workdir"] ?? "";
                    txtProcess.Text = (string)o["process"] ?? "";
                    txtArgs.Text = (string)o["args"] ?? "";
                    chkPortable.Checked = (bool?)o["portableData"] == true;
                    var env = o["env"] as JObject;
                    txtEnv.Text = env == null ? "" : string.Join("\r\n", env.Properties().Select(p => p.Name + "=" + (p.Value?.ToString() ?? "")));
                    lblVbs.Text = "（读取已有 app.json）";
                    Log("已读取 app.json：id=" + txtAppId.Text);
                    return;   // 只读加载路径：不再写回 apps.seq（避免副作用降低序号）
                }
                catch { }
            }
            // 新目录：自动分配自增数字ID（扫描同级 app.json 的 id 与数字目录名）
            txtAppId.Text = NextNumericId(Path.GetDirectoryName(dir));
            txtName.Text = Path.GetFileName(dir); // 名称默认取文件夹名（非数字 id）
            lblVbs.Text = "（未检测）";
            DoParseVbs(); // 有 vbs 直接解析；无则自动检测 exe
        }

        /// <summary>读取根目录 apps.seq 中的当前序号（缺省 0）。</summary>
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

        /// <summary>把已分配的最大序号写回根目录 apps.seq。</summary>
        private static void WriteSeq(string parentDir, int v)
        {
            try { File.WriteAllText(Path.Combine(parentDir ?? "", "apps.seq"), v.ToString()); } catch { }
        }

        /// <summary>下一个自增数字 id：以 apps.seq 为主，并取已有兄弟数字目录名/数字 id 的最大值作为下限（避免历史 id 回退）。</summary>
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

        private void Log(string s) =>
            txtLog.Invoke((Action)(() => txtLog.AppendText(s + "\r\n")));

        // -------------------- app.json 生成 --------------------
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
            chkPortable.Checked = d.portableData;
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
                ["portableData"] = chkPortable.Checked,
                ["env"] = env,
                ["args"] = txtArgs.Text.Trim()
            };
            if (File.Exists(iconPath)) obj["icon"] = "icon.ico";
            var outPath = Path.Combine(scan, "app.json");
            File.WriteAllText(outPath, JsonConvert.SerializeObject(obj, Formatting.Indented));
            // 自增序号：若保存的 id 为数字且超过当前 apps.seq，则写回根目录 apps.seq
            var parent = Path.GetDirectoryName(scan);
            if (int.TryParse(appid, out var seqV) && seqV > ReadSeq(parent)) WriteSeq(parent, seqV);
            Log("已写入 app.json：" + outPath + "（id=" + appid + "）");
            MessageBox.Show("已保存 app.json：\n" + outPath, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // -------------------- manifest.json 生成 --------------------
        /// <summary>从启动 exe 提取高清图标：① 保存为应用目录下 icon.ico（随 manifest 分发/兜底）；
        /// ② 同时直写中央图标目录 icons/{id}.ico（Propel 对外服务入口，生成即生效，无需重启服务）。</summary>
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
                if (gotHiRes)
                {
                    Log("已生成应用图标（" + how + "）：" + outPath);
                }
                else
                {
                    // 最终兜底：仅能拿到 32px
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico == null) { Log("未能提取图标：" + exePath); return; }
                        using (var fs = File.Create(outPath)) ico.Save(fs);
                        Log("已生成应用图标（32px 兜底）：" + outPath);
                    }
                }

                // ② 直写中央图标目录 icons/{id}.ico
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

        /// <summary>解析中央图标目录：为空默认 scan\..\icons；相对路径基于 scan 解析，绝对路径直接使用。</summary>
        private string ResolveIconsDir(string scan)
        {
            var rel = txtIconsDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(rel)) rel = "..\\icons";
            if (Path.IsPathRooted(rel)) return Path.GetFullPath(rel);
            return Path.GetFullPath(Path.Combine(scan, rel));
        }

        private async Task GenerateAsync()
        {
            var btn = _btnGen;
            if (btn != null) btn.Enabled = false;
            try
            {
                var scan = txtScan.Text.Trim().TrimEnd('\\');
                if (!Directory.Exists(scan)) { Log("目标目录不存在"); return; }
                var appid = txtAppId.Text.Trim();
                var version = txtVersion.Text.Trim();
                if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(version))
                { Log("请先浏览/填写 软件目录与应用ID（版本由 version.txt 自动读取）"); return; }

                Log("开始扫描：" + scan);
                ExtractIcon(scan); // 先落 icon.ico，随后会一并打进 manifest 分发
                var files = new List<Dictionary<string, object>>();
                long total = 0;
                await Task.Run(() =>
                {
                    foreach (var f in SafeWalk(scan))
                    {
                        var rel = f.Substring(scan.Length).TrimStart('\\').Replace('\\', '/');
                        if (IsIgnoredForManifest(rel)) continue; // 排除旧清单及日志/临时/缓存等运行时文件
                        var fi = new FileInfo(f);
                        var sha = Sha256File(f);
                        var entry = new Dictionary<string, object>
                        {
                            ["path"] = rel,
                            ["size"] = fi.Length.ToString(),
                            ["sha256"] = sha
                        };
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
                Log($"完成：appid={appid}，{files.Count} 个文件，共 {total} bytes");
                Log("已写入：" + outPath);
                MessageBox.Show("生成成功：\n" + outPath, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("错误：" + ex.Message); }
            finally { if (btn != null) btn.Enabled = true; }
        }

        // 生成清单时跳过的文件/目录：运行时生成或无关文件不应进入分发清单，
        // 否则应用运行后改写它们（如日志）会导致哈希变化、客户端反复误判“需更新”。
        private static bool IsIgnoredForManifest(string rel)
        {
            var lower = rel.ToLowerInvariant();
            if (lower.EndsWith(".log") || lower.EndsWith(".tmp") || lower.EndsWith(".bak") || lower.EndsWith(".old"))
                return true;
            var name = Path.GetFileName(rel);
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("app.json", StringComparison.OrdinalIgnoreCase)   // 客户端元数据（Impel 端 CacheAppList 维护），不入分发清单
                || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var seg in rel.Split('/'))
            {
                var s = seg.ToLowerInvariant();
                if (s == "logs" || s == "log" || s == "cache" || s == "caches" || s == "temp" || s == "tmp"
                    || s == "app.staging" || s == "__pycache__" || s == ".git" || s == "node_modules")
                    return true;
            }
            return false;
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

        // -------------------- 控件辅助 --------------------
        private Label AddLabel(string t, int x, int y)
        {
            var l = new Label { Left = x, Top = y, Text = t, AutoSize = true };
            Controls.Add(l); return l;
        }
        private TextBox AddTextBox(string v, int x, int y, int w)
        {
            var t = new TextBox { Left = x, Top = y, Width = w, Text = v };
            Controls.Add(t); return t;
        }
        private Button AddButton(string t, int x, int y, int w, Action onClick)
        {
            var b = new Button { Left = x, Top = y, Width = w, Text = t, Height = 26 };
            if (onClick != null) b.Click += (s, e) => onClick();
            Controls.Add(b); return b;
        }
        private static Label LblIn(string t, int x, int y) => new Label { Left = x, Top = y, Text = t, AutoSize = true };
        private static TextBox TxtIn(string v, int x, int y, int w) => new TextBox { Left = x, Top = y, Width = w, Text = v };
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

        /// <summary>依次尝试：① PE 资源直取（最高保真）② Shell 提取 256px。成功返回 true。</summary>
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
                    // ICONDIRENTRY 前 12 字节：宽、高、色数、保留、planes(2)、位深(2)、数据长度(4)
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
            // nIconSize = MAKELONG(小, 大)：请求 256px 大图标
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
                        0, 0, 0, 0, 1, 0, 32, 0, // 256px（宽高 0 表示 256）、32 位
                        (byte)(png.Length & 0xFF), (byte)((png.Length >> 8) & 0xFF),
                        (byte)((png.Length >> 16) & 0xFF), (byte)((png.Length >> 24) & 0xFF)
                    } };
                    WriteIco(new List<byte[]> { png }, metas, outIco);
                }
            }
            finally { DestroyIcon(hicon); if (hiconSmall != IntPtr.Zero) DestroyIcon(hiconSmall); }
        }

        /// <summary>按 ICO 文件格式写出：ICONDIR + ICONDIRENTRY(16B)×N + 图像数据。</summary>
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
                    bw.Write(metas[i]);          // 12 字节固定头
                    bw.Write((uint)offset);      // 数据偏移
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
                    if (em.Success)
                    {
                        d.env[em.Groups[1].Value] = Eval(em.Groups[2].Value, rootDir, vars);
                        continue;
                    }
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

                // 启动参数（Run 行中 exe 之后的字符串字面量）
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

        /// <summary>把 vbs 右侧表达式（root / dataDir / 变量 / "..." 拼接）求值为相对 rootDir 的路径。</summary>
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
                // 其余（函数调用等）忽略
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

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // 运行时按程序集名兜底加载 Newtonsoft.Json.dll（与 exe 同目录）
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)) return null;
                var dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
                return File.Exists(dll) ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll)) : null;
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
