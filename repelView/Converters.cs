using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RepelView
{
    /// <summary>在线状态 → 文字</summary>
    public class OnlineToText : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => (v is bool b && b) ? "在线" : "离线/过期";
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => null;
    }

    /// <summary>在线状态 → 颜色</summary>
    public class OnlineToBrush : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => (v is bool b && b) ? new SolidColorBrush(Color.FromRgb(46, 160, 67)) : new SolidColorBrush(Color.FromRgb(200, 60, 60));
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => null;
    }
}
