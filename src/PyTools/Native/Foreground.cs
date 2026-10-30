using System;
using System.Collections.Generic;
using System.Threading;
using PyTools.Core;

namespace PyTools.Native
{
    /// <summary>
    /// 前台窗口 capture/restore。
    /// v1 痛点根治：无 helper 进程、无 1s 超时静默放弃；恢复后采样验证 + 3 级升档重试，全程落日志。
    /// </summary>
    public static class Foreground
    {
        private const int VerifySamples = 5;      // 每次尝试后采样次数
        private const int VerifyIntervalMs = 100;
        private const int MaxAttempts = 3;

        public sealed class RestoreReport
        {
            public IntPtr Target;
            public bool Restored;
            public int Attempts;
            public List<string> Log = new List<string>();
        }

        /// <summary>记录呼出前的前台窗口。进程内同步调用，零开销。</summary>
        public static IntPtr Capture()
        {
            return NativeMethods.GetForegroundWindow();
        }

        public static RestoreReport Restore(IntPtr target)
        {
            var report = new RestoreReport { Target = target, Restored = target == IntPtr.Zero };
            if (target == IntPtr.Zero)
            {
                report.Log.Add("target=0（呼出前无前台窗口），跳过恢复");
                return report;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                report.Attempts = attempt;
                var set = TrySetForeground(target, attempt);
                var verified = VerifyForeground(target);
                report.Log.Add("attempt=" + attempt + " set=" + set + " verified=" + verified);
                if (verified)
                {
                    report.Restored = true;
                    break;
                }
            }
            sw.Stop();
            foreach (var line in report.Log) Logger.Debug("foreground restore: " + line);
            Logger.Info("foreground restore target=0x" + target.ToString("X") +
                        " restored=" + report.Restored + " attempts=" + report.Attempts +
                        " elapsed=" + sw.ElapsedMilliseconds + "ms");
            return report;
        }

        private static bool TrySetForeground(IntPtr target, int attempt)
        {
            var targetThread = NativeMethods.GetWindowThreadProcessId(target, IntPtr.Zero);
            var currentThread = NativeMethods.GetCurrentThreadId();
            var attached = false;
            try
            {
                if (targetThread != 0 && targetThread != currentThread)
                    attached = NativeMethods.AttachThreadInput(currentThread, targetThread, true);

                // 第 2 级起补一次 ALT 键事件，绕过前台锁
                if (attempt >= 2)
                {
                    NativeMethods.keybd_event(NativeMethods.VkMenu, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(NativeMethods.VkMenu, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
                }

                return NativeMethods.SetForegroundWindow(target);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }

        private static bool VerifyForeground(IntPtr target)
        {
            for (var i = 0; i < VerifySamples; i++)
            {
                if (NativeMethods.GetForegroundWindow() == target) return true;
                Thread.Sleep(VerifyIntervalMs);
            }
            return false;
        }
    }
}
