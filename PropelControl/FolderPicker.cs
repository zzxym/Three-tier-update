using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PropelControl
{
    /// <summary>纯 WPF 环境下使用的文件夹选择对话框（SHBrowseForFolder，无 WinForms 依赖）。</summary>
    internal static class FolderPicker
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHBrowseForFolder(BROWSEINFO bi);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        /// <summary>弹出文件夹选择框；取消或失败返回 null。</summary>
        public static string Pick(string title, string initial)
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = IntPtr.Zero,
                lpszTitle = title,
                ulFlags = 0x40 /*BIF_RETURNONLYFSDIRS*/ | 0x100 /*BIF_NEWDIALOGSTYLE*/
            };
            IntPtr pidl = IntPtr.Zero;
            try
            {
                pidl = SHBrowseForFolder(bi);
                if (pidl == IntPtr.Zero) return null;
                var sb = new StringBuilder(260);
                return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            }
        }
    }
}
