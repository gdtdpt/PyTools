using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PyTools.Native
{
    /// <summary>
    /// .lnk 解析（IShellLinkW + IPersistFile，零依赖 COM P/Invoke）。
    /// electron 的 shell.readShortcutLink 无 BCL 等价物；本模块同时服务扫描与右键登记。
    /// </summary>
    public static class ShellLink
    {
        private const uint StgmRead = 0;
        private const int MaxPath = 260;

        public sealed class LinkInfo
        {
            public string Target = "";
            public string Arguments = "";
            public string WorkingDirectory = "";
            public string IconPath = "";
            public int IconIndex;
        }

        public static bool TryResolve(string linkPath, out LinkInfo info)
        {
            info = null;
            try
            {
                var link = (IShellLinkW)new CShellLink();
                var persist = (IPersistFile)link;
                persist.Load(linkPath, StgmRead);

                var result = new LinkInfo();

                var target = new StringBuilder(MaxPath * 2);
                var findData = new Win32FindDataW();
                link.GetPath(target, target.Capacity, ref findData, 0);
                result.Target = target.ToString();

                var args = new StringBuilder(2048);
                link.GetArguments(args, args.Capacity);
                result.Arguments = args.ToString();

                var workDir = new StringBuilder(MaxPath);
                link.GetWorkingDirectory(workDir, workDir.Capacity);
                result.WorkingDirectory = workDir.ToString();

                var iconPath = new StringBuilder(MaxPath * 2);
                int iconIndex;
                link.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex);
                result.IconPath = iconPath.ToString();
                result.IconIndex = iconIndex;

                info = result;
                return !string.IsNullOrEmpty(result.Target);
            }
            catch
            {
                return false;
            }
        }

        // ---- COM 声明 ----

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink
        {
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindDataW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, ref Win32FindDataW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            // IPersist 继承部分（vtable 顺序敏感）
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }
}
