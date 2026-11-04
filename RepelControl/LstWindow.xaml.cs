using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RepelControl
{
    public class LstItem
    {
        public bool Distribute { get; set; }
        public string Name { get; set; }
        public string Id { get; set; }
        public string Version { get; set; }
        /// <summary>该条目的原始 JSON（all.lst 或离线时 repel.lst 中的对象），保存时直接写回 repel.lst。</summary>
        public JObject Raw { get; set; }
    }

    public partial class LstWindow : Window
    {
        private readonly int _port;
        private readonly string _token;
        private readonly Action<string> _log;
        private JArray _allLst;
        private readonly ObservableCollection<LstItem> _items = new ObservableCollection<LstItem>();
        private readonly List<Button> _busyBtns = new List<Button>();

        public LstWindow(int port, string token, Action<string> log)
        {
            InitializeComponent();
            _port = port;
            _token = token ?? "";
            _log = log ?? (_ => { });
            dgv.ItemsSource = _items;
            _busyBtns.AddRange(new[] { btnRefresh, btnSave, btnAll, btnNone });
            // 高分屏（150% 缩放）下可用高度不足时，将窗口高度钳制到工作区内，避免底部被任务栏截断
            Loaded += (s, e) =>
            {
                double max = SystemParameters.WorkArea.Height - 24;
                if (Height > max) Height = max;
                MaxHeight = max;
            };
            Loaded += (s, e) => RefreshLst();
        }

        private string AllLstUrl()
        {
            var q = string.IsNullOrEmpty(_token) ? "" : "?token=" + Uri.EscapeDataString(_token);
            return "http://127.0.0.1:" + _port + "/all.lst" + q;
        }

        private void SetBusy(bool busy)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var b in _busyBtns) b.IsEnabled = !busy;
            });
        }

        private void Info(string s) => Dispatcher.Invoke(() => lblInfo.Text = s);

        private void BtnRefresh_Click(object s, RoutedEventArgs e) => RefreshLst();

        private void RefreshLst()
        {
            SetBusy(true); Info("正在获取 all.lst ...");
            var url = AllLstUrl();
            Task.Run(() =>
            {
                var body = MainWindow.HttpGetBody(url, out var status, out var err);
                Dispatcher.Invoke(() =>
                {
                    SetBusy(false);
                    if (body == null && status == 0) { LoadOffline("连接 Repel 服务失败：" + err); return; }
                    if (status != 200) { LoadOffline("获取 all.lst 返回 HTTP " + status); return; }
                    JArray arr;
                    try { arr = JArray.Parse(Encoding.UTF8.GetString(body)); }
                    catch (Exception ex) { LoadOffline("解析 all.lst 失败：" + ex.Message); return; }
                    if (arr.Count == 0) { LoadOffline("all.lst 为空（Propel 不可达且无本地缓存）"); return; }
                    _allLst = arr;
                    var distributed = LoadRepelLstIds();
                    _items.Clear();
                    foreach (JObject o in arr)
                    {
                        var id = (string)o["id"] ?? "";
                        var name = (string)o["name"] ?? id;
                        var ver = (string)o["version"] ?? "";
                        _items.Add(new LstItem { Distribute = distributed.Contains(id), Name = name, Id = id, Version = ver, Raw = o });
                    }
                    Info($"共 {arr.Count} 个软件，已勾选 {distributed.Count} 个（当前 repel.lst 分发项）");
                });
            });
        }

        /// <summary>离线兜底：拿不到 all.lst（Propel 不可达 / 无缓存）时，加载本地 repel.lst 条目，
        /// 仍可取消勾选并保存（移除分发项）；新增软件需待 Propel 恢复后刷新。</summary>
        private void LoadOffline(string reason)
        {
            _allLst = null;
            var distributed = LoadRepelLstIds();
            _items.Clear();
            try
            {
                var p = Path.Combine(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "repel.lst");
                if (File.Exists(p))
                {
                    var arr = JArray.Parse(File.ReadAllText(p));
                    foreach (JObject o in arr)
                    {
                        var id = (string)o["id"] ?? "";
                        _items.Add(new LstItem { Distribute = true, Name = (string)o["name"] ?? id, Id = id, Version = (string)o["version"] ?? "", Raw = o });
                    }
                }
            }
            catch { }
            Info(reason + $"；已离线加载本地 repel.lst（{_items.Count} 项）。可取消勾选后保存，新增软件需连接 Propel 后刷新。");
        }

        private void BtnAll_Click(object s, RoutedEventArgs e)
        {
            foreach (var it in _items) it.Distribute = true;
            dgv.Items.Refresh();
            Info("已全选 " + _items.Count + " 项（尚未保存，请点「保存分发」）");
        }

        private void BtnNone_Click(object s, RoutedEventArgs e)
        {
            foreach (var it in _items) it.Distribute = false;
            dgv.Items.Refresh();
            Info("已全不选 " + _items.Count + " 项（尚未保存，请点「保存分发」）");
        }

        private void BtnSave_Click(object s, RoutedEventArgs e) => SaveLst();

        private void SaveLst()
        {
            if (_allLst == null && _items.Count == 0) { Info("请先点「刷新列表」"); return; }
            dgv.Items.Refresh();
            var byId = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (_allLst != null)
                foreach (JObject o in _allLst) { var id = (string)o["id"]; if (!string.IsNullOrEmpty(id)) byId[id] = o; }
            var outArr = new JArray();
            foreach (var it in _items)
            {
                if (!it.Distribute) continue;
                if (it.Raw != null) { outArr.Add(it.Raw); continue; }
                if (byId.TryGetValue(it.Id, out var o)) outArr.Add(o);
            }
            var p = Path.Combine(Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "repel.lst");
            File.WriteAllText(p, outArr.ToString(Formatting.Indented));
            Info($"已保存 repel.lst（{outArr.Count} 个软件）：{p}");
            _log($"已保存 repel.lst：{p}（{outArr.Count} 个软件）");
        }

        private HashSet<string> LoadRepelLstIds()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var p = Path.Combine(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "repel.lst");
                if (File.Exists(p))
                {
                    var arr = JArray.Parse(File.ReadAllText(p));
                    foreach (JObject o in arr) { var id = (string)o["id"]; if (!string.IsNullOrEmpty(id)) set.Add(id); }
                }
            }
            catch { }
            return set;
        }

        private void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    }
}
