using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Impel;

namespace Impel
{
    /// <summary>Mac 风格底部软件坞：图标居中、点击单独弹跳 1.2 倍（邻居不动），并触发更新+启动。</summary>
    public partial class MainWindow : Window
    {
        private ImpelConfig _cfg;
        private List<AppCfg> _apps = new List<AppCfg>();
        private DispatcherTimer _refreshTimer;
        private DateTime _lastRefresh = DateTime.MinValue;
        private CancellationTokenSource _changeCts;
        private long _menuVersion = 0;
        private List<AppCfg> _knownApps = new List<AppCfg>(); // 最近一次从 Repel 成功获取的应用元数据（用于本地兜底时匹配名称/版本）
        private readonly Dictionary<string, Border> _hoverByApp = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _busyByApp = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase); // id → 工作中的遮罩
        private readonly Dictionary<string, TextBlock> _statusByApp = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase); // id → 状态文本（校验中/更新中）
        private readonly HashSet<string> _waitingByApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 已有点击流程在等待/执行该应用（UI 线程访问，不随 dock 重建失效）
        private System.Windows.Point _dragOrigin;   // 拖拽起点（用于阈值判定，区分点击与拖动）
        private bool _dragMoved;
        private bool _selfUpdating;   // 自更新防重入：拉起 updater 之前只应执行一次
        /// <summary>窗口边缘到图标像素的距离 = 图标外层 Margin(5)。
        /// 贴边时窗口仅越出这段透明留白，使图标像素与 hover 高亮恰好紧贴屏幕边/任务栏上边缘，
        /// 不超出、不覆盖任务栏（此前误用 13 含 hover Padding，导致底部图标伸入任务栏）。</summary>
        private const double EdgeInset = 5;
        /// <summary>图标外框边长与单边外边距（DIP）：生成图标与「超出屏幕自动缩放」共用，避免两处数值不同步。</summary>
        private const double IconBox = 96;
        private const double IconGap = 5;
        private double _lastDockScale = -1;   // 上次应用的缩放系数：值不变就不重建 transform，避免无谓重排

        /// <summary>当前主窗口实例（供 App 单实例呼出使用）。</summary>
        public static MainWindow Current { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Current = this;
            _cfg = ConfigService.Load();
            Loaded += (s, e) =>
            {
                Topmost = _cfg.dock.topmost;   // 默认 false（不置顶）
                App.SetLogging(_cfg.logging);  // 应用日志开关（关则不产生 impel.log）
                App.Log("==== impel 启动（服务器 " + UpdateEngine.RepelBase(_cfg) + "，软件根目录 " + ConfigService.SoftwareRoot(_cfg) + "）====");   // 带上实际使用的服务器地址，配置错误一眼可辨
                _knownApps = _cfg.apps ?? new List<AppCfg>(); // 以已保存的列表作为本地兜底匹配基准
                BuildDock();      // 立即显示（暂无应用），不阻塞
                RefreshApps();    // 后台拉取列表，完成后刷新（Repel 不可用则本地兜底）
                System.Threading.Tasks.Task.Run(() => PeerDiscovery.RegisterAll(_cfg));   // 启动即注册本地已装应用
                StartBackend();
                StartRefreshTimer();
                StartChangeWatcher();
                // 同步开机自启：默认开启，登录后自动运行菜单；按设置重建或移除（快捷方式被删也会重建）
                try { if (_cfg.autostart) Impel.ShortcutManager.CreateAutostartShortcut(); else Impel.ShortcutManager.RemoveAutostartShortcut(); } catch { }
                // 首次启动（无配置文件或服务器地址仍为默认值）主动弹出设置窗口，引导完成配置
                bool firstRun = !System.IO.File.Exists(ConfigService.ConfigPath)
                    || string.IsNullOrWhiteSpace(_cfg.server.host)
                    || _cfg.server.host.Equals("http://gameserver", StringComparison.OrdinalIgnoreCase);
                if (firstRun) Dispatcher.BeginInvoke(new Action(OpenSettings), System.Windows.Threading.DispatcherPriority.Background);
            };
            // 重新获得焦点时立即刷新（带防抖，避免频繁触发）
            Activated += (s, e) => RefreshApps();
            Closed += (s, e) => { _changeCts?.Cancel(); };
        }

        // ---------------- 应用列表 ----------------
        private void RefreshApps(bool force = false)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastRefresh).TotalSeconds < 20) return; // 20s 防抖（强制刷新跳过）
            _lastRefresh = now;
            System.Threading.Tasks.Task.Run(() =>
            {
                var (ok, list) = UpdateEngine.FetchAppList(_cfg);
                if (ok && list != null && list.Count > 0)
                {
                    _knownApps = list;                       // 记住最近一次 Repel 成功列表，供本地兜底匹配
                    UpdateEngine.CacheAppList(_cfg, list);    // 逐应用缓存 app.json 到目录内，供 Repel 失效时离线兜底
                    _apps = list;                            // 主逻辑：以 Repel 为准（含其返回的空列表）
                }
                else
                {
                    // Repel 不可用（或拉取失败）：基于本地软件根目录兜底（仅 app.json 存在的应用才显示）
                    try { list = UpdateEngine.ScanLocalApps(_cfg); }
                    catch (Exception ex) { App.Log("本地扫描失败: " + ex.Message); list = list ?? new List<AppCfg>(); }
                    _apps = list ?? new List<AppCfg>();
                }
                _cfg.apps = _apps;
                Dispatcher.Invoke(BuildDock);
                // 列表刷新完成后立即续注册（含首次）：注册 30 分钟过期，每次拿到新列表就重报，
                // 保证即使没点过图标，约拉完列表即可上线并提供 P2P；比等 refreshTimer 更即时。
                System.Threading.Tasks.Task.Run(() => PeerDiscovery.RegisterAll(_cfg));
            });
        }

        private void BuildDock()
        {
            DockPanel.Children.Clear();
            _hoverByApp.Clear();
            _busyByApp.Clear();
            _statusByApp.Clear();
            if (_apps == null || _apps.Count == 0)
            {
                DockPanel.Children.Add(new TextBlock
                {
                    Text = "（暂无应用 · 右键设置）",
                    Foreground = Brushes.Gray,
                    FontSize = 13,
                    Margin = new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center
                });
                ApplySideLayout();
                return;
            }
            foreach (var app in _apps)
            {
                if (!app.enabled) continue;
                DockPanel.Children.Add(MakeAppIcon(app));
            }
            ApplySideLayout();
            FetchIconsAsync(); // 启动/刷新后主动从统一图标目录拉取，成功则刷新菜单图标
        }

        private UIElement MakeAppIcon(AppCfg app)
        {
            var src = IconLoader.GetIcon(_cfg, app);
            var sideNow = (_cfg.dock.side ?? "bottom").ToLower();
            // 外层：只负责点击 1.6 倍弹跳的 RenderTransform（不影响布局，邻居不动）
            var outer = new Border
            {
                Width = IconBox,
                Height = IconBox,
                Margin = new Thickness(IconGap),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                // 弹跳放大方向朝屏幕内侧：bottom 向上、top 向下、left 向右、right 向左
                RenderTransformOrigin = sideNow == "top" ? new Point(0.5, 0)
                                      : sideNow == "left" ? new Point(0, 0.5)
                                      : sideNow == "right" ? new Point(1, 0.5)
                                      : new Point(0.5, 1),
                ToolTip = app.name + (string.IsNullOrEmpty(app.version) ? "" : "  v" + app.version)
            };
            // 内层：悬停时出现圆角高亮（类似 Win11 任务栏 / macOS Dock）
            var hover = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = Brushes.Transparent,
                Padding = new Thickness(8)
            };
            if (src != null)
            {
                // 用 Viewbox 包裹：无论 ico 原生像素多大（如 512px），都自适应缩放填满规定方框，不被裁切/溢出
                hover.Child = new Viewbox { Stretch = Stretch.Uniform, Child = new Image { Source = src } };
            }
            else
            {
                hover.Child = new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(Color.FromRgb(78, 140, 255)),
                    Child = new TextBlock
                    {
                        Text = ((app.name ?? app.id ?? "?") is var nm && nm.Length > 0 ? nm.Substring(0, 1) : "?"),
                        Foreground = Brushes.White,
                        FontSize = 44,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            // 工作中的遮罩：半透明暗罩 + 中心旋转细环形加载圈（indeterminate，仅表示“正在工作”）+ 下方状态文本
            var busy = new Border
            {
                Visibility = Visibility.Hidden,
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                CornerRadius = new CornerRadius(16)
            };
            // 旋转细环形加载圈（无进度条，靠旋转表示工作中）
            var track = new Ellipse
            {
                Width = 34, Height = 34,
                Stroke = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                StrokeThickness = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var ring = new Ellipse
            {
                Width = 34, Height = 34,
                Stroke = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 30, 300 },  // 单段弧（其余为缺口），旋转即呈现加载圈
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var rotate = new RotateTransform();
            ring.RenderTransform = rotate;
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever });
            var ringHost = new Grid
            {
                Width = 34, Height = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ringHost.Children.Add(track);
            ringHost.Children.Add(ring);
            // 状态文本：叠加在遮罩底部，不参与居中，保证加载圈圆心与图标中心一致
            var status = new TextBlock
            {
                Text = "校验中",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var overlayGrid = new Grid();
            overlayGrid.Children.Add(ringHost);   // 加载圈精确居中于图标中心
            overlayGrid.Children.Add(status);     // 文本叠加在下方
            busy.Child = overlayGrid;
            var grid = new Grid();
            grid.Children.Add(hover);
            grid.Children.Add(busy);
            outer.Child = grid;
            outer.MouseEnter += (s, e) => hover.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            outer.MouseLeave += (s, e) => hover.Background = Brushes.Transparent;
            outer.MouseLeftButtonUp += (s, e) => OnAppClick(outer, app);
            if (!string.IsNullOrEmpty(app.id))
            {
                _hoverByApp[app.id] = hover; _busyByApp[app.id] = busy; _statusByApp[app.id] = status;
                // dock 定时重建会重置图标的视觉状态：若该应用的更新任务仍在后台进行，
                // 恢复"更新中"遮罩与跳动，否则用户会误以为卡死而反复点击
                if (UpdateEngine.IsUpdating(app.id))
                {
                    busy.Visibility = Visibility.Visible;
                    status.Text = "更新中";
                    StartBob(hover);
                }
            }
            return outer;
        }

        /// <summary>用拉取成功的新图标替换菜单中该应用的图标。</summary>
        private void SetIcon(AppCfg app, ImageSource src)
        {
            if (src == null || string.IsNullOrEmpty(app?.id)) return;
            if (_hoverByApp.TryGetValue(app.id, out var hover))
                hover.Child = new Viewbox { Stretch = Stretch.Uniform, Child = new Image { Source = src } };
        }

        /// <summary>后台并发从统一图标目录拉取每个应用的图标（含重试），成功后刷新对应菜单项；失败保持降级图标。</summary>
        private void FetchIconsAsync()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var app in _apps)
                {
                    if (app == null || string.IsNullOrEmpty(app.id) || !app.enabled) continue;
                    bool ok = false;
                    try { ok = UpdateEngine.FetchIcon(_cfg, app, s => App.Log(s)); }
                    catch (Exception ex) { App.Log("图标拉取异常: " + ex.Message); }
                    if (!ok) continue;
                    var src = IconLoader.LoadFromFile(System.IO.Path.Combine(ConfigService.SoftwareRoot(_cfg), "icons", app.id + ".ico"));
                    if (src != null)
                    {
                        var a = app;
                        Dispatcher.Invoke(() => SetIcon(a, src));
                    }
                }
            });
        }

        /// <summary>点击：图标放大到 1.6 倍并回弹（仅该图标），同时显示环形加载圈 + 状态文本（校验中→更新中）、
        /// 图标上下跳动（下载/启动中）；确保最新（必要时下载）并启动；完成后停跳并隐藏。</summary>
        private async void OnAppClick(Border border, AppCfg app)
        {
            // 本地已有点击流程在处理该应用（等待更新/启动中）：忽略重复点击。
            // 用独立标志而非 busy 遮罩可见性——遮罩会被 dock 定时重建重置，曾导致防重入失效
            if (_waitingByApp.Contains(app.id)) return;
            _waitingByApp.Add(app.id);
            try
            {
                Pop(border);
                _busyByApp.TryGetValue(app.id, out var busy);
                _statusByApp.TryGetValue(app.id, out var status);
                _hoverByApp.TryGetValue(app.id, out var iconHost);
                // 已有更新任务在后台进行（引擎级状态，与 dock 重建无关）：
                // 显示遮罩并等待该任务完成，绝不重复触发引擎、绝不在其完成前启动应用
                // （不完整版本启动会导致应用自身崩溃，如趣教 CefSharp 断点异常弹窗）
                bool already = UpdateEngine.IsUpdating(app.id);
                var startedAt = DateTime.UtcNow;   // 记录点击时刻，用于保证遮罩最小可见时长
                Dispatcher.Invoke(() =>
                {
                    if (busy != null) busy.Visibility = Visibility.Visible;
                    if (status != null) status.Text = already ? "更新中" : "校验中";   // 校验阶段（比对 sha256）
                    if (iconHost != null) StartBob(iconHost);
                });
                bool okToLaunch = true;
                if (already)
                {
                    while (UpdateEngine.IsUpdating(app.id)) await System.Threading.Tasks.Task.Delay(500);
                }
                else
                {
                    var res = await System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            return await UpdateEngine.UpdateAsync(_cfg, app, p =>
                            {
                                // 一旦开始下载（currentBytes 递增）即切换到“更新中”（按 id 实时取元素，dock 重建后仍有效）
                                Dispatcher.Invoke(() =>
                                {
                                    if (_statusByApp.TryGetValue(app.id, out var curStatus) && p.currentBytes > 0 && p.currentTotal > 0
                                        && p.currentFile != "已是最新" && p.currentFile != "运行中")
                                        curStatus.Text = "更新中";
                                });
                            }, s => App.Log(s));
                        }
                        catch { return null; }
                        finally { try { PeerDiscovery.RegisterApp(_cfg, app); } catch { } }
                    });
                    // 极小竞态窗口下（双击）重复触发被引擎忽略时返回 state="更新中"：等真实任务完成
                    if (res != null && res.state == "更新中")
                        while (UpdateEngine.IsUpdating(app.id)) await System.Threading.Tasks.Task.Delay(500);
                    // 更新失败（异常）、判定“需修复”、或“未安装”（清单拉取失败/服务器无此应用）时不启动：
                    // “未安装”意味着连应用文件都没有，直接启动必然报“未找到主程序”
                    if (res == null || res.state == "需修复" || res.state.StartsWith("未安装"))
                    {
                        okToLaunch = false;
                        var nm = app.name ?? app.id ?? "?";
                        var reason = res == null ? "更新异常" : res.state;
                        App.Log("跳过启动（" + reason + "）：" + nm);
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            "“" + nm + "”未能就绪（" + reason + "），本次不启动。请检查网络或源后重试。",
                            "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning));
                    }
                }
                if (okToLaunch) Launcher.Launch(_cfg, app);
                // 保证"校验中/更新中"遮罩至少显示 3 秒：
                //  - 无需更新：校验快速完成（已最新），"校验中"遮罩停留 3 秒再消失，给用户明确反馈；
                //  - 需要更新：若下载很快完成，"更新中"遮罩也至少停留 3 秒；
                //  耗时已超 3 秒（多数更新场景）则自动不触发，仅衔接应用冷启动、避免误以为没响应而重复点击
                var elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
                if (elapsed < 3000) await System.Threading.Tasks.Task.Delay(3000 - (int)elapsed);
                Dispatcher.Invoke(() =>
                {
                    // 按 id 实时取当前元素：更新期间 dock 可能已重建，点击时捕获的旧引用已不在视觉树，
                    // 只停旧引用会漏掉新元素上的遮罩与动画（表现为"更新完成、应用已启动，图标仍在动"）
                    if (_hoverByApp.TryGetValue(app.id, out var curIcon)) StopBob(curIcon);
                    if (_busyByApp.TryGetValue(app.id, out var curBusy)) curBusy.Visibility = Visibility.Hidden;
                });
            }
            finally { _waitingByApp.Remove(app.id); }
        }

        /// <summary>让图标上下小幅跳动（循环），仅作用于图标宿主，不影响外层的点击缩放。</summary>
        private static void StartBob(FrameworkElement el)
        {
            var tt = new TranslateTransform(0, 0);
            el.RenderTransform = tt;
            var a = new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(480))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            tt.BeginAnimation(TranslateTransform.YProperty, a);
        }

        private static void StopBob(FrameworkElement el)
        {
            if (el.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, null);
            el.RenderTransform = null;
        }

        private static void Pop(FrameworkElement el)
        {
            var st = new ScaleTransform(1, 1);
            el.RenderTransform = st;
            var a = new DoubleAnimation(1, 1.6, TimeSpan.FromMilliseconds(220)) { AutoReverse = true };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ---------------- 后端 / P2P ----------------
        private void StartBackend()
        {
            try { var p = new PeerProvider(_cfg); p.Start(); } catch { }
        }

        private void StartRefreshTimer()
        {
            // 兜底定时刷新（仅保险用）；主刷新由下方长轮询事件驱动，Repel 改列表即秒级生效
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _refreshTimer.Tick += (s, e) =>
            {
                RefreshApps();
                // 周期续注册：Repel 注册 20 分钟过期，每 5 分钟重新上报本地已校验应用，保证多客户端互见
                System.Threading.Tasks.Task.Run(() => PeerDiscovery.RegisterAll(_cfg));
            };
            _refreshTimer.Start();
        }

        /// <summary>长轮询监听 Repel 菜单列表变更，并接收定向下发的客户端自更新指令。
        /// 服务端宕机/不支持时退避重试；恢复后自动重连并自检版本。</summary>
        private void StartChangeWatcher()
        {
            _changeCts = new CancellationTokenSource();
            var ct = _changeCts.Token;
            bool repelOk = false; // 记录 Repel 健康状态，用于检测“恢复”瞬间
            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    ChangeResult r;
                    try { r = await System.Threading.Tasks.Task.Run(() => UpdateEngine.WaitMenuChange(_cfg, _menuVersion)); }
                    catch { r = new ChangeResult { version = _menuVersion }; }
                    if (ct.IsCancellationRequested) break;
                    _menuVersion = r.version;
                    if (r.ok)
                    {
                        if (!string.IsNullOrEmpty(r.cmd)) HandleRemoteCommand(r);
                        if (!repelOk) { repelOk = true; RefreshApps(true); } // 由不可用恢复 → 切回 Repel 主逻辑
                        else if (r.changed) RefreshApps(true);               // Repel 有变更 → 立即刷新
                        // 正常且未变更：保持现状（主逻辑数据已是最新），不轮询刷新
                    }
                    else
                    {
                        repelOk = false;
                        RefreshApps(); // Repel 不可用 → 触发本地兜底刷新（自带防抖）
                        try { await System.Threading.Tasks.Task.Delay(8000, ct); } catch { } // 退避后重连
                    }
                }
            });
        }

        /// <summary>处理 Repel 定向下发的指令。cmdId 先落盘再执行，避免升级重启后重复升级形成循环。</summary>
        private void HandleRemoteCommand(ChangeResult r)
        {
            if (r.cmd != "upgrade" || string.IsNullOrEmpty(r.cmdId)) return;
            if (r.cmdId.Equals(_cfg.lastCmdId ?? "", StringComparison.OrdinalIgnoreCase)) return;
            if (_selfUpdating) return;
            // 有第三方应用正在下载/校验时不重启：指令保留在服务端，下一轮长轮询仍会拿到
            if (UpdateEngine.HasActiveUpdates())
            {
                App.Log("收到客户端升级指令，但有应用正在更新，暂缓执行");
                return;
            }
            _selfUpdating = true;
            _cfg.lastCmdId = r.cmdId;
            try { ConfigService.Save(_cfg); } catch (Exception ex) { App.Log("保存 lastCmdId 失败：" + ex.Message); }
            App.Log("收到客户端升级指令 " + r.cmdId + "（目标版本 " + r.pkgVersion + "）");
            var cfg = _cfg;
            System.Threading.Tasks.Task.Run(() => SelfUpdater.TryUpgrade(cfg, r.pkgVersion, s => App.Log("[自更新] " + s)));
        }

        // ---------------- 布局：按 side 停靠（top/bottom/left/right）----------------
        /// <summary>根据 cfg.dock.side 切换横向/竖向布局、固定尺寸，并吸附到对应边居中。</summary>
        private void ApplySideLayout()
        {
            var side = (_cfg.dock.side ?? "bottom").ToLower();
            bool vertical = side == "left" || side == "right";
            DockPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            // 贴边方向对齐贴边侧（配合 EdgeInset 反向补偿，图标像素恰好贴住屏幕边/任务栏），
            // 非贴边方向保持居中；否则固定宽度/高度侧（left/right/top）图标会被居中而离开屏幕边缘
            DockPanel.HorizontalAlignment = side == "left" ? HorizontalAlignment.Left
                                         : side == "right" ? HorizontalAlignment.Right
                                         : HorizontalAlignment.Center;
            DockPanel.VerticalAlignment = side == "top" ? VerticalAlignment.Top
                                       : side == "bottom" ? VerticalAlignment.Bottom
                                       : VerticalAlignment.Center;
            // 贴边侧留白为 0，放大空间（1.6 倍 ≈ 58px）全部给非贴边侧
            if (side == "left")
            {
                SizeToContent = SizeToContent.Height;
                Width = _cfg.dock.width > 0 ? _cfg.dock.width : 200;
                Card.Padding = new Thickness(0, 40, 64, 40);   // 左贴边；右侧留放大空间
            }
            else if (side == "right")
            {
                SizeToContent = SizeToContent.Height;
                Width = _cfg.dock.width > 0 ? _cfg.dock.width : 200;
                Card.Padding = new Thickness(64, 40, 0, 40);   // 右贴边；左侧留放大空间
            }
            else if (side == "top")
            {
                SizeToContent = SizeToContent.WidthAndHeight;
                Width = double.NaN;
                Card.Padding = new Thickness(32, 0, 32, 64);   // 顶贴边；下方留放大空间
            }
            else
            {
                SizeToContent = SizeToContent.WidthAndHeight;
                Width = double.NaN;
                Card.Padding = new Thickness(32, 64, 32, 0);   // 底贴边；上方留放大空间
            }
            // 弹跳放大方向随 side 更新每个图标（朝屏幕内侧）
            var origin = side == "bottom" ? new Point(0.5, 1)
                       : side == "top" ? new Point(0.5, 0)
                       : side == "left" ? new Point(0, 0.5)
                       : new Point(1, 0.5);
            foreach (UIElement c in DockPanel.Children)
                if (c is FrameworkElement fe) fe.RenderTransformOrigin = origin;

            // ===== 图标多到会超出工作区时，整排等比缩小 =====
            // 左右停靠是 SizeToContent=Height，窗口高度会随图标数无限增长，居中定位会把上下都推出屏幕；
            // 上下停靠同理会撑宽窗口。自然占用按常量公式算（与当前 transform 无关），反复调用不会累积缩放。
            // 用 LayoutTransform 而非逐个改尺寸：图标、占位文字、圆角、加载圈、状态文本会一起等比缩小。
            var area = GetWorkArea();
            int iconCount = DockPanel.Children.Count;
            bool hasApps = _apps != null && _apps.Count > 0 && iconCount > 0;
            double avail = (vertical
                ? area.Height - Card.Padding.Top - Card.Padding.Bottom
                : area.Width - Card.Padding.Left - Card.Padding.Right) - 2;   // 留 2 DIP 余量，避免浮点/边框恰好贴边
            double natural = iconCount * (IconBox + 2 * IconGap);
            double scale = (hasApps && avail > 0 && natural > avail)
                ? Math.Max(0.05, avail / natural) : 1.0;
            if (Math.Abs(scale - _lastDockScale) > 0.001)
            {
                DockPanel.LayoutTransform = scale < 1.0 ? new ScaleTransform(scale, scale) : Transform.Identity;
                _lastDockScale = scale;
            }

            UpdateLayout();
            area = GetWorkArea();   // 缩放后窗口尺寸已变，重取工作区以正确定位
            double w = ActualWidth, h = ActualHeight;
            // 贴边时反向补偿 EdgeInset，使图标像素紧贴屏幕边/任务栏（顶部与左右越出部分在屏幕外，无遮挡）
            if (side == "left") { Left = area.Left - EdgeInset; Top = area.Top + (area.Height - h) / 2; }
            else if (side == "right") { Left = area.Right - w + EdgeInset; Top = area.Top + (area.Height - h) / 2; }
            else if (side == "top") { Left = area.Left + (area.Width - w) / 2; Top = area.Top - EdgeInset; }
            else { Left = area.Left + (area.Width - w) / 2; Top = area.Bottom - h + EdgeInset; }
        }

        /// <summary>取得窗口中心所在显示器的可用工作区（DIP，与 WPF 的 Left/Top/ActualWidth 单位一致；多屏正确吸附）。</summary>
        private Rect GetWorkArea()
        {
            // WPF 的坐标（Left/Top/ActualWidth）是设备无关像素(DIP)，而 Win32 工作区是物理像素。
            // 150% 缩放下 1 DIP = 1.5 物理像素，必须换算，否则定位会被推出屏幕。
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            // 窗口中心（DIP）→ 物理像素，用于定位所在显示器
            int cxPhys = (int)((Left + ActualWidth / 2) * sx);
            int cyPhys = (int)((Top + ActualHeight / 2) * sy);
            IntPtr hMon = MonitorFromPoint(new POINT { X = cxPhys, Y = cyPhys }, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref mi))
            {
                var w = mi.rcWork; // 物理像素工作区（已扣除任务栏）
                // 物理像素 → DIP
                return new Rect(w.Left / sx, w.Top / sy, (w.Right - w.Left) / sx, (w.Bottom - w.Top) / sy);
            }
            // 兜底：用 WPF 主屏工作区（DIP）
            return SystemParameters.WorkArea;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        /// <summary>拖拽结束后，按窗口中心距四条边的距离吸附到最近的边，并持久化 side。</summary>
        private void SnapToEdge()
        {
            var area = GetWorkArea();
            double cx = Left + ActualWidth / 2;
            double cy = Top + ActualHeight / 2;
            double dl = cx - area.Left, dr = area.Right - cx;
            double dt = cy - area.Top, db = area.Bottom - cy;
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            string side = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";
            if (!string.Equals(_cfg.dock.side, side, StringComparison.OrdinalIgnoreCase))
            {
                _cfg.dock.side = side;
                ConfigService.Save(_cfg);
            }
            ApplySideLayout();
        }

        // ---------------- 拖拽（阈值区分点击/拖动）----------------
        private void Win_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
        {
            _dragOrigin = e.GetPosition(this);
            _dragMoved = false;
        }

        private void Win_MouseMove(object s, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragMoved) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragOrigin.X) > 5 || Math.Abs(p.Y - _dragOrigin.Y) > 5)
            {
                _dragMoved = true;        // 超过阈值 → 视为拖动，不再触发图标点击
                try { DragMove(); } catch { }
                SnapToEdge();             // DragMove 阻塞至松开，随后吸附
            }
        }

        // ---------------- 右键菜单 ----------------
        private void Ctx_Opened(object s, RoutedEventArgs e) { }
        private void Ctx_Closed(object s, RoutedEventArgs e) { }
        private void Ctx_Settings(object s, RoutedEventArgs e) => OpenSettings();
        private void Ctx_Exit(object s, RoutedEventArgs e) => Application.Current.Shutdown();

        public void ShowDock()
        {
            Visibility = Visibility.Visible;
            Activate();
            ApplySideLayout();
        }

        private void OpenSettings()
        {
            var w = new SettingsWindow(_cfg) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (w.ShowDialog() == true)
            {
                _cfg = w.Result;
                Topmost = _cfg.dock.topmost;   // 应用置顶设置
                App.SetLogging(_cfg.logging);  // 应用日志开关
                ConfigService.Save(_cfg);
                // 保存后立刻刷新一次：强制跳过 20s 防抖；
                // 并重启长轮询（旧请求仍阻塞在原地址上，要等服务端超时才会切到新配置）
                RefreshApps(true);
                try { _changeCts?.Cancel(); } catch { }
                StartChangeWatcher();
            }
        }
    }
}
