using System;
using System.Runtime.InteropServices;
using System.Windows;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>单个显示器的度量：工作区（物理像素）+ 该屏 DPI 缩放（dpi/96）。</summary>
    internal sealed class ScreenInfo
    {
        public bool Available;
        public Rect Work;              // 物理像素，与 Win32 坐标系一致
        public Rect Bounds;            // 整屏物理矩形（含任务栏区域），仅用于 Key 的身份判定
        public string Device = "";     // 显示器设备名（"\\.\DISPLAY1"）
        public uint Dpi = 96;
        public double Scale = 1.0;     // = Dpi / 96

        /// <summary>
        /// 每屏位置记忆的身份键：设备名 + 整屏物理尺寸（2026-11-13）。
        /// 只用设备名时，改分辨率/换缩放后旧偏移会被继续沿用（越界或贴边）；带上整屏尺寸，
        /// 分辨率一变就是新键 → 自动回到默认位。取不到设备名时退化为纯尺寸键。
        /// </summary>
        public string Key
        {
            get
            {
                var size = Bounds.IsEmpty ? "0x0" : ((int)Math.Round(Bounds.Width)) + "x" + ((int)Math.Round(Bounds.Height));
                return (string.IsNullOrEmpty(Device) ? "mon" : Device) + "@" + size;
            }
        }
    }

    /// <summary>
    /// 多屏定位（2026-11-10 改为全程物理像素 + 逐屏 DPI）。
    /// 混合 DPI 下「物理 → DIP」的折算系数**逐屏不同**，任何全局系数都会把屏原点算错
    /// （高分辨率屏位置漂移、低分辨率屏整窗飞出屏幕）。因此：
    /// 定位与窗口落位一律用 Win32 物理像素（SetWindowPos），
    /// 只有配置里记的偏移是 DIP，换算时用**目标屏自己的** Scale。
    /// ClampIntoWork 是纯函数、单位无关，selftest 直接覆盖。
    /// </summary>
    internal static class ScreenLayout
    {
        /// <summary>鼠标所在显示器。取不到鼠标/显示器信息时 Available=false。</summary>
        public static ScreenInfo CursorScreen()
        {
            NativeMethods.NativePoint pt;
            if (!NativeMethods.GetCursorPos(out pt)) return new ScreenInfo();
            return FromMonitor(NativeMethods.MonitorFromPoint(pt, NativeMethods.MonitorDefaultToNearest));
        }

        /// <summary>窗口所在显示器。窗口句柄无效时 Available=false。</summary>
        public static ScreenInfo ScreenForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return new ScreenInfo();
            return FromMonitor(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest));
        }

        /// <summary>窗口矩形（物理像素）。句柄无效或调用失败返回 false。</summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out Rect rect)
        {
            rect = Rect.Empty;
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.NativeRect rc;
            if (!NativeMethods.GetWindowRect(hwnd, out rc)) return false;
            rect = new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            return true;
        }

        private static ScreenInfo FromMonitor(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero) return new ScreenInfo();
            var mi = new NativeMethods.MonitorInfo();
            mi.CbSize = Marshal.SizeOf(typeof(NativeMethods.MonitorInfo));
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return new ScreenInfo();
            var rc = mi.RcWork;
            var rcMon = mi.RcMonitor;
            var dpi = EffectiveDpi(hMonitor);
            return new ScreenInfo
            {
                Available = true,
                Work = new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top),
                Bounds = new Rect(rcMon.Left, rcMon.Top, rcMon.Right - rcMon.Left, rcMon.Bottom - rcMon.Top),
                Device = mi.SzDevice ?? "",
                Dpi = dpi,
                Scale = dpi / 96.0
            };
        }

        /// <summary>逐屏有效 DPI（Shcore.GetDpiForMonitor，Win8.1+）。取不到时按 96 兜底。</summary>
        private static uint EffectiveDpi(IntPtr hMonitor)
        {
            try
            {
                uint dpiX, dpiY;
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MdtEffectiveDpi, out dpiX, out dpiY) == 0 && dpiX > 0)
                    return dpiX;
            }
            catch (Exception)
            {
                // Win7/无 shcore：按 96 处理（目标环境 Win10 1709 不会走到这里）
            }
            return 96;
        }

        /// <summary>屏工作区的 DIP 口径（物理 ÷ 该屏 Scale），供 WPF 坐标（Left/Top/Width/Height）的代码使用。</summary>
        public static Rect WorkAreaDip(ScreenInfo screen)
        {
            if (screen == null || !screen.Available) return Rect.Empty;
            var s = screen.Scale <= 0 ? 1.0 : screen.Scale;
            return new Rect(screen.Work.Left / s, screen.Work.Top / s, screen.Work.Width / s, screen.Work.Height / s);
        }

        /// <summary>
        /// 把左上角 (left, top) 钳进工作区：至少保留 minVisibleX/minVisibleY 可见像素（窗口大部分可见），
        /// 上/左边界允许贴边（不允许整窗出屏）。width <= 0 或 work 为空时原样返回。单位与 work 一致。
        /// </summary>
        public static Tuple<double, double> ClampIntoWork(Rect work, double left, double top, double width,
            double minVisibleX, double minVisibleY)
        {
            if (work.IsEmpty || width <= 0) return Tuple.Create(left, top);
            var maxLeft = work.Right - Math.Min(minVisibleX, width);
            var maxTop = work.Bottom - Math.Min(minVisibleY, work.Height); // 高度动态，纵向按「至少可见一截」钳
            if (maxLeft < work.Left) maxLeft = work.Left;
            if (maxTop < work.Top) maxTop = work.Top;
            if (left > maxLeft) left = maxLeft;
            if (top > maxTop) top = maxTop;
            return Tuple.Create(left, top);
        }
    }
}
