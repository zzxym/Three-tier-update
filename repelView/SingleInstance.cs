using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RepelView
{
    /// <summary>
    /// 单实例辅助：首个实例返回 true 并长期持有互斥体；已有实例时，
    /// GUI 程序（gui=true）尝试呼出并置顶已有主窗口，返回 false（调用方应退出）。
    /// </summary>
    internal static class SingleInstance
    {
        private static Mutex _mutex;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public static bool Ensure(string name, bool gui)
        {
            _mutex = new Mutex(true, name, out bool created);
            if (created) return true;
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
            if (gui) ActivateExisting();
            return false;
        }

        private static void ActivateExisting()
        {
            var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                if (p.Id == self.Id) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                break;
            }
        }
    }
}
