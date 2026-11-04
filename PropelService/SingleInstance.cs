using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PropelService
{
    /// <summary>
    /// 单实例辅助：首个实例返回 true 并长期持有互斥体；已有实例时直接返回 false（调用方应退出，避免重复监听端口）。
    /// 服务/控制台程序无主窗口，gui=false 不尝试呼出。
    /// </summary>
    internal static class SingleInstance
    {
        private static Mutex _mutex;

        public static bool Ensure(string name, bool gui)
        {
            _mutex = new Mutex(true, name, out bool created);
            if (created) return true;
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
            return false;
        }
    }
}
