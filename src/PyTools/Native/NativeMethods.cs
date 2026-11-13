using System;
using System.Runtime.InteropServices;

namespace PyTools.Native
{
    internal static class NativeMethods
    {
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint WmHotkey = 0x0312;
        public const byte VkMenu = 0x12;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(uint dwProcessId);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public const uint AttachParentProcess = 0xFFFFFFFF;

        // ---- 剪贴板监听（M3）----

        public const int WmClipboardUpdate = 0x031D;
        public const int ErrorHotkeyAlreadyRegistered = 1409;
        public const uint ModNoRepeat = 0x4000;

        /// <summary>剪贴板内容变化时向窗口投递 WM_CLIPBOARDUPDATE。窗口须为顶层窗口。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public static void TryAttachConsole()
        {
            try { AttachConsole(AttachParentProcess); }
            catch { /* 自测输出尽力而为 */ }
        }

        // ---- 多屏定位（2026-11-09 呼出窗跟随鼠标）----

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        /// <summary>MONITORINFOEX：比 MONITORINFO 多一个显示器设备名（"\\.\DISPLAY1"）。
        /// 设备名是「每屏独立位置记忆」的身份来源（2026-11-13）：只按工作区几何记忆会在
        /// 两块屏工作区宽度不同的机器上互相污染（小屏被搬到偏右）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfo
        {
            public int CbSize;
            public NativeRect RcMonitor;
            public NativeRect RcWork;
            public uint DwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string SzDevice; // CCHDEVICENAME
        }

        public const uint MonitorDefaultToNearest = 2;
        public const int SmCxVirtualScreen = 76;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out NativePoint lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        /// <summary>取 MONITORINFOEX（含设备名）。CharSet 必须与结构体的 ByValTStr 一致（Unicode）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        // ---- 物理像素落位（2026-11-10 混合 DPI 定位）----

        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const int MdtEffectiveDpi = 0;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        /// <summary>逐屏有效 DPI。Win8.1+（shcore）；调用方需自行兜底旧系统。</summary>
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
