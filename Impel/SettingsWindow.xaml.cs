using System;
using System.Windows;
using Impel;

namespace Impel
{
    public partial class SettingsWindow : Window
    {
        private readonly ImpelConfig _cfg;
        public ImpelConfig Result { get; private set; }

        public SettingsWindow(ImpelConfig cfg)
        {
            InitializeComponent();
            // 高分屏下可用高度不足时钳制窗口高度，避免被任务栏截断
            Loaded += (s, e) =>
            {
                double max = SystemParameters.WorkArea.Height - 24;
                if (Height > max) Height = max;
                MaxHeight = max;
            };
            _cfg = cfg ?? ConfigService.Default();
            TxtHost.Text = _cfg.server.host;
            TxtToken.Text = _cfg.server.token;
            TxtRoot.Text = _cfg.softwareRoot;
            TxtParallel.Text = (_cfg.downloadParallel > 0 ? _cfg.downloadParallel : 6).ToString();
            TxtSmallTh.Text = (_cfg.smallFileThresholdKB > 0 ? _cfg.smallFileThresholdKB : 1024).ToString();
            ChkTopmost.IsChecked = _cfg.dock.topmost;
            ChkLogging.IsChecked = _cfg.logging;
            ChkAutostart.IsChecked = _cfg.autostart;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var c = new ImpelConfig
            {
                server = new ServerCfg
                {
                    host = TxtHost.Text.Trim(),
                    token = TxtToken.Text.Trim()
                },
                softwareRoot = TxtRoot.Text.Trim(),
                dock = _cfg.dock ?? new DockCfg(),
                p2p = _cfg.p2p ?? new P2PCfg(),
                apps = _cfg.apps ?? new System.Collections.Generic.List<AppCfg>(),
                downloadParallel = ParseParallel(),
                smallFileThresholdKB = ParseSmallTh(),
                autostart = ChkAutostart.IsChecked == true,
                logging = ChkLogging.IsChecked == true,
                installed = _cfg.installed,
                propelSource = _cfg.propelSource
            };
            c.dock.topmost = ChkTopmost.IsChecked == true;
            // 同步开机自启：勾选则在启动文件夹创建 impel.lnk，取消则删除
            try
            {
                if (c.autostart) ShortcutManager.CreateAutostartShortcut();
                else ShortcutManager.RemoveAutostartShortcut();
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("设置开机自启失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning); }
            Result = c;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        /// <summary>浏览选择软件根目录：支持相对路径（基于 exe 目录）解析为初始位置。</summary>
        private void BtnBrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择软件根目录";
                dlg.ShowNewFolderButton = true;
                var cur = TxtRoot.Text.Trim();
                if (System.IO.Directory.Exists(cur))
                    dlg.SelectedPath = cur;
                else if (!string.IsNullOrEmpty(cur))
                {
                    try
                    {
                        var p = System.IO.Path.Combine(ConfigService.ExeDir, cur);
                        if (System.IO.Directory.Exists(p)) dlg.SelectedPath = p;
                    }
                    catch { }
                }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    TxtRoot.Text = dlg.SelectedPath;
            }
        }

        /// <summary>解析"下载进程数"：非法或 ≤0 一律回退默认 6。</summary>
        private int ParseParallel()
        {
            int v;
            if (!int.TryParse(TxtParallel.Text.Trim(), out v) || v <= 0) return 6;
            return v;
        }

        /// <summary>解析"小文件阈值(KB)"：非法或 ≤0 一律回退默认 1024。</summary>
        private int ParseSmallTh()
        {
            int v;
            if (!int.TryParse(TxtSmallTh.Text.Trim(), out v) || v <= 0) return 1024;
            return v;
        }
    }
}
