using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PyTools.Native;

namespace PyTools.Core
{
    /// <summary>
    /// 剪贴板监听（M3，C1）：AddClipboardFormatListener + WM_CLIPBOARDUPDATE 事件驱动，
    /// 替代 v1 的 500ms 轮询（会漏配连续快速复制）。
    /// 窗口必须是**顶层窗口**——AddClipboardFormatListener 对 message-only 窗口的行为没有文档保证，
    /// 所以这里单独建一个不可见、无边框、不进 Alt+Tab 的顶层 HwndSource，不与热键窗口共用。
    /// </summary>
    public sealed class ClipboardWatcher : IDisposable
    {
        private const int MaxReadAttempts = 3;
        private static readonly int[] RetryDelaysMs = { 10, 30, 90 };

        private readonly Action<IDataObject> _onData;
        private HwndSource _source;
        private IntPtr _hwnd = IntPtr.Zero;
        private DispatcherTimer _retryTimer;

        public ClipboardWatcher(Action<IDataObject> onData)
        {
            _onData = onData;
        }

        public bool IsRunning { get { return _source != null; } }

        public bool Start()
        {
            if (_source != null) return true;

            var parameters = new HwndSourceParameters("PyTools.Clipboard")
            {
                Width = 1,
                Height = 1,
                WindowStyle = 0,                  // 无边框无标题，且不含 WS_VISIBLE → 不可见
                ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW：不进 Alt+Tab
                HwndSourceHook = WndProc
            };

            try
            {
                _source = new HwndSource(parameters);
                _hwnd = _source.Handle;
            }
            catch (Exception ex)
            {
                Logger.Error("clipboard watcher window failed: " + ex.Message);
                _source = null;
                return false;
            }

            if (!NativeMethods.AddClipboardFormatListener(_hwnd))
            {
                Logger.Error("AddClipboardFormatListener failed, err=" + Marshal.GetLastWin32Error());
                Dispose();
                return false;
            }
            Logger.Info("clipboard watcher started");
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WmClipboardUpdate)
            {
                handled = true;
                // 不在消息处理里读剪贴板：先回消息泵，读完再处理（读取可能耗时，阻塞会拖慢源程序）
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { TryRead(0); }));
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 消息到达瞬间剪贴板常仍被源进程锁定（OpenClipboard 返回 ERROR_ACCESS_DENIED），
        /// 所以按 10/30/90ms 退避重试；仍失败就丢弃本次——下一次复制会再触发，不留队列。
        /// </summary>
        private void TryRead(int attempt)
        {
            IDataObject data = null;
            try
            {
                data = Clipboard.GetDataObject();
            }
            catch (Exception ex)
            {
                Logger.Debug("clipboard read attempt " + attempt + " failed: " + ex.Message);
            }

            if (data == null)
            {
                if (attempt + 1 < MaxReadAttempts) { ScheduleRetry(attempt + 1); return; }
                Logger.Info("clipboard read gave up after " + MaxReadAttempts + " attempts");
                return;
            }

            try
            {
                _onData(data);
            }
            catch (Exception ex)
            {
                Logger.Error("clipboard handler failed: " + ex.Message);
            }
        }

        private void ScheduleRetry(int attempt)
        {
            if (_retryTimer != null) { _retryTimer.Stop(); _retryTimer = null; }
            _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RetryDelaysMs[attempt]) };
            _retryTimer.Tick += delegate
            {
                _retryTimer.Stop();
                _retryTimer = null;
                TryRead(attempt);
            };
            _retryTimer.Start();
        }

        public void Dispose()
        {
            if (_retryTimer != null) { _retryTimer.Stop(); _retryTimer = null; }
            if (_hwnd != IntPtr.Zero)
            {
                try { NativeMethods.RemoveClipboardFormatListener(_hwnd); }
                catch { /* 退出路径尽力而为 */ }
                _hwnd = IntPtr.Zero;
            }
            if (_source != null)
            {
                _source.Dispose();
                _source = null;
            }
        }
    }
}
