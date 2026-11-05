using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PyTools.Core
{
    /// <summary>
    /// 剪贴板文件条目的 shell 小图标（按扩展名取，全部走 SHGFI_USEFILEATTRIBUTES，
    /// 文件已删除也照样出图标）。只在内存里缓存，一次会话内同一扩展名零成本。
    /// </summary>
    public static class FileIcons
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private static readonly Dictionary<string, BitmapSource> Cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private static BitmapSource _fallback;
        private static bool _fallbackTried;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>按路径扩展名取小图标；取不到时退回通用文件图标，再取不到返回 null。</summary>
        public static BitmapSource ForPath(string path)
        {
            var extension = "";
            try { extension = System.IO.Path.GetExtension(path ?? "") ?? ""; }
            catch { extension = ""; }

            lock (Cache)
            {
                BitmapSource cached;
                if (Cache.TryGetValue(extension, out cached)) return cached;
                var icon = Load(extension);
                Cache[extension] = icon;
                return icon;
            }
        }

        private static BitmapSource Load(string extension)
        {
            var probe = "file" + (string.IsNullOrEmpty(extension) ? "" : extension);
            var icon = FromShell(probe);
            if (icon != null) return icon;

            if (!_fallbackTried)
            {
                _fallbackTried = true;
                _fallback = FromShell("file");
            }
            return _fallback;
        }

        private static BitmapSource FromShell(string probeName)
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(probeName, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
    }
}
