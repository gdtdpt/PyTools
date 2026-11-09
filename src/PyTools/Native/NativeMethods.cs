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

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int CbSize;
            public NativeRect RcMonitor;
            public NativeRect RcWork;
            public uint DwFlags;
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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
