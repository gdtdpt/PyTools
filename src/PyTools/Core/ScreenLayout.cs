using System;
using System.Windows;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>
    /// 多屏工作区定位（2026-11-09 呼出窗跟随鼠标所在屏）。
    /// 物理像素 → WPF 坐标的折算系数在运行时用「虚拟桌面 DIP 宽 / 物理像素宽」实测，
    /// 与 SystemParameters 的虚拟桌面值保持同一坐标系（不假设 DPI 数值；
    /// SystemParameters 与 GetSystemMetrics 对 DPI 感知进程按同一系统 DPI 折算，比值即物理→DIP 系数）。
    /// ClampIntoWork 是纯函数，selftest 直接覆盖。
    /// </summary>
    internal static class ScreenLayout
    {
        /// <summary>鼠标所在显示器的工作区（WPF 坐标）。取不到鼠标/显示器信息时返回 Rect.Empty。</summary>
        public static Rect CursorMonitorWorkArea()
        {
            NativeMethods.NativePoint pt;
            if (!NativeMethods.GetCursorPos(out pt)) return Rect.Empty;
            return MonitorWorkArea(NativeMethods.MonitorFromPoint(pt, NativeMethods.MonitorDefaultToNearest));
        }

        /// <summary>窗口所在显示器的工作区（WPF 坐标）。窗口无效时返回 Rect.Empty。</summary>
        public static Rect WindowMonitorWorkArea(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Rect.Empty;
            return MonitorWorkArea(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest));
        }

        private static Rect MonitorWorkArea(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero) return Rect.Empty;
            var mi = new NativeMethods.MonitorInfo();
            mi.CbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MonitorInfo));
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return Rect.Empty;
            var k = PhysToDipScale();
            var rc = mi.RcWork;
            return new Rect(rc.Left * k, rc.Top * k, (rc.Right - rc.Left) * k, (rc.Bottom - rc.Top) * k);
        }

        /// <summary>物理像素 → DIP 折算系数。GetSystemMetrics 与 SystemParameters 同按系统 DPI 折算，比值恒定；异常兜底 1.0。</summary>
        private static double PhysToDipScale()
        {
            try
            {
                var physW = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
                var dipW = SystemParameters.VirtualScreenWidth;
                if (physW <= 0 || dipW <= 0) return 1.0;
                return dipW / physW;
            }
            catch { return 1.0; }
        }

        /// <summary>
        /// 把左上角 (left, top) 钳进工作区：至少保留 minVisibleX/minVisibleY 可见像素（窗口大部分可见），
        /// 上/左边界允许贴边（不允许整窗出屏）。width <= 0 或 work 为空时原样返回。
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
