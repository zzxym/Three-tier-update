using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Impel;

namespace Impel
{
    /// <summary>
    /// 应用图标加载，优先级：
    ///   1) 统一图标目录 SoftwareRoot/icons/{id}.ico（启动拉取到本地，主来源）
    ///   2) 应用目录内随软件下发的 icon.ico
    ///   3) 从启动 exe 提取关联图标
    ///   返回 null 时由 UI 使用首字母占位兜底。
    /// </summary>
    public static class IconLoader
    {
        public static ImageSource GetIcon(ImpelConfig cfg, AppCfg app)
        {
            // 1) 统一图标目录（最先尝试，启动拉取后长期有效）
            var unified = Path.Combine(ConfigService.SoftwareRoot(cfg), "icons", app.id + ".ico");
            var s = LoadFromFile(unified);
            if (s != null) return s;

            // 2) 应用目录内已随软件下载的 icon.ico
            var root = ConfigService.AppRoot(cfg, app);
            s = LoadFromFile(Path.Combine(root, "icon.ico"));
            if (s != null) return s;

            // 3) 从 exe 提取
            var exe = Path.Combine(root, app.exe ?? "");
            if (File.Exists(exe))
            {
                try
                {
                    using (var i = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                    {
                        if (i != null)
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(i.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            return bs;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>从磁盘文件加载图标为 ImageSource；多尺寸 .ico 自动取最大帧（优先 256px 高清），失败返回 null。</summary>
        public static ImageSource LoadFromFile(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                {
                    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapFrame best = null;
                    foreach (var f in decoder.Frames)
                        if (best == null || f.PixelWidth * f.PixelHeight > best.PixelWidth * best.PixelHeight) best = f;
                    if (best != null) { best.Freeze(); return best; }
                }
            }
            catch { }
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(path, UriKind.Absolute);
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }
}
